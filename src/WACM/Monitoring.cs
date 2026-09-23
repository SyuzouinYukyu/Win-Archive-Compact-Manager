using System.Collections.Concurrent;

namespace WACM;
public sealed class MonitorEngine(AppData data, Store store, AppLog log, bool testWithoutJournal = false)
{
    private readonly ConcurrentDictionary<string, string> statuses = new();
    public string Status => "監視 " + (store.Flag("paused") == "1" ? "一時停止" : "動作中") + "
" + string.Join("
", statuses.OrderBy(x => x.Key).Select(x => x.Value));
    public async Task Run(CancellationToken token)
    {
        var settings = data.Load();
        await Task.WhenAll(settings.Roots.Where(r => r.Enabled).Select(r => Task.Run(() => RootLoop(r, settings, token), token)).Append(Idle(token)));
    }
    private static async Task Idle(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); }
    private async Task RootLoop(WatchRoot root, Settings settings, CancellationToken token)
    {
        string job = "watch:" + root.Id; store.Recover(job);
        var options = new JobOptions(root.Method, root.Skip, root.Extensions, settings.MinimumBytes, root.Parallel);
        var engine = new CompressionEngine(log);
        while (!token.IsCancellationRequested)
        {
            var path = root.Resolve();
            try
            {
                if (!Directory.Exists(path)) { statuses[root.Id] = root.Path + " : オフライン"; await Task.Delay(10000, token); continue; }
                Native.EnsureLocalNtfs(path); using (Targets.Guard(path)) { }
                var canonical = Targets.Canonical(path);
                void Event(string p, bool directory = false, uint reason = 0)
                {
                    if (!Targets.Contains(canonical, p)) return;
                    // No filesystem traversal in a watcher callback. Directory moves are queued and scanned by the worker.
                    store.Enqueue(job, root.Id, p, directory, options, due: DateTimeOffset.UtcNow.AddSeconds(settings.DebounceSeconds).ToUnixTimeMilliseconds(), refresh: true);
                }
                int overflow = 0;
                using var watcher = new FileSystemWatcher(canonical) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                void Handle(string p)
                {
                    try { Event(p, Directory.Exists(p)); }
                    catch (Exception ex) { Interlocked.Exchange(ref overflow, 1); log.Write("ERROR", p, "監視通知永続化", ex.Message); }
                }
                watcher.Created += (_, e) => Handle(e.FullPath); watcher.Changed += (_, e) => { if (!Directory.Exists(e.FullPath)) Handle(e.FullPath); }; watcher.Renamed += (_, e) => Handle(e.FullPath);
                watcher.Error += (_, e) => { Interlocked.Exchange(ref overflow, 1); log.Write("WARN", path, "監視バッファ/エラー", e.GetException().Message); };
                watcher.EnableRaisingEvents = true;
                var parallel = root.Parallel > 0 ? root.Parallel : StorageDetection.Detect(path).Parallel;
                var nextJournal = DateTime.MinValue; var nextFallback = DateTime.MinValue; bool first = true;
                while (!token.IsCancellationRequested && Directory.Exists(path))
                {
                    if (store.Flag("paused") == "1") { statuses[root.Id] = root.Path + " : 一時停止"; await Task.Delay(1000, token); continue; }
                    bool missing = Interlocked.Exchange(ref overflow, 0) != 0;
                    if (first || missing || DateTime.UtcNow >= nextJournal)
                    {
                        try
                        {
                            if (testWithoutJournal) throw new NotSupportedException("専用テスト: ボリューム全体のUSNを参照せずフォールバックを検証");
                            using var journal = new UsnJournal(Native.Volume(path).Guid); var q = journal.Query(); var cp = store.Checkpoint(root.Id);
                            if (UsnJournal.NeedsResync(q, cp))
                            {
                                statuses[root.Id] = root.Path + " : USN初期化/失効による再同期";
                                log.Write("WARN", path, "USN再同期", "チェックポイントなし、Journal変更、またはWrap。メタデータを逐次再照合します。");
                                Scan(canonical, root, options, job, token); store.Checkpoint(root.Id, q.Id, q.Next);
                            }
                            else journal.Replay(q.Id, cp!.Value.Usn, q.Next, canonical, Event, next => store.Checkpoint(root.Id, q.Id, next), () => { if (DateTime.UtcNow >= nextFallback) { Scan(canonical, root, options, job, token); nextFallback = DateTime.UtcNow.AddMinutes(15); } }, token);
                            statuses[root.Id] = root.Path + " : 監視中（USN補完有効）";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            log.Write("WARN", path, "USN補完利用不能", ex.Message);
                            statuses[root.Id] = root.Path + " : USN利用不能・15分間隔で安全な再照合";
                            if (first || missing || DateTime.UtcNow >= nextFallback) { Scan(canonical, root, options, job, token); nextFallback = DateTime.UtcNow.AddMinutes(15); }
                        }
                        first = false; nextJournal = DateTime.UtcNow.AddSeconds(30);
                    }
                    await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
                    {
                        var item = store.Claim(job); if (item == null) return;
                        try
                        {
                            if (!Targets.Contains(canonical, item.Path)) throw new NotSupportedException("監視範囲外");
                            using var guard = Targets.Guard(item.Path);
                            if (item.Directory)
                            {
                                Scan(item.Path, root, options, job, token);
                                store.Complete(item, new(Outcome.Success, item.Path)); return;
                            }
                            var before = Native.Inspect(item.Path);
                            if (store.Unchanged(root.Id, item.Path, before, root.Method)) { store.Complete(item, new(Outcome.Already, item.Path)); return; }
                            bool known = store.Known(root.Id, item.Path);
                            if (known ? !root.Updates : !root.NewFiles) { store.Seen(root.Id, item.Path, before, root.Method); store.Complete(item, new(Outcome.Skipped, item.Path)); return; }
                            if (DateTime.UtcNow.Ticks - before.WriteTicks < TimeSpan.FromSeconds(settings.DebounceSeconds).Ticks) { store.Retry(item, "書き込み静止待ち"); return; }
                            // Refuse active writers, then compare metadata a second time after a quiet interval.
                            using (new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            await Task.Delay(500, token);
                            var stable = Native.Inspect(item.Path);
                            if (stable.Length != before.Length || stable.WriteTicks != before.WriteTicks) { store.Retry(item, "書き込み継続中"); return; }
                            var result = await engine.Execute(item.Path, false, options, token);
                            if (result.Outcome is Outcome.Failed or Outcome.Denied) { store.Retry(item, result.Detail); log.Write("WARN", item.Path, "監視再試行", result.Detail); return; }
                            store.Complete(item, result);
                            if (result.Outcome != Outcome.Cancelled)
                            {
                                var after = Native.Inspect(item.Path);
                                if (after.Length == stable.Length && after.WriteTicks == stable.WriteTicks) store.Seen(root.Id, item.Path, after, root.Method);
                            }
                            log.Write("INFO", item.Path, "監視 " + result.Outcome, result.Detail);
                        }
                        catch (OperationCanceledException) { store.Complete(item, new(Outcome.Cancelled, item.Path)); throw; }
                        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { store.Complete(item, new(Outcome.Skipped, item.Path, Detail: "削除済み")); }
                        catch (FileNotFoundException) { store.Complete(item, new(Outcome.Skipped, item.Path, Detail: "削除済み")); }
                        catch (Exception ex) { store.Retry(item, ex.Message); log.Write("WARN", item.Path, "安定待機/再試行", ex.Message); }
                    }));
                    await Task.Delay(250, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { statuses[root.Id] = root.Path + " : エラー " + ex.Message; log.Write("ERROR", path, "監視", ex.ToString()); await Task.Delay(10000, token); }
        }
    }
    private void Scan(string path, WatchRoot root, JobOptions options, string job, CancellationToken token)
    {
        foreach (var entry in Targets.Enumerate([path], (p, e) => log.Write("WARN", p, "再照合", e.Message), token))
        {
            if (entry.Directory) continue;
            try
            {
                var state = Native.Inspect(entry.Path);
                if (!store.Unchanged(root.Id, entry.Path, state, root.Method)) store.Enqueue(job, root.Id, entry.Path, false, options, state.Length, DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(), true);
            }
            catch (Exception ex) { log.Write("WARN", entry.Path, "再照合", ex.Message); }
        }
    }
}
