# ShardWorker

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/ShardWorker.svg)](https://www.nuget.org/packages/ShardWorker)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-support-yellow.svg)](https://buymeacoffee.com/jayarrowz)

Running background jobs across multiple instances? ShardWorker splits work into shards so each instance processes its own slice, coordinated by your existing database. No Redis, no message broker, no extra infrastructure. See a simulation here: https://jayarrowz.github.io/ShardWorker/

## How it works

The total workload is divided into **N shards** (numbered `0` to `N-1`). Every running instance continuously tries to acquire shard locks via the configured `IShardLockProvider`. Once a shard is acquired, a worker loop is started for it. A heartbeat loop renews held locks in the background. If a lock renewal fails (e.g. the instance was too slow), the worker for that shard is stopped and the shard becomes available for another instance to claim.

```
Instance A (cursor offset 3) Instance B (cursor offset 17)
│                            │
│  walk from offset 3 →      │  walk from offset 17 →
├─ Acquire shard 3 ✓         ├─ Acquire shard 17 ✓
├─ Acquire shard 4 ✓         ├─ Acquire shard 18 ✓
├─ Acquire shard 5 ✓         ├─ Acquire shard 19 ✓
│                            │  ...
│                            │
├─ Heartbeat (renew 3,4,5)   ├─ Heartbeat (renew 17,18,19)
│                            │
└─ Graceful stop:            └─ Graceful stop:
   release 3, 4, 5              release 17, 18, 19
```

Lock rows in the database are the single source of truth. If an instance dies, its lock rows expire naturally after `LockExpiry` and other instances pick up those shards on the next acquire cycle.

---

## Scalability

### Shard count

The acquire and heartbeat loops are both **O(1) DB round-trips** regardless of `TotalShards`. The engine builds candidate lists in memory and passes them to the provider as arrays — a single SQL statement handles thousands of shards as cheaply as ten.

| Operation | DB round-trips |
|---|---|
| Acquire scan (any shard count) | 1 per `AcquireInterval` |
| Heartbeat (any shard count) | 1 per `HeartbeatInterval` |
| Release on shutdown | 1 total |

There is no hard ceiling on `TotalShards`. Values in the tens of thousands are practical.

> **Shards are buckets, not items.** `TotalShards` does not need to grow as your data grows. With `TotalShards = 30` and 100 million users, each shard simply processes ~3.3 million users. Shards get busier over time — they do not multiply. Size `TotalShards` for the maximum number of instances you ever want to run, not for the number of rows in your table.

### Instance count

Each instance independently competes for unclaimed shards. Adding instances automatically redistributes ownership — no coordination layer is needed. Instances that die release their shards via lock expiry; instances that restart reclaim shards on the next acquire cycle.

### Multiple worker types

Each `AddShardEngine<TWorker>` registration is fully independent: its own `BackgroundService`, its own options, and its own DB table. Ten worker types with 1,000 shards each still produces only 1 acquire round-trip and 1 heartbeat round-trip per worker type per interval.

### Tuning guidelines

- **More shards → finer distribution** across instances. Use more shards when you have many instances and want even load balancing.
- **Lower `AcquireInterval`** → faster rebalancing after an instance dies or joins. The cost is still one DB round-trip per interval.
- **Lower `HeartbeatInterval`** → locks renew more frequently, reducing the window where a slow instance's shards expire and get stolen. Must stay below `LockExpiry`.
- **`LockExpiry`** is the maximum time a shard is unavailable after its owning instance crashes. Set it to the longest acceptable gap in processing.
- **`MaxShardsPerInstance`** caps how many shards one instance holds. Use it when you want to guarantee headroom for other instances (e.g. `TotalShards=30`, `MaxShardsPerInstance=10` → at least 3 instances needed to cover all shards). Each instance walks shards in a round-robin order starting from a random cursor offset chosen at startup, so instances naturally spread across different parts of the index range rather than all competing for the same shards each cycle. Every shard gets equal opportunity — a fast-completing shard won't be re-acquired ahead of shards that haven't been visited yet.
- **`ReleaseOnCompletion`** turns the library into a task-queue style dispatcher — see below.
- **`ReleaseOnThrows`** releases a shard after an unhandled exception, letting another instance retry rather than the same instance looping.
- **`WorkerConcurrency`** runs N parallel execution slots per shard. Useful for I/O-bound workers where a single shard produces enough parallelisable work for multiple concurrent calls — see below.

---

## Processing modes

| Mode | `ReleaseOnCompletion` | `ReleaseOnThrows` | Shard ownership | Best for |
|---|---|---|---|---|
| **Persistent partition** | `false` | `false` | Held until shutdown or crash | Continuous polling, scheduled scans |
| **Sparse work** | `false` | `false` | Held when busy, released when idle via `shard.RequestRelease()` | Workloads with uneven or intermittent shard distribution |
| **Task-queue** | `true` | `false` | Released after each clean return | Discrete jobs, work queues |
| **Error retry** | `false` | `true` | Released after any exception | Fail-and-redistribute patterns |
| **Parallel execution** | either | either | Same as above, N slots per shard | I/O-bound workers with parallelisable work |

### Persistent partition (default)

Each instance holds its shards indefinitely. `ExecuteAsync` is called in a tight loop on the same shard:

```
Acquire shard 4
  → ExecuteAsync(shard 4) → wait WorkerInterval
  → ExecuteAsync(shard 4) → wait WorkerInterval
  → ... forever
```

Best for continuous polling workloads (e.g. consuming a per-shard queue or scanning a database partition on a schedule).

The worker receives the same shard on every call and uses it as a stable partition key:

```csharp
// Each shard owns a fixed slice of customers — shard 3 always processes customers where id % 8 == 3.
// The worker runs on a schedule; the shard never changes hands unless this instance dies.
public class CustomerSyncWorker : IShardedWorker
{
    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        var customers = await _db.GetCustomersForShardAsync(shard.Index, shard.Total, ct);
        foreach (var c in customers)
            await _sync.SyncAsync(c, ct);
    }
}

services.AddShardEngine<CustomerSyncWorker>(
    opts =>
    {
        opts.TotalShards   = 8;
        opts.WorkerInterval = TimeSpan.FromMinutes(1); // poll every minute per shard
    },
    new PostgresShardLockProvider(connectionString, "customer_sync_locks"));
```

How `TotalShards` and `MaxShardsPerInstance` interact (example: `TotalShards = 30`):

| Instances | `MaxShardsPerInstance` | Shards per instance | Unclaimed |
|---|---|---|---|
| 1 | ∞ (default) | 30 | 0 |
| 3 | ∞ (default) | ~10 each (first-come) | 0 |
| 3 | `10` | 10 each (enforced) | 0 |
| 2 | `10` | 10 each | 10 — available for a third instance |
| 1 → scales to 3 | `10` | Instance 1 keeps its 30 until restart | New instances claim the gap only if 1 restarts |

> `MaxShardsPerInstance` prevents acquiring more, but does not force an instance to give up shards it already holds. Rebalancing happens naturally on restart or lock expiry.

### Sparse work mode (`shard.RequestRelease()`)

In persistent partition mode shards are held forever, even when they have nothing to do. If your work is unevenly distributed — e.g. only 2 of 10 shards have pending users at any given time — idle shards tie up capacity that could be used by active ones.

`shard.RequestRelease()` lets the worker signal on a per-execution basis: release this shard now if there is nothing to process, but keep holding it when there is. This is safer than `ReleaseOnCompletion = true`, which releases after *every* clean return regardless of whether work was done.

```csharp
public class UserSyncWorker : IShardedWorker
{
    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        var users = await _db.GetPendingUsersForShardAsync(shard.Index, shard.Total, ct);

        if (users.Count == 0)
        {
            shard.RequestRelease(); // nothing here — free the shard for other instances
            return;
        }

        foreach (var user in users)
            await _processor.ProcessAsync(user, ct);
        // returning without RequestRelease() keeps the shard held
    }
}
```

When `RequestRelease()` is called, the shard is released at the end of that execution and goes back into the unclaimed pool. On the next `AcquireInterval` tick, any instance (including this one, if it has capacity) can claim it again. If new work has since arrived in that shard's bucket, it will be picked up within one acquire cycle.

> **`RequestRelease()` is evaluated only on a clean return.** If `ExecuteAsync` throws, the flag is ignored — the throw path is controlled by `ReleaseOnThrows`. Calling `RequestRelease()` when `ReleaseOnCompletion = true` has no additional effect; the shard would have been released anyway.

### Task-queue mode (`ReleaseOnCompletion = true`)

Here each shard represents a **single unit of work** (e.g. one report, one import job). The worker processes it once and returns — the shard is immediately released back to the pool for any instance to pick up next.

The shard index is used to partition pending work — each shard owns a slice of the `reports` table determined by `id % totalShards`:

```sql
-- GetNextPendingReportAsync: fetch one pending report belonging to this shard
SELECT TOP 1 id, payload
FROM reports
WHERE status = 'pending'
  AND id % @totalShards = @shardIndex
ORDER BY created_at
```

```csharp
public class ReportGeneratorWorker : IShardedWorker
{
    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        var report = await _db.GetNextPendingReportAsync(shard.Index, shard.Total, ct);
        if (report is null) return; // nothing to do — release and move on

        await _generator.GenerateAsync(report, ct);
        await _db.MarkReportCompleteAsync(report.Id, ct);
        // returning normally triggers the release
    }
}

services.AddShardEngine<ReportGeneratorWorker>(
    opts =>
    {
        opts.TotalShards          = 30;
        opts.MaxShardsPerInstance = 10;  // hold at most 10 at a time
        opts.ReleaseOnCompletion  = true; // release after each execution
        // WorkerInterval is not applied in task-queue mode — the worker slot exits
        // immediately after completion. Re-acquire speed is controlled by AcquireInterval.
    },
    new PostgresShardLockProvider(connectionString, "report_locks"));
```

When `ExecuteAsync` returns normally (without throwing), the shard is released immediately and goes back into the unclaimed pool. The engine then picks up a different unclaimed shard on the next acquire cycle.

```csharp
services.AddShardEngine<OrderWorker>(
    opts =>
    {
        opts.TotalShards          = 30;
        opts.MaxShardsPerInstance = 10;  // hold at most 10 at a time
        opts.ReleaseOnCompletion  = true; // release after each execution
    },
    new PostgresShardLockProvider(connectionString, "order_shard_locks"));
```

With 30 shards, 2 instances, and `MaxShardsPerInstance = 10`:

| Step | Instance A | Instance B | Unclaimed |
|---|---|---|---|
| Startup | Claims 10 shards e.g. {3,7,1,...} | Claims 10 shards e.g. {0,5,22,...} | Remaining 10 |
| A finishes shard 3 | Releases 3 → holds 9 | Holds its 10 | 3 + remaining |
| Next acquire cycle | Claims one unclaimed shard → back to 10 | Holds its 10 | One fewer unclaimed |

> Instances walk their candidate list in round-robin order starting from a random offset, so the specific shard indices claimed at startup are non-deterministic. The table uses placeholder sets to illustrate the cycling pattern.
| Steady state | 10 held, cycling through pool | 10 held, cycling through pool | Always some unclaimed |

> If `ExecuteAsync` **throws**, the shard is **not** released — it stays held and retries on the next loop iteration. Only a clean return triggers the release.

> **`MaxShardsPerInstance` is strongly recommended with `ReleaseOnCompletion`.** Without it, an instance has unlimited capacity and will re-acquire every shard it just released on the very next acquire cycle — starving other instances. Setting `MaxShardsPerInstance` to `TotalShards / expectedInstances` ensures released shards stay unclaimed long enough for other instances to compete for them.

### Error retry mode (`ReleaseOnThrows = true`)

By default, if `ExecuteAsync` throws, the error is logged and the shard is retried after `WorkerInterval`. With `ReleaseOnThrows = true`, the shard is released immediately so **any** instance (not necessarily this one) can attempt it on the next acquire cycle:

```csharp
opts.ReleaseOnThrows = true;
```

If you want the same instance to keep the shard but back off longer after a failure, use `WorkerIntervalOnThrows`:

```csharp
opts.WorkerInterval          = TimeSpan.FromSeconds(30); // normal cadence
opts.WorkerIntervalOnThrows  = TimeSpan.FromMinutes(5);  // cool-down after an exception
```

This is independent of `ReleaseOnThrows` — if `ReleaseOnThrows = true`, the shard is released and `WorkerIntervalOnThrows` has no effect.

### Parallel execution (`WorkerConcurrency > 1`)

Each shard can run multiple concurrent `ExecuteAsync` calls. Each **slot** is an independent execution loop with its own `WorkerInterval` cadence:

```csharp
services.AddShardEngine<OrderWorker>(
    opts =>
    {
        opts.TotalShards      = 10;
        opts.WorkerConcurrency = 4;  // 4 concurrent calls per shard
        opts.WorkerInterval   = TimeSpan.FromSeconds(0); // back-to-back
    },
    new PostgresShardLockProvider(connectionString, "order_shard_locks"));
```

With 10 shards and `WorkerConcurrency = 4`, a single instance runs up to **40 concurrent `ExecuteAsync` calls**. This is useful when each shard maps to a queue or partition that can be drained in parallel.

`ReleaseOnCompletion`, `ReleaseOnThrows`, and `shard.RequestRelease()` interact with concurrency as follows:
- When any slot triggers a release (via `ReleaseOnCompletion`, `ReleaseOnThrows`, or `RequestRelease()`), the shared CTS is cancelled and all other slots for that shard observe the cancellation and exit cleanly.
- The shard is released once **all** slots have exited (the `finally` block in `StartWorker` fires after `Task.WhenAll`).

---

## Partitioning your data

Every worker query uses `id % totalShards = shardIndex` to select only the rows that belong to the current shard. Understanding what makes a good partition key avoids subtle problems.

### Sizing `TotalShards`

`TotalShards` is the number of **buckets**, not a capacity limit. Every row, no matter when it was created, maps to one of the existing buckets:

```
TotalShards = 30

User 1        →     1 % 30 = shard 1
User 31       →    31 % 30 = shard 1   ← same bucket, more work inside it
User 10,000   → 10000 % 30 = shard 10
User 100,000  → 100000 % 30 = shard 10 ← same bucket, even more work
```

As data grows, each shard processes more rows per execution — but the number of shards stays the same. Size `TotalShards` for the maximum number of instances you would ever want to run:

| Expected max instances | Suggested `TotalShards` |
|---|---|
| 1–3 | 10–30 |
| 5–10 | 50–100 |
| 10–50 | 100–500 |
| 50+ | 500–1 000 |

There is no cost to choosing a larger value — pick headroom now rather than changing it later.

> **Changing `TotalShards` is a breaking migration.** Every row's shard assignment changes (`userId % 30` vs `userId % 60`), which can cause rows to be missed or double-processed during the transition. Treat it like a database schema change: plan it deliberately, don't let it creep up on you.

### Requirements

- **Stable** — the key must never change for the lifetime of a row. If a row's key changes, it silently moves to a different shard and may be missed or double-processed.
- **Numeric** — modulo requires an integer. Non-numeric keys must be hashed first (see below).
- **Not necessarily sequential or contiguous** — gaps in the sequence are fine. `id % 8` distributes rows with IDs `1, 5, 100, 10000` just as evenly as `1, 2, 3, 4`.

### Distribution

Modulo partitioning is uniform when keys are roughly evenly distributed across all remainders. This holds for:

- Auto-increment integers (`IDENTITY`, `SERIAL`) — consecutive values cycle through all remainders evenly.
- Most random / UUID-derived integers — no systematic bias.

It can skew when keys are multiples of `TotalShards` or share a common factor with it. For example, if all IDs happen to be even and `TotalShards = 8`, only even-remainder shards receive work. In practice this is rare with natural keys, but if you observe uneven load, choose a prime `TotalShards` value (e.g. 7, 11, 13) which has no common factors with typical key patterns.

### Non-numeric keys

If your primary key is a string or UUID, hash it to an integer before applying modulo:

```sql
-- PostgreSQL
WHERE id % @totalShards = @shardIndex          -- integer PK

-- PostgreSQL with UUID/text PK
WHERE abs(hashtext(id::text)) % @totalShards = @shardIndex

-- SQL Server with integer PK
WHERE id % @totalShards = @shardIndex

-- SQL Server with string/uniqueidentifier PK
WHERE abs(checksum(cast(id as nvarchar(64)))) % @totalShards = @shardIndex
```

---

## Projects

| Project | Target | Description |
|---|---|---|
| `ShardWorker.Core` | `netstandard2.0` | Interfaces and models — no dependencies |
| `ShardWorker` | `netstandard2.0` | Engine and DI registration |
| `ShardWorker.Observability` | `net8.0;net9.0;net10.0` | Optional: `System.Diagnostics.Metrics` counters + DI helpers |
| `ShardWorker.Dashboard` | `net8.0;net9.0;net10.0` | Optional: embedded Blazor dashboard at `/shard-dashboard` |
| `ShardWorker.Providers.InMemory` | `netstandard2.0` | In-process lock provider for testing |
| `ShardWorker.Providers.Postgres` | `net8.0;net9.0;net10.0` | PostgreSQL lock provider via Npgsql |
| `ShardWorker.Providers.MsSql` | `net8.0;net9.0;net10.0` | SQL Server lock provider via Microsoft.Data.SqlClient |
| `ShardWorker.Providers.MySql` | `net8.0;net9.0;net10.0` | MySQL/MariaDB lock provider via MySqlConnector |

---

## Quick start

### 1. Implement `IShardedWorker`

```csharp
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

public class OrderWorker : IShardedWorker
{
    private readonly IOrderRepository _orders;
    private readonly IOrderProcessor _processor;

    public OrderWorker(IOrderRepository orders, IOrderProcessor processor)
    {
        _orders = orders;
        _processor = processor;
    }

    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        // Divide the work by using modulo on a stable numeric key.
        // Each instance owns a slice — only rows where (id % Total == Index) land here.
        //
        //   TotalShards = 8, this instance owns shard 3:
        //   shard.Index      = 3           — zero-based shard index owned by this instance
        //   shard.Total      = 8           — total shards configured for this worker
        //   shard.InstanceId = "a3f9c2..."  — unique identifier for this running process
        //   shard.WorkerName = "OrderWorker" — class name of the worker type
        //   → orders with id % 8 == 3  (e.g. 3, 11, 19, 27 …)

        var orders = await _orders.GetPendingAsync(
            shardIndex: shard.Index,
            totalShards: shard.Total,
            ct);

        foreach (var order in orders)
            await _processor.ProcessAsync(order, ct);
    }
}
```

The matching query (SQL example):

```sql
-- Only fetch rows that belong to this shard
SELECT * FROM orders
WHERE status = 'pending'
  AND id % @totalShards = @shardIndex
```

### 2. Register with the host

```csharp
using ShardWorker;
using ShardWorker.Providers.Postgres;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddShardEngine<OrderWorker>(
    opts =>
    {
        opts.TotalShards       = 10;
        opts.LockExpiry        = TimeSpan.FromMinutes(2);
        opts.HeartbeatInterval = TimeSpan.FromSeconds(30);
        opts.AcquireInterval   = TimeSpan.FromSeconds(15);
        opts.WorkerInterval    = TimeSpan.FromSeconds(30);
        opts.ShutdownTimeout   = TimeSpan.FromSeconds(30);
    },
    new PostgresShardLockProvider(connectionString, tableName: "order_shard_locks"));

var app = builder.Build();
await app.RunAsync();
```

---

## Multiple workers in the same host

Each worker type is fully isolated — its own engine, its own options, and its own lock namespace. Pass a different `tableName` to `PostgresShardLockProvider` per worker so lock rows never collide.

```csharp
// Worker 1 — 10 shards, runs every 30 seconds
services.AddShardEngine<OrderWorker>(
    opts => { opts.TotalShards = 10; },
    new PostgresShardLockProvider(connectionString, "order_shard_locks"));

// Worker 2 — 5 shards, runs every minute
services.AddShardEngine<InvoiceWorker>(
    opts =>
    {
        opts.TotalShards      = 5;
        opts.WorkerInterval   = TimeSpan.FromMinutes(1);
        opts.LockExpiry       = TimeSpan.FromMinutes(5);
        opts.HeartbeatInterval = TimeSpan.FromMinutes(1);
    },
    new PostgresShardLockProvider(connectionString, "invoice_shard_locks"));
```

Both engines run concurrently as independent `BackgroundService` instances. Shard index 0 for `OrderWorker` and shard index 0 for `InvoiceWorker` are completely separate locks.

---

## Configuration reference

All options live in `ShardWorkerOptions`. Each worker type can have its own values when using the generic API.

| Property | Default | Description |
|---|---|---|
| `TotalShards` | `10` | Total shards divided across all instances |
| `LockExpiry` | `2 min` | How long a lock row lives before another instance may steal it |
| `HeartbeatInterval` | `30 s` | How often held shards are renewed. Must be less than `LockExpiry` |
| `AcquireInterval` | `15 s` | How often to scan for unowned/expired shards |
| `WorkerInterval` | `30 s` | Pause between consecutive `ExecuteAsync` calls on the same shard (per slot) |
| `ShutdownTimeout` | `30 s` | How long `StopAsync` waits for in-flight workers before giving up |
| `MaxShardsPerInstance` | unlimited | Maximum shards one instance will hold at once. Other shards remain unclaimed for other instances |
| `ReleaseOnCompletion` | `false` | When `true`, a shard is released immediately after `ExecuteAsync` returns normally — enabling task-queue style processing |
| `ReleaseOnThrows` | `false` | When `true`, a shard is released immediately after `ExecuteAsync` throws, so another instance can retry it |
| `WorkerIntervalOnThrows` | `null` | Override for `WorkerInterval` used when `ExecuteAsync` throws. `null` falls back to `WorkerInterval`. Use a longer value to add cool-down after failures without slowing the normal cadence |
| `WorkerConcurrency` | `1` | Number of concurrent `ExecuteAsync` calls per shard. Each slot runs its own independent loop |

> **Startup validation rules:** The engine throws `InvalidOperationException` at startup if:
> - `HeartbeatInterval >= LockExpiry`
> - `TotalShards <= 0`
> - `WorkerConcurrency <= 0`

---

## Lock providers

### PostgreSQL

```csharp
new PostgresShardLockProvider(connectionString, tableName: "__shard_locks")
```

`EnsureSchemaAsync` creates the table if it does not exist:

```sql
CREATE TABLE IF NOT EXISTS __shard_locks (
    shard_index  INTEGER      NOT NULL PRIMARY KEY,
    instance_id  TEXT         NOT NULL,
    expires_at   TIMESTAMPTZ  NOT NULL
);
```

All lock operations are **bulk, single round-trip** SQL statements regardless of shard count:

- **Acquire** — `INSERT … SELECT unnest(@candidates) … ON CONFLICT DO UPDATE … RETURNING shard_index` atomically claims all unclaimed/expired candidate shards in one CTE.
- **Renew** — `UPDATE … WHERE shard_index = ANY(@shards) RETURNING shard_index` heartbeats all held shards at once; any shard absent from the result was stolen.
- **Release** — `DELETE … WHERE shard_index = ANY(@shards)` drops all held rows in one statement.

This means 10,000 shards costs the same number of DB round-trips as 10 shards (1 per acquire interval, 1 per heartbeat interval).

**Package:** `Npgsql` 10.x. Targets `net8.0`, `net9.0`, `net10.0`.

### SQL Server

```csharp
new MsSqlShardLockProvider(connectionString, tableName: "__shard_locks")
```

`EnsureSchemaAsync` creates the table if it does not exist:

```sql
IF OBJECT_ID(N'[__shard_locks]', N'U') IS NULL
    CREATE TABLE [__shard_locks] (
        shard_index  INT             NOT NULL PRIMARY KEY,
        instance_id  NVARCHAR(256)   NOT NULL,
        expires_at   DATETIMEOFFSET  NOT NULL
    );
```

Lock operations use `MERGE … WITH (HOLDLOCK)` for atomic acquire and `UPDATE … OUTPUT` for renew — both are single round-trips.

**Package:** `Microsoft.Data.SqlClient` 6.x. Targets `net8.0`, `net9.0`, `net10.0`.

#### Limitations

- The acquire `MERGE` statement builds one parameter per candidate shard (`@s0`, `@s1`, …). SQL Server has a **2,100 parameter limit** per statement, so `TotalShards` (or more precisely `MaxShardsPerInstance`) must stay below ~2,000 if a single instance could be a candidate for all of them. In practice, with multiple instances the candidate list is much smaller.
- `MERGE` with `HOLDLOCK` takes a range lock on the target table, which can cause higher lock contention under very high acquire frequency. Keep `AcquireInterval` at a reasonable value (≥ 5 s).

### MySQL / MariaDB

```csharp
new MySqlShardLockProvider(connectionString, tableName: "__shard_locks")
```

`EnsureSchemaAsync` creates the table if it does not exist:

```sql
CREATE TABLE IF NOT EXISTS `__shard_locks` (
    shard_index  INT          NOT NULL PRIMARY KEY,
    instance_id  VARCHAR(256) NOT NULL,
    expires_at   DATETIME(6)  NOT NULL
);
```

**Package:** `MySqlConnector` 2.x. Targets `net8.0`, `net9.0`, `net10.0`.

#### Limitations

- **Acquire and Renew each cost 2 round-trips** instead of 1. MySQL/MariaDB does not support `RETURNING` on `INSERT … ON DUPLICATE KEY UPDATE` or `UPDATE`, so a follow-up `SELECT` is required to determine which shards are now owned. The heartbeat scalability table in the [Scalability](#scalability) section does not apply — both operations are 2 round-trips regardless of shard count.
- The `VALUES()` reference function used in `ON DUPLICATE KEY UPDATE` is deprecated in MySQL 8.0.20+. It is still functional on all current MySQL 8.x and MariaDB 10.x releases, but may be removed in a future major version.
- Like SQL Server, candidate parameters are expanded inline (`@s0`, `@s1`, …). MySQL's `max_allowed_packet` and server-side parameter limits are generous enough for typical shard counts, but keep `TotalShards` in the thousands rather than tens of thousands.

### In-Memory

```csharp
new InMemoryShardLockProvider()
```

Thread-safe, in-process only. Suitable for unit tests and single-instance local development. Does not persist across restarts.

**Package:** none beyond `ShardWorker.Core`. Targets `netstandard2.0`.

### Custom provider

Implement `IShardLockProvider`:

```csharp
public interface IShardLockProvider
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    // Atomically claim all candidates in one round-trip.
    // Returns the subset this instance owns after the call.
    Task<IReadOnlyList<int>> TryAcquireManyAsync(
        IReadOnlyList<int> candidates, string instanceId, TimeSpan expiry, CancellationToken ct = default);

    // Heartbeat — renew all held shards in one round-trip.
    // Returns the subset still owned; anything absent was stolen.
    Task<IReadOnlyList<int>> RenewManyAsync(
        IReadOnlyList<int> held, string instanceId, TimeSpan expiry, CancellationToken ct = default);

    // Release the given shards so other instances can claim them immediately.
    Task ReleaseManyAsync(IReadOnlyList<int> shards, string instanceId, CancellationToken ct = default);
}
```

Pass it directly to `AddShardEngine`:

```csharp
services.AddShardEngine<MyWorker>(opts => { ... }, new MySqlShardLockProvider(...));
```

Or use the factory overload when the provider needs services from the DI container:

```csharp
services.AddShardEngine<MyWorker>(
    opts => { ... },
    sp => new MySqlShardLockProvider(sp.GetRequiredService<IConfiguration>()["ConnectionString"]));
```

---

## Graceful shutdown

On `IHost` shutdown the engine:

1. Signals the `BackgroundService` stopping token — acquire and heartbeat loops exit.
2. Cancels each held shard's `CancellationTokenSource` — worker loops exit after the current `ExecuteAsync` call completes.
3. Each worker task releases its shard lock before finishing (5 s timeout). Released locks are immediately available to other instances rather than waiting to expire.
4. `StopAsync` waits up to `ShutdownTimeout` for all tasks; if exceeded it logs a warning and returns — remaining locks expire naturally.

---

## Observability

`ShardWorker` fires structured log messages at every significant lifecycle event (acquire, release, stolen lock, worker fault). For metric-level telemetry, add the optional `ShardWorker.Observability` package which publishes counters via `System.Diagnostics.Metrics` — compatible with OpenTelemetry, Prometheus exporters, and `dotnet-counters`.

### Metrics

```csharp
using ShardWorker.Observability;

builder.Services.AddShardWorkerMetrics();
```

This registers `MetricsShardEngineObserver` and exposes the following counters under the meter name **`ShardWorker`**:

| Instrument | Kind | Description |
|---|---|---|
| `shardworker.shards.acquired` | Counter | Shard lock acquired by this instance |
| `shardworker.shards.released` | Counter | Shard lock released by this instance |
| `shardworker.shards.stolen` | Counter | Heartbeat detected a stolen shard |
| `shardworker.worker.faults` | Counter | `ExecuteAsync` threw an unhandled exception |

All counters carry `worker` (worker type name) and `instance_id` tags.

### Custom observer

Implement `IShardEngineObserver` from `ShardWorker.Core` and register it with DI:

```csharp
public sealed class AlertingObserver : IShardEngineObserver
{
    private readonly IAlertService _alerts;
    public AlertingObserver(IAlertService alerts) => _alerts = alerts;

    public void OnShardAcquired(string workerName, string instanceId, int shardIndex) { }
    public void OnShardReleased(string workerName, string instanceId, int shardIndex) { }
    public void OnShardStolen(string workerName, string instanceId, int shardIndex) =>
        _alerts.Warn($"{workerName} shard {shardIndex} was stolen from {instanceId}");
    public void OnWorkerFaulted(string workerName, string instanceId, int shardIndex, Exception exception) =>
        _alerts.Error($"{workerName} shard {shardIndex} faulted", exception);
}
```

**Without metrics** — register your observer as `IShardEngineObserver` before or after `AddShardEngine` (order does not matter):

```csharp
services.AddSingleton<IShardEngineObserver, AlertingObserver>();
services.AddShardEngine<OrderWorker>(opts => { ... }, provider);
```

**With metrics** — use `AddShardEngineObserver<T>()` from `ShardWorker.Observability`; all registered `IShardEngineObserver` implementations receive every event:

```csharp
using ShardWorker.Observability;

builder.Services.AddShardWorkerMetrics();
builder.Services.AddShardEngineObserver<AlertingObserver>();
```

> **All observer methods are called on background threads.** Implementations must be thread-safe and must not throw — any exception is silently swallowed to protect the engine loop.

### .NET Aspire

Aspire's `AddServiceDefaults()` already configures an OpenTelemetry pipeline with an OTLP exporter pointing at the Aspire dashboard. You only need to register the ShardWorker metrics observer and add its meter to the existing pipeline:

```csharp
// Program.cs in your Worker Service / API project
using ShardWorker.Observability;

builder.AddServiceDefaults(); // sets up OTLP exporter, tracing, etc.

builder.Services.AddShardWorkerMetrics(); // registers MetricsShardEngineObserver

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(MetricsShardEngineObserver.MeterName));
```

`AddServiceDefaults()` is generated by the Aspire workload into your project's `ServiceDefaults/Extensions.cs`. It calls `WithMetrics()` internally, so the `.AddOpenTelemetry().WithMetrics(...)` call above merges into the same pipeline — no duplicate exporters are created.

After this, the four ShardWorker counters (`shardworker.shards.acquired`, `shardworker.shards.released`, `shardworker.shards.stolen`, `shardworker.worker.faults`) appear in the Aspire dashboard's Metrics tab, tagged by `worker` and `instance_id`.

---

## Dashboard

<img width="1910" height="879" alt="image" src="https://github.com/user-attachments/assets/1815e384-bb46-4ed4-96d2-adbb5a5405ad" />


`ShardWorker.Dashboard` adds an embedded Blazor page to any ASP.NET Core host that shows live shard ownership across every running instance. It queries the lock provider directly — no side-channel, no extra infrastructure.

### What it shows

- **Per-worker cards** — one card per registered worker type
- **Stats row** — total shards, held count, free count, distinct instance count
- **Shard grid** — one cell per shard, colour-coded by owning instance; free shards shown in grey; shards expiring within 30 s highlighted with an amber outline
- **Instance list** — each instance ID with its held shard indices
- **Auto-refresh** — page reloads every 2 seconds by default (configurable)

### Dashboard and short-lived shards

The dashboard is a **point-in-time snapshot** — it shows which shards are held at the moment the page renders. With `ReleaseOnCompletion = true` and fast-completing work, a shard can be acquired, processed, and released within milliseconds — entirely between two refreshes. In that case the dashboard shows 0 held shards even though the engine is actively cycling through work.

This is expected behaviour, not a bug. The engine is working correctly; the dashboard just cannot observe locks that come and go faster than its refresh interval.

Example scenario where this manifests:

```csharp
opts.TotalShards          = 1000;
opts.MaxShardsPerInstance = 30;
opts.AcquireInterval      = TimeSpan.FromSeconds(1);
opts.ReleaseOnCompletion  = true;
```

With only 200 items of work spread across 1 000 shards, 800 shards have nothing to process and release immediately. The 200 active shards may also complete before the next refresh. The dashboard will frequently show all shards free while the logs confirm steady acquire/release activity.

To make activity more visible in this case:

- **Reduce `refreshIntervalMs`** down toward `AcquireInterval` (e.g. 500 ms): `app.MapShardWorkerDashboard(refreshIntervalMs: 500)`. This increases the chance of catching an in-flight lock but can never guarantee it for sub-100 ms work.
- **Use `shard.RequestRelease()` instead of `ReleaseOnCompletion = true`** — the worker holds the shard and only releases when there is genuinely nothing to do, so active shards stay visible for the duration of their work.
- **Rely on logs and metrics** (`shardworker.shards.acquired` / `shardworker.shards.released` counters) for throughput visibility rather than the dashboard grid when work is very fast.

### Setup

**1. Register the dashboard services** (before `builder.Build()`):

```csharp
using ShardWorker.Dashboard;

builder.Services.AddShardWorkerDashboard();
```

**2. Map the endpoint** (after `app.Build()`):

```csharp
app.MapShardWorkerDashboard(); // serves at /shard-dashboard
```

Custom path:

```csharp
app.MapShardWorkerDashboard("/admin/shards");
```

### Prerequisites

The dashboard renders via ASP.NET Core's static SSR Razor components (`RazorComponentResult`). Your host must have Razor component rendering enabled. In a minimal API host this is done by `AddRazorComponents()`, which `AddShardWorkerDashboard()` calls automatically.

The page requires no JavaScript frameworks, no SignalR, and no persistent connections — it is a plain HTTP GET that renders and returns.

### IShardLockQueryProvider

The dashboard reads shard state by calling `GetAllLocksAsync()` on each worker's lock provider. All four built-in providers implement this interface:

| Provider | Query |
|---|---|
| `InMemoryShardLockProvider` | In-memory dictionary scan |
| `PostgresShardLockProvider` | `SELECT … WHERE expires_at > NOW()` |
| `MsSqlShardLockProvider` | `SELECT … WHERE expires_at > SYSDATETIMEOFFSET()` |
| `MySqlShardLockProvider` | `SELECT … WHERE expires_at > UTC_TIMESTAMP(6)` |

If you use a custom provider that does not implement `IShardLockQueryProvider`, the card for that worker still renders but displays a warning banner instead of lock data.

To add dashboard support to a custom provider:

```csharp
public sealed class MyProvider : IShardLockProvider, IShardLockQueryProvider
{
    public async Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default)
    {
        // return all non-expired rows from your backing store
    }

    // ... existing IShardLockProvider implementation
}
```

---

## Connection limits

Every concurrent `ExecuteAsync` call in your worker typically opens one database connection (e.g. one `DbContext`, one `SqlConnection`). The maximum number of simultaneous connections opened by a single instance is:

```
peak connections per instance = MaxShardsPerInstance × WorkerConcurrency
```

| Option | Default | Effect on connections |
|---|---|---|
| `MaxShardsPerInstance` | `int.MaxValue` | Each additional shard held = one more concurrent `ExecuteAsync` = one more connection |
| `WorkerConcurrency` | `1` | Each additional slot per shard runs another `ExecuteAsync` concurrently on the same shard |

### Sizing example

```
TotalShards = 1000
MaxShardsPerInstance = 10   ← each instance holds at most 10 shards
WorkerConcurrency = 1       ← one ExecuteAsync at a time per shard
                            ─────────────────────────────────────
peak connections            = 10 × 1 = 10 per instance
```

With 5 instances: `10 × 5 = 50` connections cluster-wide, regardless of `TotalShards`.

### Recommendations

**Always set `MaxShardsPerInstance`** rather than relying on the default. Leave headroom for other application components (EF migrations, health checks, observability pipelines) that also consume connections from the same pool.

**If you use `WorkerConcurrency > 1`**, scale down `MaxShardsPerInstance` proportionally to keep the product within budget:

```csharp
// Budget: 20 connections per instance
opts.MaxShardsPerInstance = 10;
opts.WorkerConcurrency = 2;   // 10 × 2 = 20
```

**Using `ReleaseOnCompletion = true`** (task-queue mode) reduces the _average_ connection count because shards cycle through the pool rather than being held continuously, but the _peak_ is still `MaxShardsPerInstance × WorkerConcurrency` during an acquire burst.

**Connection pool size** — Npgsql, `SqlClient`, and `MySqlConnector` all maintain a connection pool. If `peak connections > pool size`, callers will queue waiting for a free slot, which can surface as timeouts. Match `MaxShardsPerInstance × WorkerConcurrency` to your configured pool size.

---

## Worker patterns

The library handles shard distribution and locking. Retry strategies, dead-letter queues, and delayed dispatch are implemented inside `ExecuteAsync` — no library primitives are needed.

### Exponential backoff

Track consecutive failures in the worker and delay accordingly. The shard lock is held throughout, so other instances will not steal the shard while you are backing off:

```csharp
public class RetryWorker : IShardedWorker
{
    private readonly Dictionary<int, int> _failures = new();

    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        var consecutive = _failures.GetValueOrDefault(shard.Index);
        if (consecutive > 0)
        {
            var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, consecutive), 300));
            await Task.Delay(backoff, ct);
        }

        try
        {
            await DoWorkAsync(shard, ct);
            _failures[shard.Index] = 0; // reset on success
        }
        catch
        {
            _failures[shard.Index] = consecutive + 1;
            throw; // engine will log it; WorkerIntervalOnThrows adds further cool-down if set
        }
    }
}
```

For a simpler approach, set `WorkerIntervalOnThrows` to a longer delay — the engine applies it automatically after any exception:

```csharp
opts.WorkerInterval         = TimeSpan.FromSeconds(10);
opts.WorkerIntervalOnThrows = TimeSpan.FromMinutes(5);
```

### Dead-letter queue

The exact shape of a dead-letter implementation depends on your domain model — specifically, whether your rows track attempt counts, whether failures are stored in-band (a `status` column) or out-of-band (a separate table), and whether failed items should be requeued manually or archived. The pattern below assumes an `attempt_count` column on the work item and a separate dead-letter table, which is a common starting point:

```csharp
public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
{
    var items = await _repo.GetPendingAsync(shard.Index, shard.Total, ct);
    foreach (var item in items)
    {
        if (item.AttemptCount >= 5)
        {
            await _repo.MoveToDeadLetterAsync(item, ct);
            continue;
        }

        try   { await ProcessAsync(item, ct); }
        catch { await _repo.IncrementAttemptsAsync(item, ct); }
    }
}
```

Adapt this to your own model — for example, using a `status` column (`'pending'` → `'failed'` → `'dead'`) instead of a separate table, or storing the last exception message alongside the attempt count for observability.

### Delayed / scheduled dispatch

To process a row only after a specific time, add a `process_after` column and filter on it in your query:

```sql
SELECT * FROM jobs
WHERE status = 'pending'
  AND id % @totalShards = @shardIndex
  AND process_after <= NOW()
ORDER BY process_after
```

The shard poll cadence (`WorkerInterval`) becomes the scheduling resolution — a row with `process_after = NOW() + 30s` will be picked up within one `WorkerInterval` after its scheduled time.
