using Microsoft.Win32;
using System.Diagnostics;

namespace WACM;
public sealed partial class MainForm : Form, IMessageFilter
{
    private readonly AppData data;
    private readonly AppLog log;
    private readonly Store store;
    private readonly Settings settings;
    private readonly FlowLayoutPanel rows = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top };
    private readonly List<TextBox> inputs = [];
    private TextBox? selected;
    private readonly ComboBox method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Label description = new() { AutoSize = true };
    private readonly TextBox result = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TextBox serviceState = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly Label progress = new() { AutoSize = true, Text = "待機中" };
    private readonly TextBox current = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly NumericUpDown fontValue = Number(9, 17, 13), parallel = Number(0, 8, 0), debounce = Number(1, 3600, 5), minimum = Number(0, 1073741824, 4096);
    private readonly CheckBox skip = new() { Text = "圧縮効果が期待できない拡張子を事前スキップ", AutoSize = true };
    private readonly CheckBox autoStart = new() { Text = "ログオン時にGUIを起動（サービスとは独立）", AutoSize = true };
    private readonly TextBox extensions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MinimumSize = new(300, 100) };
    private readonly DataGridView watches = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
    private readonly TextBox rootExtensions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MinimumSize = new(200, 65) };
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 600 };
    private CancellationTokenSource? cancel;
    private Task? active;
    private bool exit, initializing = true;
    private string? job;
    private string latestPath = "";
    private readonly List<Button> operationButtons = [];
    private readonly bool smoke;
    private readonly Dictionary<int, Font> fontCache = new();
    private bool fontChanging;
    private bool rebindingWatches, deletingWatch;
    private string? notifiedInvalidCell;
    public MainForm(AppData data, bool smoke = false)
    {
        this.data = data; this.smoke = smoke; settings = data.Load(); log = new(data); store = new(data);
        Text = "Win Archive Compact Manager v1.0.1"; AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new(900, 650); Size = new(1150, 850); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, settings.FontSize);
        var tabs = new TabControl { Dock = DockStyle.Fill }; Controls.Add(tabs);
        AddTab(tabs, "圧縮", CompressionTab()); AddTab(tabs, "監視", MonitorTab()); AddTab(tabs, "設定", SettingsTab()); AddTab(tabs, "ログ", LogTab());
        var menu = new ContextMenuStrip(); menu.Items.Add("WACMを開く", null, (_, _) => Restore()); menu.Items.Add("監視状態", null, async (_, _) => await Call("status"));
        menu.Items.Add("監視一時停止", null, async (_, _) => await Call("pause")); menu.Items.Add("監視再開", null, async (_, _) => await Call("resume"));
        menu.Items.Add("サービス状態", null, (_, _) => MessageBox.Show(this, ServiceControl.Status(), "サービス状態")); menu.Items.Add("終了", null, async (_, _) => await ExitGui());
        tray = new() { Icon = Icon, Text = "Win Archive Compact Manager", ContextMenuStrip = menu, Visible = !smoke }; tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && !smoke) Hide(); };
        FormClosing += (_, e) => { if (!exit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else cancel?.Cancel(); };
        FormClosed += (_, _) => { timer.Stop(); tray.Dispose(); Application.RemoveMessageFilter(this); };
        timer.Tick += (_, _) => RefreshProgress(); timer.Start(); Application.AddMessageFilter(this);
        initializing = false; SetFont(settings.FontSize);
        if (!smoke && store.Jobs().Count > 0) Shown += (_, _) => MessageBox.Show(this, "未完了のジョブがあります。「未完了を再開」で現在状態を確認して続行できます。", "WACM");
    }
    private static void AddTab(TabControl tabs, string title, Control control)
    {
        var page = new TabPage(title) { Padding = new(10), AutoScroll = true };
        // DockStyle.Fill ignores a scrollable parent's content extent when the child has a minimum height.
        control.Dock = DockStyle.Top;
        void Fit() => control.Height = Math.Max(control.MinimumSize.Height, page.ClientSize.Height - page.Padding.Vertical);
        page.SizeChanged += (_, _) => Fit(); control.Layout += (_, _) => Fit();
        page.Controls.Add(control); tabs.TabPages.Add(page); Fit();
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal val) => new() { Minimum = min, Maximum = max, Value = val, Width = 130 };
    private static FlowLayoutPanel Flow(params Control[] controls) { var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Padding = new(2) }; f.Controls.AddRange(controls); return f; }
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new(4, 8, 4, 4) };
    private static TableLayoutPanel Vertical(params (Control Control, bool Fill)[] items)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = items.Length, AutoScroll = true };
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (control, fill) in items) { table.RowStyles.Add(new(fill ? SizeType.Percent : SizeType.AutoSize, fill ? 100 : 0)); control.Dock = DockStyle.Fill; table.Controls.Add(control); }
        bool adjusting = false;
        table.Layout += (_, _) =>
        {
            if (adjusting) return; adjusting = true;
            try
            {
                int height = table.Padding.Vertical;
                foreach (var (control, fill) in items)
                {
                    int width = Math.Max(1, table.ClientSize.Width - table.Padding.Horizontal - control.Margin.Horizontal);
                    int preferred = fill ? Math.Max(120, control.MinimumSize.Height) : control is Panel { AutoScroll: true } and not FlowLayoutPanel and not TableLayoutPanel ? Math.Max(130, control.MinimumSize.Height) : control.GetPreferredSize(new Size(width, 0)).Height;
                    height += preferred + control.Margin.Vertical;
                }
                table.MinimumSize = new Size(0, height);
            }
            finally { adjusting = false; }
        };
        return table;
    }
    private Button Button(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(9, 5, 9, 5), Margin = new(4) };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } }; return b;
    }
    private Button AsyncButton(string text, Func<Task> action)
    {
        var b = Button(text, () => { }); b.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } }; return b;
    }
    private Control CompressionTab()
    {
        AddRow(); var host = new Panel { AutoScroll = true, MinimumSize = new(100, 130), Height = 160, Dock = DockStyle.Fill }; host.Controls.Add(rows);
        host.SizeChanged += (_, _) => ResizeRows(host.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);
        method.Items.AddRange([Method.NTFS, Method.XPRESS4K, Method.XPRESS8K, Method.XPRESS16K, Method.LZX]); method.SelectedItem = settings.Method;
        method.SelectedIndexChanged += (_, _) => UpdateDescription(); UpdateDescription();
        var analyze = AsyncButton("高速解析", () => Begin("解析")); var compress = AsyncButton("圧縮開始", () => Begin("圧縮")); var decompress = AsyncButton("圧縮解除", () => Begin("解除")); var resume = AsyncButton("未完了を再開", ResumeJobs);
        operationButtons.AddRange([analyze, compress, decompress, resume]);
        return Vertical((Label("対象（1行に1対象）。複数D&D・貼り付けは空き行へ追加します。"), false), (host, false),
            (Flow(Button("＋追加", AddRow), Button("－削除（入力行のみ）", RemoveRow)), false),
            (Flow(Label("圧縮方式"), method, description), false),
            (Flow(analyze, compress, decompress, resume, Button("中止", () => cancel?.Cancel())), false),
            (progress, false), (current, false), (result, true));
    }
    private void UpdateDescription() => description.Text = ((Method?)method.SelectedItem ?? Method.LZX) switch { Method.NTFS => "更新頻度のあるファイル向け。NTFS標準圧縮。", Method.XPRESS4K => "速度優先。", Method.XPRESS8K => "速度と圧縮率の中間。", Method.XPRESS16K => "圧縮率優先。", _ => "最高圧縮率重視。変更の少ないアーカイブ向け。" };
    private void AddRow()
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Width = Math.Max(600, rows.Parent?.ClientSize.Width - 40 ?? 950), Margin = new(0, 3, 0, 3) };
        row.MinimumSize = new(row.Width, 0);
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.AutoSize)); row.ColumnStyles.Add(new(SizeType.AutoSize));
        var input = new TextBox { Dock = DockStyle.Fill, AllowDrop = true, Margin = new(4, 8, 4, 4), AccessibleName = "圧縮対象パス" };
        input.Enter += (_, _) => selected = input;
        input.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        input.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) Insert(paths, input); };
        row.Controls.Add(input); row.Controls.Add(Button("貼り付け", () => Insert(Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>() : Targets.Parse(Clipboard.GetText()), input)));
        row.Controls.Add(Button("参照 ▼", () =>
        {
            var menu = new ContextMenuStrip(); menu.Items.Add("ファイルを選択（複数可）", null, (_, _) => { using var d = new OpenFileDialog { Multiselect = true }; if (d.ShowDialog(this) == DialogResult.OK) Insert(d.FileNames, input); });
            menu.Items.Add("フォルダを選択", null, (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) Insert([d.SelectedPath], input); }); menu.Show(Cursor.Position);
        }));
        rows.Controls.Add(row); inputs.Add(input); selected = input;
    }
    private void ResizeRows(int width) { foreach (Control row in rows.Controls) { row.MinimumSize = new(Math.Max(500, width), 0); row.MaximumSize = new(Math.Max(500, width), 0); row.Width = Math.Max(500, width); } }
    private void RemoveRow() { if (inputs.Count <= 1) return; var input = selected ?? inputs[^1]; inputs.Remove(input); var row = input.Parent!; rows.Controls.Remove(row); row.Dispose(); selected = inputs[^1]; }
    public void Insert(IEnumerable<string> values, TextBox? preferred = null)
    {
        foreach (var value in values.SelectMany(Targets.Parse))
        {
            if (inputs.Any(x => x.Text.Equals(value, StringComparison.OrdinalIgnoreCase))) continue;
            var dest = preferred != null && string.IsNullOrWhiteSpace(preferred.Text) ? preferred : inputs.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Text));
            if (dest == null) { AddRow(); dest = inputs[^1]; } dest.Text = value; preferred = null;
        }
    }
    private JobOptions Options(Method? force = null) => new(force ?? (Method)method.SelectedItem!, skip.Checked, extensions.Text, (long)minimum.Value, (int)parallel.Value);
    private async Task Begin(string operation)
    {
        if (active != null) return;
        var targets = Targets.Reduce(inputs.Select(x => x.Text)).ToArray(); if (targets.Length == 0) throw new ArgumentException("対象を指定してください。");
        foreach (var p in targets) { if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("対象がありません。", p); Native.EnsureLocalNtfs(p); using (Targets.Guard(p)) { } }
        if (operation != "解析" && MessageBox.Show(this, operation + "する対象:
" + string.Join("
", targets) + (targets.Any(Targets.Broad) ? "
警告: ドライブ全体またはシステム領域を含みます。対象を再確認してください。" : ""), "対象と操作の確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var options = Options(operation == "解除" ? Method.None : null);
        await Work(async token =>
        {
            if (operation == "解析") { var r = Analysis.Run(targets, options, log, p => latestPath = p, token); Ui(() => result.Text = r.ToString()); }
            else { job = store.CreateJob(targets, options); await new JobRunner(store, log).Run(job, targets, options, false, p => latestPath = p, token); }
        });
    }
    private async Task ResumeJobs()
    {
        var jobs = store.Jobs(); if (jobs.Count == 0) { MessageBox.Show(this, "未完了ジョブはありません。"); return; }
        if (MessageBox.Show(this, $"未完了 {jobs.Count} ジョブを再開します。
" + string.Join("
", jobs.SelectMany(j => j.Targets).Take(30)), "再開確認", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        await Work(async token => { foreach (var j in jobs) { token.ThrowIfCancellationRequested(); job = j.Id; await new JobRunner(store, log).Run(j.Id, j.Targets, j.Options, j.Staged, p => latestPath = p, token); } });
    }
    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (active != null) return; cancel = new(); job = null; foreach (var b in operationButtons) b.Enabled = false; progress.Text = "処理中";
        try { active = Task.Run(() => action(cancel.Token)); await active; progress.Text = cancel.IsCancellationRequested ? "中止（未完了は再開できます）" : "完了"; }
        catch (OperationCanceledException) { progress.Text = "中止（未完了は再開できます）"; }
        finally { RefreshProgress(); active = null; cancel.Dispose(); cancel = null; foreach (var b in operationButtons) b.Enabled = true; }
    }
    private Control MonitorTab()
    {
        watches.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "監視フォルダ", DataPropertyName = nameof(WatchRoot.Path), Width = 320, ReadOnly = true });
        watches.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "方式", DataPropertyName = nameof(WatchRoot.Method), DataSource = new[] { Method.NTFS, Method.XPRESS4K, Method.XPRESS8K, Method.XPRESS16K, Method.LZX }, Width = 160 });
        foreach (var (title, prop) in new[] { ("監視ON", nameof(WatchRoot.Enabled)), ("新規", nameof(WatchRoot.NewFiles)), ("更新", nameof(WatchRoot.Updates)), ("事前除外", nameof(WatchRoot.Skip)) }) watches.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = title, DataPropertyName = prop, Width = 110 });
        watches.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "並列 0=Auto", DataPropertyName = nameof(WatchRoot.Parallel), Width = 160 });
        watches.DataError += (_, e) =>
        {
            e.ThrowException = false;
            if (rebindingWatches || deletingWatch || e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= watches.Rows.Count || watches.Rows[e.RowIndex].IsNewRow) return;
            var cell = watches.Rows[e.RowIndex].Cells[e.ColumnIndex];
            cell.ErrorText = "監視設定の入力値が不正です。";
            var identity = e.RowIndex + ":" + e.ColumnIndex;
            if (notifiedInvalidCell == identity) return;
            notifiedInvalidCell = identity;
            BeginInvoke(() => { if (!IsDisposed && !rebindingWatches && !deletingWatch && !smoke) MessageBox.Show(this, "監視設定の入力値が不正です。値を修正してください。", "WACM", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
        };
        watches.CellBeginEdit += (_, e) => { notifiedInvalidCell = null; if (e.RowIndex >= 0 && e.ColumnIndex >= 0) watches.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = ""; };
        BindRoots(); watches.SelectionChanged += (_, _) => { if (SelectedRoot() is { } r) rootExtensions.Text = r.Extensions; };
        var actions = Flow(Button("フォルダ登録", AddWatch), AsyncButton("選択登録を削除", RemoveSelectedWatch),
            Button("選択ルートの除外拡張子を適用", () => { if (SelectedRoot() is { } r) r.Extensions = extensionsForRoot(); }), AsyncButton("保存・監視へ反映", SaveAndReload));
        var service = Flow(AsyncButton("状態確認", async () => { serviceState.Text = await Task.Run(ServiceControl.Status); await Call("status", false); }));
        foreach (var (label, op) in new[] { ("インストール", "install"), ("開始", "start"), ("停止", "stop"), ("再起動", "restart") }) service.Controls.Add(AsyncButton(label, async () =>
        {
            SaveSettings(); if (MessageBox.Show(this, $"Windowsサービスを「{label}」します。
管理者権限が必要です。インストール時はProgram FilesへWACM.exeを保護コピーします。", "サービス管理", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await ServiceControl.Elevate(op); serviceState.Text = ServiceControl.Status();
        }));
        service.Controls.Add(AsyncButton("アンインストール", async () =>
        {
            var choice = ChooseUninstall();
            if (choice == DialogResult.Cancel) return;
            var full = choice == DialogResult.No;
            if (full) { timer.Stop(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
            try
            {
                await ServiceControl.Elevate(full ? "uninstall-full" : "uninstall-service");
                if (full) { exit = true; Close(); } else serviceState.Text = ServiceControl.Status();
            }
            catch { if (full) timer.Start(); throw; }
        }));
        return Vertical((actions, false), (watches, true), (Label("選択ルートの除外拡張子（適用ボタン後に保存）"), false), (rootExtensions, false), (service, false),
            (Flow(AsyncButton("監視一時停止", () => Call("pause")), AsyncButton("監視再開", () => Call("resume")), AsyncButton("監視失敗を再試行", () => Call("retry"))), false), (serviceState, true));
    }
    private DialogResult ChooseUninstall()
    {
        using var dialog = BuildUninstallDialog();
        return dialog.ShowDialog(this);
    }
    private Form BuildUninstallDialog()
    {
        var dialog = new Form { Text = "WACM アンインストール", Font = Font, Icon = Icon, Size = new(860, 360), MinimumSize = new(760, 320), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        var description = new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new(16), Text = "サービスのみ削除: サービス停止・登録解除・Program Filesの保護コピーを削除。設定・ログ・DBは残します。

完全削除: 上記に加え、WACM専用の設定・ログ・DBを削除。元のWACM.exeや圧縮対象は削除しません。

圧縮済みファイルの圧縮状態は変更されません。" };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 80, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new(12) };
        foreach (var (title, result) in new[] { ("サービスのみ削除", DialogResult.Yes), ("完全削除", DialogResult.No), ("キャンセル", DialogResult.Cancel) })
            buttons.Controls.Add(new Button { Text = title, DialogResult = result, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(10, 6, 10, 6), Margin = new(6) });
        dialog.Controls.Add(description); dialog.Controls.Add(buttons);
        dialog.CancelButton = buttons.Controls.OfType<Button>().Last();
        return dialog;
    }
    private string extensionsForRoot() => rootExtensions.Text;
    private WatchRoot? SelectedRoot() { if (rebindingWatches || deletingWatch) return null; var row = watches.CurrentRow; return row == null || row.IsNewRow || row.Index < 0 || row.Index >= settings.Roots.Count ? null : row.DataBoundItem as WatchRoot; }
    private void BindRoots()
    {
        rebindingWatches = true;
        try { watches.DataSource = null; watches.DataSource = new System.ComponentModel.BindingList<WatchRoot>(settings.Roots); notifiedInvalidCell = null; }
        finally { rebindingWatches = false; }
    }
    private async Task RemoveSelectedWatch()
    {
        var selectedRoot = SelectedRoot();
        if (selectedRoot == null) return;
        deletingWatch = true;
        try
        {
            watches.CancelEdit();
            if (!settings.Roots.Remove(selectedRoot)) return;
            BindRoots();
            SaveSettings();
        }
        finally { deletingWatch = false; }
        var state = ServiceControl.Status();
        if (!smoke && state.StartsWith("Running", StringComparison.Ordinal)) await Call("reload", false);
        else serviceState.Text = state;
    }
    private void AddWatch()
    {
        using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var p = Targets.Normalize(dialog.SelectedPath); Native.EnsureLocalNtfs(p); using (Targets.Guard(p)) { }
        if (Targets.Broad(p)) throw new ArgumentException("ドライブ全体・システム領域は自動監視へ登録できません。アーカイブ専用フォルダを指定してください。");
        var v = Native.Volume(p); var canonical = Targets.Canonical(p);
        if (settings.Roots.Any(r => Targets.Contains(r.Resolve(), canonical) || Targets.Contains(canonical, r.Resolve()))) throw new ArgumentException("既存監視ルートと重複・包含しています。");
        settings.Roots.Add(new() { Path = p, Volume = v.Guid, Relative = canonical[v.Guid.Length..], Method = (Method)method.SelectedItem!, Skip = skip.Checked, Extensions = extensions.Text, Parallel = (int)parallel.Value }); BindRoots();
    }
    private Control SettingsTab()
    {
        skip.Checked = settings.Skip; autoStart.Checked = settings.AutoStart; extensions.Text = settings.Extensions; parallel.Value = settings.Parallel; debounce.Value = settings.DebounceSeconds; minimum.Value = settings.MinimumBytes; fontValue.Value = (decimal)settings.FontSize;
        fontValue.ValueChanged += (_, _) => { if (!initializing && !fontChanging) SetFont((float)fontValue.Value); };
        return Vertical((Flow(Label("フォント 9～17pt（Ctrl＋ホイール）"), fontValue), false), (skip, false), (Label("除外拡張子（空白区切り）"), false), (extensions, true),
            (Flow(Button("除外拡張子を初期値へ", () => extensions.Text = Settings.DefaultExtensions)), false), (Flow(Label("最小ファイルサイズ B"), minimum), false),
            (Flow(Label("並列数 0=Auto / 1～8"), parallel), false), (Label("Auto: HDD=1 / SSD=2 / NVMe=4 / 不明=1。複数対象では安全側を採用。"), false),
            (Flow(Label("監視デバウンス 秒"), debounce), false), (autoStart, false), (Flow(AsyncButton("設定を保存・反映", SaveAndReload), Button("保存先を開く", () => Process.Start(new ProcessStartInfo(data.Root) { UseShellExecute = true })), Button("バージョン情報", About)), false));
    }
    private Control LogTab() => Vertical((Flow(Button("ログフォルダを開く", () => Process.Start(new ProcessStartInfo(log.Folder) { UseShellExecute = true })), Button("表示をクリア", () => logs.Clear())), false), (logs, true));
    private void SaveSettings()
    {
        watches.EndEdit(); settings.FontSize = Font.Size; settings.Method = (Method)method.SelectedItem!; settings.Skip = skip.Checked; settings.Extensions = extensions.Text;
        settings.MinimumBytes = (long)minimum.Value; settings.Parallel = (int)parallel.Value; settings.DebounceSeconds = (int)debounce.Value; settings.AutoStart = autoStart.Checked;
        data.Save(settings);
        if (!smoke) { using var key = Registry.CurrentUser.CreateSubKey(@"SoftwareMicrosoftWindowsCurrentVersionRun"); if (settings.AutoStart) key.SetValue("WACM", """ + Environment.ProcessPath + """); else key.DeleteValue("WACM", false); }
    }
    private async Task SaveAndReload() { SaveSettings(); if (!ServiceControl.Status().StartsWith("未インストール")) await Call("reload"); else MessageBox.Show(this, "設定を保存しました。自動監視にはサービスのインストールと開始が必要です。"); }
    private async Task Call(string request, bool showError = true)
    { var initialStatus = ServiceControl.Status(); if (initialStatus.StartsWith("未インストール", StringComparison.Ordinal) || initialStatus.StartsWith("Stopped", StringComparison.Ordinal)) { var unavailable = ServiceControl.IpcUnavailableMessage(initialStatus); serviceState.Text = initialStatus + "
" + unavailable; if (showError) MessageBox.Show(this, unavailable, "WACM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } try { var answer = await Ipc.Call(request); serviceState.Text = ServiceControl.Status() + "
" + answer; } catch (Exception ex) { log.Write("ERROR", "GUI", "IPC " + request, ex.ToString()); var status = ServiceControl.Status(); var message = ServiceControl.IpcUnavailableMessage(status); serviceState.Text = status + "
" + message; if (showError) MessageBox.Show(this, message, "WACM", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
    public void SetFont(float size)
    {
        size = Math.Clamp(size, 9, 17); fontChanging = true; SuspendLayout(); if (!fontCache.TryGetValue((int)size, out var f)) fontCache[(int)size] = f = new Font(SystemFonts.MessageBoxFont!.FontFamily, size); Font = f; fontValue.Value = (decimal)size;
        watches.Font = Font; watches.DefaultCellStyle.Font = Font; watches.ColumnHeadersDefaultCellStyle.Font = Font;
        foreach (DataGridViewColumn column in watches.Columns) column.Width = (int)((column.Index == 0 ? 320 : column.Index == 1 ? 160 : 130) * size / 13);
        ResumeLayout(true); fontChanging = false; settings.FontSize = size;
        if (!initializing) { try { data.Save(settings); } catch (Exception ex) { Error(ex); } }
    }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == 0x20A && ((long)m.WParam & 8) != 0 && ContainsFocus) { var delta = unchecked((short)((long)m.WParam >> 16)); SetFont(Font.Size + Math.Sign(delta)); return true; } return false;
    }
    private bool refreshing;
    private async void RefreshProgress()
    {
        if (refreshing || IsDisposed) return; refreshing = true;
        try {
        current.Text = latestPath;
        if (job != null)
        {
            var selectedJob = job; var t = await Task.Run(() => store.Totals(selectedJob)); if (IsDisposed || job != selectedJob) return; if (active is { IsCompleted: false }) progress.Text = $"処理 {t.Done:N0} / {t.Count:N0} ファイル ・ {t.Processed:N0} / {t.Bytes:N0} B";
            decimal reduction = (decimal)t.Before - t.After;
            result.Text = $"論理サイズ {SizeFormat.Human(t.Bytes)}
処理前ディスク使用 {SizeFormat.Human(t.Before)}
処理後ディスク使用 {SizeFormat.Human(t.After)}
削減 {SizeFormat.Human(reduction)} / {(t.Before == 0 ? 0 : 100m * reduction / t.Before):F2}%
圧縮比（処理後/論理） {(t.Processed == 0 ? 0 : (double)t.After / t.Processed):F4}
{t.Counts}
Success=成功 / Already=既設定 / Skipped=事前除外
Unsupported=非対応 / Denied=アクセス拒否 / Failed=失敗
pending=未処理・中止後再開待ち / running=処理中";
        }
        var recent = log.Recent(); if (logs.Text != recent) { logs.Text = recent; logs.SelectionStart = logs.TextLength; logs.ScrollToCaret(); }
        } catch (Exception ex) { if (!IsDisposed) progress.Text = "進捗確認: " + ex.Message; } finally { refreshing = false; }
    }
    private async Task ExitGui()
    {
        if (active != null) { cancel?.Cancel(); try { await active; } catch (Exception ex) { log.Write("WARN", "GUI", "終了", ex.Message); } }
        SaveSettings(); exit = true; Close();
    }
    private void About()
    {
        using var about = BuildAboutDialog();
        about.ShowDialog(this);
    }
    private Form BuildAboutDialog()
    {
        var about = new Form { Text = "WACM バージョン情報", Icon = Icon, Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Padding = new(16) };
        var content = "Win Archive Compact Manager 1.0.1
Windows標準 NTFS / WOF 圧縮
×はトレイへ。終了はトレイメニューから。";
        var measured = TextRenderer.MeasureText(content, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        var width = Math.Max(620, measured.Width + 160);
        var height = Math.Max(230, measured.Height + 160);
        about.ClientSize = new(width, height);
        about.MinimumSize = about.Size;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 100)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var bitmap = (Icon ?? SystemIcons.Application).ToBitmap();
        about.FormClosed += (_, _) => bitmap.Dispose();
        var picture = new PictureBox { Image = bitmap, SizeMode = PictureBoxSizeMode.Zoom, Size = new(64, 64), Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new(12) };
        var label = new Label { Text = content, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Margin = new(12) };
        var close = new Button { Text = "閉じる", DialogResult = DialogResult.OK, AutoSize = true, Padding = new(14, 5, 14, 5), Anchor = AnchorStyles.Right, Margin = new(8) };
        layout.Controls.Add(picture, 0, 0); layout.Controls.Add(label, 1, 0); layout.Controls.Add(close, 1, 1);
        about.Controls.Add(layout); about.AcceptButton = close; about.CancelButton = close;
        return about;
    }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void Ui(Action action) { if (!IsDisposed) BeginInvoke(action); }
    private void Error(Exception ex) { log.Write("ERROR", "GUI", "操作", ex.ToString()); MessageBox.Show(this, ex.Message, "WACM", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    public void CloseForTest() { exit = true; Close(); }
}
