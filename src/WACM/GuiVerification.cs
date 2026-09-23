using System.Reflection;
namespace WACM;
public sealed partial class MainForm
{
    public void VerifyGui(Action<bool, string> check, string output, bool verifyEmbeddedIcon)
    {
        Directory.CreateDirectory(output);
        var tabs = Controls.OfType<TabControl>().Single();
        check(method.Items.Cast<Method>().SequenceEqual(new[] { Method.NTFS, Method.XPRESS4K, Method.XPRESS8K, Method.XPRESS16K, Method.LZX }), "GUI five methods");
        check(ReferenceEquals(tray.Icon, Icon) && Icon != null, "form and tray share dedicated icon");
        if (verifyEmbeddedIcon)
        {
            using var bitmap = Icon!.ToBitmap(); int teal = 0;
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) { var p = bitmap.GetPixel(x, y); if (p.R == 81 && p.G == 201 && p.B == 190) teal++; }
            check(teal > 10, "embedded WACM icon design pixels"); bitmap.Save(Path.Combine(output, "embedded-icon.png"));
        }
        foreach (var size in new[] { 9, 13, 17 })
        foreach (var small in new[] { false, true })
        {
            Size = small ? MinimumSize : new Size(1150, 850); SetFont(size);
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page; PerformLayout(); Application.DoEvents();
                var issues = LayoutIssues(page);
                if (page.Controls.Cast<Control>().Any(c => c.Bottom > page.ClientSize.Height) && !page.VerticalScroll.Visible) issues.Add("content below viewport without vertical scrolling");
                check(issues.Count == 0, $"all controls font={size} minimum={small} tab={page.Text} {string.Join("; ", issues)}");
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(Path.Combine(output, $"gui-{size}-{(small ? "min" : "normal")}-{tabs.SelectedIndex}.png"));
            }
        }
        Size = new(1150, 850); tabs.SelectedIndex = 0; SetFont(13); Activate(); inputs[0].Focus(); Application.DoEvents();
        check(ContainsFocus, "wheel message target has focus");
        bool Wheel(int delta, bool ctrl)
        {
            var m = Message.Create(inputs[0].Handle, 0x20A, new IntPtr((delta << 16) | (ctrl ? 8 : 0)), IntPtr.Zero);
            return PreFilterMessage(ref m);
        }
        check(Wheel(120, true) && Font.Size == 14, "Ctrl wheel up +1pt");
        check(Wheel(-120, true) && Font.Size == 13, "Ctrl wheel down -1pt");
        check(!Wheel(120, false) && Font.Size == 13, "ordinary wheel not intercepted");
        SetFont(9); Wheel(-120, true); check(Font.Size == 9, "Ctrl wheel lower boundary");
        SetFont(17); Wheel(120, true); check(Font.Size == 17, "Ctrl wheel upper boundary");
        var fixture = Path.Combine(output, "drop-fixture"); Directory.CreateDirectory(fixture);
        int serial = 0;
        string FileItem() { var p = Path.Combine(fixture, $"file {++serial}.txt"); File.WriteAllText(p, "isolated drop fixture"); return p; }
        string FolderItem() { var p = Path.Combine(fixture, $"folder {++serial}"); Directory.CreateDirectory(p); return p; }
        var cases = new[] { new[] { FileItem() }, new[] { FileItem(), FileItem() }, new[] { FolderItem() }, new[] { FolderItem(), FolderItem() }, new[] { FileItem(), FolderItem() } };
        foreach (var paths in cases)
        {
            var previous = inputs.Where(x => x.Text.Length > 0).Select(x => x.Text).ToArray();
            var target = inputs[0]; var obj = new DataObject(DataFormats.FileDrop, paths);
            var args = new DragEventArgs(obj, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            typeof(Control).GetMethod("OnDragEnter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [args]);
            check(args.Effect == DragDropEffects.Copy, "D&D accept FileDrop event " + paths.Length);
            typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [args]);
            check(previous.Concat(paths).All(p => inputs.Any(i => i.Text == p)), "D&D preserves existing and creates rows " + string.Join(',', paths.Select(p => Directory.Exists(p) ? "folder" : "file")));
        }
        int count = inputs.Count; AddRow(); check(inputs.Count == count + 1, "plus adds row");
        while (inputs.Count > 1) RemoveRow(); RemoveRow(); check(inputs.Count == 1, "minus preserves final row");
        check(cases.SelectMany(x => x).All(p => File.Exists(p) || Directory.Exists(p)), "row deletion preserves files and folders");
        check(data.Load().FontSize == 17, "Release font saved");
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; if (child is not DataGridView && child is not NumericUpDown) foreach (var next in Descendants(child)) yield return next; }
    }
    public static List<string> LayoutIssues(Control root)
    {
        var issues = new List<string>();
        foreach (var c in Descendants(root).Where(c => c.Visible))
        {
            if (c is ButtonBase || c is Label)
            {
                var preferred = c.GetPreferredSize(c is Label ? new Size(c.Width, 0) : Size.Empty);
                if (c.Width < preferred.Width || c.Height < preferred.Height) issues.Add($"clipped {c.GetType().Name} {c.Text}: {c.Size}/{preferred}");
                if (c is Label { AutoEllipsis: true } || c is ButtonBase { AutoEllipsis: true }) issues.Add("ellipsis " + c.Text);
            }
            if (c is TableLayoutPanel or FlowLayoutPanel)
            {
                var children = c.Controls.Cast<Control>().Where(x => x.Visible && x.Width > 0 && x.Height > 0).ToArray();
                for (var i = 0; i < children.Length; i++) for (var j = i + 1; j < children.Length; j++)
                    if (children[i].Bounds.IntersectsWith(children[j].Bounds)) issues.Add("overlap " + children[i].Text + " / " + children[j].Text);
            }
        }
        return issues;
    }
}
