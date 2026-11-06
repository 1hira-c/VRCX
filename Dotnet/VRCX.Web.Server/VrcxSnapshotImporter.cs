using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace VRCX.Web.Server;

/// <summary>
/// Location and resource limits for a one-time import from a stopped desktop VRCX database.
/// The staging directory must be separate from the runtime database directory.
/// </summary>
public sealed record VrcxSnapshotImportOptions(
    string RuntimeDatabasePath,
    string StagingDirectory,
    int MaximumTables = 256,
    long MaximumRows = 20_000_000);

/// <summary>
/// An explicitly staged SQLite snapshot. Companion paths, when supplied, must be exactly
/// <c>DatabasePath + "-wal"</c> and <c>DatabasePath + "-shm"</c>.
/// </summary>
public sealed record VrcxSnapshotFiles(
    string DatabasePath,
    string? WalPath = null,
    string? SharedMemoryPath = null);

public sealed record VrcxSnapshotImportResult(
    string SnapshotId,
    bool AlreadyImported,
    int ImportedTableCount,
    long ImportedRowCount,
    IReadOnlyDictionary<string, long> ImportedRowsByTable);

public class VrcxSnapshotImportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class VrcxSnapshotSetupClosedException(string message) : VrcxSnapshotImportException(message);

public interface IVrcxSnapshotImporter
{
    Task<VrcxSnapshotImportResult> ImportAsync(
        VrcxSnapshotFiles files,
        CancellationToken cancellationToken = default);

    void CleanupStagingFiles(VrcxSnapshotFiles files);
}

