using Microsoft.EntityFrameworkCore;
using ShardWorker.Example.Model;

namespace ShardWorker.Example;

public class ExampleDbContext : DbContext
{
    public ExampleDbContext(DbContextOptions<ExampleDbContext> options) : base(options)
    {
    }

    public DbSet<ProcessingItem> ProcessingItems { get; set; }
}
