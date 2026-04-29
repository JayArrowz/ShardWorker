using ShardWorker.Core.Model;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker.Core.Interface;

/// <summary>
/// Optional extension to <see cref="IShardLockProvider"/> that exposes a read-only view of
/// all current lock rows. Implement this on a provider to enable dashboard and monitoring features.
/// </summary>
public interface IShardLockQueryProvider
{
    /// <summary>
    /// Returns all non-expired lock rows currently held in the underlying store.
    /// </summary>
    Task<IReadOnlyList<ShardLockRow>> GetAllLocksAsync(CancellationToken ct = default);
}
