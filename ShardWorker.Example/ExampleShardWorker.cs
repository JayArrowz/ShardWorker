using Microsoft.EntityFrameworkCore;
using ShardWorker.Core.Interface;
using ShardWorker.Core.Model;

namespace ShardWorker.Example;

public class ExampleShardWorker : IShardedWorker
{
    private readonly IDbContextFactory<ExampleDbContext> _exampleDbContextFactory;
    private readonly ILogger<ExampleShardWorker> _logger;

    public ExampleShardWorker(IDbContextFactory<ExampleDbContext> exampleDbContext, ILogger<ExampleShardWorker> logger)
    {
        _exampleDbContextFactory = exampleDbContext;
        _logger = logger;
    }

    public async Task ExecuteAsync(ShardContext shard, CancellationToken ct)
    {
        using var exampleDbContext = await _exampleDbContextFactory.CreateDbContextAsync(ct);
        var toProcess = exampleDbContext.ProcessingItems.Where(i => i.Id % shard.Total == shard.Index);
        if(!await toProcess.AnyAsync(ct))
        {
            _logger.LogInformation("Shard {0} has no items to process.", shard);
            shard.RequestRelease();
            return;
        }

        var toProcessItems = await toProcess.ToArrayAsync();
        foreach (var item in toProcessItems)
        {
            //Simulate 300ms of work, then update the ProcessedCount for all items in this shard
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            item.ProcessedCount++;
            exampleDbContext.Update(item);
        }

        await exampleDbContext.SaveChangesAsync();
    }
}
