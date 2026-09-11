using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

public static class WebhookSubscriptionDefaults
{
    public const string Name = "Default";
}

/// <summary>
/// Serialized by name rather than by ordinal, so a client reads "ExpiringSoon" and adding a status
/// later cannot silently change what an existing one means.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubscriptionStatus>))]
public enum SubscriptionStatus
{
    Missing,
    Active,
    ExpiringSoon,
    Expired
}

public enum SubscriptionAction
{
    Created,
    Renewed,
    Unchanged
}

/// <summary>
/// One consolidated subscription row. A tracked database record can have a null <see cref="Id"/> and
/// expiration when its Microsoft Graph subscription is missing. <see cref="ClientStateMatches"/> is
/// reported rather than the client state itself because that shared secret must not leave the server.
/// </summary>
public sealed record SubscriptionView(
    string? Id,
    Guid? DatabaseId,
    string Name,
    string Resource,
    string NotificationUrl,
    DateTimeOffset? ExpirationUtc,
    bool ClientStateMatches,
    bool ResourceMatches,
    bool NotificationUrlMatches,
    bool IsTracked,
    bool IsManaged,
    bool IsDefault,
    SubscriptionStatus Status);

/// <summary>Thrown when a notification URL is already taken by another subscription.</summary>
public sealed class DuplicateNotificationUrlException(string url)
    : Exception($"A subscription for '{url}' already exists. Each subscription needs its own notification URL.");

public sealed class DuplicateSubscriptionNameException(string name)
    : Exception($"A subscription named '{name}' already exists.");

/// <summary>
/// Thrown for an operation the default subscription does not allow. The default is the one on the
/// configured <c>SharePoint:NotificationUrl</c>: the worker recreates and renews it, so removing it or
/// pointing it elsewhere from here would only be undone on the next renewal pass.
/// </summary>
public sealed class ProtectedSubscriptionException(string message) : Exception(message);

/// <summary>
/// Every subscription on the tenant's application registration, together with the configuration the
/// worker would create one from, so a viewer can tell a subscription this deployment owns from one
/// another deployment left behind.
/// </summary>
public sealed record SubscriptionOverview(
    string ExpectedResource,
    string ExpectedNotificationUrl,
    bool RenewalEnabled,
    int LifetimeDays,
    int RenewalCheckHours,
    int RenewalThresholdDays,
    IReadOnlyList<SubscriptionView> Items);

public sealed record EnsureSubscriptionResult(SubscriptionAction Action, SubscriptionView Subscription);

/// <summary>
/// The outcome of an edit. <see cref="Replaced"/> says the notification URL changed, which Microsoft
/// Graph can only express as a new subscription, so the ID in <see cref="Subscription"/> is a new one.
/// </summary>
public sealed record UpdateSubscriptionResult(SubscriptionView Subscription, bool Replaced, string? Warning);

