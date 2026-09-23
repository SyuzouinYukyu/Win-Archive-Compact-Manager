using WACM;

internal static class LayoutTests
{
    private static IEnumerable<Control> Descendants(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    public static void Run(string fixture, Action<bool, string> check)
    {
        using var form = new MainForm(new AppData(Path.Combine(fixture, "layout-state")), true);
        var existing = Path.Combine(fixture, "既存の入力.txt");
        var newA = Path.Combine(fixture, "新規 A.txt");
        var newB = Path.Combine(fixture, "新規 B.txt");
        form.Show(); form.Insert([existing]); form.Insert([newA, newB, newA]);
        var targets = Descendants(form).OfType<TextBox>().Where(t => t.AccessibleName == "圧縮対象パス").ToArray();
        check(targets.Length == 3 && targets[0].Text == existing, "multiple input preserves existing and deduplicates");
        var tabs = Descendants(form).OfType<TabControl>().Single();
        foreach (var size in Enumerable.Range(9, 9))
        {
            form.SetFont(size);
            foreach (TabPage tab in tabs.TabPages)
            {
                tabs.SelectedTab = tab; Application.DoEvents();
                var clipped = Descendants(tab).OfType<Button>().Where(b => b.Width < b.PreferredSize.Width || b.Height < b.PreferredSize.Height).Select(b => b.Text).ToArray();
                check(clipped.Length == 0, $"layout buttons font={size} tab={tab.Text} clipped={string.Join(',', clipped)}");
                if (size is 13 or 17)
                {
                    using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                    image.Save(Path.Combine(fixture, $"layout-{size}-{tabs.SelectedIndex}.png"));
                }
            }
        }
        form.Size = form.MinimumSize; Application.DoEvents();
        foreach (TabPage tab in tabs.TabPages) { tabs.SelectedTab = tab; Application.DoEvents(); check(Descendants(tab).OfType<Button>().All(b => b.Width >= b.PreferredSize.Width && b.Height >= b.PreferredSize.Height), "minimum window buttons " + tab.Text); }
        form.VerifyGui(check, Path.Combine(fixture, "extended-gui"), false);
        form.CloseForTest();
    }
}
