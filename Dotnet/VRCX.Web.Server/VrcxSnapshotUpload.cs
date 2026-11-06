using System.Buffers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace VRCX.Web.Server;

public sealed class VrcxSnapshotUploadOptions
{
    public string StagingDirectory { get; set; } = "/data/import-staging";

    public long MaximumDatabaseBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public long MaximumWalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public long MaximumSharedMemoryBytes { get; set; } = 64L * 1024 * 1024;

    public long MaximumTotalFileBytes { get; set; } = 6L * 1024 * 1024 * 1024 + 64L * 1024 * 1024;

    public long MaximumRequestBytes { get; set; } = 6L * 1024 * 1024 * 1024 + 65L * 1024 * 1024;
}

public sealed class VrcxSnapshotUploadException(string errorCode, int statusCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int StatusCode { get; } = statusCode;
}

public sealed class VrcxSnapshotImportCoordinator
{
    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumBoundaryLength = 128;
    private readonly VrcxSnapshotUploadOptions options;
    private readonly IVrcxSnapshotImporter importer;
    private readonly IVrcxWorkerLifecycle worker;
    private readonly ILogger<VrcxSnapshotImportCoordinator> logger;
    private readonly SemaphoreSlim importLock = new(1, 1);
    private readonly string stagingDirectory;

    public VrcxSnapshotImportCoordinator(
        VrcxSnapshotUploadOptions options,
        IVrcxSnapshotImporter importer,
        IVrcxWorkerLifecycle worker,
        ILogger<VrcxSnapshotImportCoordinator> logger)
    {
        this.options = options;
        this.importer = importer;
        this.worker = worker;
        this.logger = logger;
        stagingDirectory = ValidateOptions(options);
    }

