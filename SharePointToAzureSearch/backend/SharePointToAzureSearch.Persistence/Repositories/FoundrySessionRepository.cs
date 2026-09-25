using Microsoft.EntityFrameworkCore;
using SharePointToAzureSearch.Application;

namespace SharePointToAzureSearch.Persistence.Repositories;

/// <summary>Persists sandbox affinity across API restarts; branches start with no sandbox binding.</summary>
public sealed class FoundrySessionRepository(IDbContextFactory<SharePointIndexDbContext> factory) : IFoundrySessionRepository
{
    public async Task<string?> GetAsync(Guid conversationId, string endpoint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatConversations.Where(c => c.Id == conversationId && c.FoundryEndpoint == endpoint)
            .Select(c => c.FoundrySessionId).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(Guid conversationId, string endpoint, string sessionId, CancellationToken cancellationToken)
    {
        if (sessionId.Length > 200 || endpoint.Length > 2048)
            throw new InvalidOperationException("The Foundry session binding exceeds the supported size.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var count = await db.ChatConversations.Where(c => c.Id == conversationId
                && (c.FoundryEndpoint != endpoint || c.FoundrySessionId == null || c.FoundrySessionId == sessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FoundryEndpoint, endpoint)
                .SetProperty(c => c.FoundrySessionId, sessionId), cancellationToken);
        if (count != 1)
            throw new InvalidOperationException("The conversation was deleted or another request established a different Foundry session.");
    }
}
