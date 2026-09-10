using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// One table in the worker's SQL Server database, created on first use unless
/// <see cref="SqlServerOptions.AutoCreateTables"/> is false.
/// </summary>
internal sealed class SqlTable(SqlServerOptions options, ILogger logger, string tableName, string columns, string keyColumns)
{
    // The table is prepared once per process; the gate keeps concurrent first calls to one CREATE attempt.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    /// <summary>Schema-qualified and quoted, ready to interpolate into a statement.</summary>
    public string Name { get; } = $"{QuoteName(options.SchemaName)}.{QuoteName(tableName)}";

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await EnsureAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public SqlCommand CreateCommand(SqlConnection connection, string text) =>
        new(text, connection) { CommandTimeout = options.CommandTimeoutSeconds };

    private async Task EnsureAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_ready || !options.AutoCreateTables)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready)
            {
                return;
            }

            await using var command = CreateCommand(connection, $"""
                IF OBJECT_ID(N'{EscapeLiteral(Name)}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {Name}
                    (
                        {columns},
                        CONSTRAINT {QuoteName($"PK_{tableName}")} PRIMARY KEY ({keyColumns})
                    );
                END
                """);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _ready = true;
            logger.LogInformation("Using SQL Server table {Table}.", Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string QuoteName(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

/// <summary>
/// Keeps the Microsoft Graph delta link for each drive in SQL Server, together with the reconciliation
/// round it belongs to. The link is the worker's only checkpoint: it is written after a whole delta page
/// has been indexed, so a pass that fails is repeated from where the last successful one ended.
/// </summary>
public sealed class SqlDeltaStateStore : IDeltaStateStore
{
    private readonly SqlTable _table;

    public SqlDeltaStateStore(IOptions<SqlServerOptions> options, ILogger<SqlDeltaStateStore> logger)
    {
        var value = options.Value;
        _table = new SqlTable(value, logger, value.DeltaStateTableName,
            """
                        DriveId NVARCHAR(200) NOT NULL,
                        DeltaLink NVARCHAR(MAX) NOT NULL,
                        ScanId UNIQUEIDENTIFIER NOT NULL,
                        SweptScanId UNIQUEIDENTIFIER NULL,
                        UpdatedAtUtc DATETIMEOFFSET(7) NOT NULL
            """,
            "DriveId");
    }

    public async Task<DeltaCheckpoint?> GetAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"SELECT DeltaLink, ScanId, SweptScanId FROM {_table.Name} WHERE DriveId = @driveId;");
        AddDriveId(command, driveId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeltaCheckpoint(reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2))
            : null;
    }

    public async Task SetAsync(string driveId, DeltaCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"""
            UPDATE {_table.Name}
               SET DeltaLink = @deltaLink, ScanId = @scanId, UpdatedAtUtc = @updatedAtUtc
             WHERE DriveId = @driveId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {_table.Name} (DriveId, DeltaLink, ScanId, SweptScanId, UpdatedAtUtc)
                VALUES (@driveId, @deltaLink, @scanId, NULL, @updatedAtUtc);
            END
            """);
        AddDriveId(command, driveId);
        command.Parameters.Add("@deltaLink", SqlDbType.NVarChar, -1).Value = checkpoint.DeltaLink;
        command.Parameters.Add("@scanId", SqlDbType.UniqueIdentifier).Value = checkpoint.ScanId;
        command.Parameters.Add("@updatedAtUtc", SqlDbType.DateTimeOffset).Value = DateTimeOffset.UtcNow;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSweptAsync(string driveId, Guid scanId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"UPDATE {_table.Name} SET SweptScanId = @scanId WHERE DriveId = @driveId AND ScanId = @scanId;");
        AddDriveId(command, driveId);
        command.Parameters.Add("@scanId", SqlDbType.UniqueIdentifier).Value = scanId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(string driveId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"DELETE FROM {_table.Name} WHERE DriveId = @driveId;");
        AddDriveId(command, driveId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddDriveId(SqlCommand command, string driveId) =>
        command.Parameters.Add("@driveId", SqlDbType.NVarChar, 200).Value = driveId;
}

/// <summary>
/// Tracks what was last indexed for each SharePoint file in SQL Server.
/// </summary>
public sealed class SqlFileMetadataStore : IFileMetadataStore
{
    private const int IdentifierLength = 200;

    private readonly SqlTable _table;

    public SqlFileMetadataStore(IOptions<SqlServerOptions> options, ILogger<SqlFileMetadataStore> logger)
    {
        var value = options.Value;
        _table = new SqlTable(value, logger, value.FileMetadataTableName,
            $"""
                        DriveId NVARCHAR({IdentifierLength}) NOT NULL,
                        ItemId NVARCHAR({IdentifierLength}) NOT NULL,
                        FileName NVARCHAR(400) NOT NULL,
                        ParentPath NVARCHAR(1000) NULL,
                        WebUrl NVARCHAR(2000) NULL,
                        MimeType NVARCHAR(200) NULL,
                        SizeBytes BIGINT NULL,
                        LastModifiedUtc DATETIMEOFFSET(7) NULL,
                        ETag NVARCHAR(200) NULL,
                        CTag NVARCHAR(200) NULL,
                        PermissionsHash CHAR(44) NOT NULL,
                        IndexFingerprint NVARCHAR(200) NOT NULL,
                        ChunkCount INT NOT NULL,
                        ScanId UNIQUEIDENTIFIER NOT NULL,
                        IndexedAtUtc DATETIMEOFFSET(7) NOT NULL
            """,
            "DriveId, ItemId");
    }

    public async Task<FileIndexRecord?> GetAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"""
            SELECT FileName, ParentPath, WebUrl, MimeType, SizeBytes, LastModifiedUtc, ETag, CTag,
                   PermissionsHash, IndexFingerprint, ChunkCount, ScanId, IndexedAtUtc
            FROM {_table.Name}
            WHERE DriveId = @driveId AND ItemId = @itemId;
            """);
        AddKeyParameters(command, driveId, itemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FileIndexRecord(
            driveId,
            itemId,
            reader.GetString(0),
            ReadString(reader, 1),
            ReadString(reader, 2),
            ReadString(reader, 3),
            ReadNullable<long>(reader, 4),
            ReadNullable<DateTimeOffset>(reader, 5),
            ReadString(reader, 6),
            ReadString(reader, 7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetGuid(11),
            reader.GetFieldValue<DateTimeOffset>(12));
    }

    public async Task SaveAsync(FileIndexRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);

        // A single delta pass at a time owns a file, so an update followed by a conditional insert is
        // enough and avoids MERGE.
        await using var command = _table.CreateCommand(connection, $"""
            UPDATE {_table.Name}
               SET FileName = @fileName,
                   ParentPath = @parentPath,
                   WebUrl = @webUrl,
                   MimeType = @mimeType,
                   SizeBytes = @sizeBytes,
                   LastModifiedUtc = @lastModifiedUtc,
                   ETag = @eTag,
                   CTag = @cTag,
                   PermissionsHash = @permissionsHash,
                   IndexFingerprint = @indexFingerprint,
                   ChunkCount = @chunkCount,
                   ScanId = @scanId,
                   IndexedAtUtc = @indexedAtUtc
             WHERE DriveId = @driveId AND ItemId = @itemId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO {_table.Name}
                    (DriveId, ItemId, FileName, ParentPath, WebUrl, MimeType, SizeBytes, LastModifiedUtc,
                     ETag, CTag, PermissionsHash, IndexFingerprint, ChunkCount, ScanId, IndexedAtUtc)
                VALUES
                    (@driveId, @itemId, @fileName, @parentPath, @webUrl, @mimeType, @sizeBytes, @lastModifiedUtc,
                     @eTag, @cTag, @permissionsHash, @indexFingerprint, @chunkCount, @scanId, @indexedAtUtc);
            END
            """);
        AddKeyParameters(command, record.DriveId, record.ItemId);
        command.Parameters.Add("@fileName", SqlDbType.NVarChar, 400).Value = Truncate(record.Name, 400);
        command.Parameters.Add("@parentPath", SqlDbType.NVarChar, 1000).Value = OrNull(Truncate(record.ParentPath, 1000));
        command.Parameters.Add("@webUrl", SqlDbType.NVarChar, 2000).Value = OrNull(Truncate(record.WebUrl, 2000));
        command.Parameters.Add("@mimeType", SqlDbType.NVarChar, 200).Value = OrNull(Truncate(record.MimeType, 200));
        command.Parameters.Add("@sizeBytes", SqlDbType.BigInt).Value = OrNull(record.Size);
        command.Parameters.Add("@lastModifiedUtc", SqlDbType.DateTimeOffset).Value = OrNull(record.LastModifiedUtc);
        command.Parameters.Add("@eTag", SqlDbType.NVarChar, 200).Value = OrNull(Truncate(record.ETag, 200));
        command.Parameters.Add("@cTag", SqlDbType.NVarChar, 200).Value = OrNull(Truncate(record.CTag, 200));
        command.Parameters.Add("@permissionsHash", SqlDbType.Char, 44).Value = record.PermissionsHash;
        command.Parameters.Add("@indexFingerprint", SqlDbType.NVarChar, 200).Value = Truncate(record.IndexFingerprint, 200)!;
        command.Parameters.Add("@chunkCount", SqlDbType.Int).Value = record.ChunkCount;
        command.Parameters.Add("@scanId", SqlDbType.UniqueIdentifier).Value = record.ScanId;
        command.Parameters.Add("@indexedAtUtc", SqlDbType.DateTimeOffset).Value = record.IndexedAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSeenAsync(string driveId, string itemId, Guid scanId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"UPDATE {_table.Name} SET ScanId = @scanId WHERE DriveId = @driveId AND ItemId = @itemId;");
        AddKeyParameters(command, driveId, itemId);
        command.Parameters.Add("@scanId", SqlDbType.UniqueIdentifier).Value = scanId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListItemsOutsideScanAsync(string driveId, Guid scanId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"SELECT TOP (@limit) ItemId FROM {_table.Name} WHERE DriveId = @driveId AND ScanId <> @scanId;");
        command.Parameters.Add("@driveId", SqlDbType.NVarChar, IdentifierLength).Value = driveId;
        command.Parameters.Add("@scanId", SqlDbType.UniqueIdentifier).Value = scanId;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;

        var itemIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            itemIds.Add(reader.GetString(0));
        }
        return itemIds;
    }

    public async Task DeleteAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        await using var connection = await _table.OpenAsync(cancellationToken);
        await using var command = _table.CreateCommand(connection, $"DELETE FROM {_table.Name} WHERE DriveId = @driveId AND ItemId = @itemId;");
        AddKeyParameters(command, driveId, itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddKeyParameters(SqlCommand command, string driveId, string itemId)
    {
        command.Parameters.Add("@driveId", SqlDbType.NVarChar, IdentifierLength).Value = driveId;
        command.Parameters.Add("@itemId", SqlDbType.NVarChar, IdentifierLength).Value = itemId;
    }

    private static string? ReadString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T? ReadNullable<T>(SqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static object OrNull(object? value) => value ?? DBNull.Value;

    /// <summary>
    /// Keeps an over-long SharePoint value from failing the write. Only descriptive columns are truncated;
    /// the key and change-detection columns are never shortened.
    /// </summary>
    private static string? Truncate(string? value, int length) =>
        value is not null && value.Length > length ? value[..length] : value;
}