    public async Task<VrcxSnapshotImportResult> ImportAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        await importLock.WaitAsync(cancellationToken);
        var databasePath = Path.Combine(stagingDirectory, $"snapshot-{Guid.NewGuid():N}.sqlite3");
        var cleanupFiles = new VrcxSnapshotFiles(databasePath);
        var pauseRequested = false;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var stagedFiles = await StageMultipartAsync(request, databasePath, cancellationToken);
            pauseRequested = true;
            await worker.PauseAsync(cancellationToken);
            return await importer.ImportAsync(stagedFiles, cancellationToken);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                importer.CleanupStagingFiles(cleanupFiles);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure = exception;
                logger.LogError("Could not remove the staged VRCX snapshot after import.");
            }

            try
            {
                if (pauseRequested)
                {
                    await worker.ResumeAsync(CancellationToken.None);
                }
            }
            finally
            {
                importLock.Release();
            }

            if (cleanupFailure is not null)
            {
                throw new VrcxSnapshotUploadException(
                    "snapshot_cleanup_failed",
                    StatusCodes.Status503ServiceUnavailable);
            }
        }
    }

    private async Task<VrcxSnapshotFiles> StageMultipartAsync(
        HttpRequest request,
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is < 1 || request.ContentLength > options.MaximumRequestBytes)
        {
            throw TooLarge();
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidMultipart();
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximumBoundaryLength)
        {
            throw InvalidMultipart();
        }

        var requestBody = new RequestLimitStream(request.Body, options.MaximumRequestBytes);
        var reader = new MultipartReader(boundary, requestBody)
        {
            HeadersCountLimit = 8,
            HeadersLengthLimit = 8 * 1024,
            BodyLengthLimit = Math.Max(
                options.MaximumDatabaseBytes,
                Math.Max(options.MaximumWalBytes, options.MaximumSharedMemoryBytes))
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalFileBytes = 0;
        string? walPath = null;
        string? sharedMemoryPath = null;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) ||
                (disposition.FileName.Value is null && disposition.FileNameStar.Value is null))
            {
                throw InvalidMultipart();
            }

            var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (fieldName is null || !seen.Add(fieldName))
            {
                throw InvalidMultipart();
            }

            var (path, maximumBytes) = fieldName switch
            {
                "database" => (databasePath, options.MaximumDatabaseBytes),
                "wal" => (databasePath + "-wal", options.MaximumWalBytes),
                "shm" => (databasePath + "-shm", options.MaximumSharedMemoryBytes),
                _ => throw InvalidMultipart()
            };

            var bytes = await CopySectionAsync(section.Body, path, maximumBytes, cancellationToken);
            totalFileBytes = checked(totalFileBytes + bytes);
            if (totalFileBytes > options.MaximumTotalFileBytes)
            {
                throw TooLarge();
            }

            if (fieldName == "wal")
            {
                walPath = path;
            }
            else if (fieldName == "shm")
            {
                sharedMemoryPath = path;
            }
        }

        if (!seen.Contains("database") || new FileInfo(databasePath).Length == 0)
        {
            throw InvalidMultipart();
        }
        return new VrcxSnapshotFiles(databasePath, walPath, sharedMemoryPath);
    }

    private static async Task<long> CopySectionAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            long length = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                length = checked(length + count);
                if (length > maximumBytes)
                {
                    throw TooLarge();
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ValidateOptions(VrcxSnapshotUploadOptions configured)
    {
        if (string.IsNullOrWhiteSpace(configured.StagingDirectory) ||
            !Path.IsPathFullyQualified(configured.StagingDirectory))
        {
            throw new InvalidOperationException("The VRCX snapshot staging directory must be absolute.");
        }

        var limits = new[]
        {
            configured.MaximumDatabaseBytes,
            configured.MaximumWalBytes,
            configured.MaximumSharedMemoryBytes,
            configured.MaximumTotalFileBytes,
            configured.MaximumRequestBytes
        };
        if (limits.Any(value => value is < 1 or > 64L * 1024 * 1024 * 1024) ||
            configured.MaximumTotalFileBytes < configured.MaximumDatabaseBytes ||
            configured.MaximumRequestBytes <= configured.MaximumTotalFileBytes)
        {
            throw new InvalidOperationException("The VRCX snapshot upload limits are invalid.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured.StagingDirectory));
    }

    private static VrcxSnapshotUploadException InvalidMultipart() =>
        new("invalid_snapshot_upload", StatusCodes.Status400BadRequest);

    private static VrcxSnapshotUploadException TooLarge() =>
        new("snapshot_upload_too_large", StatusCodes.Status413PayloadTooLarge);

    private sealed class RequestLimitStream(Stream inner, long maximumBytes) : Stream
    {
        private long consumed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => consumed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            AddConsumed(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            AddConsumed(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            AddConsumed(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        private int AddConsumed(int count)
        {
            consumed = checked(consumed + count);
            if (consumed > maximumBytes)
            {
                throw TooLarge();
            }
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The ASP.NET request owns the underlying stream.
            base.Dispose(disposing);
        }
    }
}

public static class VrcxSnapshotUploadEndpoints
{
    public const string RateLimitPolicy = "snapshot-import";

    public static RouteGroupBuilder MapVrcxSnapshotUploadApi(this RouteGroupBuilder api)
    {
        api.MapPost("/setup/import", ImportAsync)
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy);
        return api;
    }

    private static async Task<IResult> ImportAsync(
        HttpRequest request,
        VrcxSnapshotImportCoordinator coordinator,
        ILogger<VrcxSnapshotImportCoordinator> logger,
        CancellationToken cancellationToken)
    {
        var requestSizeFeature = request.HttpContext.Features.Get<
            Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (requestSizeFeature is { IsReadOnly: false })
        {
            // The streaming coordinator below applies its own lower request/file limits.
            requestSizeFeature.MaxRequestBodySize = null;
        }

        try
        {
            var result = await coordinator.ImportAsync(request, cancellationToken);
            return Results.Ok(new
            {
                alreadyImported = result.AlreadyImported,
                importedTableCount = result.ImportedTableCount,
                importedRowCount = result.ImportedRowCount
            });
        }
        catch (VrcxSnapshotUploadException exception)
        {
            return Results.Json(new { error = exception.ErrorCode }, statusCode: exception.StatusCode);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new { error = "invalid_snapshot_upload" });
        }
        catch (VrcxSnapshotSetupClosedException)
        {
            return Results.Conflict(new { error = "snapshot_setup_closed" });
        }
        catch (VrcxSnapshotImportException)
        {
            return Results.UnprocessableEntity(new { error = "snapshot_rejected" });
        }
        catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError("The VRCX snapshot import boundary was unavailable ({ErrorType}).", exception.GetType().Name);
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "VRCX snapshot import is temporarily unavailable.");
        }
    }
}
