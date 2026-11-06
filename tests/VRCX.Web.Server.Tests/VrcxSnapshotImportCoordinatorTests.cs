using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VRCX.Web.Server;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed class VrcxSnapshotImportCoordinatorTests
{
    [Fact]
    public async Task StagesFixedDatabaseAndWalNamesAndImportsOnlyWhileWorkerIsPaused()
    {
        using var workspace = new CoordinatorWorkspace();
        var worker = new RecordingWorkerLifecycle();
        byte[]? importedDatabase = null;
        byte[]? importedWal = null;
        var importer = new RecordingImporter(async (files, _) =>
        {
            Assert.True(worker.IsPaused);
            Assert.StartsWith(workspace.StagingDirectory + Path.DirectorySeparatorChar, files.DatabasePath, StringComparison.Ordinal);
            Assert.Equal(files.DatabasePath + "-wal", files.WalPath);
            Assert.Equal(files.DatabasePath + "-shm", files.SharedMemoryPath);
            Assert.DoesNotContain("caller-name", files.DatabasePath, StringComparison.Ordinal);
            importedDatabase = await File.ReadAllBytesAsync(files.DatabasePath);
            importedWal = await File.ReadAllBytesAsync(files.WalPath!);
            return Result(alreadyImported: false);
        });
        var coordinator = workspace.CreateCoordinator(importer, worker);
        var request = await CreateMultipartRequestAsync(
            ("database", "caller-name.sqlite3", new byte[] { 1, 2, 3 }),
            ("wal", "caller-name.sqlite3-wal", new byte[] { 4, 5 }),
            ("shm", "caller-name.sqlite3-shm", new byte[] { 6 }));

        var result = await coordinator.ImportAsync(request);

        Assert.False(result.AlreadyImported);
        Assert.Equal(new byte[] { 1, 2, 3 }, importedDatabase);
        Assert.Equal(new byte[] { 4, 5 }, importedWal);
        Assert.Equal(1, worker.PauseCount);
        Assert.Equal(1, worker.ResumeCount);
        Assert.False(worker.IsPaused);
        Assert.Empty(Directory.EnumerateFiles(workspace.StagingDirectory));
    }

    [Fact]
    public async Task ResumesWorkerAndCleansEveryCompanionWhenImportFails()
    {
        using var workspace = new CoordinatorWorkspace();
        var worker = new RecordingWorkerLifecycle();
        var importer = new RecordingImporter((_, _) =>
            throw new VrcxSnapshotImportException("synthetic rollback"));
        var coordinator = workspace.CreateCoordinator(importer, worker);
        var request = await CreateMultipartRequestAsync(
            ("database", "snapshot.db", new byte[] { 1 }),
            ("wal", "snapshot.db-wal", new byte[] { 2 }),
            ("shm", "snapshot.db-shm", new byte[] { 3 }));

        await Assert.ThrowsAsync<VrcxSnapshotImportException>(() => coordinator.ImportAsync(request));

        Assert.Equal(1, worker.PauseCount);
        Assert.Equal(1, worker.ResumeCount);
        Assert.False(worker.IsPaused);
        Assert.Equal(1, importer.CleanupCount);
        Assert.Empty(Directory.EnumerateFiles(workspace.StagingDirectory));
    }

    [Fact]
    public async Task ConcurrentUploadsAreSerializedAcrossStagingPauseAndImport()
    {
        using var workspace = new CoordinatorWorkspace();
        var worker = new RecordingWorkerLifecycle();
        var active = 0;
        var maximumActive = 0;
        var importer = new RecordingImporter(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Delay(100, cancellationToken);
            Interlocked.Decrement(ref active);
            return Result(alreadyImported: false);
        });
        var coordinator = workspace.CreateCoordinator(importer, worker);
        var first = await CreateMultipartRequestAsync(("database", "one.db", new byte[] { 1 }));
        var second = await CreateMultipartRequestAsync(("database", "two.db", new byte[] { 2 }));

        await Task.WhenAll(coordinator.ImportAsync(first), coordinator.ImportAsync(second));

        Assert.Equal(1, maximumActive);
        Assert.Equal(2, worker.PauseCount);
        Assert.Equal(2, worker.ResumeCount);
        Assert.Empty(Directory.EnumerateFiles(workspace.StagingDirectory));
    }

    [Fact]
    public async Task RepeatingTheSameUploadedSnapshotReturnsTheImporterNoOpResult()
    {
        using var workspace = new CoordinatorWorkspace();
        var sourcePath = Path.Combine(workspace.Root, "source.sqlite3");
        await using (var source = await OpenAsync(sourcePath))
        {
            await using var command = source.CreateCommand();
            command.CommandText = """
                CREATE TABLE memos (user_id TEXT PRIMARY KEY, edited_at TEXT, memo TEXT);
                INSERT INTO memos VALUES ('usr_duplicate', '2026-11-06', 'same snapshot');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var worker = new RecordingWorkerLifecycle();
        var importer = new VrcxSnapshotImporter(new VrcxSnapshotImportOptions(
            workspace.RuntimeDatabasePath,
            workspace.StagingDirectory));
        var coordinator = workspace.CreateCoordinator(importer, worker);

        var first = await coordinator.ImportAsync(await CreateMultipartRequestAsync(
            ("database", "first.sqlite3", sourceBytes)));
        var second = await coordinator.ImportAsync(await CreateMultipartRequestAsync(
            ("database", "retry.sqlite3", sourceBytes)));

        Assert.False(first.AlreadyImported);
        Assert.True(second.AlreadyImported);
        Assert.Equal(2, worker.PauseCount);
        Assert.Equal(2, worker.ResumeCount);
        Assert.Empty(Directory.EnumerateFiles(workspace.StagingDirectory));
    }

    [Fact]
    public async Task EnforcesRequestIndividualFileAndAggregateFileLimitsBeforePause()
    {
        using var workspace = new CoordinatorWorkspace();
        var worker = new RecordingWorkerLifecycle();
        var importer = new RecordingImporter((_, _) => Task.FromResult(Result(alreadyImported: false)));

        var requestLimited = workspace.CreateCoordinator(importer, worker, workspace.Options(
            database: 10,
            wal: 10,
            sharedMemory: 10,
            total: 20,
            request: 100));
        var oversizedRequest = await CreateMultipartRequestAsync(("database", "main.db", new byte[] { 1 }));
        oversizedRequest.ContentLength = 101;
        var requestError = await Assert.ThrowsAsync<VrcxSnapshotUploadException>(
            () => requestLimited.ImportAsync(oversizedRequest));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, requestError.StatusCode);

        var fileLimited = workspace.CreateCoordinator(importer, worker, workspace.Options(
            database: 5,
            wal: 10,
            sharedMemory: 10,
            total: 20,
            request: 1000));
        var oversizedFile = await CreateMultipartRequestAsync(("database", "main.db", new byte[6]));
        var fileError = await Assert.ThrowsAsync<VrcxSnapshotUploadException>(
            () => fileLimited.ImportAsync(oversizedFile));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, fileError.StatusCode);

        var totalLimited = workspace.CreateCoordinator(importer, worker, workspace.Options(
            database: 10,
            wal: 10,
            sharedMemory: 10,
            total: 10,
            request: 1000));
        var oversizedTotal = await CreateMultipartRequestAsync(
            ("database", "main.db", new byte[6]),
            ("wal", "main.db-wal", new byte[6]));
        var totalError = await Assert.ThrowsAsync<VrcxSnapshotUploadException>(
            () => totalLimited.ImportAsync(oversizedTotal));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, totalError.StatusCode);

        Assert.Equal(0, worker.PauseCount);
        Assert.Empty(Directory.EnumerateFiles(workspace.StagingDirectory));
    }

    private static VrcxSnapshotImportResult Result(bool alreadyImported) => new(
        new string('a', 64),
        alreadyImported,
        alreadyImported ? 0 : 1,
        alreadyImported ? 0 : 1,
        new Dictionary<string, long>(StringComparer.Ordinal));

    private static async Task<HttpRequest> CreateMultipartRequestAsync(
        params (string Field, string FileName, byte[] Contents)[] files)
    {
        using var content = new MultipartFormDataContent("vrcx-snapshot-test-boundary");
        foreach (var file in files)
        {
            content.Add(new ByteArrayContent(file.Contents), file.Field, file.FileName);
        }
        var body = await content.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext();
        context.Request.ContentType = content.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body, writable: false);
        return context.Request;
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class RecordingWorkerLifecycle : IVrcxWorkerLifecycle
    {
        public bool IsPaused { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(IsPaused);
            IsPaused = true;
            PauseCount++;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            Assert.True(IsPaused);
            IsPaused = false;
            ResumeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingImporter(
        Func<VrcxSnapshotFiles, CancellationToken, Task<VrcxSnapshotImportResult>> import) : IVrcxSnapshotImporter
    {
        public int CleanupCount { get; private set; }

        public Task<VrcxSnapshotImportResult> ImportAsync(
            VrcxSnapshotFiles files,
            CancellationToken cancellationToken = default) => import(files, cancellationToken);

        public void CleanupStagingFiles(VrcxSnapshotFiles files)
        {
            CleanupCount++;
            foreach (var path in new[] { files.DatabasePath + "-wal", files.DatabasePath + "-shm", files.DatabasePath })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private sealed class CoordinatorWorkspace : IDisposable
    {
        public CoordinatorWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "vrcx-snapshot-coordinator-tests", Guid.NewGuid().ToString("N"));
            StagingDirectory = Path.Combine(Root, "staging");
            RuntimeDatabasePath = Path.Combine(Root, "runtime", "VRCX.sqlite3");
            Directory.CreateDirectory(StagingDirectory);
        }

        public string Root { get; }
        public string StagingDirectory { get; }
        public string RuntimeDatabasePath { get; }

        public VrcxSnapshotImportCoordinator CreateCoordinator(
            IVrcxSnapshotImporter importer,
            IVrcxWorkerLifecycle worker,
            VrcxSnapshotUploadOptions? options = null) => new(
                options ?? Options(
                    database: 1024 * 1024,
                    wal: 1024 * 1024,
                    sharedMemory: 1024 * 1024,
                    total: 3 * 1024 * 1024,
                    request: 4 * 1024 * 1024),
                importer,
                worker,
                NullLogger<VrcxSnapshotImportCoordinator>.Instance);

        public VrcxSnapshotUploadOptions Options(long database, long wal, long sharedMemory, long total, long request) =>
            new()
            {
                StagingDirectory = StagingDirectory,
                MaximumDatabaseBytes = database,
                MaximumWalBytes = wal,
                MaximumSharedMemoryBytes = sharedMemory,
                MaximumTotalFileBytes = total,
                MaximumRequestBytes = request
            };

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
