using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WACM;
// Verified against Windows SDK 10.0.26100.0: winbase.h, winioctl.h, wofapi.h.
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Standard { public long Allocation, Length; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] public struct Tag { public uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] public struct WofInfo { public uint Algorithm, Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct FileId { [FieldOffset(0)] public uint Size; [FieldOffset(4)] public uint Type; [FieldOffset(8)] public ulong Value; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int kind, out Standard info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")] public static extern bool GetTag(SafeFileHandle h, int kind, out Tag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool GetVolumeNameForVolumeMountPointW(string path, StringBuilder volume, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool GetVolumeInformationW(string root, StringBuilder? name, uint size, out uint serial, out uint max, out uint flags, StringBuilder fs, uint fsSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileId id, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetFileAttributesW(string path);
    [DllImport("wofutil.dll", CharSet = CharSet.Unicode)] public static extern int WofIsExternalFile(string path, [MarshalAs(UnmanagedType.Bool)] out bool external, out uint provider, out WofInfo info, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern uint GetCompressedFileSizeW(string path, out uint high);
    [DllImport("wofutil.dll")] public static extern int WofSetFileDataLocation(SafeFileHandle file, uint provider, ref WofInfo info, uint length);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeFileHandle OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelSynchronousIo(SafeFileHandle thread);
    public static string Extended(string p) => p.StartsWith(@"\?", StringComparison.Ordinal) ? p : @"\?" + p;
    public static SafeFileHandle Open(string path, uint access = 0, uint share = 7, bool noFollow = true)
    {
        var h = CreateFileW(Extended(path), access, share, IntPtr.Zero, 3, 0x02000000u | (noFollow ? 0x00200000u : 0), IntPtr.Zero);
        if (h.IsInvalid) { var e = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(e, path); }
        return h;
    }
    public static string Final(SafeFileHandle h, bool guid = true)
    {
        var b = new StringBuilder(32768); var n = GetFinalPathNameByHandleW(h, b, (uint)b.Capacity, guid ? 1u : 0u);
        if (n == 0 || n >= b.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return b.ToString();
    }
    public static (string Mount, string Guid) Volume(string path)
    {
        var mount = new StringBuilder(32768); var guid = new StringBuilder(64);
        if (!GetVolumePathNameW(Extended(path), mount, (uint)mount.Capacity) || !GetVolumeNameForVolumeMountPointW(mount.ToString(), guid, 64)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (mount.ToString(), guid.ToString());
    }
    public static void EnsureLocalNtfs(string path)
    {
        if (path.StartsWith(@"\") && !path.StartsWith(@"\?Volume{", StringComparison.OrdinalIgnoreCase) && !(path.StartsWith(@"\?") && path.Length > 6 && path[5] == ':')) throw new NotSupportedException("UNC/ネットワークは対象外です。");
        var v = Volume(path); var fs = new StringBuilder(32);
        if (GetDriveTypeW(v.Mount) == 4 || !GetVolumeInformationW(v.Guid, null, 0, out _, out _, out _, fs, 32) || fs.ToString() != "NTFS") throw new NotSupportedException("ローカルNTFSだけが対象です。");
    }
    public static FileState Inspect(string path)
    {
        using var h = Open(path);
        if (!GetFileInformationByHandleEx(h, 1, out var s, (uint)Marshal.SizeOf<Standard>()) || !GetTag(h, 9, out var tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attr = (FileAttributes)tag.Attributes;
        if (attr.HasFlag(FileAttributes.ReparsePoint) && (s.Directory != 0 || tag.ReparseTag != 0x80000017)) throw new NotSupportedException("Reparse Pointは追跡しません。");
        if ((attr & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.Device)) != 0) throw new NotSupportedException("暗号化・オフライン・特殊属性です。");
        if (s.Directory == 0 && s.Links > 1) throw new NotSupportedException("対象外のハードリンクへ波及するためスキップします。");
        var method = attr.HasFlag(FileAttributes.Compressed) ? Method.NTFS : Method.None;
        if (s.Directory == 0)
        {
            uint size = 8; var hr = WofIsExternalFile(Extended(path), out var external, out var provider, out var w, ref size);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (external) method = provider == 2 ? w.Algorithm switch { 0 => Method.XPRESS4K, 1 => Method.LZX, 2 => Method.XPRESS8K, 3 => Method.XPRESS16K, _ => Method.Unknown } : Method.Unknown;
            // Includes the WOF backing stream, unlike FILE_STANDARD_INFO on the sparse primary stream.
            Marshal.SetLastPInvokeError(0);
            var low = GetCompressedFileSizeW(Extended(path), out var high);
            var err = Marshal.GetLastWin32Error();
            if (low == uint.MaxValue && err != 0) throw new Win32Exception(err);
            if (method != Method.None || attr.HasFlag(FileAttributes.SparseFile)) s.Allocation = (long)(((ulong)high << 32) | low);
        }
        return new(s.Length, s.Allocation, File.GetLastWriteTimeUtc(path).Ticks, method, attr, s.Links);
    }
    public static void SetDirectory(string path, bool compress)
    {
        using var h = Open(path, 0xC0000000);
        var b = BitConverter.GetBytes((ushort)(compress ? 1 : 0));
        if (!DeviceIoControl(h, 0x9C040, b, b.Length, [], 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
