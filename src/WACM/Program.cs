using System.ServiceProcess;

namespace WACM;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--service")) { ServiceBase.Run(new WacmService()); return 0; }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 3 && args[0] == "--admin") { ServiceControl.Admin(args[1], args[2]).GetAwaiter().GetResult(); return 0; }
            if (args.Length >= 2 && args[0] == "--smoke")
            {
                var data = new AppData(args[1]);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                var lines = new List<string>();
                try
                {
                    using (var form = new MainForm(data, smoke: true))
                    {
                        form.Show(); Application.DoEvents();
                        form.VerifyGui((okay, label) => { lines.Add((okay ? "PASS " : "FAIL ") + label); if (!okay) throw new InvalidOperationException(label); }, Path.Combine(data.Root, "gui-proof"), true);
                        form.CloseForTest();
                    }
                    using (var restored = new MainForm(data, smoke: true)) { restored.Show(); Application.DoEvents(); if (restored.Font.Size != 17) throw new InvalidOperationException("Release font restore failed"); restored.CloseForTest(); }
                    lines.Add("PASS Release font restored on second GUI instance");
                    lines.Add("EXE=" + Environment.ProcessPath);
                    var metadata = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
                    if (metadata.ProductName != "Win Archive Compact Manager" || metadata.ProductVersion != "1.0.1" || metadata.FileVersion != "1.0.1.0") throw new InvalidOperationException("EXE metadata mismatch");
                    lines.Add("PASS ProductName/FileVersion/ProductVersion");
                    using var process = System.Diagnostics.Process.GetCurrentProcess();
                    var modules = process.Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => m.FileName).ToArray();
                    lines.Add("RUNTIME=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                    foreach (var module in modules) lines.Add("MODULE=" + module);
                    File.WriteAllText(Path.Combine(data.Root, "smoke.ok"), "GUI_START_OK; FONT_9_17_OK; VERSION=1.0.1; CHECKS=" + lines.Count);
                    return 0;
                }
                catch (Exception ex) { lines.Add(ex.ToString()); return 1; }
                finally { File.WriteAllLines(Path.Combine(data.Root, "smoke.log"), lines); }
            }
            using var mutex = new Mutex(true, @"LocalWACM.GUI", out var first);
            if (!first) { MessageBox.Show("WACMは既に起動しています。タスクトレイから開いてください。", "WACM"); return 0; }
            Application.Run(new MainForm(new AppData())); return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "WACM エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
