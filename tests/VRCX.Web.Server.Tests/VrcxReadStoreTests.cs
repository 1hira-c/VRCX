using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

using ReadModelOptions = global::VRCX.Web.Server.VrcxReadModelOptions;
using ReadStore = global::VRCX.Web.Server.VrcxReadStore;

namespace VRCX.Web.Server.Tests;

public sealed class VrcxReadStoreTests
{
    [Fact]
    public async Task ReadsOriginalSchemaAndMeaningfulProjectionFieldsWithoutWriting()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        var store = fixture.Store;

        var status = await store.GetStatusAsync();
        Assert.True(status.Configured);
        Assert.False(status.AuthenticationRequired);
        Assert.Equal("usr_owner", status.CurrentUserId);
        Assert.Equal("connected", status.Pipeline.State);
        Assert.Equal(1, status.Counts.Friends);
        Assert.Equal(1, status.Counts.Notifications);
        Assert.Equal(1, status.Counts.GameLog);

        var friends = await store.GetFriendsAsync(0, 20);
        var friend = Assert.Single(friends.Items);
        Assert.Equal("Example Friend", friend.DisplayName);
        Assert.Equal("wrld_example:12345", friend.Location);
        Assert.Equal("Example World", friend.WorldName);

        var notifications = await store.GetNotificationsAsync(0, 20);
        var invite = Assert.Single(notifications.Items);
        Assert.Equal("invite", invite.Type);
        Assert.Equal("usr_sender", invite.Payload.GetProperty("senderUserId").GetString());
        Assert.Equal("Sender Name", invite.Payload.GetProperty("senderUsername").GetString());
        Assert.Equal("wrld_example:12345", invite.Payload.GetProperty("worldId").GetString());
        Assert.Equal("Example World", invite.Payload.GetProperty("worldName").GetString());
        Assert.Equal("Come join us", invite.Payload.GetProperty("inviteMessage").GetString());

        var gameLog = await store.GetGameLogAsync(0, 20);
        var location = Assert.Single(gameLog.Items);
        Assert.Equal("wrld_example", location.Payload.GetProperty("worldId").GetString());
        Assert.Equal("Example World", location.Payload.GetProperty("worldName").GetString());

        var analytics = await store.GetAnalyticsAsync();
        Assert.Equal(2, analytics.ActivitySessionCount);
        Assert.Equal(1, analytics.ActivityUserCount);
        Assert.Equal(2, analytics.ActivityMinutes);

        var groups = await store.SearchAsync("group", "Test", 0, 20);
        var group = Assert.Single(groups.Items);
        Assert.Equal("grp_example", group.Id);
        Assert.Equal("Test Group", group.Name);

        Assert.Equal(2, await store.GetLatestEventSequenceAsync());
        var events = await store.GetEventsAfterAsync(0, 20);
        Assert.Collection(
            events,
            item =>
            {
                Assert.Equal("status", item.Type);
                Assert.Null(item.Resource);
            },
            item =>
            {
                Assert.Equal("data", item.Type);
                Assert.Equal("notifications", item.Resource);
                Assert.Equal("not_1", item.Payload.GetProperty("id").GetString());
            });

