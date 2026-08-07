using System.Net;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Options;
using SdkSubscription = Microsoft.Graph.Models.Subscription;

namespace SharePointToAzureSearch.Core;

public sealed class GraphApiClient(
    GraphServiceClient graph,
    IOptions<SharePointOptions> options)
{
    private readonly SharePointOptions _options = options.Value;

    public async Task<DeltaPage> GetDeltaPageAsync(string? url, CancellationToken cancellationToken)
    {
        url ??= $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(_options.DriveId)}/root/delta";
        try
        {
            var response = await new DeltaRequestBuilder(url, graph.RequestAdapter)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Microsoft Graph returned an empty delta response.");

            var items = (response.Value ?? []).Select(item => new DriveItemChange(
                item.Id ?? throw new InvalidDataException("Microsoft Graph returned a drive item without an ID."),
                item.Name ?? "",
                item.WebUrl,
                item.File?.MimeType,
                item.Size,
                item.LastModifiedDateTime,
                item.ETag,
                item.File is not null,
                item.Deleted is not null,
                item.ParentReference?.Path)).ToArray();

            return new(items, response.OdataNextLink, response.OdataDeltaLink);
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == (int)HttpStatusCode.Gone)
        {
            throw new GraphDeltaTokenExpiredException();
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    public async Task<byte[]> DownloadContentAsync(string itemId, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = await graph.Drives[_options.DriveId].Items[itemId].Content
                .GetAsync(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Microsoft Graph returned an empty content stream.");
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new FileTooLargeException(output.Length + read, maxBytes);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return output.ToArray();
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    public async Task<PermissionSnapshot> GetPermissionsAsync(string itemId, CancellationToken cancellationToken)
    {
        try
        {
            var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anonymous = false;
            var response = await graph.Drives[_options.DriveId].Items[itemId].Permissions
                .GetAsync(cancellationToken: cancellationToken);

            while (response is not null)
            {
                foreach (var permission in response.Value ?? [])
                {
                    foreach (var role in permission.Roles ?? []) roles.Add(role);
                    if (string.Equals(permission.Link?.Scope, "anonymous", StringComparison.OrdinalIgnoreCase))
                    {
                        anonymous = true;
                        principals.Add("anonymous");
                    }

                    AddIdentitySet(permission.GrantedToV2, principals);
                    foreach (var identity in permission.GrantedToIdentitiesV2 ?? []) AddIdentitySet(identity, principals);
                }

                response = response.OdataNextLink is { Length: > 0 } nextLink
                    ? await graph.Drives[_options.DriveId].Items[itemId].Permissions.WithUrl(nextLink)
                        .GetAsync(cancellationToken: cancellationToken)
                    : null;
            }
            return new(principals.Order().ToArray(), roles.Order().ToArray(), anonymous);
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    public async Task<IReadOnlyList<GraphSubscription>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var subscriptions = new List<GraphSubscription>();
            var response = await graph.Subscriptions.GetAsync(cancellationToken: cancellationToken);
            while (response is not null)
            {
                subscriptions.AddRange((response.Value ?? []).Select(ToGraphSubscription));
                response = response.OdataNextLink is { Length: > 0 } nextLink
                    ? await graph.Subscriptions.WithUrl(nextLink).GetAsync(cancellationToken: cancellationToken)
                    : null;
            }
            return subscriptions;
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    public async Task<GraphSubscription> CreateSubscriptionAsync(DateTimeOffset expiration, CancellationToken cancellationToken)
    {
        try
        {
            var result = await graph.Subscriptions.PostAsync(new SdkSubscription
            {
                ChangeType = "updated",
                NotificationUrl = _options.NotificationUrl,
                Resource = $"drives/{_options.DriveId}/root",
                ExpirationDateTime = expiration,
                ClientState = _options.ClientState,
                LatestSupportedTlsVersion = "v1_2"
            }, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Microsoft Graph returned an empty subscription response.");
            return ToGraphSubscription(result);
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    public async Task RenewSubscriptionAsync(string id, DateTimeOffset expiration, CancellationToken cancellationToken)
    {
        try
        {
            await graph.Subscriptions[id].PatchAsync(new SdkSubscription
            {
                ExpirationDateTime = expiration
            }, cancellationToken: cancellationToken);
        }
        catch (ApiException ex)
        {
            throw ToHttpRequestException(ex);
        }
    }

    private static GraphSubscription ToGraphSubscription(SdkSubscription item) => new(
        item.Id ?? throw new InvalidDataException("Microsoft Graph returned a subscription without an ID."),
        item.Resource ?? "",
        item.NotificationUrl ?? "",
        item.ExpirationDateTime ?? DateTimeOffset.MinValue,
        item.ClientState);

    private static void AddIdentitySet(SharePointIdentitySet? identitySet, HashSet<string> principals)
    {
        if (identitySet is null) return;
        AddIdentity("user", identitySet.User, principals);
        AddIdentity("group", identitySet.Group, principals);
        AddIdentity("siteGroup", identitySet.SiteGroup, principals);
        AddIdentity("siteGroup", identitySet.SharePointGroup, principals);
        AddIdentity("siteUser", identitySet.SiteUser, principals);
        AddIdentity("application", identitySet.Application, principals);
    }

    private static void AddIdentity(string kind, Identity? identity, HashSet<string> principals)
    {
        if (identity?.Id is { Length: > 0 } id) principals.Add($"{kind}:{id}");
        if (identity is SharePointIdentity { LoginName.Length: > 0 } sharePointIdentity && sharePointIdentity.LoginName.Contains('@'))
            principals.Add($"email:{sharePointIdentity.LoginName.ToLowerInvariant()}");
        if (identity?.AdditionalData.TryGetValue("email", out var email) == true && email?.ToString() is { Length: > 0 } value)
            principals.Add($"email:{value.ToLowerInvariant()}");
    }

    private static HttpRequestException ToHttpRequestException(ApiException exception)
    {
        HttpStatusCode? statusCode = exception.ResponseStatusCode is >= 100 and <= 599
            ? (HttpStatusCode)exception.ResponseStatusCode
            : null;
        return new HttpRequestException(exception.Message, exception, statusCode);
    }
}

public sealed record GraphSubscription(string Id, string Resource, string NotificationUrl, DateTimeOffset ExpirationUtc, string? ClientState);
public sealed class GraphDeltaTokenExpiredException : Exception;
public sealed class FileTooLargeException(long actual, long maximum) : Exception($"File is {actual} bytes; the configured limit is {maximum} bytes.");
