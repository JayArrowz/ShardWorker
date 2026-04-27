using ShardWorker.Core.Model;
using System.Threading;
using System.Threading.Tasks;

namespace ShardWorker.Core.Interface;

public interface IShardedWorker
{
    Task ExecuteAsync(ShardContext shard, CancellationToken ct);
}
