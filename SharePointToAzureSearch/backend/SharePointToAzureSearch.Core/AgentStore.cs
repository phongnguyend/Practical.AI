using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

/// <summary>A named, reusable set of instructions for an AI agent.</summary>
public sealed record AgentDefinition(
    Guid Id,
    string Name,
    string ModelId,
    string Instructions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class AgentDefaults
{
    public const string Name = "Default";
}

public interface IAgentStore
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<AgentDefinition?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken);
    Task<AgentDefinition> CreateAsync(
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken);
    Task<AgentDefinition?> UpdateAsync(
        Guid id,
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken);
}

public sealed class AgentNameConflictException(string name)
    : Exception($"An agent named '{name}' already exists.");

public sealed class DefaultAgentNameChangeException()
    : Exception("The default agent's name cannot be changed.");

/// <summary>Stores agent definitions in the application's SQL Server database.</summary>
public sealed class EfAgentStore(
    IDbContextFactory<SharePointIndexDbContext> contextFactory,
    IOptions<OpenAiOptions> openAiOptions) : IAgentStore
{
    private readonly string _defaultModelId = openAiOptions.Value.ChatDeployment;

    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentDefinitions
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgentDefinition(
                a.Id, a.Name, a.ModelId ?? _defaultModelId, a.Instructions, a.CreatedAtUtc, a.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentDefinition?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AgentDefinition(
                a.Id, a.Name, a.ModelId ?? _defaultModelId, a.Instructions, a.CreatedAtUtc, a.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.Name == name)
            .Select(a => new AgentDefinition(
                a.Id, a.Name, a.ModelId ?? _defaultModelId, a.Instructions, a.CreatedAtUtc, a.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgentDefinition> CreateAsync(
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new AgentDefinitionEntity
        {
            Name = name,
            ModelId = modelId,
            Instructions = instructions,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.AgentDefinitions.AnyAsync(a => a.Name == name, cancellationToken))
        {
            throw new AgentNameConflictException(name);
        }

        context.AgentDefinitions.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            // The unique index closes the race between the check above and this insert.
            throw new AgentNameConflictException(name);
        }

        return ToRecord(entity);
    }

    public async Task<AgentDefinition?> UpdateAsync(
        Guid id,
        string name,
        string modelId,
        string instructions,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (string.Equals(entity.Name, AgentDefaults.Name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.Name, name, StringComparison.Ordinal))
        {
            throw new DefaultAgentNameChangeException();
        }

        if (await context.AgentDefinitions.AnyAsync(a => a.Id != id && a.Name == name, cancellationToken))
        {
            throw new AgentNameConflictException(name);
        }

        entity.Name = name;
        entity.ModelId = modelId;
        entity.Instructions = instructions;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            throw new AgentNameConflictException(name);
        }

        return ToRecord(entity);
    }

    private AgentDefinition ToRecord(AgentDefinitionEntity entity) => new(
        entity.Id,
        entity.Name,
        entity.ModelId ?? _defaultModelId,
        entity.Instructions,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);

    private static bool IsUniqueNameViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
