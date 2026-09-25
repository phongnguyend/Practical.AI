using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Persistence.Repositories;

namespace SharePointToAzureSearch.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Binds the SQL Server settings, registers the Entity Framework Core context, and maps every
    /// repository contract onto its implementation — once per application however many features ask for
    /// it, so the layer's wiring lives in one place rather than being spread across whichever feature
    /// group happens to need a given table.
    /// <para>
    /// The context is pooled and handed out by an <see cref="IDbContextFactory{TContext}"/>, because the
    /// repositories are singletons shared by the worker's hosted services, which have no request scope of
    /// their own. A scoped <see cref="SharePointIndexDbContext"/> is registered alongside it so an API
    /// endpoint can inject the context directly and query with LINQ where a repository method would be
    /// more than it needs; it comes from the same pool and returns to it with the request.
    /// </para>
    /// <para>
    /// Each repository is a singleton over that factory and opens a context per call, so registering them
    /// all together costs nothing: one an application never resolves is never constructed.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<SharePointIndexDbContext>)))
        {
            return services;
        }

        services.AddOptions<SqlServerOptions>().Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddPooledDbContextFactory<SharePointIndexDbContext>((sp, builder) =>
        {
            var options = sp.GetRequiredService<IOptions<SqlServerOptions>>().Value;
            builder.UseSqlServer(options.ConnectionString, sql => sql.CommandTimeout(options.CommandTimeoutSeconds));
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<SharePointIndexDbContext>>().CreateDbContext());

        services.AddSingleton<IAgentRepository, AgentRepository>();
        services.AddSingleton<IChatRepository, ChatRepository>();
        services.AddSingleton<IDeltaStateRepository, DeltaStateRepository>();
        services.AddSingleton<IFileMetadataRepository, FileMetadataRepository>();
        services.AddSingleton<IFoundrySessionRepository, FoundrySessionRepository>();
        services.AddSingleton<IIndexStateRepository, IndexStateRepository>();
        services.AddSingleton<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();

        if (configuration.GetValue($"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.AutoMigrate)}", true))
        {
            services.AddHostedService<DatabaseMigrationHostedService>();
        }

        return services;
    }
}
