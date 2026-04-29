using Microsoft.EntityFrameworkCore;
using ShardWorker;
using ShardWorker.Dashboard;
using ShardWorker.Example;
using ShardWorker.Example.Model;
using ShardWorker.Observability;
using ShardWorker.Providers.Postgres;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddLogging();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;

Action<DbContextOptionsBuilder> dbContextOptions = options => options.UseNpgsql(connectionString);
builder.Services
    .AddDbContextFactory<ExampleDbContext>(dbContextOptions);

//If you require DbContext registration too
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ExampleDbContext>>().CreateDbContext());

builder.Services
    .AddShardWorkerMetrics()
    .AddShardEngine<ExampleShardWorker>(opts =>
    {
        opts.TotalShards = 1000;
        opts.MaxShardsPerInstance = 30;
        opts.LockExpiry = TimeSpan.FromMinutes(2);
        opts.HeartbeatInterval = TimeSpan.FromSeconds(30);
        opts.AcquireInterval = TimeSpan.FromSeconds(15);
        opts.WorkerInterval = TimeSpan.FromSeconds(30);
        opts.ShutdownTimeout = TimeSpan.FromSeconds(30);
        opts.ReleaseOnCompletion = true;
        opts.ReleaseOnThrows = true;
    }, new PostgresShardLockProvider(connectionString, "_example_locks"))
    .AddShardWorkerDashboard();

var app = builder.Build();

using var scope = app.Services.CreateScope();
using var dbContext = scope.ServiceProvider.GetRequiredService<ExampleDbContext>();
if (!await dbContext.ProcessingItems.AnyAsync())
{
    await AddExampleWork(dbContext);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.MapShardWorkerDashboard();
app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

async Task AddExampleWork(ExampleDbContext dbContext)
{
    dbContext.AddRange(Enumerable.Range(1, 100000).Select(i => new ProcessingItem()));
    await dbContext.SaveChangesAsync();
}