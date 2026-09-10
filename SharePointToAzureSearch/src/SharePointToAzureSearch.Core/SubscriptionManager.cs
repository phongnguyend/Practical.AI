using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// Serialized by name rather than by ordinal, so a client reads "ExpiringSoon" and adding a status
/// later cannot silently change what an existing one means.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubscriptionStatus>))]
public enum SubscriptionStatus
{
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
/// A Microsoft Graph subscription as an operator needs to see it. <see cref="ClientStateMatches"/> is
/// reported rather than the client state itself, because that value is the shared secret the webhook
/// authenticates notifications with and must not leave the server.
/// </summary>
public sealed record SubscriptionView(
    string Id,
    string Resource,
    string NotificationUrl,
    DateTimeOffset ExpirationUtc,
    bool ClientStateMatches,
    bool ResourceMatches,
    bool NotificationUrlMatches,
    bool IsManaged,
    bool IsDefault,
    SubscriptionStatus Status);

/// <summary>Thrown when a notification URL is already taken by another subscription.</summary>
public sealed class DuplicateNotificationUrlException(string url)
    : Exception($"A subscription for '{url}' already exists. Each subscription needs its own notification URL.");

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
public sealed class SubscriptionManager(SharePointClient sharePointClient, IOptions<SharePointOptions> options)
{
    /// <summary>How close to expiry a subscription has to be before a renewal pass extends it.</summary>
    public const int RenewalThresholdDays = 3;

    private readonly SharePointOptions _options = options.Value;

    public async Task<SubscriptionOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);

        return new SubscriptionOverview(
            resource,
            _options.NotificationUrl,
            _options.SubscriptionRenewalEnabled,
            _options.SubscriptionLifetimeDays,
            _options.RenewalCheckHours,
            RenewalThresholdDays,
            [.. subscriptions.Select(x => ToView(x, resource)).OrderByDescending(x => x.IsManaged).ThenBy(x => x.ExpirationUtc)]);
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
    public async Task<SubscriptionView> CreateAsync(int? days, string? notificationUrl, CancellationToken cancellationToken)
    {
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var url = notificationUrl ?? _options.NotificationUrl;

        var existing = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        ThrowIfUrlTaken(existing, url, exceptId: null);

        var created = await sharePointClient.CreateSubscriptionAsync(ExpirationFor(days), notificationUrl, cancellationToken);
        return ToView(created, resource);
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
        int? days,
        string? notificationUrl,
        CancellationToken cancellationToken)
    {
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var current = all.FirstOrDefault(x => x.Id == id)
            ?? throw new KeyNotFoundException($"No subscription with ID '{id}'.");

        var url = string.IsNullOrWhiteSpace(notificationUrl) ? current.NotificationUrl : notificationUrl.Trim();
        var urlChanged = !string.Equals(url, current.NotificationUrl, StringComparison.OrdinalIgnoreCase);

        if (urlChanged && IsDefault(current))
        {
            throw new ProtectedSubscriptionException(
                "This is the default subscription, on the configured SharePoint:NotificationUrl. Change SharePoint:NotificationUrl to move it; the renewal service would otherwise recreate it here.");
        }

        if (!urlChanged)
        {
            return new UpdateSubscriptionResult(await RenewAsync(id, days, cancellationToken), Replaced: false, Warning: null);
        }

        ThrowIfUrlTaken(all, url, exceptId: id);

        var created = await sharePointClient.CreateSubscriptionAsync(ExpirationFor(days), url, cancellationToken);

        string? warning = null;
        try
        {
            await sharePointClient.DeleteSubscriptionAsync(id, cancellationToken);
        }
        catch (Exception ex)
        {
            warning = $"The replacement was created, but removing the previous subscription ({id}) failed: {ex.Message}. Delete it by hand.";
        }

        return new UpdateSubscriptionResult(ToView(created, resource), Replaced: true, warning);
    }

    /// <summary>
    /// Whether a notification URL is one Microsoft Graph will accept: absolute, and HTTPS, which Graph
    /// requires so that it can validate the endpoint and deliver notifications over TLS.
    /// </summary>
    public static bool IsValidNotificationUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public async Task<SubscriptionView> RenewAsync(string id, int? days, CancellationToken cancellationToken)
    {
        var expiration = ExpirationFor(days);
        await sharePointClient.RenewSubscriptionAsync(id, expiration, cancellationToken);

        // Graph's PATCH response is not returned by the client, so the refreshed list is what confirms
        // the new expiry rather than the value that was requested.
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var renewed = subscriptions.FirstOrDefault(x => x.Id == id)
            ?? new GraphSubscription(id, resource, _options.NotificationUrl, expiration, _options.ClientState);
        return ToView(renewed, resource);
    }

