# ShardWorker

A lightweight .NET library for running distributed, sharded background workers coordinated by database locks. Multiple instances of your application compete for ownership of numbered shards; each shard is processed by exactly one instance at a time.

## How it works

The total workload is divided into **N shards** (numbered `0` to `N-1`). Every running instance continuously tries to acquire shard locks via the configured `IShardLockProvider`. Once a shard is acquired, a worker loop is started for it. A heartbeat loop renews held locks in the background. If a lock renewal fails (e.g. the instance was too slow), the worker for that shard is stopped and the shard becomes available for another instance to claim.

```
Instance A                Instance B
│                         │
├─ Acquire shard 0 ✓      ├─ Acquire shard 0 ✗ (held by A)
├─ Acquire shard 1 ✓      ├─ Acquire shard 1 ✗ (held by A)
├─ Acquire shard 2 ✗      ├─ Acquire shard 2 ✓
│  ...                    │  ...
│                         │
├─ Heartbeat (renew 0,1)  ├─ Heartbeat (renew 2)
│                         │
└─ Graceful stop:         └─ Graceful stop:
   release 0, 1              release 2
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

### Instance count

Each instance independently competes for unclaimed shards. Adding instances automatically redistributes ownership — no coordination layer is needed. Instances that die release their shards via lock expiry; instances that restart reclaim shards on the next acquire cycle.

### Multiple worker types

Each `AddShardEngine<TWorker>` registration is fully independent: its own `BackgroundService`, its own options, and its own DB table. Ten worker types with 1,000 shards each still produces only 1 acquire round-trip and 1 heartbeat round-trip per worker type per interval.

### Tuning guidelines

- **More shards → finer distribution** across instances. Use more shards when you have many instances and want even load balancing.
- **Lower `AcquireInterval`** → faster rebalancing after an instance dies or joins. The cost is still one DB round-trip per interval.
- **Lower `HeartbeatInterval`** → locks renew more frequently, reducing the window where a slow instance's shards expire and get stolen. Must stay below `LockExpiry`.
- **`LockExpiry`** is the maximum time a shard is unavailable after its owning instance crashes. Set it to the longest acceptable gap in processing.
- **`MaxShardsPerInstance`** caps how many shards one instance holds. Use it when you want to guarantee headroom for other instances (e.g. `TotalShards=30`, `MaxShardsPerInstance=10` → at least 3 instances needed to cover all shards).
- **`ReleaseOnCompletion`** turns the library into a task-queue style dispatcher — see below.
- **`ReleaseOnThrows`** releases a shard after an unhandled exception, letting another instance retry rather than the same instance looping.
- **`WorkerConcurrency`** runs N parallel execution slots per shard. Useful for I/O-bound workers where a single shard produces enough parallelisable work for multiple concurrent calls — see below.

---

## Processing modes

### Persistent partition (default)

Each instance holds its shards indefinitely. `ExecuteAsync` is called in a tight loop on the same shard:

```
Acquire shard 4
  → ExecuteAsync(shard 4) → wait WorkerInterval
  → ExecuteAsync(shard 4) → wait WorkerInterval
  → ... forever
```

Best for continuous polling workloads (e.g. consuming a per-shard queue or scanning a database partition on a schedule).

### Task-queue mode (`ReleaseOnCompletion = true`)

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

1. Instance A claims shards 0–9, Instance B claims shards 10–19. Shards 20–29 are unclaimed.
2. Instance A finishes shard 3 → shard 3 released immediately.
3. Instance A is now at 9 held → next acquire cycle picks up shard 20.
4. Over time all 30 shards are processed continuously across both instances.

> If `ExecuteAsync` **throws**, the shard is **not** released — it stays held and retries on the next loop iteration. Only a clean return triggers the release.

> **`MaxShardsPerInstance` is strongly recommended with `ReleaseOnCompletion`.** Without it, an instance has unlimited capacity and will re-acquire every shard it just released on the very next acquire cycle — starving other instances. Setting `MaxShardsPerInstance` to `TotalShards / expectedInstances` ensures released shards stay unclaimed long enough for other instances to compete for them.

### Error retry mode (`ReleaseOnThrows = true`)

By default, if `ExecuteAsync` throws, the error is logged and the shard is retried after `WorkerInterval`. With `ReleaseOnThrows = true`, the shard is released immediately so **any** instance (not necessarily this one) can attempt it on the next acquire cycle:

```csharp
opts.ReleaseOnThrows = true;
```

Combine with `ReleaseOnCompletion` to get a pure task-queue where every execution — successful or not — returns the shard to the pool.

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

`ReleaseOnCompletion` and `ReleaseOnThrows` interact with concurrency as follows:
- When any slot calls `cts.Cancel()` (via `ReleaseOnCompletion` or `ReleaseOnThrows`), all other slots for that shard observe the cancellation and exit cleanly.
- The shard is released once **all** slots have exited (the `finally` block in `StartWorker` fires after `Task.WhenAll`).

---

## Projects

| Project | Target | Description |
|---|---|---|
| `ShardWorker.Core` | `netstandard2.0` | Interfaces and models — no dependencies |
| `ShardWorker` | `netstandard2.0` | Engine and DI registration |
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
| `WorkerConcurrency` | `1` | Number of concurrent `ExecuteAsync` calls per shard. Each slot runs its own independent loop |

> **Rule:** `HeartbeatInterval` must be less than `LockExpiry`. The engine throws at startup if this is violated.

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
