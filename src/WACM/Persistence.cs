using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace WACM;
public sealed class AppData
{
    public string Root { get; }
    public string SettingsFile => System.IO.Path.Combine(Root, "settings.json");
    public AppData(string? root = null)
    {
        Root = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WACM");
        Directory.CreateDirectory(Root);
        if (File.GetAttributes(Root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("データ保存先がReparse Pointです。");
    }
    public Settings Load()
    {
        if (!File.Exists(SettingsFile)) return new();
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? throw new InvalidDataException("設定が空です。");
        result.Validate(); return result;
    }
    public void Save(Settings settings)
    {
        settings.Validate();
        using var gate = new FileStream(System.IO.Path.Combine(Root, "settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var tmp = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(f, settings, new JsonSerializerOptions { WriteIndented = true }); f.Flush(true); }
            File.Move(tmp, SettingsFile, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
public sealed class AppLog
{
    public const long Limit = 10L * 1024 * 1024;
    private readonly object gate = new();
    private readonly string folder;
    private readonly string name;
    private readonly TimeProvider clock;
    private readonly Queue<string> recent = new();
    private readonly Dictionary<(string Level, string Path, string Action, string Message), (DateTimeOffset First, long Suppressed)> warnings = new();
    private int writes;
    public string Folder => folder;
    public AppLog(AppData data, string name = "gui", TimeProvider? clock = null) { folder = System.IO.Path.Combine(data.Root, "Logs"); Directory.CreateDirectory(folder); this.name = name; this.clock = clock ?? TimeProvider.System; }
    public void Write(string level, string path, string operation, string detail)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (level == "WARN")
            {
                var key = (level, path, operation, detail);
                if (warnings.TryGetValue(key, out var previous))
                {
                    if (now - previous.First < TimeSpan.FromMinutes(10)) { warnings[key] = (previous.First, previous.Suppressed + 1); return; }
                    if (previous.Suppressed > 0) Emit(now, "WARN", path, operation, $"同一警告を {previous.Suppressed} 回抑制しました。");
                }
                warnings[key] = (now, 0);
            }
            Emit(now, level, path, operation, detail);
            if (++writes % 512 == 0)
                foreach (var key in warnings.Where(x => now - x.Value.First > TimeSpan.FromMinutes(20)).Select(x => x.Key).ToArray()) warnings.Remove(key);
        }
    }
    private void Emit(DateTimeOffset now, string level, string path, string operation, string detail)
    {
        var line = $"{now.ToLocalTime():O}	{level}	{JsonSerializer.Serialize(path)}	{operation.Replace('', ' ').Replace('
', ' ')}	{detail.Replace('', ' ').Replace('
', ' ')}";
        // Keep even exceptional single messages within one file's bound without splitting a UTF-8 line.
        var maxChars = (int)Math.Min(int.MaxValue, Limit / 4);
        if (line.Length > maxChars) line = line[..maxChars] + "…";
        var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
        while (bytes.LongLength > Limit && line.Length > 0) { line = line[..(line.Length / 2)] + "…"; bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine); }
        var file = System.IO.Path.Combine(folder, name + ".log");
        try
        {
            var length = File.Exists(file) ? new FileInfo(file).Length : 0;
            if (length + bytes.LongLength > Limit) Rotate(file);
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must not stop compression or monitoring. A failed rename may still allow append.
            try { using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read); stream.Write(bytes); } catch (Exception fallback) when (fallback is IOException or UnauthorizedAccessException) { }
        }
        recent.Enqueue(line); while (recent.Count > 300) recent.Dequeue();
    }
    private static void Rotate(string file)
    {
        for (int i = 5; i >= 1; i--)
        {
            var source = i == 1 ? file : file + "." + (i - 1);
            var target = file + "." + i;
            if (File.Exists(source)) File.Move(source, target, true);
        }
    }
    public string Recent() { lock (gate) return string.Join(Environment.NewLine, recent); }
}
public sealed class Store
{
    private readonly string connectionString;
    private readonly object cleanupGate = new();
    private DateTimeOffset nextCleanup;
    public Store(AppData data)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(data.Root, "state.db"), DefaultTimeout = 30, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, targets TEXT NOT NULL, options TEXT NOT NULL, staged INTEGER NOT NULL DEFAULT 0, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work(id INTEGER PRIMARY KEY, job TEXT NOT NULL, root TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, directory INTEGER NOT NULL, state TEXT NOT NULL DEFAULT 'pending', due INTEGER NOT NULL DEFAULT 0, retry INTEGER NOT NULL DEFAULT 0, generation INTEGER NOT NULL DEFAULT 1, options TEXT NOT NULL, logical INTEGER NOT NULL DEFAULT 0, beforeBytes INTEGER NOT NULL DEFAULT 0, afterBytes INTEGER NOT NULL DEFAULT 0, detail TEXT NOT NULL DEFAULT '', UNIQUE(job,path));
            CREATE INDEX IF NOT EXISTS work_due ON work(job,state,due);
            CREATE TABLE IF NOT EXISTS checkpoints(root TEXT PRIMARY KEY, journal TEXT NOT NULL, usn INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS seen(root TEXT NOT NULL, path TEXT NOT NULL COLLATE NOCASE, length INTEGER NOT NULL, ticks INTEGER NOT NULL, PRIMARY KEY(root,path));
            CREATE TABLE IF NOT EXISTS flags(name TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        // Additive migration: old rows remain usable and are never pruned without a completion time.
        foreach (var (table, column, definition) in new[] { ("work", "completedUtc", "INTEGER NOT NULL DEFAULT 0"), ("seen", "requested", "TEXT"), ("seen", "actual", "TEXT") })
        {
            using var info = c.CreateCommand(); info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader(); bool exists = false;
            while (reader.Read()) if (reader.GetString(1) == column) exists = true;
            reader.Close();
            if (!exists) { using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}"; alter.ExecuteNonQuery(); }
        }
        MaybeCleanup();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static SqliteCommand Command(SqliteConnection c, string sql, params (string, object?)[] args)
    { var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value); return cmd; }
    public void Exec(string sql, params (string, object?)[] args) { using var c = Open(); using var cmd = Command(c, sql, args); cmd.ExecuteNonQuery(); }
    public string CreateJob(string[] targets, JobOptions options)
    {
        var id = Guid.NewGuid().ToString("N"); Exec("INSERT INTO jobs(id,targets,options,created) VALUES($id,$t,$o,$c)", ("$id", id), ("$t", JsonSerializer.Serialize(targets)), ("$o", JsonSerializer.Serialize(options)), ("$c", DateTime.UtcNow.ToString("O"))); return id;
    }
    public List<(string Id, string[] Targets, JobOptions Options, bool Staged)> Jobs()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT id,targets,options,staged FROM jobs WHERE staged=0 OR EXISTS(SELECT 1 FROM work WHERE job=jobs.id AND state IN ('pending','running','Failed','Cancelled','Denied')) ORDER BY created"); using var r = cmd.ExecuteReader();
        var list = new List<(string, string[], JobOptions, bool)>();
        while (r.Read()) list.Add((r.GetString(0), JsonSerializer.Deserialize<string[]>(r.GetString(1))!, JsonSerializer.Deserialize<JobOptions>(r.GetString(2))!, r.GetInt64(3) != 0)); return list;
    }
    public void Enqueue(string job, string root, string path, bool directory, JobOptions options, long bytes = 0, long? due = null, bool refresh = false)
    {
        var conflict = refresh ? "DO UPDATE SET generation=work.generation+1,completedUtc=0,due=excluded.due,retry=0,options=excluded.options,directory=excluded.directory,state=CASE WHEN work.state='running' THEN 'running' ELSE 'pending' END" : "DO NOTHING";
        Exec("INSERT INTO work(job,root,path,directory,options,logical,due) VALUES($j,$r,$p,$d,$o,$b,$u) ON CONFLICT(job,path) " + conflict,
            ("$j", job), ("$r", root), ("$p", path), ("$d", directory ? 1 : 0), ("$o", JsonSerializer.Serialize(options)), ("$b", bytes), ("$u", due ?? 0));
    }
    public void RecordIssue(string job, string path, JobOptions options, Exception ex)
    {
        var state = ex is NotSupportedException ? "Unsupported" : ex is UnauthorizedAccessException ? "Denied" : "Failed";
        Exec("INSERT INTO work(job,root,path,directory,options,state,detail) VALUES($j,'',$p,1,$o,$s,$e) ON CONFLICT(job,path) DO UPDATE SET state=$s,detail=$e", ("$j",job),("$p",path),("$o",JsonSerializer.Serialize(options)),("$s",state),("$e",ex.Message));
    }
    public WorkItem? Claim(string job)
    {
        MaybeCleanup();
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, "SELECT id,job,root,path,directory,retry,generation,options FROM work WHERE job=$j AND state='pending' AND due<=$n ORDER BY directory DESC,id LIMIT 1", ("$j", job), ("$n", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())); cmd.Transaction = tx;
        WorkItem? item;
        using (var r = cmd.ExecuteReader()) { item = r.Read() ? new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt32(5), r.GetInt64(6), JsonSerializer.Deserialize<JobOptions>(r.GetString(7))!) : null; }
        if (item != null) { using var update = Command(c, "UPDATE work SET state='running' WHERE id=$i", ("$i", item.Id)); update.Transaction = tx; update.ExecuteNonQuery(); }
        tx.Commit(); return item;
    }
    public void Complete(WorkItem item, Result result)
    {
        Exec("UPDATE work SET state=CASE WHEN generation<>$g OR $s='Cancelled' THEN 'pending' ELSE $s END,completedUtc=CASE WHEN generation<>$g OR $s='Cancelled' OR $s NOT IN ('Success','Already','Skipped') THEN 0 ELSE $now END,logical=$l,beforeBytes=$b,afterBytes=$a,detail=$d WHERE id=$i",
            ("$g", item.Generation), ("$s", result.Outcome.ToString()), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$l", result.Logical), ("$b", result.Before), ("$a", result.After), ("$d", result.Detail), ("$i", item.Id));
    }
    public void Retry(WorkItem item, string detail)
    {
        var failed = item.Retry >= 7;
        Exec("UPDATE work SET state=CASE WHEN generation<>$g THEN 'pending' ELSE $s END,completedUtc=0,retry=CASE WHEN generation<>$g THEN 0 ELSE retry+1 END,due=CASE WHEN generation<>$g THEN due ELSE $d END,detail=$e WHERE id=$i", ("$s", failed ? "Failed" : "pending"), ("$d", DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * (1 << item.Retry))).ToUnixTimeMilliseconds()), ("$e", detail), ("$i", item.Id), ("$g", item.Generation));
    }
    public void Recover(string job, bool failures = false) => Exec("UPDATE work SET state='pending',completedUtc=0,retry=0 WHERE job=$j AND state IN (" + (failures ? "'running','Failed','Denied','Cancelled'" : "'running'") + ")", ("$j", job));
    public void RetryFailed(string job) => Exec("UPDATE work SET state='pending',completedUtc=0,retry=0,due=0 WHERE job=$j AND state IN ('Failed','Denied','Cancelled')", ("$j", job));
    public void Staged(string job) => Exec("UPDATE jobs SET staged=1 WHERE id=$j", ("$j", job));
    public Totals Totals(string job)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT COUNT(*),COALESCE(SUM(CASE WHEN state NOT IN ('pending','running') THEN 1 ELSE 0 END),0),COALESCE(SUM(logical),0),COALESCE(SUM(CASE WHEN state NOT IN ('pending','running') THEN logical ELSE 0 END),0),COALESCE(SUM(beforeBytes),0),COALESCE(SUM(afterBytes),0) FROM work WHERE job=$j AND directory=0", ("$j", job));
        long[] a = new long[6]; using (var r = cmd.ExecuteReader()) { r.Read(); for (var i = 0; i < 6; i++) a[i] = r.GetInt64(i); }
        using var counts = Command(c, "SELECT state,COUNT(*) FROM work WHERE job=$j GROUP BY state", ("$j", job)); using var reader = counts.ExecuteReader(); var list = new List<string>(); while (reader.Read()) list.Add($"{reader.GetString(0)}={reader.GetInt64(1):N0}");
        return new(a[0], a[1], a[2], a[3], a[4], a[5], string.Join(" / ", list));
    }
    public (ulong Journal, long Usn)? Checkpoint(string root)
    { using var c = Open(); using var cmd = Command(c, "SELECT journal,usn FROM checkpoints WHERE root=$r", ("$r", root)); using var r = cmd.ExecuteReader(); return r.Read() ? (ulong.Parse(r.GetString(0)), r.GetInt64(1)) : null; }
    public void Checkpoint(string root, ulong id, long usn) => Exec("INSERT INTO checkpoints VALUES($r,$j,$u) ON CONFLICT(root) DO UPDATE SET journal=$j,usn=$u", ("$r", root), ("$j", id.ToString()), ("$u", usn));
    public bool Unchanged(string root, string path, FileState state, Method? requested = null)
    {
        var method = requested ?? state.Method;
        if (state.Method != method) return false;
        using var c = Open(); using var cmd = Command(c, "SELECT 1 FROM seen WHERE root=$r AND path=$p AND length=$l AND ticks=$t AND requested=$q AND actual=$a", ("$r", root), ("$p", path), ("$l", state.Length), ("$t", state.WriteTicks), ("$q", method.ToString()), ("$a", state.Method.ToString())); return cmd.ExecuteScalar() != null;
    }
    public bool Known(string root, string path)
    { using var c = Open(); using var cmd = Command(c, "SELECT 1 FROM seen WHERE root=$r AND path=$p", ("$r", root), ("$p", path)); return cmd.ExecuteScalar() != null; }
    public void Seen(string root, string path, FileState state, Method? requested = null) => Exec("INSERT INTO seen(root,path,length,ticks,requested,actual) VALUES($r,$p,$l,$t,$q,$a) ON CONFLICT(root,path) DO UPDATE SET length=$l,ticks=$t,requested=$q,actual=$a", ("$r", root), ("$p", path), ("$l", state.Length), ("$t", state.WriteTicks), ("$q", (requested ?? state.Method).ToString()), ("$a", state.Method.ToString()));
    public int CleanupCompleted(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-30).ToUnixTimeSeconds();
        using var c = Open(); using var tx = c.BeginTransaction();
        using var work = Command(c, "DELETE FROM work WHERE state IN ('Success','Already','Skipped') AND completedUtc>0 AND completedUtc<$cutoff", ("$cutoff", cutoff)); work.Transaction = tx;
        int removed = work.ExecuteNonQuery();
        using var jobs = Command(c, "DELETE FROM jobs WHERE staged=1 AND created<$created AND NOT EXISTS(SELECT 1 FROM work WHERE job=jobs.id)", ("$created", now.AddDays(-30).UtcDateTime.ToString("O"))); jobs.Transaction = tx; jobs.ExecuteNonQuery();
        tx.Commit(); return removed;
    }
    private void MaybeCleanup()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextCleanup) return;
        lock (cleanupGate)
        {
            if (now < nextCleanup) return;
            var last = Flag("cleanup.completedUtc");
            if (!long.TryParse(last, out var timestamp) || now.ToUnixTimeSeconds() - timestamp >= 86400)
            {
                CleanupCompleted(now);
                timestamp = now.ToUnixTimeSeconds();
                FlagSet("cleanup.completedUtc", timestamp.ToString());
            }
            nextCleanup = DateTimeOffset.FromUnixTimeSeconds(timestamp).AddHours(24);
        }
    }
    public string Flag(string name, string fallback = "") { using var c = Open(); using var cmd = Command(c, "SELECT value FROM flags WHERE name=$n", ("$n", name)); return cmd.ExecuteScalar() as string ?? fallback; }
    public void FlagSet(string name, string value) => Exec("INSERT INTO flags VALUES($n,$v) ON CONFLICT(name) DO UPDATE SET value=$v", ("$n", name), ("$v", value));
}
