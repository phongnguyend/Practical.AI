using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using SharePointToAzureSearch.Core.Data;

namespace SharePointToAzureSearch.Core;

/// <summary>A named, reusable set of instructions for an AI agent.</summary>
public sealed record AgentDefinition(
    Guid Id,
    string Name,
    string Instructions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface IAgentStore
{
    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken);
    Task<AgentDefinition?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AgentDefinition> CreateAsync(string name, string instructions, CancellationToken cancellationToken);
    Task<AgentDefinition?> UpdateAsync(Guid id, string name, string instructions, CancellationToken cancellationToken);
}

public sealed class AgentNameConflictException(string name)
    : Exception($"An agent named '{name}' already exists.");

/// <summary>Stores agent definitions in the application's SQL Server database.</summary>
public sealed class EfAgentStore(IDbContextFactory<SharePointIndexDbContext> contextFactory) : IAgentStore
{
    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentDefinitions
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgentDefinition(a.Id, a.Name, a.Instructions, a.CreatedAtUtc, a.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentDefinition?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AgentDefinition(a.Id, a.Name, a.Instructions, a.CreatedAtUtc, a.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgentDefinition> CreateAsync(
        string name,
        string instructions,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
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
        string instructions,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (await context.AgentDefinitions.AnyAsync(a => a.Id != id && a.Name == name, cancellationToken))
        {
            throw new AgentNameConflictException(name);
        }

        entity.Name = name;
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

    private static AgentDefinition ToRecord(AgentDefinitionEntity entity) => new(
        entity.Id,
        entity.Name,
        entity.Instructions,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc);

    private static bool IsUniqueNameViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
