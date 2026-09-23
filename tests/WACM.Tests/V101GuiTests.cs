using System.Reflection;
using System.Security.Cryptography;
using WACM;

internal static class V101GuiTests
{
    public static void Run(string root, Action<bool,string> check)
    {
        var data = new AppData(Path.Combine(root, "gui-v101"));
        var first = Path.Combine(root, "監視対象A"); var second = Path.Combine(root, "監視対象B");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var a = Path.Combine(first, "A.txt"); var b = Path.Combine(second, "B.txt");
        File.WriteAllText(a, "Aの原本"); File.WriteAllText(b, "Bの原本");
        var ha = SHA256.HashData(File.ReadAllBytes(a)); var hb = SHA256.HashData(File.ReadAllBytes(b));
        var settings = new Settings(); settings.Roots.Add(new() { Path = first, Method = Method.NTFS }); settings.Roots.Add(new() { Path = second, Method = Method.LZX }); data.Save(settings);
        using (var form = new MainForm(data, smoke: true))
        {
            form.Show(); Application.DoEvents();
            var aboutFactory = typeof(MainForm).GetMethod("BuildAboutDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var size in new[] { 9f, 13f, 17f })
            {
                form.SetFont(size);
                using var about = (Form)aboutFactory.Invoke(form, null)!;
                about.Show(form); Application.DoEvents();
                var table = about.Controls.OfType<TableLayoutPanel>().Single();
                var label = table.Controls.OfType<Label>().Single(); var picture = table.Controls.OfType<PictureBox>().Single(); var close = table.Controls.OfType<Button>().Single();
                var measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(2000, 1000), TextFormatFlags.NoPadding);
                check(about.ClientSize.Width >= 600 && about.Text == "WACM バージョン情報" && label.Text.Contains("Win Archive Compact Manager 1.0.1") && label.ClientSize.Width >= measured.Width && label.ClientSize.Height >= measured.Height, "About full title product version body font " + size);
                check(picture.Image != null && !picture.Bounds.IntersectsWith(label.Bounds) && close.Right <= table.ClientSize.Width && close.Bottom <= table.ClientSize.Height && close.Width >= close.GetPreferredSize(Size.Empty).Width, "About icon no overlap close button font " + size);
                var proof = Path.Combine(root, "about-" + size + ".png"); using (var image = new Bitmap(about.Width, about.Height)) { about.DrawToBitmap(image, new Rectangle(0,0,image.Width,image.Height)); image.Save(proof); }
                about.Close(); check(!about.Visible, "About closes font " + size);
            }
            var grid = (DataGridView)typeof(MainForm).GetField("watches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            check(grid.Rows.Count == 2, "watch registration two rows shown");
            var onError = typeof(DataGridView).GetMethod("OnDataError", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var invalid = new DataGridViewDataErrorEventArgs(new FormatException("invalid"), 1, 0, DataGridViewDataErrorContexts.Display);
            onError.Invoke(grid, [true, invalid]); onError.Invoke(grid, [true, invalid]); Application.DoEvents();
            check(!string.IsNullOrWhiteSpace(grid.Rows[0].Cells[1].ErrorText), "invalid watch value visibly marked without modal recursion in smoke");
            var remove = typeof(MainForm).GetMethod("RemoveSelectedWatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
            grid.CurrentCell = grid.Rows[0].Cells[0];
            ((Task)remove.Invoke(form, null)!).GetAwaiter().GetResult(); Application.DoEvents();
            check(data.Load().Roots.Count == 1 && data.Load().Roots[0].Path == second && grid.Rows.Count == 1, "watch delete selected only and persist remaining");
            check(File.Exists(a) && File.Exists(b) && ha.SequenceEqual(SHA256.HashData(File.ReadAllBytes(a))) && hb.SequenceEqual(SHA256.HashData(File.ReadAllBytes(b))), "watch delete preserves both target files");
            grid.CurrentCell = grid.Rows[0].Cells[0]; ((Task)remove.Invoke(form, null)!).GetAwaiter().GetResult(); Application.DoEvents();
            check(data.Load().Roots.Count == 0 && grid.Rows.Count == 0, "watch final registration delete to zero");
            grid.AllowUserToAddRows = true; Application.DoEvents();
            if (grid.Rows.Count > 0 && grid.Rows[^1].IsNewRow) { grid.CurrentCell = grid.Rows[^1].Cells[0]; ((Task)remove.Invoke(form, null)!).GetAwaiter().GetResult(); }
            check(data.Load().Roots.Count == 0, "watch new-entry placeholder never treated as registration");
            ((TabControl)form.Controls.OfType<TabControl>().Single()).SelectedIndex = 2; Application.DoEvents();
            check(!form.IsDisposed, "watch delete still permits tab navigation");
            form.CloseForTest();
        }
        using (var restored = new MainForm(data, smoke: true))
        { restored.Show(); Application.DoEvents(); var grid = (DataGridView)typeof(MainForm).GetField("watches", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restored)!; check(grid.Rows.Count == 0 && data.Load().Roots.Count == 0, "watch deletion persists after GUI restart"); restored.CloseForTest(); }
        check(File.Exists(a) && File.Exists(b), "watch GUI restart leaves targets intact");
        var cases = new (decimal Bytes, string Unit)[] { (0," B"),(1," B"),(1023," B"),(1024," KB"),(1048575," KB"),(1048576," MB"),(1073741823," MB"),(1073741824," GB"),(1099511627775m," GB"),(1099511627776m," TB"),(1099511627777m," TB"),(long.MaxValue," TB"),(long.MinValue," TB") };
        foreach (var (bytes, unit) in cases) check(SizeFormat.Human(bytes).EndsWith(unit, StringComparison.Ordinal), "capacity unit boundary " + bytes);
        check(SizeFormat.Human(1024) == "1.00 KB" && SizeFormat.Human(1536) == "1.50 KB" && SizeFormat.Human(1023) == "1,023 B", "capacity precision and binary units");
    }
}
