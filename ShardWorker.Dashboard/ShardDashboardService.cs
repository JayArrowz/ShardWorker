using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

namespace ShardWorker.Dashboard;

/// <summary>Snapshot of all worker states at a point in time.</summary>
public sealed record DashboardSnapshot(DateTimeOffset Timestamp, IReadOnlyList<WorkerSnapshot> Workers)
{
    public static DashboardSnapshot Empty => new(DateTimeOffset.UtcNow, Array.Empty<WorkerSnapshot>());
}

/// <summary>State of a single worker type including all held locks.</summary>
public sealed record WorkerSnapshot(
    string WorkerName,
    int TotalShards,
    IReadOnlyList<ShardLockRow> HeldLocks,
    bool SupportsQuery)
{
    public int HeldCount => HeldLocks.Count;
    public int FreeCount => TotalShards - HeldCount;

    // Lazy dictionary for O(1) per-shard lookup (important when rendering dot grids)
    private Dictionary<int, ShardLockRow>? _byIndex;
    public ShardLockRow? GetLock(int shardIndex) =>
        (_byIndex ??= HeldLocks.ToDictionary(l => l.ShardIndex))
            .TryGetValue(shardIndex, out var row) ? row : null;
}

/// <summary>
/// Aggregates live shard state from all registered <see cref="WorkerRegistration"/> instances
/// by querying each lock provider that implements <see cref="IShardLockQueryProvider"/>.
/// </summary>
public sealed class ShardDashboardService
{
    private readonly IEnumerable<WorkerRegistration> _registrations;

    public ShardDashboardService(IEnumerable<WorkerRegistration> registrations)
        => _registrations = registrations;

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var workers = new List<WorkerSnapshot>();
        foreach (var reg in _registrations)
        {
            if (reg.LockProvider is IShardLockQueryProvider queryable)
            {
                var locks = await queryable.GetAllLocksAsync(ct);
                workers.Add(new WorkerSnapshot(reg.WorkerName, reg.TotalShards, locks, SupportsQuery: true));
            }
            else
            {
                workers.Add(new WorkerSnapshot(reg.WorkerName, reg.TotalShards, Array.Empty<ShardLockRow>(), SupportsQuery: false));
            }
        }
        return new DashboardSnapshot(DateTimeOffset.UtcNow, workers);
    }
}
