using System.Security.Cryptography;
using System.Security.Principal;
using WACM;

internal static class IntegrationTests
{
    public static async Task Run(string fixture, byte[] payload, Action<bool, string> check)
    {
        var folder = Path.Combine(fixture, "監視専用"); Directory.CreateDirectory(folder);
        var canonical = Targets.Canonical(folder); using (Targets.Guard(canonical)) { }
        check(canonical.StartsWith(@"\?Volume{"), "Volume GUID canonical guard");
        var data = new AppData(Path.Combine(fixture, "monitor-state")); var log = new AppLog(data, "monitor"); var store = new Store(data);
        var volume = Native.Volume(folder).Guid;
        var root = new WatchRoot { Path = folder, Volume = volume, Relative = canonical[volume.Length..], Method = Method.LZX, Skip = false };
        data.Save(new Settings { Roots = [root], DebounceSeconds = 1 });
        var monitor = new MonitorEngine(data, store, log, true);
        using var stop = new CancellationTokenSource(); var run = Task.Run(() => monitor.Run(stop.Token));
        try
        {
            await Wait(() => monitor.Status.Contains("USN利用不能"), "monitor startup: " + canonical);
            var a = Path.Combine(folder, "A.txt"); var b = Path.Combine(folder, "B.txt");
            await File.WriteAllBytesAsync(a, payload); await File.WriteAllBytesAsync(b, payload);
            await Wait(() => State(a) == Method.LZX && State(b) == Method.LZX, "new file compression");
            check(true, "watcher new files automatic LZX");
            await Task.Delay(1500);
            var beforeB = Native.Inspect(b); var bHash = SHA256.HashData(File.ReadAllBytes(b));
            await File.AppendAllTextAsync(a, "変更ファイルだけ再圧縮
");
            var changedHash = SHA256.HashData(File.ReadAllBytes(a));
            await Wait(() => State(a) == Method.LZX && store.Unchanged(root.Id, Targets.Canonical(a), Native.Inspect(a)), "updated file recompression completion");
            check(changedHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(a))), "watcher updated A logical content");
            check(Native.Inspect(b) == beforeB && bHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(b))), "watcher A update leaves B unchanged");
            var locked = Path.Combine(folder, "書き込み中.txt");
            using (var writer = new FileStream(locked, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                await writer.WriteAsync(payload); await writer.FlushAsync(); await Task.Delay(3500);
                check(State(locked) == Method.None, "locked writer never compressed");
            }
            await Wait(() => State(locked) == Method.LZX, "locked writer retry after close"); check(true, "locked file bounded retry succeeds after close");
            store.FlagSet("paused", "1"); await Task.Delay(1500);
            var paused = Path.Combine(folder, "一時停止.txt"); await File.WriteAllBytesAsync(paused, payload); await Task.Delay(2000);
            check(State(paused) == Method.None, "monitor pause"); store.FlagSet("paused", "0");
            await Wait(() => State(paused) == Method.LZX, "monitor resume"); check(true, "monitor resume pending queue");
        }
        finally { await stop.CancelAsync(); try { await run; } catch (OperationCanceledException) { } }
        check(!run.IsFaulted, "monitor clean cancellation without service install");
        using var ipcStop = new CancellationTokenSource();
        var testPipe = "WACM.Test." + Guid.NewGuid().ToString("N");
        var server = Ipc.Serve(WindowsIdentity.GetCurrent().User!.Value, request => Task.FromResult(request == "status" ? "service-logic-ok" : "denied"), log, ipcStop.Token, testPipe);
        try { check(await Ipc.Call("status", testPipe) == "service-logic-ok", "actual local named pipe authenticated roundtrip"); }
        finally { await ipcStop.CancelAsync(); await server; }
    }
    private static Method State(string path) { try { return Native.Inspect(path).Method; } catch { return Method.Unknown; } }
    private static async Task Wait(Func<bool> predicate, string detail)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(detail); await Task.Delay(200); }
    }
}
