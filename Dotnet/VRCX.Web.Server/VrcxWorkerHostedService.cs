using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRCX.Web.Server;

public sealed class VrcxWorkerProcessOptions
{
    public bool Enabled { get; set; }

    public string SetsidPath { get; set; } = "/usr/bin/setsid";

    public string XvfbRunPath { get; set; } = "/usr/bin/xvfb-run";

    public string ElectronPath { get; set; } = "/app/worker/electron/electron";

    public string ApplicationPath { get; set; } = "/app/worker";

    public string DataDirectory { get; set; } = "/data/runtime";

    /// <summary>
    /// Makes Chromium use /tmp instead of /dev/shm. Hosts that cannot enlarge the container's
    /// 64 MB /dev/shm (for example Home Assistant add-ons) need this to avoid renderer crashes.
    /// </summary>
    public bool DisableDevShmUsage { get; set; }

    public TimeSpan InitialRestartDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaximumRestartDelay { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

public interface IVrcxWorkerLifecycle
{
    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the original VRCX Electron renderer alive as a hidden, local worker.
/// VRChat synchronization remains in that renderer; this service only owns its process lifetime.
/// </summary>
public sealed class VrcxWorkerHostedService(
    VrcxWorkerProcessOptions options,
    ILogger<VrcxWorkerHostedService> logger) : BackgroundService, IVrcxWorkerLifecycle
{
    private const int SignalTerminate = 15;
    private const int SignalKill = 9;
    private const int SignalProbe = 0;
    private const int ErrorNoSuchProcess = 3;
    private static readonly TimeSpan MaximumGracefulShutdownTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ForcedShutdownWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly object processLock = new();
    private readonly SemaphoreSlim lifecycleOperation = new(1, 1);
    private Process? workerProcess;
    private int? workerProcessGroupId;
    private TaskCompletionSource resumeSignal = CreateResumeSignal(completed: true);
    private volatile bool isPaused;
    private volatile bool isStopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("The VRCX worker process is disabled.");
            return;
        }

        ValidateOptions(options);
        Directory.CreateDirectory(options.DataDirectory);

        var restartDelay = ClampRestartDelay(options.InitialRestartDelay);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitUntilResumedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var startedAt = DateTimeOffset.UtcNow;
            using var process = CreateProcess(options);
            try
            {
                lock (processLock)
                {
                    if (isStopping || stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    if (isPaused)
                    {
                        continue;
                    }

                    if (!process.Start())
                    {
                        throw new InvalidOperationException("The VRCX worker process could not be started.");
                    }

                    workerProcess = process;
                    // setsid(1) keeps this PID while it execs xvfb-run. It therefore also
                    // becomes the process-group ID for Xvfb, Electron and their descendants.
                    workerProcessGroupId = process.Id;
                }

                _ = DrainAsync(process.StandardOutput);
                _ = DrainAsync(process.StandardError);
                logger.LogInformation("The hidden VRCX worker started with process id {ProcessId}.", process.Id);
                await process.WaitForExitAsync(stoppingToken);
                if (isStopping || stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!isPaused)
                {
                    logger.LogWarning("The hidden VRCX worker exited with code {ExitCode}; it will be restarted.", process.ExitCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The hidden VRCX worker failed; it will be restarted.");
            }
            finally
            {
                lock (processLock)
                {
                    if (ReferenceEquals(workerProcess, process))
                    {
                        workerProcess = null;
                        workerProcessGroupId = null;
                    }
                }
            }

            if (isPaused)
            {
                continue;
            }

            if (DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(5))
            {
                restartDelay = ClampRestartDelay(options.InitialRestartDelay);
            }

            try
            {
                await Task.Delay(restartDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            restartDelay = TimeSpan.FromMilliseconds(Math.Min(
                restartDelay.TotalMilliseconds * 2,
                ClampRestartDelay(options.MaximumRestartDelay).TotalMilliseconds));
        }
    }

    /// <summary>
    /// Prevents the hidden renderer from starting and waits until its complete process group has
    /// stopped. A successful return is the write-exclusion boundary used by snapshot import.
    /// </summary>
    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleOperation.WaitAsync(cancellationToken);
        try
        {
            Process? process;
            int? processGroupId;
            lock (processLock)
            {
                if (isStopping)
                {
                    throw new InvalidOperationException("The hidden VRCX worker is stopping.");
                }

                if (!isPaused)
                {
                    isPaused = true;
                    resumeSignal = CreateResumeSignal(completed: false);
                }
                process = workerProcess;
                processGroupId = workerProcessGroupId;
            }

            // Once pause has been published, finish the bounded shutdown even if the upload
            // client disconnects. This prevents resume from racing an unconfirmed old writer.
            if (!await StopWorkerProcessAsync(process, processGroupId, CancellationToken.None, "snapshot import"))
            {
                throw new InvalidOperationException("The hidden VRCX worker could not be paused safely.");
            }
        }
        finally
        {
            lifecycleOperation.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleOperation.WaitAsync(cancellationToken);
        try
        {
            TaskCompletionSource? signal = null;
            lock (processLock)
            {
                if (!isStopping && isPaused)
                {
                    isPaused = false;
                    signal = resumeSignal;
                }
            }
            signal?.TrySetResult();
        }
        finally
        {
            lifecycleOperation.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource resume;
        lock (processLock)
        {
            isStopping = true;
            isPaused = false;
            resume = resumeSignal;
        }
        resume.TrySetResult();

        var operationHeld = false;
        try
        {
            await lifecycleOperation.WaitAsync(cancellationToken);
            operationHeld = true;
            Process? process;
            int? processGroupId;
            lock (processLock)
            {
                process = workerProcess;
                processGroupId = workerProcessGroupId;
            }
            await StopWorkerProcessAsync(process, processGroupId, cancellationToken, "server shutdown");
        }
        finally
        {
            if (operationHeld)
            {
                lifecycleOperation.Release();
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (processLock)
            {
                if (!isPaused)
                {
                    return;
                }
                wait = resumeSignal.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private async Task<bool> StopWorkerProcessAsync(
        Process? process,
        int? processGroupId,
        CancellationToken cancellationToken,
        string reason)
    {
        if (process is null || processGroupId is null)
        {
            return true;
        }

        var terminateSent = false;
        try
        {
            terminateSent = TrySendSignal(-processGroupId.Value, SignalTerminate);
        }
        catch (Win32Exception exception)
        {
            logger.LogWarning(exception, "Could not send SIGTERM to the hidden VRCX worker process group.");
        }

        // Lifecycle control can race with setsid before it creates the new group. In that
        // narrow window there cannot be established descendants yet, so terminate the
        // just-started process directly and retain the force-kill fallback.
        if (!terminateSent && !HasExited(process))
        {
            try
            {
                terminateSent = TrySendSignal(process.Id, SignalTerminate);
            }
            catch (Win32Exception exception)
            {
                logger.LogWarning(exception, "Could not send SIGTERM to the hidden VRCX worker process.");
            }
        }

        if (terminateSent)
        {
            logger.LogInformation("Waiting for the hidden VRCX worker to stop gracefully for {Reason}.", reason);
        }

        var stopped = await WaitForShutdownAsync(
            process,
            processGroupId.Value,
            options.GracefulShutdownTimeout,
            cancellationToken);
        if (stopped)
        {
            return true;
        }

        logger.LogWarning("The hidden VRCX worker did not stop during the grace period; forcing termination.");
        ForceTerminate(process, processGroupId.Value);
        return await WaitForShutdownAsync(
            process,
            processGroupId.Value,
            ForcedShutdownWait,
            cancellationToken);
    }

    private static Process CreateProcess(VrcxWorkerProcessOptions configured)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = configured.SetsidPath,
            WorkingDirectory = configured.ApplicationPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(configured.XvfbRunPath);
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("-screen 0 1280x720x24 -nolisten tcp");
        startInfo.ArgumentList.Add(configured.ElectronPath);
        startInfo.ArgumentList.Add(configured.ApplicationPath);
        startInfo.ArgumentList.Add("--server-mode");
        startInfo.ArgumentList.Add($"--config={configured.DataDirectory}");
        startInfo.ArgumentList.Add("--no-sandbox");
        if (configured.DisableDevShmUsage)
        {
            startInfo.ArgumentList.Add("--disable-dev-shm-usage");
        }
        startInfo.Environment["ELECTRON_DISABLE_SECURITY_WARNINGS"] = "true";
        startInfo.Environment["NO_AT_BRIDGE"] = "1";
        return new Process { StartInfo = startInfo };
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0)
        {
            // Drain without copying renderer output into the Web server log. This keeps
            // credentials and VRChat response bodies out of container-level logging.
        }
    }

    private static void ValidateOptions(VrcxWorkerProcessOptions configured)
    {
        var paths = new[]
        {
            configured.SetsidPath,
            configured.XvfbRunPath,
            configured.ElectronPath,
            configured.ApplicationPath,
            configured.DataDirectory
        };
        if (paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
        {
            throw new InvalidOperationException("VRCX worker paths must be absolute.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("The VRCX worker process is supported only on Linux.");
        }

        if (!File.Exists(configured.SetsidPath) ||
            !File.Exists(configured.XvfbRunPath) ||
            !File.Exists(configured.ElectronPath) ||
            !Directory.Exists(configured.ApplicationPath))
        {
            throw new InvalidOperationException("The configured VRCX worker runtime is incomplete.");
        }

        if (configured.GracefulShutdownTimeout <= TimeSpan.Zero ||
            configured.GracefulShutdownTimeout > MaximumGracefulShutdownTimeout)
        {
            throw new InvalidOperationException("The VRCX worker graceful shutdown timeout must be between zero and 25 seconds.");
        }
    }

    private async Task<bool> WaitForShutdownAsync(
        Process process,
        int processGroupId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!HasShutdown(process, processGroupId))
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= timeout || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var remaining = timeout - elapsed;
            var delay = remaining < ShutdownPollInterval ? remaining : ShutdownPollInterval;
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return true;
    }

    private void ForceTerminate(Process process, int processGroupId)
    {
        try
        {
            if (TrySendSignal(-processGroupId, SignalKill))
            {
                return;
            }
        }
        catch (Win32Exception exception)
        {
            logger.LogWarning(exception, "Could not send SIGKILL to the hidden VRCX worker process group.");
        }

        try
        {
            if (!HasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            logger.LogWarning(exception, "Could not force the hidden VRCX worker process tree to exit.");
        }
    }

    private static bool HasShutdown(Process process, int processGroupId) =>
        !ProcessGroupExists(processGroupId) && HasExited(process);

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool ProcessGroupExists(int processGroupId)
    {
        if (NativeMethods.Kill(-processGroupId, SignalProbe) == 0)
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() != ErrorNoSuchProcess;
    }

    private static bool TrySendSignal(int processOrGroupId, int signal)
    {
        if (NativeMethods.Kill(processOrGroupId, signal) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoSuchProcess)
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    private static TimeSpan ClampRestartDelay(TimeSpan value) =>
        TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 250, TimeSpan.FromMinutes(5).TotalMilliseconds));

    private static TaskCompletionSource CreateResumeSignal(bool completed)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            signal.SetResult();
        }
        return signal;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int processId, int signal);
    }
}