/// <summary>
/// Imports a stopped desktop VRCX SQLite snapshot into the original VRCX schema.
///
/// This service deliberately has no generic SQL or table-name API. It copies only known VRCX
/// domain tables/columns and a small set of non-secret UI configuration keys. It never reads the
/// cookies, credentials, server-auth, proxy, API-key, token, or secret settings from the source.
/// </summary>
public sealed class VrcxSnapshotImporter : IVrcxSnapshotImporter
{
    private const string ImportManifestTable = "vrcx_snapshot_imports";
    private static readonly Regex UserTablePattern = new(
        "^usr[0-9a-f]{32}_(?<suffix>[a-z0-9_]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, TableRule> GlobalRules = CreateGlobalRules();
    private static readonly IReadOnlyDictionary<string, TableRule> UserRules = CreateUserRules();

    private const string LastUserLoggedInConfigKey = "config:lastuserloggedin";

    // Keys are transformed to lower case by VRCX's ConfigRepository. This list intentionally
    // contains display/layout/filter preferences plus the non-secret user ID needed to associate
    // imported per-user tables with their owner. It excludes savedCredentials, primary-password
    // state, API keys/endpoints, proxy settings, launch paths, and all session material.
    private static readonly HashSet<string> SafeConfigKeys = new(StringComparer.Ordinal)
    {
        "config:vrcx_accessiblestatusindicators",
        "config:vrcx_applanguage",
        "config:vrcx_cjkfontpack",
        "config:vrcx_compacttablemode",
        "config:vrcx_customfontfamily",
        "config:vrcx_dashboardconfigs",
        "config:vrcx_datatablestriped",
        "config:vrcx_displayvrcprofilebackgrounds",
        "config:vrcx_displayvrcprofilecosmetics",
        "config:vrcx_displayvrcprofilethemes",
        "config:vrcx_dthour12",
        "config:vrcx_dtisoformat",
        "config:vrcx_feedtablefilters",
        "config:vrcx_feedtablevipfilter",
        "config:vrcx_fontfamily",
        "config:vrcx_friendlogtablefilters",
        "config:vrcx_gamelogtablefilters",
        "config:vrcx_gamelogtablevipfilter",
        "config:vrcx_gamelogviewmode",
        "config:vrcx_hidedevicesfromfeed",
        "config:vrcx_hidefriendsinsameinstance",
        "config:vrcx_hidenicknames",
        "config:vrcx_hideprivatefromfeed",
        "config:vrcx_hideunfriends",
        "config:vrcx_hideuptimefromfeed",
        "config:vrcx_hideusermemos",
        "config:vrcx_hideusernotes",
        "config:vrcx_instanceactivitybarwidth",
        "config:vrcx_instanceactivitydetailvisible",
        "config:vrcx_instanceactivitynofriendinstancevisible",
        "config:vrcx_instanceactivitysoloinstancevisible",
        LastUserLoggedInConfigKey,
        "config:vrcx_instanceuserssortalphabetical",
        "config:vrcx_isagegatedinstancesvisible",
        "config:vrcx_isclosedinstancesvisible",
        "config:vrcx_lastdarktheme",
        "config:vrcx_localfavoritefriendsgroups",
        "config:vrcx_maxtablesize_v2",
        "config:vrcx_minimalfeed",
        "config:vrcx_naviscollapsed",
        "config:vrcx_navpanelwidth",
        "config:vrcx_notificationicondot",
        "config:vrcx_notificationtablefilters",
        "config:vrcx_profilebackgroundopacity",
        "config:vrcx_randomusercolours",
        "config:vrcx_sameinstanceabovefavorites",
        "config:vrcx_searchlimit",
        "config:vrcx_showinstanceidinlocation",
        "config:vrcx_shownewdashboardbutton",
        "config:vrcx_sidebarcosmetics",
        "config:vrcx_sidebardividebyfriendgroup",
        "config:vrcx_sidebarfavoritegrouporder",
        "config:vrcx_sidebarfavoritegroups",
        "config:vrcx_sidebargroupbyinstance",
        "config:vrcx_sidebarsortmethods",
        "config:vrcx_sortfavorites",
        "config:vrcx_tabledensity",
        "config:vrcx_tablepagesize",
        "config:vrcx_tablepagesizes",
        "config:vrcx_thememode",
        "config:vrcx_weekstartson",
        "config:sharedfeedfilters",
        "config:orderfriendgroup0",
        "config:orderfriendgroupgps",
        "config:orderfriendgroupprivate",
        "config:orderfriendsgroupstatus"
    };

    private readonly VrcxSnapshotImportOptions options;
    private readonly string runtimeDatabasePath;
    private readonly string stagingDirectory;

    public VrcxSnapshotImporter(VrcxSnapshotImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumTables is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumTables is outside the supported range.");
        }
        if (options.MaximumRows is < 1 or > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumRows is outside the supported range.");
        }

        this.options = options;
        runtimeDatabasePath = Path.GetFullPath(options.RuntimeDatabasePath);
        stagingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StagingDirectory));

        if (string.Equals(runtimeDatabasePath, stagingDirectory, PathComparison) ||
            IsDirectChild(runtimeDatabasePath, stagingDirectory))
        {
            throw new ArgumentException("The runtime database must not be inside the snapshot staging directory.", nameof(options));
        }
    }

    /// <summary>
    /// Imports a validated snapshot. Call <see cref="CleanupStagingFiles"/> in a finally block, or
    /// use <see cref="ImportAndCleanupAsync"/>, so uploaded database/WAL files never remain staged.
    /// </summary>
    public async Task<VrcxSnapshotImportResult> ImportAsync(
        VrcxSnapshotFiles files,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ValidateSnapshotFiles(files);
        if (string.Equals(snapshot.DatabasePath, runtimeDatabasePath, PathComparison))
        {
            throw new VrcxSnapshotImportException("The staged snapshot and runtime database must be different files.");
        }

        var initialStamp = SnapshotStamp.Capture(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeDatabasePath)!);

        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshot.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

        try
        {
            await source.OpenAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new VrcxSnapshotImportException("The staged file is not a readable SQLite snapshot.", exception);
        }

        await ExecuteNonQueryAsync(source, "PRAGMA query_only = ON;", cancellationToken);
        await ExecuteNonQueryAsync(source, "BEGIN;", cancellationToken);
        try
        {
            await ValidateIntegrityAsync(source, "snapshot", cancellationToken);
            var tables = await DiscoverSourceTablesAsync(source, cancellationToken);
            var snapshotId = await ComputeSnapshotIdAsync(source, tables, cancellationToken);

            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = runtimeDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            await target.OpenAsync(cancellationToken);
            await ExecuteNonQueryAsync(target, "PRAGMA busy_timeout = 5000;", cancellationToken);
            await ValidateIntegrityAsync(target, "runtime database", cancellationToken);

            await using var transaction = target.BeginTransaction(deferred: false);
            try
            {
                await EnsureManifestTableAsync(target, transaction, cancellationToken);
                if (await IsAlreadyImportedAsync(target, transaction, snapshotId, cancellationToken))
                {
                    transaction.Commit();
                    return new VrcxSnapshotImportResult(
                        snapshotId,
                        true,
                        0,
                        0,
                        new Dictionary<string, long>(StringComparer.Ordinal));
                }

                await EnsureTargetHasNoDomainRowsAsync(target, transaction, cancellationToken);

                var importedRows = new Dictionary<string, long>(StringComparer.Ordinal);
                long totalRows = 0;
                foreach (var table in tables)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await EnsureTargetTableAsync(target, transaction, table, cancellationToken);
                    var count = await CopyTableAsync(source, target, transaction, table, cancellationToken);
                    totalRows = checked(totalRows + count);
                    if (totalRows > options.MaximumRows)
                    {
                        throw new VrcxSnapshotImportException("The snapshot exceeds the configured row limit.");
                    }
                    importedRows.Add(table.Name, count);
                }

                if (!initialStamp.MatchesCurrent(snapshot))
                {
                    throw new VrcxSnapshotImportException(
                        "The staged snapshot changed during import. Use a stopped VRCX SQLite/WAL snapshot.");
                }

                await InsertManifestAsync(
                    target,
                    transaction,
                    snapshotId,
                    tables.Count,
                    totalRows,
                    cancellationToken);
                transaction.Commit();
                return new VrcxSnapshotImportResult(
                    snapshotId,
                    false,
                    tables.Count,
                    totalRows,
                    importedRows);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (VrcxSnapshotImportException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new VrcxSnapshotImportException("The VRCX snapshot import failed and was rolled back.", exception);
        }
        finally
        {
            try
            {
                await ExecuteNonQueryAsync(source, "ROLLBACK;", CancellationToken.None);
            }
            catch (SqliteException)
            {
                // The connection may already have aborted its read transaction after a fatal error.
            }
        }
    }

    /// <summary>
    /// Production-safe entry point: cleanup is attempted after both success and failure.
    /// </summary>
    public async Task<VrcxSnapshotImportResult> ImportAndCleanupAsync(
        VrcxSnapshotFiles files,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ImportAsync(files, cancellationToken);
        }
        finally
        {
            CleanupStagingFiles(files);
        }
    }

    /// <summary>
    /// Deletes only the validated snapshot basename and its exact SQLite companion names from the
    /// configured staging directory. No caller-supplied arbitrary companion path is deleted.
    /// </summary>
    public void CleanupStagingFiles(VrcxSnapshotFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var databasePath = GetSafeStagingDatabasePath(files.DatabasePath);
        foreach (var path in new[] { databasePath + "-wal", databasePath + "-shm", databasePath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private ValidatedSnapshot ValidateSnapshotFiles(VrcxSnapshotFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var databasePath = GetSafeStagingDatabasePath(files.DatabasePath);
        if (!File.Exists(databasePath) || IsSymbolicLink(databasePath))
        {
            throw new VrcxSnapshotImportException("The staged SQLite main database is missing or is a symbolic link.");
        }

        var walPath = ValidateOptionalCompanion(files.WalPath, databasePath + "-wal", "WAL");
        var shmPath = ValidateOptionalCompanion(files.SharedMemoryPath, databasePath + "-shm", "shared-memory");

        if (File.Exists(databasePath + "-wal") && walPath is null)
        {
            throw new VrcxSnapshotImportException("A staged WAL exists but was not explicitly included in the snapshot.");
        }
        if (File.Exists(databasePath + "-shm") && shmPath is null)
        {
            throw new VrcxSnapshotImportException("A staged shared-memory file exists but was not explicitly included in the snapshot.");
        }

        return new ValidatedSnapshot(databasePath, walPath, shmPath);
    }

    private string? ValidateOptionalCompanion(string? providedPath, string expectedPath, string label)
    {
        if (providedPath is null)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(providedPath);
        if (!string.Equals(fullPath, expectedPath, PathComparison) ||
            !IsDirectChild(fullPath, stagingDirectory) ||
            !File.Exists(fullPath) ||
            IsSymbolicLink(fullPath))
        {
            throw new VrcxSnapshotImportException($"The staged {label} path is missing or does not match the SQLite main database.");
        }
        return fullPath;
    }

    private string GetSafeStagingDatabasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new VrcxSnapshotImportException("A staged SQLite main database path is required.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!IsDirectChild(fullPath, stagingDirectory) ||
            fullPath.EndsWith("-wal", StringComparison.Ordinal) ||
            fullPath.EndsWith("-shm", StringComparison.Ordinal))
        {
            throw new VrcxSnapshotImportException("The SQLite snapshot must be a direct file in the configured staging directory.");
        }
        return fullPath;
    }

    private async Task<IReadOnlyList<SourceTable>> DiscoverSourceTablesAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        var names = new List<(string Name, string Sql)>();
        await using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT name, COALESCE(sql, '') FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (names.Count > options.MaximumTables)
        {
            throw new VrcxSnapshotImportException("The snapshot exceeds the configured table limit.");
        }

        var tables = new List<SourceTable>();
        foreach (var (name, createSql) in names)
        {
            var rule = FindRule(name);
            if (rule is null)
            {
                continue;
            }
            tables.Add(await ReadSourceTableAsync(source, name, createSql, rule, cancellationToken));
        }

        if (!tables.Any(table => !table.IsConfigTable))
        {
            throw new VrcxSnapshotImportException("The snapshot does not contain a supported VRCX domain schema.");
        }
        return tables;
    }

    private static async Task<SourceTable> ReadSourceTableAsync(
        SqliteConnection source,
        string tableName,
        string createSql,
        TableRule rule,
        CancellationToken cancellationToken)
    {
        var columns = new List<ColumnSchema>();
        await using (var command = source.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                var hidden = reader.GetInt32(6);
                if (!rule.AllowedColumns.Contains(name) || hidden != 0)
                {
                    continue;
                }

                var declaredType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                ValidateDeclaredType(declaredType, tableName, name);
                var defaultSql = reader.IsDBNull(4) ? null : reader.GetString(4);
                ValidateDefaultSql(defaultSql, tableName, name);
                columns.Add(new ColumnSchema(
                    name,
                    declaredType,
                    reader.GetInt32(3) != 0,
                    defaultSql,
                    reader.GetInt32(5)));
            }
        }

        var columnNames = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var missing = rule.RequiredColumns.Where(column => !columnNames.Contains(column)).ToArray();
        if (missing.Length != 0)
        {
            throw new VrcxSnapshotImportException(
                $"The VRCX table '{tableName}' is missing required schema columns: {string.Join(", ", missing)}.");
        }

        var primaryKey = columns.Where(column => column.PrimaryKeyOrder > 0)
            .OrderBy(column => column.PrimaryKeyOrder)
            .Select(column => column.Name)
            .ToArray();
        if (!primaryKey.SequenceEqual(rule.IdentityColumns, StringComparer.Ordinal))
        {
            throw new VrcxSnapshotImportException($"The VRCX table '{tableName}' has an unexpected primary key.");
        }

        var indexes = await ReadIndexesAsync(source, tableName, columnNames, cancellationToken);
        var autoIncrement = primaryKey.Length == 1 &&
                            createSql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase);
        return new SourceTable(
            tableName,
            rule.IsConfigTable,
            columns,
            primaryKey,
            indexes,
            autoIncrement);
    }

    private static async Task<IReadOnlyList<IndexSchema>> ReadIndexesAsync(
        SqliteConnection source,
        string tableName,
        IReadOnlySet<string> selectedColumns,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Name, bool Unique, string Origin, bool Partial)>();
        await using (var command = source.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(1), reader.GetInt32(2) != 0, reader.GetString(3), reader.GetInt32(4) != 0));
            }
        }

        var indexes = new List<IndexSchema>();
        foreach (var candidate in candidates)
        {
            if (candidate.Origin == "pk" || candidate.Partial)
            {
                continue;
            }

            var columns = new List<(int Sequence, string Name)>();
            var hasExpression = false;
            await using (var command = source.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_info({QuoteIdentifier(candidate.Name)});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.IsDBNull(2))
                    {
                        hasExpression = true;
                        continue;
                    }
                    columns.Add((reader.GetInt32(0), reader.GetString(2)));
                }
            }

            var orderedColumns = columns.OrderBy(column => column.Sequence).Select(column => column.Name).ToArray();
            if (hasExpression || orderedColumns.Length == 0 || orderedColumns.Any(column => !selectedColumns.Contains(column)))
            {
                continue;
            }
            indexes.Add(new IndexSchema(candidate.Name, candidate.Unique, candidate.Origin == "u", orderedColumns));
        }
        return indexes;
    }

    private static async Task<string> ComputeSnapshotIdAsync(
        SqliteConnection source,
        IReadOnlyList<SourceTable> tables,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            AppendHashString(hash, table.Name);
            foreach (var column in table.Columns)
            {
                AppendHashString(hash, column.Name);
                AppendHashString(hash, column.DeclaredType);
            }

            await using var command = CreateSourceSelect(source, table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                for (var index = 0; index < table.Columns.Count; index++)
                {
                    AppendHashValue(hash, reader.GetValue(index));
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task EnsureTargetTableAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        SourceTable table,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(target, transaction, table.Name, cancellationToken))
        {
            var targetColumns = await ReadTargetColumnsAsync(target, transaction, table.Name, cancellationToken);
            foreach (var sourceColumn in table.Columns)
            {
                if (!targetColumns.TryGetValue(sourceColumn.Name, out var targetType) ||
                    !string.Equals(GetTypeAffinity(sourceColumn.DeclaredType), GetTypeAffinity(targetType), StringComparison.Ordinal))
                {
                    throw new VrcxSnapshotImportException(
                        $"The runtime table '{table.Name}' is incompatible with snapshot column '{sourceColumn.Name}'.");
                }
            }
            return;
        }

        await using (var command = target.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = BuildCreateTableSql(table);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var index in table.Indexes.Where(index => !index.IsTableConstraint))
        {
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BuildCreateIndexSql(table.Name, index);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<long> CopyTableAsync(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        SourceTable table,
        CancellationToken cancellationToken)
    {
        await using var select = CreateSourceSelect(source, table);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        await using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        var columnSql = string.Join(", ", table.Columns.Select(column => QuoteIdentifier(column.Name)));
        var parameterSql = string.Join(", ", table.Columns.Select((_, index) => $"@value{index}"));
        insert.CommandText = table.IsConfigTable
            ? $"INSERT OR REPLACE INTO {QuoteIdentifier(table.Name)} ({columnSql}) VALUES ({parameterSql});"
            : $"INSERT INTO {QuoteIdentifier(table.Name)} ({columnSql}) VALUES ({parameterSql});";
        for (var index = 0; index < table.Columns.Count; index++)
        {
            insert.Parameters.Add(new SqliteParameter($"@value{index}", null));
        }

        long rows = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (table.IsConfigTable && !IsSafeConfigRow(table, reader))
            {
                continue;
            }
            for (var index = 0; index < table.Columns.Count; index++)
            {
                insert.Parameters[index].Value = reader.GetValue(index);
            }
            await insert.ExecuteNonQueryAsync(cancellationToken);
            rows = checked(rows + 1);
        }
        return rows;
    }

    private static bool IsSafeConfigRow(SourceTable table, SqliteDataReader reader)
    {
        var keyIndex = FindColumnIndex(table.Columns, "key");
        var valueIndex = FindColumnIndex(table.Columns, "value");
        if (keyIndex < 0 || valueIndex < 0 || reader.IsDBNull(keyIndex))
        {
            return false;
        }

        var key = reader.GetString(keyIndex).ToLowerInvariant();
        if (!string.Equals(key, LastUserLoggedInConfigKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (reader.IsDBNull(valueIndex) || reader.GetValue(valueIndex) is not string value)
        {
            return false;
        }
        return value is { Length: > 4 and <= 128 } &&
            value.StartsWith("usr_", StringComparison.Ordinal) &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static int FindColumnIndex(IReadOnlyList<ColumnSchema> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index].Name, name, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static SqliteCommand CreateSourceSelect(SqliteConnection source, SourceTable table)
    {
        var command = source.CreateCommand();
        var columns = string.Join(", ", table.Columns.Select(column => QuoteIdentifier(column.Name)));
        var orderBy = string.Join(", ", table.PrimaryKey.Select(QuoteIdentifier));
        command.CommandText = $"SELECT {columns} FROM {QuoteIdentifier(table.Name)}";
        if (table.IsConfigTable)
        {
            var parameters = SafeConfigKeys.Select((_, index) => $"@key{index}").ToArray();
            command.CommandText += $" WHERE lower({QuoteIdentifier("key")}) IN ({string.Join(", ", parameters)})";
            var index = 0;
            foreach (var key in SafeConfigKeys.Order(StringComparer.Ordinal))
            {
                command.Parameters.AddWithValue($"@key{index++}", key);
            }
        }
        command.CommandText += $" ORDER BY {orderBy};";
        return command;
    }

    private static async Task EnsureTargetHasNoDomainRowsAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tableNames = new List<string>();
        await using (var command = target.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        foreach (var tableName in tableNames)
        {
            var rule = FindRule(tableName);
            if (rule is null || rule.IsConfigTable)
            {
                continue;
            }
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {QuoteIdentifier(tableName)} LIMIT 1);";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                throw new VrcxSnapshotSetupClosedException(
                    "The runtime database already contains VRCX domain data. Import into a fresh server runtime database.");
            }
        }
    }

    private static async Task EnsureManifestTableAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {QuoteIdentifier(ImportManifestTable)} (
                snapshot_id TEXT PRIMARY KEY,
                imported_at TEXT NOT NULL,
                table_count INTEGER NOT NULL,
                row_count INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAlreadyImportedAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {QuoteIdentifier(ImportManifestTable)} WHERE snapshot_id = @id);";
        command.Parameters.AddWithValue("@id", snapshotId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task InsertManifestAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        string snapshotId,
        int tableCount,
        long rowCount,
        CancellationToken cancellationToken)
    {
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {QuoteIdentifier(ImportManifestTable)}
                (snapshot_id, imported_at, table_count, row_count)
            VALUES (@id, @importedAt, @tableCount, @rowCount);
            """;
        command.Parameters.AddWithValue("@id", snapshotId);
        command.Parameters.AddWithValue("@importedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@tableCount", tableCount);
        command.Parameters.AddWithValue("@rowCount", rowCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateIntegrityAsync(
        SqliteConnection connection,
        string label,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var sawResult = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                sawResult = true;
                if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new VrcxSnapshotImportException($"The {label} failed SQLite integrity validation.");
                }
            }
            if (!sawResult)
            {
                throw new VrcxSnapshotImportException($"The {label} did not return an SQLite integrity result.");
            }
        }
        catch (SqliteException exception)
        {
            throw new VrcxSnapshotImportException($"The {label} failed SQLite integrity validation.", exception);
        }
    }

    private static async Task<Dictionary<string, string>> ReadTargetColumnsAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt32(6) == 0)
            {
                columns.Add(reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
            }
        }
        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection target,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = target.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = @name);";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    private static string BuildCreateTableSql(SourceTable table)
    {
        var definitions = new List<string>();
        var singlePrimaryKey = table.PrimaryKey.Count == 1 ? table.PrimaryKey[0] : null;
        foreach (var column in table.Columns)
        {
            var definition = new StringBuilder(QuoteIdentifier(column.Name));
            if (column.DeclaredType.Length != 0)
            {
                definition.Append(' ').Append(column.DeclaredType);
            }
            if (column.NotNull)
            {
                definition.Append(" NOT NULL");
            }
            if (column.DefaultSql is not null)
            {
                definition.Append(" DEFAULT ").Append(column.DefaultSql);
            }
            if (string.Equals(singlePrimaryKey, column.Name, StringComparison.Ordinal))
            {
                definition.Append(" PRIMARY KEY");
                if (table.AutoIncrement)
                {
                    definition.Append(" AUTOINCREMENT");
                }
            }
            definitions.Add(definition.ToString());
        }
        if (table.PrimaryKey.Count > 1)
        {
            definitions.Add($"PRIMARY KEY ({string.Join(", ", table.PrimaryKey.Select(QuoteIdentifier))})");
        }
        foreach (var index in table.Indexes.Where(index => index.IsTableConstraint && index.Unique))
        {
            definitions.Add($"UNIQUE ({string.Join(", ", index.Columns.Select(QuoteIdentifier))})");
        }
        return $"CREATE TABLE {QuoteIdentifier(table.Name)} ({string.Join(", ", definitions)});";
    }

    private static string BuildCreateIndexSql(string tableName, IndexSchema index)
    {
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        return $"CREATE {unique}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} " +
               $"({string.Join(", ", index.Columns.Select(QuoteIdentifier))});";
    }

    private static void ValidateDeclaredType(string declaredType, string tableName, string columnName)
    {
        if (declaredType.Length > 64 ||
            (declaredType.Length != 0 && !Regex.IsMatch(
                declaredType,
                "^[A-Za-z]+(?:\\s+[A-Za-z]+)*(?:\\s*\\(\\s*\\d+(?:\\s*,\\s*\\d+)?\\s*\\))?$",
                RegexOptions.CultureInvariant)))
        {
            throw new VrcxSnapshotImportException(
                $"The VRCX table '{tableName}' has an invalid declared type for column '{columnName}'.");
        }
    }

    private static void ValidateDefaultSql(string? defaultSql, string tableName, string columnName)
    {
        if (defaultSql is null)
        {
            return;
        }
        if (defaultSql.Length > 1_024 || !Regex.IsMatch(
                defaultSql,
                "^(?:NULL|[-+]?\\d+(?:\\.\\d+)?|'(?:''|[^'])*'|CURRENT_(?:TIME|DATE|TIMESTAMP))$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            throw new VrcxSnapshotImportException(
                $"The VRCX table '{tableName}' has an unsafe default for column '{columnName}'.");
        }
    }

    private static string GetTypeAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0) return "BLOB";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        return "NUMERIC";
    }

    private static TableRule? FindRule(string tableName)
    {
        if (GlobalRules.TryGetValue(tableName, out var globalRule))
        {
            return globalRule;
        }
        var match = UserTablePattern.Match(tableName);
        return match.Success && UserRules.TryGetValue(match.Groups["suffix"].Value, out var userRule)
            ? userRule
            : null;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendHashValue(IncrementalHash hash, object value)
    {
        switch (value)
        {
            case DBNull:
                hash.AppendData([0]);
                break;
            case long integer:
                hash.AppendData([1]);
                hash.AppendData(BitConverter.GetBytes(integer));
                break;
            case double real:
                hash.AppendData([2]);
                hash.AppendData(BitConverter.GetBytes(real));
                break;
            case string text:
                hash.AppendData([3]);
                AppendHashString(hash, text);
                break;
            case byte[] blob:
                hash.AppendData([4]);
                hash.AppendData(BitConverter.GetBytes(blob.Length));
                hash.AppendData(blob);
                break;
            default:
                throw new VrcxSnapshotImportException("The snapshot contains an unsupported SQLite value type.");
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsSymbolicLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static bool IsDirectChild(string path, string directory) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
            directory,
            PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static IReadOnlyDictionary<string, TableRule> CreateGlobalRules() =>
        new Dictionary<string, TableRule>(StringComparer.Ordinal)
        {
            ["configs"] = Rule(true, ["key", "value"], ["key", "value"], ["key"]),
            ["gamelog_location"] = Rule(false, ["id", "created_at", "location", "world_id", "world_name", "time", "group_name"], ["id", "created_at", "location"], ["id"]),
            ["gamelog_join_leave"] = Rule(false, ["id", "created_at", "type", "display_name", "location", "user_id", "time"], ["id", "created_at", "type", "display_name"], ["id"]),
            ["gamelog_portal_spawn"] = Rule(false, ["id", "created_at", "display_name", "location", "user_id", "instance_id", "world_name"], ["id", "created_at", "display_name"], ["id"]),
            ["gamelog_video_play"] = Rule(false, ["id", "created_at", "video_url", "video_name", "video_id", "location", "display_name", "user_id"], ["id", "created_at", "video_url"], ["id"]),
            ["gamelog_resource_load"] = Rule(false, ["id", "created_at", "resource_url", "resource_type", "location"], ["id", "created_at", "resource_url"], ["id"]),
            ["gamelog_event"] = Rule(false, ["id", "created_at", "data"], ["id", "created_at", "data"], ["id"]),
            ["gamelog_external"] = Rule(false, ["id", "created_at", "message", "display_name", "user_id", "location"], ["id", "created_at", "message"], ["id"]),
            ["cache_avatar"] = Rule(false, ["id", "added_at", "author_id", "author_name", "created_at", "description", "image_url", "name", "release_status", "thumbnail_image_url", "updated_at", "version"], ["id", "added_at", "name"], ["id"]),
            ["cache_world"] = Rule(false, ["id", "added_at", "author_id", "author_name", "created_at", "description", "image_url", "name", "release_status", "thumbnail_image_url", "updated_at", "version"], ["id", "added_at", "name"], ["id"]),
            ["favorite_world"] = Rule(false, ["id", "created_at", "world_id", "group_name"], ["id", "created_at", "world_id", "group_name"], ["id"]),
            ["favorite_print"] = Rule(false, ["id", "print_id", "created_at"], ["id", "print_id", "created_at"], ["id"]),
            ["favorite_avatar"] = Rule(false, ["id", "created_at", "avatar_id", "group_name"], ["id", "created_at", "avatar_id", "group_name"], ["id"]),
            ["favorite_friend"] = Rule(false, ["id", "created_at", "user_id", "group_name"], ["id", "created_at", "user_id", "group_name"], ["id"]),
            ["memos"] = Rule(false, ["user_id", "edited_at", "memo"], ["user_id", "edited_at", "memo"], ["user_id"]),
            ["world_memos"] = Rule(false, ["world_id", "edited_at", "memo"], ["world_id", "edited_at", "memo"], ["world_id"]),
            ["avatar_memos"] = Rule(false, ["avatar_id", "edited_at", "memo"], ["avatar_id", "edited_at", "memo"], ["avatar_id"]),
            ["avatar_tags"] = Rule(false, ["avatar_id", "tag", "color"], ["avatar_id", "tag"], ["avatar_id", "tag"])
        };

    private static IReadOnlyDictionary<string, TableRule> CreateUserRules() =>
        new Dictionary<string, TableRule>(StringComparer.Ordinal)
        {
            ["feed_gps"] = Rule(false, ["id", "created_at", "user_id", "display_name", "location", "world_name", "previous_location", "time", "group_name"], ["id", "created_at", "user_id", "display_name", "location"], ["id"]),
            ["feed_status"] = Rule(false, ["id", "created_at", "user_id", "display_name", "status", "status_description", "previous_status", "previous_status_description"], ["id", "created_at", "user_id", "display_name", "status"], ["id"]),
            ["feed_bio"] = Rule(false, ["id", "created_at", "user_id", "display_name", "bio", "previous_bio", "bio_links", "previous_bio_links"], ["id", "created_at", "user_id", "display_name", "bio"], ["id"]),
            ["feed_avatar"] = Rule(false, ["id", "created_at", "user_id", "display_name", "owner_id", "avatar_name", "current_avatar_image_url", "current_avatar_thumbnail_image_url", "previous_current_avatar_image_url", "previous_current_avatar_thumbnail_image_url"], ["id", "created_at", "user_id", "display_name", "avatar_name"], ["id"]),
            ["feed_online_offline"] = Rule(false, ["id", "created_at", "user_id", "display_name", "type", "location", "world_name", "time", "group_name"], ["id", "created_at", "user_id", "display_name", "type"], ["id"]),
            ["activity_sync_state_v2"] = Rule(false, ["user_id", "updated_at", "is_self", "source_last_created_at", "pending_session_start_at", "cached_range_days"], ["user_id", "updated_at", "is_self", "source_last_created_at", "cached_range_days"], ["user_id"]),
            ["activity_sessions_v2"] = Rule(false, ["session_id", "user_id", "start_at", "end_at", "is_open_tail", "source_revision"], ["session_id", "user_id", "start_at", "end_at"], ["session_id"]),
            ["activity_bucket_cache_v2"] = Rule(false, ["user_id", "target_user_id", "range_days", "view_kind", "exclude_key", "bucket_version", "raw_buckets_json", "normalized_buckets_json", "built_from_cursor", "summary_json", "built_at"], ["user_id", "target_user_id", "range_days", "view_kind", "exclude_key", "raw_buckets_json", "normalized_buckets_json", "summary_json"], ["user_id", "target_user_id", "range_days", "view_kind", "exclude_key"]),
            ["friend_log_current"] = Rule(false, ["user_id", "display_name", "trust_level", "friend_number"], ["user_id", "display_name"], ["user_id"]),
            ["friend_log_history"] = Rule(false, ["id", "created_at", "type", "user_id", "display_name", "previous_display_name", "trust_level", "previous_trust_level", "friend_number"], ["id", "created_at", "type", "user_id", "display_name"], ["id"]),
            ["notifications"] = Rule(false, ["id", "created_at", "type", "sender_user_id", "sender_username", "receiver_user_id", "message", "world_id", "world_name", "image_url", "invite_message", "request_message", "response_message", "expired"], ["id", "created_at", "type"], ["id"]),
            ["notifications_v2"] = Rule(false, ["id", "created_at", "updated_at", "expires_at", "type", "link", "link_text", "message", "title", "image_url", "seen", "sender_user_id", "sender_username", "data", "responses", "details"], ["id", "created_at", "type"], ["id"]),
            ["moderation"] = Rule(false, ["user_id", "updated_at", "display_name", "block", "mute"], ["user_id", "updated_at", "display_name"], ["user_id"]),
            ["avatar_history"] = Rule(false, ["avatar_id", "created_at", "time"], ["avatar_id", "created_at"], ["avatar_id"]),
            ["notes"] = Rule(false, ["user_id", "display_name", "note", "created_at"], ["user_id", "display_name", "note"], ["user_id"]),
            ["mutual_graph_friends"] = Rule(false, ["friend_id"], ["friend_id"], ["friend_id"]),
            ["mutual_graph_links"] = Rule(false, ["friend_id", "mutual_id"], ["friend_id", "mutual_id"], ["friend_id", "mutual_id"]),
            ["mutual_graph_meta"] = Rule(false, ["friend_id", "last_fetched_at", "opted_out"], ["friend_id", "last_fetched_at"], ["friend_id"]),
            ["user_profile"] = Rule(false, ["user_id", "bio", "bio_links", "updated_at"], ["user_id", "bio", "updated_at"], ["user_id"])
        };

    private static TableRule Rule(bool config, string[] allowed, string[] required, string[] identity) =>
        new(
            config,
            allowed.ToHashSet(StringComparer.Ordinal),
            required.ToHashSet(StringComparer.Ordinal),
            identity);

    private sealed record TableRule(
        bool IsConfigTable,
        IReadOnlySet<string> AllowedColumns,
        IReadOnlySet<string> RequiredColumns,
        IReadOnlyList<string> IdentityColumns);

    private sealed record ColumnSchema(
        string Name,
        string DeclaredType,
        bool NotNull,
        string? DefaultSql,
        int PrimaryKeyOrder);

    private sealed record IndexSchema(
        string Name,
        bool Unique,
        bool IsTableConstraint,
        IReadOnlyList<string> Columns);

    private sealed record SourceTable(
        string Name,
        bool IsConfigTable,
        IReadOnlyList<ColumnSchema> Columns,
        IReadOnlyList<string> PrimaryKey,
        IReadOnlyList<IndexSchema> Indexes,
        bool AutoIncrement);

    private sealed record ValidatedSnapshot(string DatabasePath, string? WalPath, string? SharedMemoryPath);

    private sealed record FileStamp(string Path, long Length, DateTime LastWriteTimeUtc)
    {
        public static FileStamp Capture(string path)
        {
            var info = new FileInfo(path);
            return new FileStamp(path, info.Length, info.LastWriteTimeUtc);
        }

        public bool MatchesCurrent()
        {
            var info = new FileInfo(Path);
            return info.Exists && info.Length == Length && info.LastWriteTimeUtc == LastWriteTimeUtc;
        }
    }

    private sealed record SnapshotStamp(IReadOnlyList<FileStamp> Files)
    {
        public static SnapshotStamp Capture(ValidatedSnapshot snapshot)
        {
            var files = new List<FileStamp> { FileStamp.Capture(snapshot.DatabasePath) };
            if (snapshot.WalPath is not null)
            {
                files.Add(FileStamp.Capture(snapshot.WalPath));
            }
            return new SnapshotStamp(files);
        }

        public bool MatchesCurrent(ValidatedSnapshot _) => Files.All(file => file.MatchesCurrent());
    }
}
