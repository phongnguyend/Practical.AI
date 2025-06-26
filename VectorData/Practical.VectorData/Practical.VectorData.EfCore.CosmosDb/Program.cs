using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Cosmos.Extensions;
using Microsoft.Extensions.Configuration;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();

using var dbContext = new TestDbContext(configuration["ConnectionStrings:CosmosDb"]);
await dbContext.Database.EnsureDeletedAsync();
await dbContext.Database.EnsureCreatedAsync();

var blogs = new[] {
    new Blog
    {
        Id = 1,
        Description = "This is a blog about AI and machine learning.",
        Embedding = [0.1f, 0.2f, 0.3f]
    },
    new Blog
    {
        Id = 2,
        Description = "This is a blog about animals and plants.",
        Embedding = [99.1f, 50f, 3f],
    },
    new Blog
    {
        Id = 3,
        Description = "This is a blog about sports and outdoor activities.",
        Embedding = [3f, 60f, 240f],
    }
};

dbContext.Blogs.AddRange(blogs);
await dbContext.SaveChangesAsync();

var queryEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

#pragma warning disable EF9103 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var query = dbContext.Blogs
    .OrderBy(p => EF.Functions.VectorDistance(p.Embedding!, queryEmbedding))
    .Take(5);
#pragma warning restore EF9103 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

foreach (var result in await query.ToArrayAsync())
{
    Console.WriteLine($"Similar blog ID: {result.Id}, Description: {result.Description}");
}

public class Blog
{
    public int Id { get; set; }

    public required string Description { get; set; }

    public float[] Embedding { get; set; }
}

public class TestDbContext : DbContext
{
    private readonly string _connectionString;

    public DbSet<Blog> Blogs { get; set; } = null!;

    public TestDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseCosmos(_connectionString, "cosmosdb");

        //optionsBuilder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
        //    .EnableSensitiveDataLogging()
        //    .EnableDetailedErrors();

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
#pragma warning disable EF9103 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        modelBuilder.Entity<Blog>().Property(p => p.Embedding).IsVector(DistanceFunction.Cosine, dimensions: 3);
#pragma warning restore EF9103 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    }
}