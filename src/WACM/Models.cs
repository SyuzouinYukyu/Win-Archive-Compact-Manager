using System.Text.Json;

namespace WACM;
public enum Method { None, NTFS, XPRESS4K, XPRESS8K, XPRESS16K, LZX, Unknown }
public enum Outcome { Success, Already, Skipped, Unsupported, Denied, Failed, Cancelled }
public sealed record FileState(long Length, long Allocated, long WriteTicks, Method Method, FileAttributes Attributes, uint Links);
public sealed record Result(Outcome Outcome, string Path, long Logical = 0, long Before = 0, long After = 0, string Detail = "");
public sealed class WatchRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Volume { get; set; } = "";
    public string Relative { get; set; } = "";
    public Method Method { get; set; } = Method.LZX;
    public bool Enabled { get; set; } = true;
    public bool NewFiles { get; set; } = true;
    public bool Updates { get; set; } = true;
    public bool Skip { get; set; } = true;
    public string Extensions { get; set; } = Settings.DefaultExtensions;
    public int Parallel { get; set; }
    public string Resolve() => Volume.Length > 0 ? Volume + Relative : Path;
}
public sealed class Settings
{
    public const string DefaultExtensions = ".jpg .jpeg .png .gif .webp .avif .heic .heif .jxl .mp3 .aac .m4a .flac .ogg .opus .ape .wv .mp4 .mkv .webm .mov .m2ts .mts .ts .zip .7z .rar .gz .bz2 .xz .zst .cab .wim .esd .swm .docx .xlsx .pptx .odt .ods .odp .epub .jar .apk .nupkg";
    public float FontSize { get; set; } = 13;
    public Method Method { get; set; } = Method.LZX;
    public bool Skip { get; set; } = true;
    public string Extensions { get; set; } = DefaultExtensions;
    public long MinimumBytes { get; set; } = 4096;
    public int Parallel { get; set; }
    public int DebounceSeconds { get; set; } = 5;
    public bool AutoStart { get; set; }
    public List<WatchRoot> Roots { get; set; } = [];
    public void Validate()
    {
        FontSize = float.IsFinite(FontSize) ? Math.Clamp(FontSize, 9, 17) : 13;
        Parallel = Math.Clamp(Parallel, 0, 8); DebounceSeconds = Math.Clamp(DebounceSeconds, 1, 3600);
        MinimumBytes = Math.Clamp(MinimumBytes, 0, 1L << 40);
        if (Method is Method.None or Method.Unknown) Method = Method.LZX;
        if (Roots.Count > 100) throw new InvalidDataException("監視ルートは最大100件です。");
        var paths = new List<string>();
        foreach (var r in Roots)
        {
            if (r.Method is Method.None or Method.Unknown) throw new InvalidDataException("監視方式が不正です。");
            r.Path = Targets.Normalize(r.Path);
            if (r.Volume.Length > 0)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(r.Volume, @"^\\?\Volume{[0-9a-fA-F-]{36}}\$") || !Guid.TryParse(r.Volume.Substring(11, 36), out _)) throw new InvalidDataException("Volume GUIDが不正です。");
                if (System.IO.Path.IsPathRooted(r.Relative) || r.Relative.Split('\').Any(x => x is ".." or ".")) throw new InvalidDataException("監視相対パスが不正です。");
            }
            var p = r.Resolve();
            if (paths.Any(x => Targets.Contains(x, p) || Targets.Contains(p, x))) throw new InvalidDataException("監視ルートが重複・包含しています。");
            paths.Add(p); r.Parallel = Math.Clamp(r.Parallel, 0, 8);
        }
    }
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
}
public sealed record JobOptions(Method Method, bool Skip, string Extensions, long MinimumBytes, int Parallel);
public sealed record WorkItem(long Id, string Job, string Root, string Path, bool Directory, int Retry, long Generation, JobOptions Options);
public sealed record Totals(long Count, long Done, long Bytes, long Processed, long Before, long After, string Counts);
