using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

var builder = new ConfigurationBuilder().AddUserSecrets<Program>();
var configuration = builder.Build();

using var dbContext = new TestDbContext("Host=127.0.0.1;Database=VectorDb;Username=postgres;Password=postgres");
await dbContext.Database.EnsureDeletedAsync();
await dbContext.Database.EnsureCreatedAsync();

var blogs = new[] {
    new Blog
    {
        Description = "This is a blog about AI and machine learning.",
        Embedding = new Vector(new float[]{0.1f, 0.2f, 0.3f })
    },
    new Blog
    {
        Description = "This is a blog about animals and plants.",
        Embedding = new Vector(new float[]{99.1f, 50f, 3f}),
    },
    new Blog
    {
        Description = "This is a blog about sports and outdoor activities.",
        Embedding = new Vector(new float[]{3f, 60f, 240f}),
    }
};

dbContext.Blogs.AddRange(blogs);
await dbContext.SaveChangesAsync();

var queryEmbedding = new Vector(new float[] { 0.1f, 0.2f, 0.3f });

var query = dbContext.Blogs
    .OrderBy(p => p.Embedding!.CosineDistance(queryEmbedding))
    .Take(5);

//var query = dbContext.Blogs
//    .FromSqlInterpolated($"SELECT b.\"Id\", b.\"Description\", b.\"Embedding\" FROM \"Blogs\" AS b ORDER BY b.\"Embedding\" <=> {JsonSerializer.Serialize(queryEmbedding.Memory)}::vector LIMIT 5");

foreach (var result in await query.ToArrayAsync())
{
    Console.WriteLine($"Similar blog ID: {result.Id}, Description: {result.Description}");
}

public class Blog
{
    public int Id { get; set; }

    public required string Description { get; set; }

    public Vector? Embedding { get; set; }
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
        optionsBuilder.UseNpgsql(_connectionString);
        optionsBuilder.UseNpgsql(_connectionString, o => o.UseVector());

        //optionsBuilder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
        //    .EnableSensitiveDataLogging()
        //    .EnableDetailedErrors();

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.Entity<Blog>().Property(p => p.Embedding).HasColumnType("vector(3)");
    }
}