        Assert.Equal("preserved", await fixture.ScalarStringAsync("SELECT value FROM test_marker WHERE id = 1;"));
    }

    [Fact]
    public async Task NotificationV2ExposesMeaningfulScalarContextButNotActionsOrSecrets()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            UPDATE usrowner_notifications SET receiver_user_id = NULL WHERE id = 'not_1';
            CREATE TABLE usrowner_notifications_v2 (
                id TEXT PRIMARY KEY, created_at TEXT, updated_at TEXT, expires_at TEXT,
                type TEXT, link TEXT, link_text TEXT, message TEXT, title TEXT, image_url TEXT,
                seen INTEGER, sender_user_id TEXT, sender_username TEXT, data TEXT,
                responses TEXT, details TEXT
            );
            INSERT INTO usrowner_notifications_v2 VALUES (
                'not_v2', '2026-11-06T10:30:00Z', '2026-11-06T10:31:00Z',
                '2026-11-07T10:30:00Z', 'group.announcement', 'group:grp_example',
                'Open group', 'A group announcement', 'Announcement',
                'https://example.invalid/group.png', 0, 'usr_sender_v2', 'Sender V2',
                '{"announcementTitle":"Meetup","groupId":"grp_example","groupName":"Example Group","ownerName":"Owner","password":"drop-password","accessToken":"drop-token","cookie":"drop-cookie","unknown":"drop-unknown","nested":{"value":"drop-nested"}}',
                '[{"type":"link","text":"Unsafe action","data":"https://example.invalid/action","icon":"reply"}]',
                '{"worldId":"wrld_context","worldName":"Context World","groupId":"grp_context","groupName":"Context Group","location":"wrld_context:instance","instanceId":"instance","instanceName":"Friends+","inviteMessage":"Come here","requestMessage":"May I join?","responseMessage":"On my way","senderUserId":"usr_sender_v2","senderUsername":"Sender V2","recipientUserId":"usr_owner","recipientUsername":"Owner","targetUserId":"usr_target","targetUsername":"Target","imageUrl":"https://example.invalid/context.png","emojiId":"emoji_wave","emojiVersion":2,"inventoryItemId":"inv_example","password":"drop-password","token":"drop-token","cookie":"drop-cookie","unknown":"drop-unknown","nested":{"value":"drop-nested"}}'
            );
            """);

        var notifications = await fixture.Store.GetNotificationsAsync(0, 20);
        var legacy = notifications.Items.Single(item => item.Id == "not_1");
        var v2 = notifications.Items.Single(item => item.Id == "not_v2");

        Assert.Equal("usr_owner", legacy.Payload.GetProperty("receiverUserId").GetString());
        Assert.Equal("usr_sender_v2", v2.Payload.GetProperty("senderUserId").GetString());
        Assert.Equal("usr_owner", v2.Payload.GetProperty("receiverUserId").GetString());
        Assert.Equal(2, v2.Payload.GetProperty("version").GetInt32());
        Assert.Equal("group:grp_example", v2.Payload.GetProperty("link").GetString());
        Assert.Equal("Open group", v2.Payload.GetProperty("linkText").GetString());
        Assert.Equal("https://example.invalid/group.png", v2.Payload.GetProperty("imageUrl").GetString());

        var data = v2.Payload.GetProperty("data");
        Assert.Equal("Meetup", data.GetProperty("announcementTitle").GetString());
        Assert.Equal("grp_example", data.GetProperty("groupId").GetString());
        Assert.Equal("Example Group", data.GetProperty("groupName").GetString());
        Assert.Equal("Owner", data.GetProperty("ownerName").GetString());
        Assert.False(data.TryGetProperty("password", out _));
        Assert.False(data.TryGetProperty("accessToken", out _));
        Assert.False(data.TryGetProperty("cookie", out _));
        Assert.False(data.TryGetProperty("unknown", out _));
        Assert.False(data.TryGetProperty("nested", out _));

        var details = v2.Payload.GetProperty("details");
        Assert.Equal("emoji_wave", details.GetProperty("emojiId").GetString());
        Assert.Equal(2, details.GetProperty("emojiVersion").GetInt32());
        Assert.Equal("inv_example", details.GetProperty("inventoryItemId").GetString());
        Assert.Equal("wrld_context:instance", details.GetProperty("location").GetString());
        Assert.Equal("Context World", details.GetProperty("worldName").GetString());
        Assert.Equal("grp_context", details.GetProperty("groupId").GetString());
        Assert.Equal("usr_owner", details.GetProperty("recipientUserId").GetString());
        Assert.Equal("usr_target", details.GetProperty("targetUserId").GetString());
        Assert.Equal("Come here", details.GetProperty("inviteMessage").GetString());
        Assert.Equal("May I join?", details.GetProperty("requestMessage").GetString());
        Assert.False(details.TryGetProperty("password", out _));
        Assert.False(details.TryGetProperty("token", out _));
        Assert.False(details.TryGetProperty("cookie", out _));
        Assert.False(details.TryGetProperty("unknown", out _));
        Assert.False(details.TryGetProperty("nested", out _));

        var responses = v2.Payload.GetProperty("responses");
        Assert.Equal(JsonValueKind.Array, responses.ValueKind);
        Assert.Equal(0, responses.GetArrayLength());
        Assert.DoesNotContain("drop-", v2.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unsafe action", v2.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnsafeCurrentUserAndEntityIdentifiers()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync("usr_bad\";DROP_TABLE");

        var status = await fixture.Store.GetStatusAsync();

        Assert.False(status.Configured);
        Assert.True(status.AuthenticationRequired);
        Assert.Null(await fixture.Store.GetFriendAsync("usr_good' OR 1=1 --"));
        Assert.Equal("preserved", await fixture.ScalarStringAsync("SELECT value FROM test_marker WHERE id = 1;"));
    }

    [Fact]
    public async Task MissingDatabaseReturnsAnExplicitUnconfiguredState()
    {
        var path = Path.Combine(Path.GetTempPath(), "vrcx-web-read-missing", Guid.NewGuid().ToString("N"), "VRCX.sqlite3");
        var store = new ReadStore(new ReadModelOptions { DatabasePath = path });

        var status = await store.GetStatusAsync();
        var friends = await store.GetFriendsAsync(0, 20);

        Assert.False(status.Configured);
        Assert.True(status.AuthenticationRequired);
        Assert.Equal("not_configured", status.Sync.State);
        Assert.Empty(friends.Items);
    }

    [Fact]
    public async Task EmptyWebProjectionFallsBackToOriginalFriendHistory()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("DELETE FROM vrcx_web_user_snapshot;");

        var status = await fixture.Store.GetStatusAsync();
        var friends = await fixture.Store.GetFriendsAsync(0, 20);
        var friendDetail = await fixture.Store.GetFriendAsync("usr_legacy_friend");

        Assert.Equal(1, status.Counts.Friends);
        var friend = Assert.Single(friends.Items);
        Assert.Equal("usr_legacy_friend", friend.Id);
        Assert.Equal("Legacy Friend", friend.DisplayName);
        Assert.NotNull(friendDetail);
        Assert.Equal("Legacy Friend", friendDetail.DisplayName);
    }

    [Fact]
    public async Task NonEmptyWebProjectionIsAuthoritativeForFriendReadsAndCounts()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO usrowner_friend_log_current VALUES (
                'usr_stale_friend', 'Stale Friend', 'Known User', 2
            );
            """);

        var status = await fixture.Store.GetStatusAsync();
        var friends = await fixture.Store.GetFriendsAsync(0, 20);

        Assert.Equal(1, status.Counts.Friends);
        Assert.Equal("usr_friend", Assert.Single(friends.Items).Id);
        Assert.Null(await fixture.Store.GetFriendAsync("usr_stale_friend"));
    }

    [Fact]
    public async Task ReadyFavoriteProjectionPreservesRemoteIdsTypesGroupsAndOrder()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE favorite_world (
                id INTEGER PRIMARY KEY, created_at TEXT, world_id TEXT, group_name TEXT
            );
            INSERT INTO favorite_world VALUES (
                99, '2026-11-01T00:00:00Z', 'wrld_stale', 'worlds1'
            );
            CREATE TABLE vrcx_web_favorite_projection_state (
                id INTEGER PRIMARY KEY, ready INTEGER NOT NULL,
                current_user_id TEXT NOT NULL, updated_at TEXT
            );
            CREATE TABLE vrcx_web_favorite_snapshot (
                id TEXT PRIMARY KEY, type TEXT NOT NULL, favorite_id TEXT NOT NULL,
                tags_json TEXT NOT NULL, group_key TEXT NOT NULL, sort_order INTEGER NOT NULL,
                target_name TEXT NOT NULL, target_payload_json TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE vrcx_web_favorite_group_snapshot (
                group_key TEXT PRIMARY KEY, type TEXT NOT NULL, name TEXT NOT NULL,
                display_name TEXT NOT NULL, capacity INTEGER NOT NULL, count INTEGER NOT NULL,
                visibility TEXT NOT NULL, sort_order INTEGER NOT NULL, updated_at TEXT NOT NULL
            );
            INSERT INTO vrcx_web_favorite_projection_state VALUES (
                1, 1, 'usr_owner', '2026-11-06T10:00:00Z'
            );
            INSERT INTO vrcx_web_favorite_snapshot VALUES (
                'fvrt_remote', 'vrcPlusWorld', 'wrld_remote', '["vrcPlusWorlds1"]',
                'vrcPlusWorld:vrcPlusWorlds1', 7, 'Remote World',
                '{"id":"wrld_remote","name":"Remote World","authorName":"Creator"}',
                '2026-11-06T10:00:00Z'
            );
            INSERT INTO vrcx_web_favorite_group_snapshot VALUES (
                'vrcPlusWorld:vrcPlusWorlds1', 'vrcPlusWorld', 'vrcPlusWorlds1',
                'VRC+ Group 1', 100, 1, 'private', 3, '2026-11-06T10:00:00Z'
            );
            """);

        var favorites = await fixture.Store.GetFavoritesAsync(0, 20);
        var groups = await fixture.Store.GetFavoriteGroupsAsync();
        var status = await fixture.Store.GetStatusAsync();

        Assert.True(favorites.Ready);
        var favorite = Assert.Single(favorites.Items);
        Assert.Equal("fvrt_remote", favorite.Id);
        Assert.Equal("vrcPlusWorld", favorite.Type);
        Assert.Equal("wrld_remote", favorite.FavoriteId);
        Assert.Equal(["vrcPlusWorlds1"], favorite.Tags);
        Assert.Equal("vrcPlusWorld:vrcPlusWorlds1", favorite.GroupKey);
        Assert.Equal(7, favorite.SortOrder);
        Assert.Equal("Remote World", favorite.TargetPayload.GetProperty("name").GetString());
        Assert.True(groups.Ready);
        var group = Assert.Single(groups.Items);
        Assert.Equal("VRC+ Group 1", group.DisplayName);
        Assert.Equal(3, group.SortOrder);
        Assert.Equal(1, status.Counts.Favorites);
    }

    [Fact]
    public async Task ReadyEmptyFavoriteProjectionIsAuthoritativeOverLocalFallback()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE favorite_world (
                id INTEGER PRIMARY KEY, created_at TEXT, world_id TEXT, group_name TEXT
            );
            INSERT INTO favorite_world VALUES (
                99, '2026-11-01T00:00:00Z', 'wrld_stale', 'worlds1'
            );
            CREATE TABLE vrcx_web_favorite_projection_state (
                id INTEGER PRIMARY KEY, ready INTEGER NOT NULL,
                current_user_id TEXT NOT NULL, updated_at TEXT
            );
            CREATE TABLE vrcx_web_favorite_snapshot (
                id TEXT PRIMARY KEY, type TEXT NOT NULL, favorite_id TEXT NOT NULL,
                tags_json TEXT NOT NULL, group_key TEXT NOT NULL, sort_order INTEGER NOT NULL,
                target_name TEXT NOT NULL, target_payload_json TEXT NOT NULL, updated_at TEXT NOT NULL
            );
            CREATE TABLE vrcx_web_favorite_group_snapshot (
                group_key TEXT PRIMARY KEY, type TEXT NOT NULL, name TEXT NOT NULL,
                display_name TEXT NOT NULL, capacity INTEGER NOT NULL, count INTEGER NOT NULL,
                visibility TEXT NOT NULL, sort_order INTEGER NOT NULL, updated_at TEXT NOT NULL
            );
            INSERT INTO vrcx_web_favorite_projection_state VALUES (
                1, 1, 'usr_owner', '2026-11-06T10:00:00Z'
            );
            """);

        var favorites = await fixture.Store.GetFavoritesAsync(0, 20);
        var groups = await fixture.Store.GetFavoriteGroupsAsync();
        var status = await fixture.Store.GetStatusAsync();

        Assert.True(favorites.Ready);
        Assert.Empty(favorites.Items);
        Assert.Equal(0, favorites.Total);
        Assert.True(groups.Ready);
        Assert.Empty(groups.Items);
        Assert.Equal(0, status.Counts.Favorites);
    }

    [Fact]
    public async Task DirectReadStoreCallsClampLargeOffsets()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();

        var friends = await fixture.Store.GetFriendsAsync(int.MaxValue, 1);

        Assert.Equal(10_000, friends.Offset);
    }

    [Fact]
    public async Task GameLogCountsEachPhysicalTableOnce()
    {
        await using var fixture = await ReadStoreFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            CREATE TABLE gamelog_video_play (
                id INTEGER PRIMARY KEY, created_at TEXT, video_url TEXT, video_name TEXT,
                video_id TEXT, location TEXT, display_name TEXT, user_id TEXT
            );
            INSERT INTO gamelog_video_play VALUES (
                2, '2026-11-06T09:50:00Z', 'https://example.invalid/video', 'Example Video',
                'video-1', 'wrld_example:12345', 'Example Friend', 'usr_friend'
            );
            """);

        var status = await fixture.Store.GetStatusAsync();
        var gameLog = await fixture.Store.GetGameLogAsync(0, 20);

        Assert.Equal(2, status.Counts.GameLog);
        Assert.Equal(2, gameLog.Total);
        Assert.Equal(2, gameLog.Items.Count);
    }

    private sealed class ReadStoreFixture : IAsyncDisposable
    {
        private ReadStoreFixture(string directoryPath, string databasePath)
        {
            DirectoryPath = directoryPath;
            DatabasePath = databasePath;
            Store = new ReadStore(new ReadModelOptions
            {
                DatabasePath = databasePath,
                StaleAfter = TimeSpan.FromDays(1)
            });
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public ReadStore Store { get; }

        public static async Task<ReadStoreFixture> CreateAsync(string currentUserId = "usr_owner")
        {
            var directory = Path.Combine(Path.GetTempPath(), "vrcx-web-read-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "VRCX.sqlite3");
            var fixture = new ReadStoreFixture(directory, path);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE configs (`key` TEXT PRIMARY KEY, `value` TEXT);
                CREATE TABLE test_marker (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE vrcx_web_projection_state (
                    id INTEGER PRIMARY KEY, current_user_id TEXT, sync_state TEXT NOT NULL,
                    last_success_at TEXT, last_attempt_at TEXT, next_retry_at TEXT, error_code TEXT,
                    pipeline_state TEXT NOT NULL, pipeline_connected_at TEXT, pipeline_last_event_at TEXT,
                    pipeline_last_attempt_at TEXT, pipeline_next_retry_at TEXT, pipeline_error_code TEXT
                );
                CREATE TABLE vrcx_web_user_snapshot (
                    user_id TEXT PRIMARY KEY, display_name TEXT NOT NULL, state TEXT,
                    is_friend INTEGER NOT NULL, updated_at TEXT NOT NULL, payload_json TEXT NOT NULL
                );
                CREATE TABLE vrcx_web_entity_snapshot (
                    entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, display_name TEXT NOT NULL,
                    owner_id TEXT, description TEXT, updated_at TEXT NOT NULL, payload_json TEXT NOT NULL,
                    PRIMARY KEY(entity_type, entity_id)
                );
                CREATE TABLE vrcx_web_event_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL,
                    type TEXT NOT NULL, resource TEXT, payload_json TEXT NOT NULL
                );
                CREATE TABLE usrowner_notifications (
                    id TEXT PRIMARY KEY, created_at TEXT, type TEXT, sender_user_id TEXT,
                    sender_username TEXT, receiver_user_id TEXT, message TEXT, world_id TEXT,
                    world_name TEXT, image_url TEXT, invite_message TEXT, request_message TEXT,
                    response_message TEXT, expired INTEGER
                );
                CREATE TABLE usrowner_friend_log_current (
                    user_id TEXT PRIMARY KEY, display_name TEXT NOT NULL,
                    trust_level TEXT, friend_number INTEGER
                );
                CREATE TABLE usrowner_activity_sessions_v2 (
                    session_id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL,
                    start_at INTEGER NOT NULL, end_at INTEGER NOT NULL,
                    is_open_tail INTEGER NOT NULL DEFAULT 0, source_revision TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE gamelog_location (
                    id INTEGER PRIMARY KEY, created_at TEXT, location TEXT, world_id TEXT,
                    world_name TEXT, time INTEGER, group_name TEXT
                );
                INSERT INTO configs (`key`, `value`) VALUES ('config:lastuserloggedin', @currentUserId);
                INSERT INTO test_marker VALUES (1, 'preserved');
                INSERT INTO vrcx_web_projection_state VALUES (
                    1, 'usr_owner', 'synchronized', '2026-11-06T10:00:00Z',
                    '2026-11-06T10:00:00Z', NULL, NULL, 'connected',
                    '2026-11-06T09:00:00Z', '2026-11-06T10:00:00Z', NULL, NULL, NULL
                );
                INSERT INTO vrcx_web_user_snapshot VALUES (
                    'usr_friend', 'Example Friend', 'online', 1, '2026-11-06T10:00:00Z',
                    @friendPayload
                );
                INSERT INTO vrcx_web_entity_snapshot VALUES (
                    'group', 'grp_example', 'Test Group', 'usr_owner', 'A useful group',
                    '2026-11-06T10:00:00Z', @groupPayload
                );
                INSERT INTO vrcx_web_event_outbox
                    (occurred_at, type, resource, payload_json)
                    VALUES
                    ('2026-11-06T10:00:00Z', 'status', NULL, '{}'),
                    ('2026-11-06T10:01:00Z', 'data', 'notifications', '{"id":"not_1"}');
                INSERT INTO usrowner_notifications VALUES (
                    'not_1', '2026-11-06T09:30:00Z', 'invite', 'usr_sender', 'Sender Name',
                    'usr_owner', 'Join this instance', 'wrld_example:12345', 'Example World', NULL,
                    'Come join us', NULL, NULL, 0
                );
                INSERT INTO usrowner_friend_log_current VALUES (
                    'usr_legacy_friend', 'Legacy Friend', 'Trusted User', 1
                );
                INSERT INTO usrowner_activity_sessions_v2
                    (user_id, start_at, end_at, is_open_tail, source_revision)
                    VALUES ('usr_friend', 0, 120000, 0, 'one'), ('usr_friend', 120000, 60000, 0, 'invalid');
                INSERT INTO gamelog_location VALUES (
                    1, '2026-11-06T09:45:00Z', 'wrld_example:12345', 'wrld_example',
                    'Example World', 60000, 'Friends+'
                );
                """;
            command.Parameters.AddWithValue("@currentUserId", currentUserId);
            command.Parameters.AddWithValue("@friendPayload", JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["id"] = "usr_friend",
                    ["displayName"] = "Example Friend",
                    ["username"] = "example.friend",
                    ["status"] = "active",
                    ["location"] = "wrld_example:12345",
                    ["bio"] = "A profile",
                    ["$location"] = new { worldName = "Example World" }
                }));
            command.Parameters.AddWithValue("@groupPayload", JsonSerializer.Serialize(new
            {
                id = "grp_example",
                name = "Test Group",
                description = "A useful group"
            }));
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        public async Task<string?> ScalarStringAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string?)await command.ExecuteScalarAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
