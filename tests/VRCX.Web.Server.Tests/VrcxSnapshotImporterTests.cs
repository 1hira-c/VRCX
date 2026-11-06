using Microsoft.Data.Sqlite;
using VRCX.Web.Server;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed class VrcxSnapshotImporterTests
{
    private const string UserPrefix = "usr0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ImportsRowsThatExistOnlyInAnExplicitWalSnapshotAndCleansStaging()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = Path.Combine(workspace.Root, "desktop.sqlite3");
        var stagedPath = Path.Combine(workspace.StagingDirectory, "upload.sqlite3");

        await using (var writer = await OpenAsync(sourcePath))
        {
            await ExecuteAsync(writer, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0;");
            await ExecuteAsync(writer, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            await ExecuteAsync(
                writer,
                "INSERT INTO memos VALUES ('usr_aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-01-02T03:04:05.000Z', 'visible only through WAL');");

            Assert.True(File.Exists(sourcePath + "-wal"));
            Assert.True(new FileInfo(sourcePath + "-wal").Length > 0);
            File.Copy(sourcePath, stagedPath);
            File.Copy(sourcePath + "-wal", stagedPath + "-wal");
            if (File.Exists(sourcePath + "-shm"))
            {
                File.Copy(sourcePath + "-shm", stagedPath + "-shm");
            }
        }

        var files = new VrcxSnapshotFiles(
            stagedPath,
            stagedPath + "-wal",
            File.Exists(stagedPath + "-shm") ? stagedPath + "-shm" : null);
        var importer = workspace.CreateImporter();
        var result = await importer.ImportAndCleanupAsync(files);

        Assert.False(result.AlreadyImported);
        Assert.Equal(1, result.ImportedRowsByTable["memos"]);
        await using var target = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal("visible only through WAL", await ScalarStringAsync(target, "SELECT memo FROM memos;"));
        Assert.False(File.Exists(stagedPath));
        Assert.False(File.Exists(stagedPath + "-wal"));
        Assert.False(File.Exists(stagedPath + "-shm"));
    }

    [Fact]
    public async Task ReimportingTheSameLogicalSnapshotIsANoOp()
    {
        using var workspace = new TestWorkspace();
        var stagedPath = Path.Combine(workspace.StagingDirectory, "repeat.sqlite3");
        await using (var source = await OpenAsync(stagedPath))
        {
            await ExecuteAsync(source, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(source, "INSERT INTO memos VALUES ('usr_repeat', '2026-01-01', 'one row');");
        }

        var importer = workspace.CreateImporter();
        var files = new VrcxSnapshotFiles(stagedPath);
        var first = await importer.ImportAsync(files);
        var second = await importer.ImportAsync(files);

        Assert.False(first.AlreadyImported);
        Assert.True(second.AlreadyImported);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(0, second.ImportedRowCount);
        await using var target = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1L, await ScalarInt64Async(target, "SELECT COUNT(*) FROM memos;"));
        Assert.Equal(1L, await ScalarInt64Async(target, "SELECT COUNT(*) FROM vrcx_snapshot_imports;"));

        importer.CleanupStagingFiles(files);
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task RollsBackEveryTableAndCleansStagingWhenAnyInsertFails()
    {
        using var workspace = new TestWorkspace();
        var stagedPath = Path.Combine(workspace.StagingDirectory, "rollback.sqlite3");
        await using (var source = await OpenAsync(stagedPath))
        {
            await ExecuteAsync(source, "CREATE TABLE gamelog_event (id INTEGER PRIMARY KEY, created_at TEXT, data TEXT, UNIQUE(created_at, data));");
            await ExecuteAsync(source, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(source, "INSERT INTO gamelog_event VALUES (1, '2026-02-01', '{\"event\":\"first\"}');");
            await ExecuteAsync(source, "INSERT INTO memos VALUES ('usr_failure', '2026-02-01', 'must roll back');");
        }
        await using (var target = await OpenAsync(workspace.RuntimeDatabasePath))
        {
            await ExecuteAsync(target, "CREATE TABLE gamelog_event (id INTEGER PRIMARY KEY, created_at TEXT, data TEXT, UNIQUE(created_at, data));");
            await ExecuteAsync(target, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(target, "CREATE TRIGGER reject_memo BEFORE INSERT ON memos BEGIN SELECT RAISE(ABORT, 'forced test failure'); END;");
        }

        var importer = workspace.CreateImporter();
        await Assert.ThrowsAsync<VrcxSnapshotImportException>(
            () => importer.ImportAndCleanupAsync(new VrcxSnapshotFiles(stagedPath)));

        await using var check = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(0L, await ScalarInt64Async(check, "SELECT COUNT(*) FROM gamelog_event;"));
        Assert.Equal(0L, await ScalarInt64Async(check, "SELECT COUNT(*) FROM memos;"));
        Assert.Equal(0L, await ScalarInt64Async(
            check,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'vrcx_snapshot_imports';"));
        Assert.False(File.Exists(stagedPath));
    }

    [Fact]
    public async Task ExcludesCredentialsCookiesProxyTokensAndServerAuthData()
    {
        using var workspace = new TestWorkspace();
        var stagedPath = Path.Combine(workspace.StagingDirectory, "safe.sqlite3");
        await using (var source = await OpenAsync(stagedPath))
        {
            await ExecuteAsync(source, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(source, "INSERT INTO memos VALUES ('usr_safe', '2026-03-01', 'non-secret memo');");
            await ExecuteAsync(source, "CREATE TABLE configs (key TEXT PRIMARY KEY, value TEXT);");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:vrcx_applanguage', 'ja');");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:lastuserloggedin', 'usr_01234567-89ab-cdef-0123-456789abcdef');");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:savedcredentials', 'credential-secret');");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:vrcx_youtubeapikey', 'api-key-secret');");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:vrcx_translationapiendpoint', 'https://secret.invalid/token');");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:proxyserver', 'proxy-secret');");
            await ExecuteAsync(source, "CREATE TABLE cookies (key TEXT PRIMARY KEY, value TEXT);");
            await ExecuteAsync(source, "INSERT INTO cookies VALUES ('auth', 'cookie-secret');");
            await ExecuteAsync(source, "CREATE TABLE admin_credentials (id INTEGER PRIMARY KEY, password_hash TEXT);");
            await ExecuteAsync(source, "INSERT INTO admin_credentials VALUES (1, 'password-secret');");
            await ExecuteAsync(source, "CREATE TABLE auth_sessions (id TEXT PRIMARY KEY, token_hash TEXT);");
            await ExecuteAsync(source, "INSERT INTO auth_sessions VALUES ('session', 'token-secret');");
            await ExecuteAsync(source, "CREATE TABLE browser_tokens (id TEXT PRIMARY KEY, token TEXT);");
            await ExecuteAsync(source, "INSERT INTO browser_tokens VALUES ('one', 'browser-secret');");
        }

        var importer = workspace.CreateImporter();
        await importer.ImportAndCleanupAsync(new VrcxSnapshotFiles(stagedPath));

        await using var target = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(0L, await ScalarInt64Async(
            target,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('cookies', 'admin_credentials', 'auth_sessions', 'browser_tokens');"));
        Assert.Equal(2L, await ScalarInt64Async(target, "SELECT COUNT(*) FROM configs;"));
        Assert.Equal("ja", await ScalarStringAsync(target, "SELECT value FROM configs WHERE key = 'config:vrcx_applanguage';"));
        Assert.Equal(
            "usr_01234567-89ab-cdef-0123-456789abcdef",
            await ScalarStringAsync(target, "SELECT value FROM configs WHERE key = 'config:lastuserloggedin';"));
        Assert.Equal(0L, await ScalarInt64Async(
            target,
            "SELECT COUNT(*) FROM configs WHERE lower(key) LIKE '%credential%' OR lower(key) LIKE '%password%' OR lower(key) LIKE '%proxy%' OR lower(key) LIKE '%token%' OR lower(key) LIKE '%apikey%' OR lower(key) LIKE '%endpoint%';"));
    }

    [Fact]
    public async Task RejectsMalformedImportedCurrentUserMapping()
    {
        using var workspace = new TestWorkspace();
        var stagedPath = Path.Combine(workspace.StagingDirectory, "unsafe-user-map.sqlite3");
        await using (var source = await OpenAsync(stagedPath))
        {
            await ExecuteAsync(source, "CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);");
            await ExecuteAsync(source, "INSERT INTO memos VALUES ('usr_safe', '2026-03-01', 'non-secret memo');");
            await ExecuteAsync(source, "CREATE TABLE configs (key TEXT PRIMARY KEY, value TEXT);");
            await ExecuteAsync(source, "INSERT INTO configs VALUES ('config:lastuserloggedin', 'usr_bad''; DROP TABLE memos; --');");
        }

        var importer = workspace.CreateImporter();
        await importer.ImportAndCleanupAsync(new VrcxSnapshotFiles(stagedPath));

        await using var target = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(0L, await ScalarInt64Async(target, "SELECT COUNT(*) FROM configs;"));
        Assert.Equal("non-secret memo", await ScalarStringAsync(target, "SELECT memo FROM memos WHERE user_id = 'usr_safe';"));
    }

    [Fact]
    public async Task PreservesRepresentativeHistoryNotificationContextAndActivityShapes()
    {
        using var workspace = new TestWorkspace();
        var stagedPath = Path.Combine(workspace.StagingDirectory, "history.sqlite3");
        await using (var source = await OpenAsync(stagedPath))
        {
            await ExecuteAsync(source, $$"""
                CREATE TABLE {{UserPrefix}}_friend_log_current (
                    user_id TEXT PRIMARY KEY, display_name TEXT, trust_level TEXT, friend_number INTEGER);
                CREATE TABLE {{UserPrefix}}_friend_log_history (
                    id INTEGER PRIMARY KEY, created_at TEXT, type TEXT, user_id TEXT, display_name TEXT,
                    previous_display_name TEXT, trust_level TEXT, previous_trust_level TEXT, friend_number INTEGER);
                CREATE TABLE {{UserPrefix}}_feed_gps (
                    id INTEGER PRIMARY KEY, created_at TEXT, user_id TEXT, display_name TEXT, location TEXT,
                    world_name TEXT, previous_location TEXT, time INTEGER, group_name TEXT);
                CREATE TABLE {{UserPrefix}}_notifications_v2 (
                    id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT, type TEXT,
                    link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT, seen INTEGER,
                    sender_user_id TEXT, sender_username TEXT, data TEXT, responses TEXT, details TEXT);
                CREATE TABLE {{UserPrefix}}_activity_sessions_v2 (
                    session_id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, start_at INTEGER NOT NULL,
                    end_at INTEGER NOT NULL, is_open_tail INTEGER NOT NULL DEFAULT 0,
                    source_revision TEXT NOT NULL DEFAULT '');
                CREATE TABLE {{UserPrefix}}_activity_bucket_cache_v2 (
                    user_id TEXT NOT NULL, target_user_id TEXT NOT NULL DEFAULT '', range_days INTEGER NOT NULL,
                    view_kind TEXT NOT NULL, exclude_key TEXT NOT NULL DEFAULT '', bucket_version INTEGER NOT NULL DEFAULT 1,
                    raw_buckets_json TEXT NOT NULL DEFAULT '[]', normalized_buckets_json TEXT NOT NULL DEFAULT '[]',
                    built_from_cursor TEXT NOT NULL DEFAULT '', summary_json TEXT NOT NULL DEFAULT '{}',
                    built_at TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (user_id, target_user_id, range_days, view_kind, exclude_key));
                CREATE TABLE gamelog_location (
                    id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT, world_name TEXT,
                    time INTEGER, group_name TEXT, UNIQUE(created_at, location));
                CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
                """);

            await ExecuteAsync(source, $$"""
                INSERT INTO {{UserPrefix}}_friend_log_current VALUES ('usr_friend', 'Friend Name', 'trusted', 42);
                INSERT INTO {{UserPrefix}}_friend_log_history VALUES
                    (7, '2026-04-01T01:00:00Z', 'DisplayName', 'usr_friend', 'Friend Name', 'Old Name', 'trusted', 'known', 42);
                INSERT INTO {{UserPrefix}}_feed_gps VALUES
                    (8, '2026-04-01T02:00:00Z', 'usr_friend', 'Friend Name', 'wrld_world:123~group(grp_context)',
                     'Meaningful World', 'offline', 3600, 'Context Group');
                INSERT INTO {{UserPrefix}}_notifications_v2 VALUES
                    ('not_1', '2026-04-01T03:00:00Z', '2026-04-01T03:01:00Z', NULL, 'invite',
                     'vrchat://launch?id=wrld_world:123', 'Join', 'Come here', 'Invite', 'https://images.invalid/invite.png', 0,
                     'usr_sender', 'Sender Name',
                     '{"worldId":"wrld_world","worldName":"Meaningful World","location":"wrld_world:123","groupId":"grp_context"}',
                     '[{"type":"accept"}]',
                     '{"inviteMessage":"Come here","requestMessage":"May I join?"}');
                INSERT INTO {{UserPrefix}}_activity_sessions_v2
                    (session_id, user_id, start_at, end_at, is_open_tail, source_revision)
                    VALUES (11, 'usr_friend', 1000, 4600, 0, 'revision-one');
                INSERT INTO {{UserPrefix}}_activity_bucket_cache_v2
                    (user_id, target_user_id, range_days, view_kind, exclude_key, bucket_version,
                     raw_buckets_json, normalized_buckets_json, built_from_cursor, summary_json, built_at)
                    VALUES ('usr_self', 'usr_friend', 30, 'friend', '', 2, '[1,2]', '[0.5,1]', 'cursor',
                            '{"totalSeconds":3600}', '2026-04-01T04:00:00Z');
                INSERT INTO gamelog_location VALUES
                    (12, '2026-04-01T05:00:00Z', 'wrld_world:123', 'wrld_world', 'Meaningful World', 7200, 'Context Group');
                INSERT INTO memos VALUES ('usr_friend', '2026-04-01T06:00:00Z', 'meaningful memo');
                """);
        }

        var importer = workspace.CreateImporter();
        var result = await importer.ImportAndCleanupAsync(new VrcxSnapshotFiles(stagedPath));

        Assert.Equal(8, result.ImportedTableCount);
        Assert.Equal(8, result.ImportedRowCount);
        await using var target = await OpenAsync(workspace.RuntimeDatabasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal("Old Name", await ScalarStringAsync(target, $"SELECT previous_display_name FROM {UserPrefix}_friend_log_history WHERE id = 7;"));
        Assert.Equal("Context Group", await ScalarStringAsync(target, $"SELECT group_name FROM {UserPrefix}_feed_gps WHERE id = 8;"));
        Assert.Equal("usr_sender", await ScalarStringAsync(target, $"SELECT sender_user_id FROM {UserPrefix}_notifications_v2 WHERE id = 'not_1';"));
        Assert.Contains("worldId", await ScalarStringAsync(target, $"SELECT data FROM {UserPrefix}_notifications_v2 WHERE id = 'not_1';"));
        Assert.Contains("requestMessage", await ScalarStringAsync(target, $"SELECT details FROM {UserPrefix}_notifications_v2 WHERE id = 'not_1';"));
        Assert.Equal(3600L, await ScalarInt64Async(target, $"SELECT end_at - start_at FROM {UserPrefix}_activity_sessions_v2 WHERE session_id = 11;"));
        Assert.Equal("{\"totalSeconds\":3600}", await ScalarStringAsync(target, $"SELECT summary_json FROM {UserPrefix}_activity_bucket_cache_v2;"));
        Assert.Equal("Meaningful World", await ScalarStringAsync(target, "SELECT world_name FROM gamelog_location WHERE id = 12;"));
        Assert.Equal("meaningful memo", await ScalarStringAsync(target, "SELECT memo FROM memos WHERE user_id = 'usr_friend';"));
        Assert.Equal(4L, await ScalarInt64Async(
            target,
            $"SELECT COUNT(*) FROM pragma_table_info('{UserPrefix}_notifications_v2') WHERE name IN ('sender_user_id', 'data', 'responses', 'details');"));
    }

    private static async Task<SqliteConnection> OpenAsync(
        string path,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        if (mode == SqliteOpenMode.ReadWriteCreate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "vrcx-snapshot-import-tests", Guid.NewGuid().ToString("N"));
            StagingDirectory = Path.Combine(Root, "staging");
            RuntimeDatabasePath = Path.Combine(Root, "runtime", "VRCX.sqlite3");
            Directory.CreateDirectory(StagingDirectory);
        }

        public string Root { get; }
        public string StagingDirectory { get; }
        public string RuntimeDatabasePath { get; }

        public VrcxSnapshotImporter CreateImporter() => new(new VrcxSnapshotImportOptions(
            RuntimeDatabasePath,
            StagingDirectory));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
