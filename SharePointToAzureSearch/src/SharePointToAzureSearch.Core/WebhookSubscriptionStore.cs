using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

public sealed record WebhookSubscriptionDefinition(
    Guid Id,
    string? GraphSubscriptionId,
    string Name,
    string NotificationUrl,
    int LifetimeDays,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IWebhookSubscriptionStore
{
    Task<IReadOnlyList<WebhookSubscriptionDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition> CreateAsync(
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task<WebhookSubscriptionDefinition?> UpdateAsync(
        Guid id,
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        int lifetimeDays,
        CancellationToken cancellationToken);
    Task DeleteAsync(string graphSubscriptionId, CancellationToken cancellationToken);
}

public sealed class EfWebhookSubscriptionStore(
    IDbContextFactory<SharePointIndexDbContext> contextFactory) : IWebhookSubscriptionStore
{
    public async Task<IReadOnlyList<WebhookSubscriptionDefinition>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.WebhookSubscriptions
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new WebhookSubscriptionDefinition(
                item.Id,
                item.GraphSubscriptionId,
                item.Name,
                item.NotificationUrl,
                item.LifetimeDays,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<WebhookSubscriptionDefinition?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.WebhookSubscriptions
            .AsNoTracking()
            .Where(item => item.Name == name)
            .Select(item => new WebhookSubscriptionDefinition(
                item.Id,
                item.GraphSubscriptionId,
                item.Name,
                item.NotificationUrl,
                item.LifetimeDays,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<WebhookSubscriptionDefinition> CreateAsync(
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        int lifetimeDays,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var entity = new WebhookSubscriptionEntity
        {
            GraphSubscriptionId = graphSubscriptionId,
            Name = name,
            NotificationUrl = notificationUrl,
            LifetimeDays = Math.Clamp(lifetimeDays, 1, 29),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        context.WebhookSubscriptions.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<WebhookSubscriptionDefinition?> UpdateAsync(
        Guid id,
        string graphSubscriptionId,
        string name,
        string notificationUrl,
        int lifetimeDays,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.WebhookSubscriptions.FirstOrDefaultAsync(
            item => item.Id == id,
            cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.GraphSubscriptionId = graphSubscriptionId;
        entity.Name = name;
        entity.NotificationUrl = notificationUrl;
        entity.LifetimeDays = Math.Clamp(lifetimeDays, 1, 29);
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task DeleteAsync(string graphSubscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.WebhookSubscriptions.FirstOrDefaultAsync(
            item => item.GraphSubscriptionId == graphSubscriptionId,
            cancellationToken);
        if (entity is null)
        {
            return;
        }

        context.WebhookSubscriptions.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static WebhookSubscriptionDefinition ToRecord(WebhookSubscriptionEntity item) => new(
        item.Id,
        item.GraphSubscriptionId,
        item.Name,
        item.NotificationUrl,
        item.LifetimeDays,
        item.CreatedAtUtc,
        item.UpdatedAtUtc);
}
