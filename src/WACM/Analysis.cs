namespace WACM;
public sealed class AnalysisReport
{
    public long Files, Folders, Logical, Allocated, Candidate, Skipped, Compressed, Errors;
    public Dictionary<string, (long Count, long Bytes)> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Method, long> Methods { get; } = [];
    public override string ToString() => $"ファイル {Files:N0} / フォルダ {Folders:N0} / 確認エラー {Errors:N0}\r\n論理 {Logical:N0} B / ディスク使用 {Allocated:N0} B\r\n候補 {Candidate:N0} B / 事前スキップ {Skipped:N0} B / 圧縮済 {Compressed:N0}\r\n" +
        string.Join(" / ", Methods.Select(x => $"{x.Key}: {x.Value:N0}")) + "\r\n拡張子別（容量順、先頭200種類）\r\n" + string.Join("\r\n", Extensions.OrderByDescending(x => x.Value.Bytes).Take(200).Select(x => $"{x.Key}: {x.Value.Count:N0}件 {x.Value.Bytes:N0} B"));
}
public static class Analysis
{
    public static AnalysisReport Run(IEnumerable<string> paths, JobOptions options, AppLog log, Action<string> progress, CancellationToken token)
    {
        var r = new AnalysisReport(); var last = Environment.TickCount64;
        foreach (var entry in Targets.Enumerate(paths, (p, e) => { r.Errors++; log.Write("WARN", p, "解析", e.Message); }, token))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Directory) { r.Folders++; continue; }
            try
            {
                using var g = Targets.Guard(entry.Path); var s = Native.Inspect(entry.Path);
                r.Files++; r.Logical += s.Length; r.Allocated += s.Allocated;
                if (s.Method != Method.None) r.Compressed++;
                r.Methods[s.Method] = r.Methods.GetValueOrDefault(s.Method) + 1;
                if (CompressionEngine.Excluded(entry.Path, s, options)) r.Skipped += s.Length; else r.Candidate += s.Length;
                var ext = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
                if (ext.Length == 0) ext = "(拡張子なし)";
                if (r.Extensions.Count >= 4096 && !r.Extensions.ContainsKey(ext)) ext = "(その他)";
                var old = r.Extensions.GetValueOrDefault(ext); r.Extensions[ext] = (old.Count + 1, old.Bytes + s.Length);
            }
            catch (Exception ex) { r.Errors++; log.Write("WARN", entry.Path, "解析", ex.Message); }
            if (Environment.TickCount64 - last >= 300) { progress($"解析中 {r.Files:N0} 件 / {r.Logical:N0} B\r\n{entry.Path}"); last = Environment.TickCount64; }
        }
        return r;
    }
}
