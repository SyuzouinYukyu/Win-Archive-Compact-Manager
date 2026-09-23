using Microsoft.Data.Sqlite;
using System.ServiceProcess;

namespace WACM;
public static partial class ServiceControl
{
    public sealed record UninstallResult(bool Success, string Text);
    private static async Task<UninstallResult> Uninstall(bool full)
    {
        var report = new List<string>();
        try
        {
            using (var sc = new ServiceController(Name))
            {
                bool installed;
                try { _ = sc.Status; installed = true; }
                catch (InvalidOperationException) { installed = false; }
                if (installed)
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)));
                        report.Add("成功: サービス停止");
                    }
                    else report.Add("確認: サービス停止済み");
                }
                else report.Add("確認: サービス未登録");
            }
            // Do not delete the protected binary or data unless the service has stopped.
            if (!report.Contains("確認: サービス未登録")) { await Sc("delete", Name); report.Add("成功: サービス登録削除"); }
        }
        catch (Exception ex) { report.Add("失敗: サービス停止・登録解除: " + ex.Message); return new(false, string.Join("
", report) + "
保護コピーとデータは残しています。"); }
        var protectedDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WACM");
        var protectedExe = System.IO.Path.Combine(protectedDirectory, "WACM.exe");
        bool success = true;
        try
        {
            if (File.Exists(protectedExe)) { GuardOwnedFile(protectedExe); File.Delete(protectedExe); report.Add("成功: サービス用保護コピー削除"); }
            else report.Add("確認: サービス用保護コピーなし");
            if (Directory.Exists(protectedDirectory) && !Directory.EnumerateFileSystemEntries(protectedDirectory).Any()) { Directory.Delete(protectedDirectory); report.Add("成功: 空のProgram Files\WACMを削除"); }
        }
        catch (Exception ex) { success = false; report.Add("失敗: サービス用保護コピー: " + ex.Message); }
        if (full)
        {
            var dataRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WACM");
            var dataResult = RemoveOwnedData(dataRoot);
            success &= dataResult.Success; report.Add(dataResult.Text);
        }
        else report.Add("設定・ログ・SQLite DBは残しました。");
        report.Add("元のWACM.exe、圧縮対象は削除していません。圧縮済みファイルの圧縮状態は変更されません。");
        return new(success, string.Join("
", report));
    }
    private static void GuardOwnedFile(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse Pointは削除しません: " + path);
    }
    public static UninstallResult RemoveOwnedData(string root)
    {
        var report = new List<string>(); bool success = true;
        if (!Directory.Exists(root)) return new(true, "確認: WACM専用データなし");
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return new(false, "失敗: データフォルダがReparse Pointです。削除しません。");
        SqliteConnection.ClearAllPools();
        var entries = new[] { "settings.json", "settings.lock", "state.db", "state.db-wal", "state.db-shm", "owner.sid", "gui-origin.txt" };
        foreach (var item in entries)
        {
            var path = System.IO.Path.Combine(root, item);
            try { if (File.Exists(path)) { GuardOwnedFile(path); File.Delete(path); report.Add("削除: " + item); } }
            catch (Exception ex) { success = false; report.Add("残存: " + item + " (" + ex.Message + ")"); }
        }
        var logs = System.IO.Path.Combine(root, "Logs");
        if (Directory.Exists(logs))
        {
            if (File.GetAttributes(logs).HasFlag(FileAttributes.ReparsePoint)) { success = false; report.Add("残存: Logs (Reparse Point)"); }
            else
            {
                foreach (var name in new[] { "gui.log", "service.log" })
                    for (int generation = 0; generation <= 5; generation++)
                    {
                        var fileName = name + (generation == 0 ? "" : "." + generation);
                        var file = System.IO.Path.Combine(logs, fileName);
                        try { if (File.Exists(file)) { GuardOwnedFile(file); File.Delete(file); report.Add("削除: " + fileName); } }
                        catch (Exception ex) { success = false; report.Add("残存: " + fileName + " (" + ex.Message + ")"); }
                    }
                try { if (!Directory.EnumerateFileSystemEntries(logs).Any()) Directory.Delete(logs); }
                catch (Exception ex) { success = false; report.Add("残存: Logs (" + ex.Message + ")"); }
            }
        }
        try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
        catch (Exception ex) { success = false; report.Add("残存: データフォルダ (" + ex.Message + ")"); }
        report.Add("圧縮済みファイルの圧縮状態は変更されません。");
        return new(success, string.Join("
", report));
    }
}
