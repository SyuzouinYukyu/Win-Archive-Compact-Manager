using Microsoft.Win32.SafeHandles;

namespace WACM;
public static class Targets
{
    public static string Normalize(string p)
    {
        p = p.Trim().Trim('"').Trim();
        if (p.Length == 0 || !System.IO.Path.IsPathFullyQualified(p)) throw new ArgumentException("絶対パスを指定してください。");
        p = System.IO.Path.GetFullPath(p);
        var root = System.IO.Path.GetPathRoot(p)!;
        return p.Length > root.Length ? p.TrimEnd('\') : p;
    }
    public static string[] Parse(string text) => text.Split(['', '
'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('"').Trim()).Where(x => x.Length > 0).ToArray();
    public static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd('\') + "\", StringComparison.OrdinalIgnoreCase);
    public static List<string> Reduce(IEnumerable<string> paths)
    {
        var output = new List<string>();
        foreach (var p in paths.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length))
            if (!output.Any(x => Contains(x, p))) output.Add(p);
        return output;
    }
    public static bool Broad(string p) => p.TrimEnd('\').Equals(System.IO.Path.GetPathRoot(p)!.TrimEnd('\'), StringComparison.OrdinalIgnoreCase) ||
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Any(x => Contains(p, x) || Contains(x, p));
    public static string Canonical(string p) { using var guard = Guard(p); using var h = Native.Open(p); return Native.Final(h); }
    public static IDisposable Guard(string p)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            string? current = Normalize(p);
            while (current != null)
            {
                var h = Native.Open(current, share: 3); handles.Add(h);
                if (!Native.GetTag(h, 9, out var t, 8)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                if ((t.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && (current != p || (t.Attributes & 16) != 0 || t.ReparseTag != 0x80000017)) throw new NotSupportedException("リンクまたはマウントポイントを経由するパスです。");
                current = System.IO.Path.GetDirectoryName(current.TrimEnd('\'));
                if (current is { Length: 2 } && current[1] == ':') current += "\";
            }
            return new HandleGroup(handles);
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    private sealed class HandleGroup(List<SafeFileHandle> handles) : IDisposable { public void Dispose() { foreach (var h in handles) h.Dispose(); } }
    public static IEnumerable<(string Path, bool Directory)> Enumerate(IEnumerable<string> roots, Action<string, Exception> error, CancellationToken token)
    {
        foreach (var root in Reduce(roots))
        {
            token.ThrowIfCancellationRequested();
            bool dir;
            try { Native.EnsureLocalNtfs(root); using var g = Guard(root); dir = File.GetAttributes(root).HasFlag(FileAttributes.Directory); }
            catch (Exception ex) { error(root, ex); continue; }
            if (!dir) { yield return (root, false); continue; }
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.TryPop(out var p))
            {
                token.ThrowIfCancellationRequested();
                IDisposable? guard = null; IEnumerator<string>? items = null;
                try { guard = Guard(p); items = Directory.EnumerateFileSystemEntries(p).GetEnumerator(); }
                catch (Exception ex) { guard?.Dispose(); error(p, ex); continue; }
                using (guard) using (items)
                {
                    yield return (p, true);
                    while (true)
                    {
                        token.ThrowIfCancellationRequested(); string entry;
                        try { if (!items.MoveNext()) break; entry = items.Current; }
                        catch (Exception ex) { error(p, ex); break; }
                        FileAttributes a;
                        try { a = File.GetAttributes(entry); }
                        catch (Exception ex) { error(entry, ex); continue; }
                        if (a.HasFlag(FileAttributes.Directory))
                        {
                            if (a.HasFlag(FileAttributes.ReparsePoint)) error(entry, new NotSupportedException("Reparse Pointフォルダをスキップ"));
                            else stack.Push(entry);
                        }
                        else yield return (entry, false);
                    }
                }
            }
        }
    }
}
