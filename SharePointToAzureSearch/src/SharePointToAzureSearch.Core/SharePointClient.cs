using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using System.Net;
using SdkSubscription = Microsoft.Graph.Models.Subscription;

namespace SharePointToAzureSearch.Core;

public sealed class SharePointClient(
    GraphServiceClient graph,
    IMemoryCache memoryCache,
    IOptions<SharePointOptions> options)
{
    private readonly SharePointOptions _options = options.Value;

    private async Task<Site> GetSiteAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"SharePointSite_{_options.SiteHostname}_{_options.SitePath}";

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(5)
        };

        return await memoryCache.GetOrSetAsync(cacheKey, async () =>
        {
            var site = await graph.Sites[$"{_options.SiteHostname}:{_options.SitePath}"]
                .GetAsync(cancellationToken: cancellationToken);
            return site ?? throw new InvalidDataException("Microsoft Graph returned an empty site response.");
        }, cacheOptions);
    }

    private async Task<Drive> GetDocumentLibraryAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"SharePointDrive_{_options.SiteHostname}_{_options.SitePath}_{_options.DocumentLibraryName}";

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(5)
        };

        return await memoryCache.GetOrSetAsync(cacheKey, async () =>
        {
            var site = await GetSiteAsync(cancellationToken);

            var drives = await graph.Sites[site.Id].Drives
                .GetAsync(cancellationToken: cancellationToken);

            var drive = (drives?.Value ?? []).FirstOrDefault(d =>
                string.Equals(d.Name, _options.DocumentLibraryName, StringComparison.OrdinalIgnoreCase));

            if (drive == null)
            {
                throw new InvalidOperationException($"Document library '{_options.DocumentLibraryName}' not found");
            }

            return drive;
        }, cacheOptions);
    }

    public async Task<string> GetDriveIdAsync(CancellationToken cancellationToken = default)
    {
        var drive = await GetDocumentLibraryAsync(cancellationToken);
        return drive.Id ?? throw new InvalidDataException("Microsoft Graph returned a document library without an ID.");
    }

    /// <summary>
    /// Resolves the principal tokens that grant a user access to indexed content: the user's own object ID,
    /// their mail addresses, and every group they are a transitive member of. The tokens use the same shape
    /// as <see cref="SearchChunkDocument.AllowedPrincipals"/>, so they can be compared directly in a filter.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUserPrincipalsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"SharePointUserPrincipals_{userId}";

        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(5)
        };

        return await memoryCache.GetOrSetAsync<IReadOnlyList<string>>(cacheKey, async () =>
        {
            try
            {
                var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var user = await graph.Users[userId].GetAsync(request =>
                    request.QueryParameters.Select = ["id", "mail", "userPrincipalName"],
                    cancellationToken)
                    ?? throw new InvalidDataException("Microsoft Graph returned an empty user response.");

                if (user.Id is { Length: > 0 } id) principals.Add($"user:{id}");
                if (user.Mail is { Length: > 0 } mail) principals.Add($"email:{mail.ToLowerInvariant()}");
                if (user.UserPrincipalName is { Length: > 0 } upn) principals.Add($"email:{upn.ToLowerInvariant()}");

                var response = await graph.Users[userId].TransitiveMemberOf
                    .GetAsync(cancellationToken: cancellationToken);
                while (response is not null)
                {
                    foreach (var directoryObject in response.Value ?? [])
                    {
                        if (directoryObject is Group { Id: { Length: > 0 } groupId })
                            principals.Add($"group:{groupId}");
                    }

                    response = response.OdataNextLink is { Length: > 0 } nextLink
                        ? await graph.Users[userId].TransitiveMemberOf.WithUrl(nextLink)
                            .GetAsync(cancellationToken: cancellationToken)
                        : null;
                }

                return principals.Order().ToArray();
            }
            catch (ApiException ex)
            {
                throw ToHttpRequestException(ex);
            }
        }, cacheOptions);
    }

    public async Task<DeltaPage> GetDeltaPageAsync(string? url, CancellationToken cancellationToken)
    {
        try
        {
            url ??= $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(await GetDriveIdAsync(cancellationToken))}/root/delta";

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
            var driveId = await GetDriveIdAsync(cancellationToken);
            await using var input = await graph.Drives[driveId].Items[itemId].Content
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
            var driveId = await GetDriveIdAsync(cancellationToken);
            var response = await graph.Drives[driveId].Items[itemId].Permissions
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
                    ? await graph.Drives[driveId].Items[itemId].Permissions.WithUrl(nextLink)
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
            var driveId = await GetDriveIdAsync(cancellationToken);
            var result = await graph.Subscriptions.PostAsync(new SdkSubscription
            {
                ChangeType = "updated",
                NotificationUrl = _options.NotificationUrl,
                Resource = $"drives/{driveId}/root",
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
