using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Application;
using SharePointToAzureSearch.Domain;

namespace SharePointToAzureSearch.Infrastructure;

/// <summary>
/// Creating, renewing, and removing the Microsoft Graph webhook subscription, and deciding which of the
/// tenant's subscriptions this deployment owns. The renewal background service and the management API
/// both go through here so that they cannot disagree about which subscription is "ours".
/// </summary>
public sealed class SubscriptionManager(
    SharePointClient sharePointClient,
    IWebhookSubscriptionRepository subscriptionRepository,
    IOptions<SharePointOptions> options,
    ILogger<SubscriptionManager> logger)
{
    /// <summary>How close to expiry a subscription has to be before a renewal pass extends it.</summary>
    public const int RenewalThresholdDays = 3;

    private readonly SharePointOptions _options = options.Value;

    public async Task<SubscriptionOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
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
    /// <see cref="EnsureEnabledAsync"/> is the call that avoids duplicates.
    /// <para>
    /// A <paramref name="notificationUrl"/> other than the configured one creates an additional
    /// subscription. The user can enable automatic renewal for that record separately.
    /// </para>
    /// </summary>
    public async Task<SubscriptionView> CreateAsync(
        string? name,
        int? days,
        string? notificationUrl,
        string? clientState,
        CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? "Additional" : name.Trim();
        var url = notificationUrl ?? definition.NotificationUrl;
        var lifetimeDays = Math.Clamp(days ?? definition.LifetimeDays, 1, 29);
        var customClientState = string.IsNullOrWhiteSpace(clientState) ? null : clientState.Trim();

        await ThrowIfNameTakenAsync(normalizedName, exceptGraphSubscriptionId: null, cancellationToken);

        var existing = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        ThrowIfUrlTaken(existing, url, exceptId: null);

        var created = await sharePointClient.CreateSubscriptionAsync(
            ExpirationFor(lifetimeDays, definition.LifetimeDays),
            url,
            customClientState ?? SubscriptionClientState.Create(normalizedName, _options.ClientState),
            cancellationToken);
        try
        {
            var saved = await subscriptionRepository.CreateAsync(
                created.Id, normalizedName, url, customClientState, lifetimeDays, cancellationToken);
            return ToView(created, resource, definition, saved);
        }
        catch
        {
            await TryDeleteCreatedSubscriptionAsync(created.Id, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Changes a subscription's lifetime, name, notification URL, or client state.
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
        string? clientState,
        CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var databaseDefinition = Guid.TryParse(id, out var databaseId)
            ? definitions.FirstOrDefault(item => item.Id == databaseId)
            : null;
        var graphSubscriptionId = databaseDefinition?.GraphSubscriptionId ?? id;
        var current = all.FirstOrDefault(x => x.Id == graphSubscriptionId);
        var requestedClientState = string.IsNullOrWhiteSpace(clientState) ? null : clientState.Trim();

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
                throw new ProtectedSubscriptionException("'Default' is reserved for the default subscription.");
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
            var recordClientState = requestedClientState ?? databaseDefinition.ClientState;
            ThrowIfUrlTaken(all, recordUrl, exceptId: databaseDefinition.GraphSubscriptionId);

            var createdForRecord = await sharePointClient.CreateSubscriptionAsync(
                ExpirationFor(recordLifetimeDays, databaseDefinition.LifetimeDays),
                recordUrl,
                recordClientState ?? SubscriptionClientState.Create(recordName, _options.ClientState),
                cancellationToken);
            try
            {
                var savedDefinition = await subscriptionRepository.UpdateAsync(
                    databaseDefinition.Id,
                    createdForRecord.Id,
                    recordName,
                    recordUrl,
                    recordClientState,
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
        var effectiveClientState = requestedClientState ?? currentDefinition.ClientState;
        var nameChanged = !string.Equals(normalizedName, currentView.Name, StringComparison.Ordinal);
        var urlChanged = !string.Equals(url, current.NotificationUrl, StringComparison.OrdinalIgnoreCase);
        var clientStateChanged = requestedClientState is not null &&
            !SubscriptionClientState.IsExactMatch(current.ClientState, requestedClientState);

        if (currentView.IsDefault &&
            !string.Equals(normalizedName, WebhookSubscriptionDefaults.Name, StringComparison.Ordinal))
        {
            throw new ProtectedSubscriptionException("The default subscription's name cannot be changed.");
        }

        if (!currentView.IsDefault &&
            string.Equals(normalizedName, WebhookSubscriptionDefaults.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtectedSubscriptionException("'Default' is reserved for the default subscription.");
        }

        await ThrowIfNameTakenAsync(normalizedName, current.Id, cancellationToken);

        if (!nameChanged && !urlChanged && !clientStateChanged)
        {
            currentDefinition = await subscriptionRepository.UpdateAsync(
                currentDefinition.Id,
                current.Id,
                normalizedName,
                url,
                effectiveClientState,
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
            effectiveClientState ?? SubscriptionClientState.Create(normalizedName, _options.ClientState),
            cancellationToken);

        currentDefinition = await subscriptionRepository.UpdateAsync(
            currentDefinition.Id,
            created.Id,
            normalizedName,
            url,
            effectiveClientState,
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

    public static bool IsValidCustomClientState(string clientState) =>
        clientState.Length is >= 16 and <= 128 && !string.IsNullOrWhiteSpace(clientState);

    public async Task SetAutoRenewAsync(Guid databaseId, bool enabled, CancellationToken cancellationToken)
    {
        if (!await subscriptionRepository.SetAutoRenewAsync(databaseId, enabled, cancellationToken))
        {
            throw new KeyNotFoundException($"No tracked subscription with ID '{databaseId}'.");
        }
    }

    public async Task<SubscriptionView> RenewAsync(string id, int? days, CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var expiration = ExpirationFor(days, definition.LifetimeDays);
        await sharePointClient.RenewSubscriptionAsync(id, expiration, cancellationToken);

        // Graph's PATCH response is not returned by the client, so the refreshed list is what confirms
        // the new expiry rather than the value that was requested.
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var renewed = subscriptions.FirstOrDefault(x => x.Id == id)
            ?? new GraphSubscription(id, resource, definition.NotificationUrl, expiration, null);
        return ToView(renewed, resource, definition, FindDefinition(definitions, id));
    }

    /// <summary>
    /// Removes a subscription. The default one — the subscription on the configured notification URL —
    /// is reserved in the database, so it cannot be deleted here.
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var definition = await GetDefaultDefinitionAsync(cancellationToken);
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var current = all.FirstOrDefault(x => x.Id == id);
        if (current is not null && IsDefault(current, resource, definition, FindDefinition(definitions, id)))
        {
            throw new ProtectedSubscriptionException(
                "The default subscription cannot be deleted.");
        }

        await sharePointClient.DeleteSubscriptionAsync(id, cancellationToken);
        await subscriptionRepository.DeleteAsync(id, cancellationToken);
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
    /// Recreates or renews every tracked subscription with automatic renewal enabled.
    /// </summary>
    public async Task<IReadOnlyList<EnsureSubscriptionResult>> EnsureEnabledAsync(CancellationToken cancellationToken)
    {
        var defaultDefinition = await GetDefaultDefinitionAsync(cancellationToken);
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = (await sharePointClient.ListSubscriptionsAsync(cancellationToken)).ToList();
        var results = new List<EnsureSubscriptionResult>();

        foreach (var definition in definitions.Where(item => item.AutoRenewEnabled))
        {
            try
            {
                results.Add(await EnsureDefinitionAsync(definition, defaultDefinition, resource, subscriptions, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to renew webhook subscription {SubscriptionName} ({SubscriptionId}).", definition.Name, definition.GraphSubscriptionId);
            }
        }

        return results;
    }

    private async Task<EnsureSubscriptionResult> EnsureDefinitionAsync(
        WebhookSubscriptionDefinition definition,
        WebhookSubscriptionDefinition defaultDefinition,
        string resource,
        List<GraphSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var isDefault = definition.Id == defaultDefinition.Id;
        var existing = subscriptions.FirstOrDefault(x =>
            string.Equals(x.Id, definition.GraphSubscriptionId, StringComparison.Ordinal) ||
            (isDefault && IsDefault(x, resource, defaultDefinition)));

        if (existing is null)
        {
            ThrowIfUrlTaken(subscriptions, definition.NotificationUrl, exceptId: null);
            var created = await sharePointClient.CreateSubscriptionAsync(
                ExpirationFor(null, definition.LifetimeDays),
                definition.NotificationUrl,
                definition.ClientState ?? SubscriptionClientState.Create(definition.Name, _options.ClientState),
                cancellationToken);
            definition = await subscriptionRepository.UpdateAsync(
                definition.Id,
                created.Id,
                definition.Name,
                definition.NotificationUrl,
                definition.ClientState,
                definition.LifetimeDays,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The webhook subscription record is unavailable.");
            subscriptions.Add(created);
            return new EnsureSubscriptionResult(
                SubscriptionAction.Created,
                ToView(created, resource, isDefault ? definition : defaultDefinition, definition));
        }

        definition = await subscriptionRepository.UpdateAsync(
            definition.Id,
            existing.Id,
            definition.Name,
            definition.NotificationUrl,
            definition.ClientState,
            definition.LifetimeDays,
            cancellationToken) ?? throw new InvalidOperationException(
                "The webhook subscription record is unavailable.");

        if (existing.ExpirationUtc >= DateTimeOffset.UtcNow.AddDays(RenewalThresholdDays))
        {
            return new EnsureSubscriptionResult(
                SubscriptionAction.Unchanged,
                ToView(existing, resource, isDefault ? definition : defaultDefinition, definition));
        }

        var renewed = await RenewAsync(existing.Id, definition.LifetimeDays, cancellationToken);
        return new EnsureSubscriptionResult(SubscriptionAction.Renewed, renewed);
    }

    private async Task<WebhookSubscriptionDefinition> GetDefaultDefinitionAsync(
        CancellationToken cancellationToken) =>
        await subscriptionRepository.GetByNameAsync(WebhookSubscriptionDefaults.Name, cancellationToken)
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
        && (trackedDefinition?.Id == definition.Id || ClientStateMatches(subscription, trackedDefinition));

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

        return ClientStateMatches(subscription, trackedDefinition)
            && NotificationUrlMatches(subscription, definition.NotificationUrl)
            ? WebhookSubscriptionDefaults.Name
            : "Untracked";
    }

    private static bool ResourceMatches(GraphSubscription subscription, string expectedResource) =>
        string.Equals(subscription.Resource.TrimStart('/'), expectedResource, StringComparison.OrdinalIgnoreCase);

    private static bool NotificationUrlMatches(GraphSubscription subscription, string expectedUrl) =>
        string.Equals(subscription.NotificationUrl, expectedUrl, StringComparison.OrdinalIgnoreCase);

    private bool ClientStateMatches(GraphSubscription subscription, WebhookSubscriptionDefinition? trackedDefinition = null) =>
        trackedDefinition?.ClientState is { } customClientState
            ? SubscriptionClientState.IsExactMatch(subscription.ClientState, customClientState)
            : SubscriptionClientState.IsValid(subscription.ClientState, _options.ClientState);

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
            ClientStateMatches(subscription, trackedDefinition),
            trackedDefinition?.ClientState is not null,
            trackedDefinition?.AutoRenewEnabled ?? false,
            ResourceMatches(subscription, expectedResource),
            NotificationUrlMatches(subscription, definition.NotificationUrl),
            trackedDefinition is not null,
            ResourceMatches(subscription, expectedResource) && ClientStateMatches(subscription, trackedDefinition),
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
        HasCustomClientState: definition.ClientState is not null,
        AutoRenewEnabled: definition.AutoRenewEnabled,
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
        var definitions = await subscriptionRepository.ListAsync(cancellationToken);
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
