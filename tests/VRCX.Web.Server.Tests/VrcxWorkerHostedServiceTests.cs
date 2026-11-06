using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VRCX.Web.Server;
using Xunit;

namespace VRCX.Web.Server.Tests;

public sealed class VrcxWorkerHostedServiceTests
{
    [Fact]
    public async Task PauseStopsWorkerWithoutRestartUntilResume()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new WorkerWorkspace();
        workspace.WriteExecutable("xvfb-run", """
            #!/bin/sh
            shift 3
            exec "$@"
            """);
        workspace.WriteExecutable("electron", """
            #!/bin/sh
            app_dir="$1"
            count_file="$app_dir/start-count"
            count=0
            if [ -f "$count_file" ]; then read -r count < "$count_file"; fi
            count=$((count + 1))
            printf '%s' "$count" > "$count_file"
            trap 'printf "%s" "$count" > "$app_dir/last-terminated"; exit 0' TERM
            while :; do
                sleep 1 &
                wait $!
            done
            """);

        using var service = workspace.CreateService(TimeSpan.FromSeconds(5));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForConditionAsync(() => ReadInt(workspace.PathOf("start-count")) == 1);

            using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await service.PauseAsync(pauseTimeout.Token);
            Assert.Equal(1, ReadInt(workspace.PathOf("last-terminated")));

            await Task.Delay(400);
            Assert.Equal(1, ReadInt(workspace.PathOf("start-count")));

            await service.ResumeAsync(CancellationToken.None);
            await WaitForConditionAsync(() => ReadInt(workspace.PathOf("start-count")) == 2);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(cleanupTimeout.Token);
        }
    }

    [Fact]
    public async Task StopAsyncSendsSigtermToTheWorkerAndItsDescendants()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new WorkerWorkspace();
        workspace.WriteExecutable("xvfb-run", """
            #!/bin/sh
            shift 3
            exec "$@"
            """);
        workspace.WriteExecutable("child-worker", """
            #!/bin/sh
            app_dir="$1"
            trap 'printf terminated > "$app_dir/child-term"; exit 0' TERM
            : > "$app_dir/child-started"
            while :; do
                sleep 1 &
                wait $!
            done
            """);
        workspace.WriteExecutable("electron", """
            #!/bin/sh
            app_dir="$1"
            "$app_dir/child-worker" "$app_dir" &
            child_pid=$!
            trap 'printf terminated > "$app_dir/parent-term"; wait "$child_pid"; exit 0' TERM
            : > "$app_dir/parent-started"
            wait "$child_pid"
            """);

        using var service = workspace.CreateService(TimeSpan.FromSeconds(5));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForFileAsync(workspace.PathOf("parent-started"));
            await WaitForFileAsync(workspace.PathOf("child-started"));

            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await service.StopAsync(stopTimeout.Token);

            Assert.True(File.Exists(workspace.PathOf("parent-term")));
            Assert.True(File.Exists(workspace.PathOf("child-term")));
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await service.StopAsync(cleanupTimeout.Token);
        }
    }

    [Fact]
    public async Task StopAsyncForceKillsAWorkerThatDoesNotExitAfterSigterm()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = new WorkerWorkspace();
        workspace.WriteExecutable("xvfb-run", """
            #!/bin/sh
            shift 3
            exec "$@"
            """);
        workspace.WriteExecutable("electron", """
            #!/bin/sh
            app_dir="$1"
            trap 'printf received > "$app_dir/term-received"' TERM
            printf '%s' "$$" > "$app_dir/worker-pid"
            : > "$app_dir/worker-started"
            while :; do
                sleep 1 &
                wait $!
            done
            """);

        using var service = workspace.CreateService(TimeSpan.FromMilliseconds(150));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForFileAsync(workspace.PathOf("worker-started"));
            var processId = int.Parse(await File.ReadAllTextAsync(workspace.PathOf("worker-pid")));
            var stopwatch = Stopwatch.StartNew();

            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await service.StopAsync(stopTimeout.Token);

            Assert.True(File.Exists(workspace.PathOf("term-received")));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(() => !Directory.Exists($"/proc/{processId}"));
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await service.StopAsync(cleanupTimeout.Token);
        }
    }

    private static async Task WaitForFileAsync(string path) =>
        await WaitForConditionAsync(() => File.Exists(path));

    private static int ReadInt(string path) =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path), out var value) ? value : 0;

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The worker process did not reach the expected state before the timeout.");
    }

    private sealed class WorkerWorkspace : IDisposable
    {
        public WorkerWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"vrcx-worker-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string PathOf(string name) => Path.Combine(Root, name);

        public void WriteExecutable(string name, string contents)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException();
            }

            var path = PathOf(name);
            File.WriteAllText(path, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        public VrcxWorkerHostedService CreateService(TimeSpan gracefulShutdownTimeout) =>
            new(
                new VrcxWorkerProcessOptions
                {
                    Enabled = true,
                    SetsidPath = "/usr/bin/setsid",
                    XvfbRunPath = PathOf("xvfb-run"),
                    ElectronPath = PathOf("electron"),
                    ApplicationPath = Root,
                    DataDirectory = PathOf("data"),
                    GracefulShutdownTimeout = gracefulShutdownTimeout
                },
                NullLogger<VrcxWorkerHostedService>.Instance);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
