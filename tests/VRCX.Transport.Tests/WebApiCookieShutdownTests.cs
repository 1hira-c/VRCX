using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VRCX.Transport;
using Xunit;

namespace VRCX.Transport.Tests;

public sealed class WebApiCookieShutdownTests
{
    [Fact]
    public void ExitDrainsAnInProgressTimerSaveBeforeDisposingTheCookieKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var protector = new ServerCookieProtector(key);
        using var verifier = new ServerCookieProtector(key);
        CryptographicOperations.ZeroMemory(key);
        var api = CreateApi(protector);
        var persisted = new ConcurrentQueue<string>();
        using var saving = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        using var exiting = new ManualResetEventSlim();
        using var exited = new ManualResetEventSlim();
        Exception? exitFailure = null;
        Thread? exitThread = null;
        SQLite.Instance.Write = (_, parameters) =>
        {
            if (persisted.IsEmpty)
            {
                saving.Set();
                Assert.True(releaseSave.Wait(TimeSpan.FromSeconds(10)));
            }
            persisted.Enqueue((string)parameters["@value"]);
        };
        try
        {
            api.GetCookies();
            GetField<System.Threading.Timer>(api, "_timer").Change(0, Timeout.Infinite);
            Assert.True(saving.Wait(TimeSpan.FromSeconds(5)));
            exitThread = new Thread(() =>
            {
                exiting.Set();
                try { api.Exit(); }
                catch (Exception exception) { exitFailure = exception; }
                finally { exited.Set(); }
            });
            exitThread.Start();
            Assert.True(exiting.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => exited.IsSet ||
                (exitThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            Assert.False(exited.IsSet);
            Assert.Equal("still alive"u8.ToArray(), protector.Unprotect(protector.Protect("still alive"u8)));

            releaseSave.Set();
            Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(exitFailure);
            var snapshot = Assert.Single(persisted);
            Assert.Contains("test-cookie", Encoding.UTF8.GetString(verifier.Unprotect(snapshot)), StringComparison.Ordinal);
            Assert.Throws<ObjectDisposedException>(() => protector.Protect("closed"u8));

            // A late renderer/HTTP completion cannot schedule another encrypted
            // write with the destroyed key after SQLite is about to close.
            api.GetCookies();
            api.SaveCookies();
            api.Exit();
            Assert.False(GetField<bool>(api, "_cookieDirty"));
            Assert.Single(persisted);
        }
        finally
        {
            releaseSave.Set();
            if (exitThread?.IsAlive == true) exitThread.Join(TimeSpan.FromSeconds(10));
            api.Exit();
            SQLite.Instance.Write = null;
        }
    }

    [Fact]
    public async Task DirtyNotificationDuringASaveIsPreservedForTheNextSnapshot()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var protector = new ServerCookieProtector(key);
        using var verifier = new ServerCookieProtector(key);
        CryptographicOperations.ZeroMemory(key);
        var api = CreateApi(protector);
        var snapshots = new ConcurrentQueue<string>();
        using var saving = new ManualResetEventSlim();
        using var releaseSave = new ManualResetEventSlim();
        using var marking = new ManualResetEventSlim();
        using var marked = new ManualResetEventSlim();
        Thread? markThread = null;
        Task? save = null;
        Exception? markFailure = null;
        SQLite.Instance.Write = (_, parameters) =>
        {
            if (snapshots.IsEmpty)
            {
                saving.Set();
                Assert.True(releaseSave.Wait(TimeSpan.FromSeconds(10)));
            }
            snapshots.Enqueue((string)parameters["@value"]);
        };
        try
        {
            api.GetCookies();
            save = Task.Run(api.SaveCookies);
            Assert.True(saving.Wait(TimeSpan.FromSeconds(5)));
            api.CookieContainer.Add(new Cookie("late-cookie", "new-value", "/", "example.invalid"));
            markThread = new Thread(() =>
            {
                marking.Set();
                try { api.GetCookies(); }
                catch (Exception exception) { markFailure = exception; }
                finally { marked.Set(); }
            });
            markThread.Start();
            Assert.True(marking.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => marked.IsSet ||
                (markThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            Assert.False(marked.IsSet);
            releaseSave.Set();
            await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(marked.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(markFailure);
            api.SaveCookies();
            Assert.Equal(2, snapshots.Count);
            Assert.DoesNotContain("late-cookie", Encoding.UTF8.GetString(verifier.Unprotect(snapshots.First())), StringComparison.Ordinal);
            Assert.Contains("late-cookie", Encoding.UTF8.GetString(verifier.Unprotect(snapshots.Last())), StringComparison.Ordinal);
        }
        finally
        {
            releaseSave.Set();
            if (save is not null) await save.WaitAsync(TimeSpan.FromSeconds(10));
            if (markThread?.IsAlive == true) markThread.Join(TimeSpan.FromSeconds(10));
            api.Exit();
            SQLite.Instance.Write = null;
        }
    }

    private static WebApi CreateApi(ServerCookieProtector protector)
    {
        var api = new WebApi();
        typeof(WebApi).GetField("_serverCookieProtector", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, protector);
        api.CookieContainer.Add(new Cookie("test-cookie", "test-value", "/", "example.invalid"));
        return api;
    }

    private static T GetField<T>(WebApi api, string name) =>
        (T)typeof(WebApi).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(api)!;
}
