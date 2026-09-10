using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SharePointToAzureSearch.Core.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without starting an application host, so migrations can be
/// added from the Core project alone. Only the provider matters when scaffolding a migration; the
/// connection string is used by commands that reach the database, such as <c>dotnet ef database update</c>,
/// and comes from <c>SqlServer__ConnectionString</c> when it is set.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SharePointIndexDbContext>
{
    private const string DefaultConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=SharePointSearch;Integrated Security=true;TrustServerCertificate=true";

    public SharePointIndexDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SqlServer__ConnectionString");
        var builder = new DbContextOptionsBuilder<SharePointIndexDbContext>()
            .UseSqlServer(string.IsNullOrWhiteSpace(connectionString) ? DefaultConnectionString : connectionString);
        return new SharePointIndexDbContext(builder.Options);
    }
}
