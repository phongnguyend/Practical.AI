using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();

using var dbContext = new TestDbContext(configuration["ConnectionStrings:CosmosDb"]);
await dbContext.Database.EnsureDeletedAsync();
await dbContext.Database.EnsureCreatedAsync();

var blogTemplates = new[]
{
    new { Description = "This is a blog about AI and machine learning.",      Embedding = new float[] { 0.1f, 0.2f, 0.3f } },
    new { Description = "This is a blog about animals and plants.",           Embedding = new float[] { 99.1f, 50f, 3f } },
    new { Description = "This is a blog about sports and outdoor activities.", Embedding = new float[] { 3f, 60f, 240f } },
};

var tenants = new[] { "tenant-1", "tenant-2", "tenant-3" };
var blogs = tenants
    .SelectMany((tenantId, ti) => blogTemplates.Select((t, bi) => new Blog
    {
        Id = ti * blogTemplates.Length + bi + 1,
        TenantId = tenantId,
        Description = t.Description,
        Embedding = t.Embedding
    }))
    .ToArray();

dbContext.Blogs.AddRange(blogs);
await dbContext.SaveChangesAsync();

var queryEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

var query = dbContext.Blogs
    .Where(p => p.TenantId == "tenant-1")
    .OrderBy(p => EF.Functions.VectorDistance(p.Embedding!, queryEmbedding))
    .Take(5);

foreach (var result in await query.ToArrayAsync())
{
    Console.WriteLine($"Similar blog ID: {result.Id}, Description: {result.Description}");
}

public class Blog
{
    public int Id { get; set; }

    public required string TenantId { get; set; }

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
        modelBuilder.Entity<Blog>().HasPartitionKey(p => p.TenantId);
        modelBuilder.Entity<Blog>().Property(p => p.Embedding).IsVectorProperty(DistanceFunction.Cosine, dimensions: 3);
    }
}