    /// <summary>
    /// Removes a subscription. The default one — the subscription on the configured notification URL —
    /// is refused: the renewal service recreates it, so deleting it here would achieve nothing beyond a
    /// gap in notifications until the next pass.
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var all = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var current = all.FirstOrDefault(x => x.Id == id);
        if (current is not null && IsDefault(current))
        {
            throw new ProtectedSubscriptionException(
                "This is the default subscription, on the configured SharePoint:NotificationUrl, and cannot be deleted here. Turn off SharePoint:SubscriptionRenewalEnabled or change the configured URL first.");
        }

        await sharePointClient.DeleteSubscriptionAsync(id, cancellationToken);
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

    /// <summary>The subscription on the configured notification URL, which the worker owns.</summary>
    private bool IsDefault(GraphSubscription subscription) => NotificationUrlMatches(subscription);

    /// <summary>
    /// Makes sure exactly one subscription this deployment owns exists and is not about to expire. This
    /// is what the renewal background service runs on its interval.
    /// </summary>
    public async Task<EnsureSubscriptionResult> EnsureAsync(CancellationToken cancellationToken)
    {
        var resource = await GetExpectedResourceAsync(cancellationToken);
        var subscriptions = await sharePointClient.ListSubscriptionsAsync(cancellationToken);
        var existing = subscriptions.FirstOrDefault(x => IsManaged(x, resource));

        if (existing is null)
        {
            var created = await sharePointClient.CreateSubscriptionAsync(ExpirationFor(null), null, cancellationToken);
            return new EnsureSubscriptionResult(SubscriptionAction.Created, ToView(created, resource));
        }

        if (existing.ExpirationUtc >= DateTimeOffset.UtcNow.AddDays(RenewalThresholdDays))
        {
            return new EnsureSubscriptionResult(SubscriptionAction.Unchanged, ToView(existing, resource));
        }

        var renewed = await RenewAsync(existing.Id, null, cancellationToken);
        return new EnsureSubscriptionResult(SubscriptionAction.Renewed, renewed);
    }

    private async Task<string> GetExpectedResourceAsync(CancellationToken cancellationToken) =>
        $"drives/{await sharePointClient.GetDriveIdAsync(cancellationToken)}/root";

    /// <summary>
    /// Clamps a requested lifetime to the range the options allow. Microsoft Graph caps a drive
    /// subscription at roughly 30 days, which is what that range encodes.
    /// </summary>
    private DateTimeOffset ExpirationFor(int? days) =>
        DateTimeOffset.UtcNow.AddDays(Math.Clamp(days ?? _options.SubscriptionLifetimeDays, 1, 29));

    /// <summary>
    /// A subscription belongs to this deployment when all three of resource, notification URL, and
    /// client state match what it would create. Another deployment pointing at the same drive has a
    /// different notification URL, and a stale one usually has a different client state.
    /// </summary>
    private bool IsManaged(GraphSubscription subscription, string expectedResource) =>
        ResourceMatches(subscription, expectedResource)
        && NotificationUrlMatches(subscription)
        && ClientStateMatches(subscription);

    private static bool ResourceMatches(GraphSubscription subscription, string expectedResource) =>
        string.Equals(subscription.Resource.TrimStart('/'), expectedResource, StringComparison.OrdinalIgnoreCase);

    private bool NotificationUrlMatches(GraphSubscription subscription) =>
        string.Equals(subscription.NotificationUrl, _options.NotificationUrl, StringComparison.OrdinalIgnoreCase);

    private bool ClientStateMatches(GraphSubscription subscription) =>
        string.Equals(subscription.ClientState, _options.ClientState, StringComparison.Ordinal);

    private SubscriptionView ToView(GraphSubscription subscription, string expectedResource)
    {
        var remaining = subscription.ExpirationUtc - DateTimeOffset.UtcNow;
        var status = remaining <= TimeSpan.Zero
            ? SubscriptionStatus.Expired
            : remaining <= TimeSpan.FromDays(RenewalThresholdDays)
                ? SubscriptionStatus.ExpiringSoon
                : SubscriptionStatus.Active;

        return new SubscriptionView(
            subscription.Id,
            subscription.Resource,
            subscription.NotificationUrl,
            subscription.ExpirationUtc,
            ClientStateMatches(subscription),
            ResourceMatches(subscription, expectedResource),
            NotificationUrlMatches(subscription),
            IsManaged(subscription, expectedResource),
            IsDefault(subscription),
            status);
    }
}
