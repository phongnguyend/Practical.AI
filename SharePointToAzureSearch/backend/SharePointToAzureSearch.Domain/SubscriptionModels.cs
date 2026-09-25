using System.Text.Json.Serialization;

namespace SharePointToAzureSearch.Domain;

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
    bool HasCustomClientState,
    bool AutoRenewEnabled,
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
/// Thrown for an operation the reserved Default subscription does not allow.
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
/// The outcome of an edit. <see cref="Replaced"/> says Graph required a new subscription for the
/// changed name, notification URL, or client state, so <see cref="Subscription"/> has a new ID.
/// </summary>
public sealed record UpdateSubscriptionResult(SubscriptionView Subscription, bool Replaced, string? Warning);

/// <summary>One webhook subscription as this deployment tracks it in its own database.</summary>
public sealed record WebhookSubscriptionDefinition(
    Guid Id,
    string? GraphSubscriptionId,
    string Name,
    string NotificationUrl,
    string? ClientState,
    bool AutoRenewEnabled,
    int LifetimeDays,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
