namespace WACM;
public sealed class JobRunner(Store store, AppLog log)
{
    public async Task Run(string id, string[] targets, JobOptions options, bool staged, Action<string> progress, CancellationToken token)
    {
        store.Recover(id, true);
        if (!staged)
        {
            long count = 0; bool incomplete = false;
            foreach (var e in Targets.Enumerate(targets, (p, ex) => { log.Write("ERROR", p, "対象列挙", ex.Message); store.RecordIssue(id, p, options, ex); if (ex is not NotSupportedException) incomplete = true; }, token))
            {
                long bytes = 0;
                try { if (!e.Directory) bytes = new FileInfo(e.Path).Length; } catch (Exception ex) { log.Write("WARN", e.Path, "サイズ取得", ex.Message); }
                store.Enqueue(id, "", e.Path, e.Directory, options, bytes);
                if (++count % 100 == 0) progress($"対象を永続キューへ登録中 {count:N0} 件\r\n{e.Path}");
            }
            if (!incomplete) store.Staged(id);
        }
        var parallel = options.Parallel > 0 ? options.Parallel : targets.Select(p => StorageDetection.Detect(p).Parallel).DefaultIfEmpty(1).Min();
        var engine = new CompressionEngine(log);
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            while (!token.IsCancellationRequested)
            {
                var item = store.Claim(id); if (item == null) break;
                progress(item.Path);
                var r = await engine.Execute(item.Path, item.Directory, item.Options, token); store.Complete(item, r);
                log.Write(r.Outcome is Outcome.Failed or Outcome.Denied ? "ERROR" : "INFO", r.Path, r.Outcome.ToString(), r.Detail);
                if (r.Outcome == Outcome.Cancelled) break;
            }
        }));
    }
}
