using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WACM;
public sealed class CompressionEngine(AppLog log)
{
    public static bool Excluded(string path, FileState s, JobOptions o) => o.Method != Method.None && ((o.Skip && o.Extensions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) || s.Length < o.MinimumBytes);
    public async Task<Result> Execute(string path, bool directory, JobOptions o, CancellationToken token)
    {
        FileState? before = null;
        try
        {
            token.ThrowIfCancellationRequested(); Native.EnsureLocalNtfs(path);
            using var guard = Targets.Guard(path);
            before = Native.Inspect(path);
            if (before.Method == Method.Unknown) return new(Outcome.Unsupported, path, Detail: "不明なWOFプロバイダー");
            if (directory)
            {
                if (o.Method is not (Method.NTFS or Method.None)) return new(Outcome.Skipped, path, Detail: "WOFはフォルダ属性に継承しません");
                if (before.Method == o.Method) return new(Outcome.Already, path);
                Native.SetDirectory(path, o.Method == Method.NTFS);
                if (Native.Inspect(path).Method != o.Method) throw new IOException("フォルダ属性の検証不一致");
                return new(Outcome.Success, path);
            }
            if (before.Method == o.Method) return new(Outcome.Already, path, before.Length, before.Allocated, before.Allocated);
            if (Excluded(path, before, o)) return new(Outcome.Skipped, path, before.Length, before.Allocated, before.Allocated, "拡張子/最小サイズによる事前スキップ");
            if ((before.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) return new(Outcome.Unsupported, path, before.Length, before.Allocated, before.Allocated, "読み取り専用/システム属性");
            if (before.Method != Method.None) await ApplyNative(path, Method.None, token);
            if (o.Method != Method.None) await ApplyNative(path, o.Method, token);
            var after = Native.Inspect(path);
            if (after.Length != before.Length || after.Method != o.Method) throw new IOException($"処理後状態不一致: {after.Method}, 期待={o.Method}");
            return new(Outcome.Success, path, before.Length, before.Allocated, after.Allocated);
        }
        catch (NoBenefitException)
        {
            var after = Native.Inspect(path);
            return new(Outcome.Skipped, path, before?.Length ?? 0, before?.Allocated ?? 0, after.Allocated, "Windows判定: 圧縮効果なし");
        }
        catch (OperationCanceledException)
        {
            long after = before?.Allocated ?? 0;
            try { after = Native.Inspect(path).Allocated; } catch (Exception ex) { log.Write("WARN", path, "中止後確認", ex.Message); }
            return new(Outcome.Cancelled, path, before?.Length ?? 0, before?.Allocated ?? 0, after, "未完了は再開時に状態を再確認");
        }
        catch (Exception ex)
        {
            var outcome = ex is NotSupportedException ? Outcome.Unsupported : ex is UnauthorizedAccessException || ex is Win32Exception { NativeErrorCode: 5 } ? Outcome.Denied : Outcome.Failed;
            return new(outcome, path, before?.Length ?? 0, before?.Allocated ?? 0, before?.Allocated ?? 0, ex.Message);
        }
    }
    private static Task ApplyNative(string path, Method method, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var thread = Native.OpenThread(1, false, Native.GetCurrentThreadId());
        using var registration = token.Register(() => { if (!thread.IsInvalid) Native.CancelSynchronousIo(thread); });
        var before = Native.Inspect(path);
        using var handle = Native.Open(path, 0xC0000000, 1, true);
        before = Native.Inspect(path);
        if (method == Method.None && before.Method == Method.None) return;
        bool okay;
        if (method == Method.None && before.Method is not (Method.None or Method.NTFS))
            okay = Native.DeviceIoControl(handle, 0x90314, null, 0, [], 0, out _, IntPtr.Zero);
        else if (method is Method.NTFS or Method.None)
        {
            var input = BitConverter.GetBytes((ushort)(method == Method.NTFS ? 1 : 0));
            okay = Native.DeviceIoControl(handle, 0x9C040, input, 2, [], 0, out _, IntPtr.Zero);
        }
        else
        {
            var info = new Native.WofInfo { Algorithm = method switch { Method.XPRESS4K => 0u, Method.LZX => 1u, Method.XPRESS8K => 2u, Method.XPRESS16K => 3u, _ => throw new ArgumentOutOfRangeException(nameof(method)) }, Flags = 0 };
            int hr = Native.WofSetFileDataLocation(handle, 2, ref info, 8);
            token.ThrowIfCancellationRequested();
            if (hr == unchecked((int)0x80070158)) throw new NoBenefitException();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            okay = true;
        }
        var error = Marshal.GetLastWin32Error(); token.ThrowIfCancellationRequested();
        if (!okay) throw new Win32Exception(error);
    }, CancellationToken.None);
    private sealed class NoBenefitException : Exception;
}