/// <summary>
/// Creating, renewing, and removing the Microsoft Graph webhook subscription, and deciding which of the
/// tenant's subscriptions this deployment owns. The renewal background service and the management API
/// both go through here so that they cannot disagree about which subscription is "ours".
/// </summary>
public sealed class SubscriptionManager(
    SharePointClient sharePointClient,
    IWebhookSubscriptionStore subscriptionStore,
    IOptions<SharePointOptions> options)
{
    /// <summary>How close to expiry a subscription has to be before a renewal pass extends it.</summary>
    public const int RenewalThresholdDays = 3;

    private readonly SharePointOptions _options = options.Value;

    public async Task<SubscriptionOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var definitions = await subscriptionStore.ListAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var graphRecords = subscriptions
            .Select(subscription => new
            {
                Subscription = subscription,
                Definition = ResolveDefinition(definitions, subscription, resource, definition)
            })
            .ToList();
        var matchedDefinitionIds = graphRecords
            .Where(record => record.Definition is not null)
            .Select(record => record.Definition!.Id)
            .ToHashSet();
        var items = graphRecords
            .Select(record => ToView(record.Subscription, resource, definition, record.Definition))
            .Concat(definitions
                .Where(item => !matchedDefinitionIds.Contains(item.Id))
                .Select(item => ToMissingTrackedView(item, resource)))
            .ToList();

        return new SubscriptionOverview(
            resource,
            definition.NotificationUrl,
            _options.SubscriptionRenewalEnabled,
            definition.LifetimeDays,
            _options.RenewalCheckHours,
            RenewalThresholdDays,
            [.. items
                .OrderByDescending(x => x.IsDefault)
                .ThenByDescending(x => x.IsTracked)
                .ThenByDescending(x => x.IsManaged)
                .ThenBy(x => x.ExpirationUtc)]);
    }

    /// <summary>
    /// Creates a subscription over the configured resource. Graph allows more than one subscription over
    /// the same resource, so this does not replace an existing one — the caller decides, and
    /// <see cref="EnsureAsync"/> is the call that avoids duplicates.
    /// <para>
    /// A <paramref name="notificationUrl"/> other than the configured one produces a subscription this
    /// deployment does not consider its own, so the renewal service will neither renew it nor count it
    /// when deciding whether to create another. That is deliberate: it is how a subscription is pointed
    /// at a tunnel or a replacement host without the worker fighting the change.
    /// </para>
    /// </summary>
    public async Task<SubscriptionView> CreateAsync(
        string? name,
        int? days,
        string? notificationUrl,
        CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? "Additional" : name.Trim();
        var url = notificationUrl ?? definition.NotificationUrl;
        var lifetimeDays = Math.Clamp(days ?? definition.LifetimeDays, 1, 29);

        await ThrowIfNameTakenAsync(normalizedName, exceptGraphSubscriptionId: null, cancellationToken);

        var existing = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        ThrowIfUrlTaken(existing, url, exceptId: null);

        var created = await sharePointClient.CreateSubscriptionAsync(
            ExpirationFor(lifetimeDays, definition.LifetimeDays),
            url,
            SubscriptionClientState.Create(normalizedName, _options.ClientState),
            cancellationToken);
        try
        {
            var saved = await subscriptionStore.CreateAsync(
                created.Id, normalizedName, url, lifetimeDays, cancellationToken);
            return ToView(created, resource, definition, saved);
        }
        catch
        {
            await TryDeleteCreatedSubscriptionAsync(created.Id, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Changes a subscription's lifetime and, if asked, its notification URL.
    /// <para>
    /// Microsoft Graph only lets a PATCH change the expiry, so a new URL is applied by creating the
    /// replacement first and removing the old subscription only once that has succeeded. A failure
    /// therefore leaves the original in place rather than leaving the drive with no subscription at
    /// all — at the cost of the ID changing when a URL does.
    /// </para>
    /// </summary>
    public async Task<UpdateSubscriptionResult> UpdateAsync(
        string id,
        string? name,
        int? days,
        string? notificationUrl,
        CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var definitions = await subscriptionStore.ListAsync(cancellationToken);
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var databaseDefinition = Guid.TryParse(id, out var databaseId)
            ? definitions.FirstOrDefault(item => item.Id == databaseId)
            : null;
        var graphSubscriptionId = databaseDefinition?.GraphSubscriptionId ?? id;
        var current = all.FirstOrDefault(x => x.Id == graphSubscriptionId);

        if (current is null && databaseDefinition is not null)
        {
            var recordName = string.IsNullOrWhiteSpace(name) ? databaseDefinition.Name : name.Trim();
            var isDefault = string.Equals(
                databaseDefinition.Name,
                WebhookSubscriptionDefaults.Name,
                StringComparison.Ordinal);
            if (isDefault &&
                !string.Equals(recordName, WebhookSubscriptionDefaults.Name, StringComparison.Ordinal))
            {
                throw new ProtectedSubscriptionException("The default subscription's name cannot be changed.");
            }

            if (!isDefault &&
                string.Equals(recordName, WebhookSubscriptionDefaults.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProtectedSubscriptionException("'Default' is reserved for the auto-renewed subscription.");
            }

            if (definitions.Any(item =>
                    item.Id != databaseDefinition.Id &&
                    string.Equals(item.Name, recordName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicateSubscriptionNameException(recordName);
            }

            var recordUrl = string.IsNullOrWhiteSpace(notificationUrl)
                ? databaseDefinition.NotificationUrl
                : notificationUrl.Trim();
            var recordLifetimeDays = Math.Clamp(days ?? databaseDefinition.LifetimeDays, 1, 29);
            ThrowIfUrlTaken(all, recordUrl, exceptId: databaseDefinition.GraphSubscriptionId);

            var createdForRecord = await sharePointClient.CreateSubscriptionAsync(
                ExpirationFor(recordLifetimeDays, databaseDefinition.LifetimeDays),
                recordUrl,
                SubscriptionClientState.Create(recordName, _options.ClientState),
                cancellationToken);
            try
            {
                var savedDefinition = await subscriptionStore.UpdateAsync(
                    databaseDefinition.Id,
                    createdForRecord.Id,
                    recordName,
                    recordUrl,
                    recordLifetimeDays,
                    cancellationToken) ?? throw new InvalidOperationException(
                        "The webhook subscription record is unavailable.");
                if (isDefault)
                {
                    definition = savedDefinition;
                }

                return new UpdateSubscriptionResult(
                    ToView(createdForRecord, resource, definition, savedDefinition),
                    Replaced: false,
                    Warning: null);
            }
            catch
            {
                await TryDeleteCreatedSubscriptionAsync(createdForRecord.Id, cancellationToken);
                throw;
            }
        }

        if (current is null)
        {
            throw new KeyNotFoundException($"No subscription with ID '{id}'.");
        }
        var currentDefinition = databaseDefinition ??
            FindDefinition(definitions, current.Id) ??
            (IsDefault(current, resource, definition) ? definition : null);
        if (currentDefinition is null)
        {
            throw new ProtectedSubscriptionException(
                "Untracked subscriptions cannot be updated. Delete and recreate the subscription to track it.");
        }

        var currentView = ToView(current, resource, definition, currentDefinition);

        var normalizedName = string.IsNullOrWhiteSpace(name) ? currentView.Name : name.Trim();
        var url = string.IsNullOrWhiteSpace(notificationUrl) ? current.NotificationUrl : notificationUrl.Trim();
        var lifetimeDays = Math.Clamp(days ?? definition.LifetimeDays, 1, 29);
        var nameChanged = !string.Equals(normalizedName, currentView.Name, StringComparison.Ordinal);
        var urlChanged = !string.Equals(url, current.NotificationUrl, StringComparison.OrdinalIgnoreCase);

        if (currentView.IsDefault &&
            !string.Equals(normalizedName, WebhookSubscriptionDefaults.Name, StringComparison.Ordinal))
        {
            throw new ProtectedSubscriptionException("The default subscription's name cannot be changed.");
        }

        if (!currentView.IsDefault &&
            string.Equals(normalizedName, WebhookSubscriptionDefaults.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtectedSubscriptionException("'Default' is reserved for the auto-renewed subscription.");
        }

        await ThrowIfNameTakenAsync(normalizedName, current.Id, cancellationToken);

        if (!nameChanged && !urlChanged)
        {
            currentDefinition = await subscriptionStore.UpdateAsync(
                currentDefinition.Id,
                current.Id,
                normalizedName,
                url,
                lifetimeDays,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The webhook subscription record is unavailable.");
            if (currentView.IsDefault)
            {
                definition = currentDefinition;
            }

            return new UpdateSubscriptionResult(
                await RenewAsync(current.Id, lifetimeDays, cancellationToken),
                Replaced: false,
                Warning: null);
        }

        ThrowIfUrlTaken(all, url, exceptId: current.Id);

        var created = await sharePointClient.CreateSubscriptionAsync(
            ExpirationFor(lifetimeDays, definition.LifetimeDays),
            url,
            SubscriptionClientState.Create(normalizedName, _options.ClientState),
            cancellationToken);

        currentDefinition = await subscriptionStore.UpdateAsync(
            currentDefinition.Id,
            created.Id,
            normalizedName,
            url,
            lifetimeDays,
            cancellationToken) ?? throw new InvalidOperationException(
                "The webhook subscription record is unavailable.");
        if (currentView.IsDefault)
        {
            definition = currentDefinition;
        }

        string? warning = null;
        try
        {
            await sharePointClient.DeleteSubscriptionAsync(current.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            warning = $"The replacement was created, but removing the previous subscription ({current.Id}) failed: {ex.Message}. Delete it by hand.";
        }

        return new UpdateSubscriptionResult(
            ToView(created, resource, definition, currentDefinition), Replaced: true, warning);
    }

    /// <summary>
    /// Whether a notification URL is one Microsoft Graph will accept: absolute, and HTTPS, which Graph
    /// requires so that it can validate the endpoint and deliver notifications over TLS.
    /// </summary>
    public static bool IsValidNotificationUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public bool IsValidName(string name) =>
        SubscriptionClientState.TryCreate(name, _options.ClientState, out _);

    public async Task<SubscriptionView> RenewAsync(string id, int? days, CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var expiration = ExpirationFor(days, definition.LifetimeDays);
        await sharePointClient.RenewSubscriptionAsync(id, expiration, cancellationToken);

        // Graph's PATCH response is not returned by the client, so the refreshed list is what confirms
        // the new expiry rather than the value that was requested.
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var definitions = await subscriptionStore.ListAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var renewed = subscriptions.FirstOrDefault(x => x.Id == id)
            ?? new GraphSubscription(id, resource, definition.NotificationUrl, expiration, null);
        return ToView(renewed, resource, definition, FindDefinition(definitions, id));
    }

    /// <summary>
    /// Removes a subscription. The default one — the subscription on the configured notification URL —
    /// is refused: the renewal service recreates it, so deleting it here would achieve nothing beyond a
    /// gap in notifications until the next pass.
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var definitions = await subscriptionStore.ListAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var current = all.FirstOrDefault(x => x.Id == id);
        if (current is not null && IsDefault(current, resource, definition, FindDefinition(definitions, id)))
        {
            throw new ProtectedSubscriptionException(
                "The default subscription cannot be deleted while automatic renewal manages it.");
        }

        await sharePointClient.DeleteSubscriptionAsync(id, cancellationToken);
        await subscriptionStore.DeleteAsync(id, cancellationToken);
    }

    private void ThrowIfUrlTaken(IEnumerable<GraphSubscription> subscriptions, string url, string? exceptId)
    {
        if (subscriptions.Any(x =>
                x.Id != exceptId &&
                string.Equals(x.NotificationUrl, url, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicateNotificationUrlException(url);
        }
    }

    /// <summary>
    /// Makes sure exactly one subscription this deployment owns exists and is not about to expire. This
    /// is what the renewal background service runs on its interval.
    /// </summary>
    public async Task<EnsureSubscriptionResult> EnsureAsync(CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var existing = subscriptions.FirstOrDefault(x =>
            IsDefault(
                x,
                resource,
                definition,
                string.Equals(x.Id, definition.GraphSubscriptionId, StringComparison.Ordinal)
                    ? definition
                    : null));

        if (existing is null)
        {
            var created = await sharePointClient.CreateSubscriptionAsync(
                ExpirationFor(null, definition.LifetimeDays),
                definition.NotificationUrl,
                SubscriptionClientState.Create(definition.Name, _options.ClientState),
                cancellationToken);
            definition = await subscriptionStore.UpdateAsync(
                definition.Id,
                created.Id,
                definition.Name,
                definition.NotificationUrl,
                definition.LifetimeDays,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The default webhook subscription is unavailable.");
            return new EnsureSubscriptionResult(
                SubscriptionAction.Created,
                ToView(created, resource, definition, definition));
        }

        definition = await subscriptionStore.UpdateAsync(
            definition.Id,
            existing.Id,
            definition.Name,
            definition.NotificationUrl,
            definition.LifetimeDays,
            cancellationToken) ?? throw new InvalidOperationException(
                "The default webhook subscription is unavailable.");

        if (existing.ExpirationUtc >= DateTimeOffset.UtcNow.AddDays(RenewalThresholdDays))
        {
            return new EnsureSubscriptionResult(
                SubscriptionAction.Unchanged,
                ToView(existing, resource, definition, definition));
        }

        var renewed = await RenewAsync(existing.Id, definition.LifetimeDays, cancellationToken);
        return new EnsureSubscriptionResult(SubscriptionAction.Renewed, renewed);
    }

    private async Task<WebhookSubscriptionDefinition> GetDefaultDefinitionAsync(
        CancellationToken cancellationToken) =>
        await subscriptionStore.GetByNameAsync(WebhookSubscriptionDefaults.Name, cancellationToken)
            ?? throw new InvalidOperationException("The default webhook subscription is unavailable.");

    private async Task<string> GetExpectedResourceAsync(CancellationToken cancellationToken) =>
        $"drives/{await sharePointClient.GetDriveIdAsync(cancellationToken)}/root";

    /// <summary>
    /// Clamps a requested lifetime to the range the options allow. Microsoft Graph caps a drive
    /// subscription at roughly 30 days, which is what that range encodes.
    /// </summary>
    private static DateTimeOffset ExpirationFor(int? days, int defaultDays) =>
        DateTimeOffset.UtcNow.AddDays(Math.Clamp(days ?? defaultDays, 1, 29));

    /// <summary>
    /// A subscription belongs to this deployment when all three of resource, notification URL, and
    /// client state match what it would create. Another deployment pointing at the same drive has a
    /// different notification URL, and a stale one usually has a different client state.
    /// </summary>
    private bool IsDefault(
        GraphSubscription subscription,
        string expectedResource,
        WebhookSubscriptionDefinition definition,
        WebhookSubscriptionDefinition? trackedDefinition = null) =>
        string.Equals(
            GetName(subscription, definition, trackedDefinition),
            WebhookSubscriptionDefaults.Name,
            StringComparison.Ordinal)
        && ResourceMatches(subscription, expectedResource)
        && NotificationUrlMatches(subscription, definition.NotificationUrl)
        && (trackedDefinition?.Id == definition.Id || ClientStateMatches(subscription));

    private string GetName(
        GraphSubscription subscription,
        WebhookSubscriptionDefinition definition,
        WebhookSubscriptionDefinition? trackedDefinition = null)
    {
        if (trackedDefinition is not null)
        {
            return trackedDefinition.Name;
        }

        if (SubscriptionClientState.TryGetName(subscription.ClientState, _options.ClientState, out var name))
        {
            return name;
        }

        return ClientStateMatches(subscription)
            && NotificationUrlMatches(subscription, definition.NotificationUrl)
            ? WebhookSubscriptionDefaults.Name
            : "Untracked";
    }

    private static bool ResourceMatches(GraphSubscription subscription, string expectedResource) =>
        string.Equals(subscription.Resource.TrimStart('/'), expectedResource, StringComparison.OrdinalIgnoreCase);

    private static bool NotificationUrlMatches(GraphSubscription subscription, string expectedUrl) =>
        string.Equals(subscription.NotificationUrl, expectedUrl, StringComparison.OrdinalIgnoreCase);

    private bool ClientStateMatches(GraphSubscription subscription) =>
        SubscriptionClientState.IsValid(subscription.ClientState, _options.ClientState);

    private SubscriptionView ToView(
        GraphSubscription subscription,
        string expectedResource,
        WebhookSubscriptionDefinition definition,
        WebhookSubscriptionDefinition? trackedDefinition = null)
    {
        var remaining = subscription.ExpirationUtc - DateTimeOffset.UtcNow;
        var status = remaining <= TimeSpan.Zero
            ? SubscriptionStatus.Expired
            : remaining <= TimeSpan.FromDays(RenewalThresholdDays)
                ? SubscriptionStatus.ExpiringSoon
                : SubscriptionStatus.Active;

        return new SubscriptionView(
            subscription.Id,
            trackedDefinition?.Id,
            GetName(subscription, definition, trackedDefinition),
            subscription.Resource,
            subscription.NotificationUrl,
            subscription.ExpirationUtc,
            ClientStateMatches(subscription),
            ResourceMatches(subscription, expectedResource),
            NotificationUrlMatches(subscription, definition.NotificationUrl),
            trackedDefinition is not null,
            ResourceMatches(subscription, expectedResource) && ClientStateMatches(subscription),
            IsDefault(subscription, expectedResource, definition, trackedDefinition),
            status);
    }

    private static SubscriptionView ToMissingTrackedView(
        WebhookSubscriptionDefinition definition,
        string expectedResource) => new(
        Id: null,
        DatabaseId: definition.Id,
        definition.Name,
        expectedResource,
        definition.NotificationUrl,
        ExpirationUtc: null,
        ClientStateMatches: false,
        ResourceMatches: true,
        NotificationUrlMatches: true,
        IsTracked: true,
        IsManaged: false,
        IsDefault: string.Equals(
            definition.Name,
            WebhookSubscriptionDefaults.Name,
            StringComparison.Ordinal),
        SubscriptionStatus.Missing);

    private async Task ThrowIfNameTakenAsync(
        string name,
        string? exceptGraphSubscriptionId,
        CancellationToken cancellationToken)
    {
        var definitions = await subscriptionStore.ListAsync(cancellationToken);
        if (definitions.Any(item =>
                !string.Equals(item.GraphSubscriptionId, exceptGraphSubscriptionId, StringComparison.Ordinal) &&
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicateSubscriptionNameException(name);
        }
    }

    private static WebhookSubscriptionDefinition? FindDefinition(
        IEnumerable<WebhookSubscriptionDefinition> definitions,
        string graphSubscriptionId) =>
        definitions.FirstOrDefault(item =>
            string.Equals(item.GraphSubscriptionId, graphSubscriptionId, StringComparison.Ordinal));

    private WebhookSubscriptionDefinition? ResolveDefinition(
        IEnumerable<WebhookSubscriptionDefinition> definitions,
        GraphSubscription subscription,
        string expectedResource,
        WebhookSubscriptionDefinition defaultDefinition) =>
        FindDefinition(definitions, subscription.Id) ??
        (IsDefault(subscription, expectedResource, defaultDefinition) ? defaultDefinition : null);

    private async Task TryDeleteCreatedSubscriptionAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await sharePointClient.DeleteSubscriptionAsync(id, cancellationToken);
        }
        catch
        {
            // Preserve the database error. The Graph subscription can still be found and removed in the UI.
        }
    }
}
