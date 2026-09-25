using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Application;

namespace SharePointToAzureSearch.Persistence;

public static class DependencyInjection
{
    /// <summary>
    /// Binds the SQL Server settings and registers the Entity Framework Core context, once per
    /// application however many features ask for it.
    /// <para>
    /// The context is pooled and handed out by an <see cref="IDbContextFactory{TContext}"/>, because the
    /// stores that use it are singletons shared by the worker's hosted services, which have no request
    /// scope of their own. A scoped <see cref="SharePointIndexDbContext"/> is registered alongside it so
    /// an API endpoint can inject the context directly and query with LINQ where a store method would be
    /// more than it needs; it comes from the same pool and returns to it with the request.
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
        services.AddSingleton<IWebhookSubscriptionStore, EfWebhookSubscriptionStore>();

        if (configuration.GetValue($"{SqlServerOptions.SectionName}:{nameof(SqlServerOptions.AutoMigrate)}", true))
        {
            services.AddHostedService<DatabaseMigrationHostedService>();
        }

        return services;
    }
}
