using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace WACM;
public sealed class WacmService : ServiceBase
{
    private CancellationTokenSource? stop, monitoring;
    private Task? main, monitorTask;
    public WacmService() { ServiceName = ServiceControl.Name; CanStop = true; CanShutdown = true; AutoLog = true; }
    protected override void OnStart(string[] args)
    {
        stop = new(); main = Task.Run(() => Run(stop.Token));
        _ = main.ContinueWith(t => { if (t.IsFaulted) { ExitCode = 1; Stop(); } }, TaskScheduler.Default);
    }
    private async Task Run(CancellationToken token)
    {
        var data = new AppData(); var log = new AppLog(data, "service"); var store = new Store(data);
        var owner = File.ReadAllText(System.IO.Path.Combine(data.Root, "owner.sid")).Trim();
        var engine = new MonitorEngine(data, store, log);
        async Task Reload()
        {
            if (monitoring != null) { await monitoring.CancelAsync(); try { if (monitorTask != null) await monitorTask; } catch (OperationCanceledException) { } monitoring.Dispose(); }
            monitoring = CancellationTokenSource.CreateLinkedTokenSource(token);
            engine = new(data, store, log); monitorTask = Task.Run(() => engine.Run(monitoring.Token), monitoring.Token);
            _ = monitorTask.ContinueWith(t => { if (t.Exception != null) log.Write("ERROR", "service", "監視停止", t.Exception.ToString()); }, TaskScheduler.Default);
        }
        await Reload();
        try
        {
            await Ipc.Serve(owner, async request =>
            {
                switch (request)
                {
                    case "status": return engine.Status + (monitorTask?.IsFaulted == true ? "
監視処理が停止しています。設定とログを確認してください。" : "");
                    case "pause": store.FlagSet("paused", "1"); return "監視を一時停止しました。";
                    case "resume": store.FlagSet("paused", "0"); return "監視を再開しました。";
                    case "reload": data.Load().Validate(); await Reload(); return "監視設定を再読込しました。";
                    case "retry": foreach (var r in data.Load().Roots) store.RetryFailed("watch:" + r.Id); return "監視の失敗キューを再試行します。";
                    default: return "ERROR: 未対応のIPC操作";
                }
            }, log, token);
        }
        finally { if (monitoring != null) await monitoring.CancelAsync(); if (monitorTask != null) try { await monitorTask; } catch (OperationCanceledException) { } }
    }
    protected override void OnStop()
    {
        if (stop == null) return; stop.Cancel();
        while (main is { IsCompleted: false }) { RequestAdditionalTime(10000); Thread.Sleep(250); }
        monitoring?.Dispose(); stop.Dispose();
    }
    protected override void OnShutdown() => OnStop();
}
public static partial class ServiceControl
{
    public const string Name = "WACMService";
    public static string ParseImagePath(string imagePath)
    {
        var value = imagePath.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end < 2) return "";
            return value[1..end];
        }
        // SCM paths containing spaces are required to be quoted by our installer.
        var space = value.IndexOfAny([' ', '	']);
        return space < 0 ? value : value[..space];
    }
    public static string DescribeExecutable(ServiceControllerStatus state, string imagePath, int fileError)
    {
        if (string.IsNullOrWhiteSpace(ParseImagePath(imagePath))) return " / 登録EXE: SCM情報を取得できません";
        if (fileError == 0) return " / 登録EXE: 確認済み";
        if (fileError == 5) return " / 登録EXE: 保護されています";
        if (fileError is 2 or 3)
            return state == ServiceControllerStatus.Running ? " / 登録EXE: サービス実行中（パスの参照状態を確認できません）" : " / 警告: 登録EXEがありません。再インストールしてください。";
        return " / 登録EXE: 確認できません（アクセス状態を確認してください）";
    }
    public static string IpcUnavailableMessage(string serviceStatus)
    {
        if (serviceStatus.StartsWith("未インストール", StringComparison.Ordinal)) return "監視サービスはインストールされていません。";
        if (serviceStatus.StartsWith("Stopped", StringComparison.Ordinal) || serviceStatus.StartsWith("StopPending", StringComparison.Ordinal)) return "監視サービスは停止しています。";
        return "監視サービスへ接続できませんでした。";
    }
    public static string Status()
    {
        try
        {
            using var sc = new ServiceController(Name);
            var state = sc.Status;
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEMCurrentControlSetServices" + Name);
            var imagePath = key?.GetValue("ImagePath") as string ?? "";
            var executable = ParseImagePath(imagePath);
            int fileError;
            if (executable.Length == 0) fileError = 0;
            else
            {
                Marshal.SetLastPInvokeError(0);
                var attributes = Native.GetFileAttributesW(Native.Extended(Environment.ExpandEnvironmentVariables(executable)));
                fileError = attributes == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0;
            }
            var message = state + DescribeExecutable(state, imagePath, fileError);
            var origin = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WACM", "gui-origin.txt");
            try { if (File.Exists(origin) && !File.ReadAllText(origin).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) message += " / GUIの場所が変更されています（サービスは保護されたコピーで動作）。"; }
            catch (UnauthorizedAccessException) { /* Protected service metadata is not absence. */ }
            return message + "
登録: " + imagePath;
        }
        catch (InvalidOperationException) { return "未インストール"; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1060) { return "未インストール"; }
        catch (UnauthorizedAccessException) { return "サービス状態を確認できません（アクセス拒否）。"; }
    }
    public static async Task Elevate(string operation)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--admin"); start.ArgumentList.Add(operation); start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        using var p = Process.Start(start) ?? throw new IOException("管理操作を起動できません。");
        await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new IOException("サービス管理操作に失敗しました。表示されたエラーを確認してください。");
    }
    public static async Task Admin(string operation, string owner)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("管理者権限が必要です。");
        if (operation == "install")
        {
            var data = new AppData(); Secure(data.Root, owner);
            File.WriteAllText(System.IO.Path.Combine(data.Root, "owner.sid"), new SecurityIdentifier(owner).Value);
            var ownerFile = new FileInfo(System.IO.Path.Combine(data.Root, "owner.sid"));
            var ownerAcl = new FileSecurity(); ownerAcl.SetAccessRuleProtection(true, false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            ownerAcl.SetOwner(administrators);
            ownerAcl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            ownerAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            ownerAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            ownerFile.SetAccessControl(ownerAcl);
            File.WriteAllText(System.IO.Path.Combine(data.Root, "gui-origin.txt"), Environment.ProcessPath!);
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WACM");
            Directory.CreateDirectory(folder); Secure(folder, null);
            var exe = System.IO.Path.Combine(folder, "WACM.exe");
            if (!string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, exe, true);
            await Sc("create", Name, "binPath=", """ + exe + "" --service", "start=", "delayed-auto", "DisplayName=", "Win Archive Compact Manager Service");
            await Sc("description", Name, "WACM: ローカルNTFSアーカイブの変更ファイルを監視します。");
            return;
        }
        if (operation is "uninstall-service" or "uninstall-full")
        {
            var outcome = await Uninstall(operation == "uninstall-full");
            MessageBox.Show(outcome.Text, "WACM アンインストール", MessageBoxButtons.OK, outcome.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (!outcome.Success) throw new IOException("アンインストールが一部失敗しました。残った項目は直前の表示を確認してください。");
            return;
        }
        using var sc = new ServiceController(Name);
        switch (operation)
        {
            case "start": sc.Start(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))); break;
            case "stop": if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60))); } break;
            case "restart": if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60))); } sc.Start(); await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))); break;
            default: throw new ArgumentException("未知の管理操作");
        }
    }
    private static async Task Sc(params string[] args)
    {
        var s = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) s.ArgumentList.Add(a);
        using var p = Process.Start(s)!; var a1 = p.StandardOutput.ReadToEndAsync(); var a2 = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        var message = await a1 + await a2; if (p.ExitCode != 0) throw new IOException($"sc.exe ({p.ExitCode}): {message}");
    }
    private static void Secure(string folder, string? owner)
    {
        using (Targets.Guard(folder)) { }
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var directory = new DirectorySecurity(); directory.SetAccessRuleProtection(true, false); directory.SetOwner(admin);
        foreach (var sid in new[] { admin, system }) directory.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (owner != null) directory.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner), FileSystemRights.Modify | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("インストール先にReparse Pointがあります。");
            if (Directory.Exists(entry)) Secure(entry, owner);
            else
            {
                var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(admin);
                acl.AddAccessRule(new FileSystemAccessRule(admin, FileSystemRights.FullControl, AccessControlType.Allow)); acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
                if (owner != null) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(owner), FileSystemRights.Modify | FileSystemRights.Synchronize, AccessControlType.Allow));
                new FileInfo(entry).SetAccessControl(acl);
            }
        }
    }
}
