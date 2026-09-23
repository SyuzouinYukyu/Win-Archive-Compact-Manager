using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WACM;
public readonly record struct UsnRecord(ulong FileId, ulong ParentId, uint Reason, bool Directory, string Name);
public sealed class UsnJournal : IDisposable
{
    private readonly SafeFileHandle handle;
    public UsnJournal(string volume) { handle = Native.Open(volume.TrimEnd('\'), 0x80000000); }
    public (ulong Id, long First, long Next) Query()
    {
        var data = new byte[128];
        if (!Native.DeviceIoControl(handle, 0x900F4, null, 0, data, data.Length, out var n, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (n < 56) throw new InvalidDataException("USN Journal応答長が不正です。");
        return (BitConverter.ToUInt64(data, 0), Math.Max(BitConverter.ToInt64(data, 8), BitConverter.ToInt64(data, 24)), BitConverter.ToInt64(data, 16));
    }
    public static List<UsnRecord> Parse(byte[] data, int length)
    {
        if (length < 8 || length > data.Length) throw new InvalidDataException("USN応答長が不正です。");
        var list = new List<UsnRecord>();
        for (int offset = 8; offset < length;)
        {
            if (length - offset < 60) throw new InvalidDataException("USN_RECORD_V2が切れています。");
            var recordLength = BitConverter.ToUInt32(data, offset);
            if (recordLength < 60 || recordLength > length - offset || (recordLength & 7) != 0 || BitConverter.ToUInt16(data, offset + 4) != 2) throw new InvalidDataException("未対応または不正なUSNレコード。再同期が必要です。");
            var nameLength = BitConverter.ToUInt16(data, offset + 56); var nameOffset = BitConverter.ToUInt16(data, offset + 58);
            if (nameOffset < 60 || nameOffset + nameLength > recordLength || (nameLength & 1) != 0) throw new InvalidDataException("USNファイル名境界が不正です。");
            var name = System.Text.Encoding.Unicode.GetString(data, offset + nameOffset, nameLength);
            if (name is "." or ".." || name.IndexOfAny(['\', '/', ':']) >= 0) throw new InvalidDataException("USNファイル名が不正です。");
            list.Add(new(BitConverter.ToUInt64(data, offset + 8), BitConverter.ToUInt64(data, offset + 16), BitConverter.ToUInt32(data, offset + 40), (BitConverter.ToUInt32(data, offset + 52) & 16) != 0, name));
            offset += checked((int)recordLength);
        }
        return list;
    }
    public static bool NeedsResync((ulong Id, long First, long Next) journal, (ulong Journal, long Usn)? checkpoint) => checkpoint == null || checkpoint.Value.Journal != journal.Id || checkpoint.Value.Usn < journal.First || checkpoint.Value.Usn > journal.Next;
    public static bool WithinRoot(string root, string parent) => Targets.Contains(root, parent);
    private SafeFileHandle OpenId(ulong id)
    {
        var descriptor = new Native.FileId { Size = 24, Type = 0, Value = id };
        return Native.OpenFileById(handle, ref descriptor, 0, 7, IntPtr.Zero, 0x02200000);
    }
    private (string? Path, int Error) ResolveId(ulong id)
    {
        using var file = OpenId(id);
        if (file.IsInvalid) return (null, Marshal.GetLastWin32Error());
        return (Native.Final(file), 0);
    }
    public static bool ProcessEntries(IEnumerable<UsnRecord> entries, string root, Func<ulong, (string? Path, int Error)> resolve, Action<string, bool, uint> changed, CancellationToken token)
    {
        bool uncertain = false;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var parent = resolve(entry.ParentId);
            if (parent.Path == null)
            {
                if (parent.Error is 2 or 3 or 5) { uncertain = true; continue; }
                throw new Win32Exception(parent.Error, "USN親ディレクトリIDを解決できません。");
            }
            if (!WithinRoot(root, parent.Path)) continue;
            var file = resolve(entry.FileId);
            if (file.Path == null)
            {
                if (file.Error is 2 or 3) continue;
                if (file.Error == 5) { changed(System.IO.Path.Combine(parent.Path, entry.Name), entry.Directory, entry.Reason); continue; }
                throw new Win32Exception(file.Error, "USNファイルIDを解決できません。");
            }
            if (WithinRoot(root, file.Path)) changed(file.Path, entry.Directory, entry.Reason);
        }
        return uncertain;
    }
    public void Replay(ulong journal, long start, long end, string root, Action<string, bool, uint> changed, Action<long> checkpoint, Action unresolved, CancellationToken token)
    {
        var input = new byte[48]; var output = new byte[1024 * 1024];
        BitConverter.GetBytes(0x00002177u).CopyTo(input, 8); // data, named data, create, rename-new; omit compression's own metadata events
        BitConverter.GetBytes(journal).CopyTo(input, 32);
        BitConverter.GetBytes((ushort)2).CopyTo(input, 40); BitConverter.GetBytes((ushort)2).CopyTo(input, 42);
        while (start < end)
        {
            token.ThrowIfCancellationRequested(); BitConverter.GetBytes(start).CopyTo(input, 0);
            if (!Native.DeviceIoControl(handle, 0x900BB, input, input.Length, output, output.Length, out var n, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var entries = Parse(output, n); var next = BitConverter.ToInt64(output, 0);
            if (next <= start) throw new InvalidDataException("USN読み取りが前進しません。");
            var uncertain = ProcessEntries(entries, root, ResolveId, changed, token);
            // A protected or deleted parent may belong to the root. Reconcile before checkpointing;
            // the caller rate-limits this metadata scan. Watcher events remain active throughout.
            if (uncertain) unresolved();
            checkpoint(next); start = next;
        }
    }
    public void Dispose() => handle.Dispose();
}
