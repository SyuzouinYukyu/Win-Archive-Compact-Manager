using System.Security.Cryptography;
using System.Security.Principal;
using WACM;

internal static class Program
{
    private static int count;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
    [STAThread] private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "WACM-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            if (args.Contains("--v101-only")) { V101Tests.CompressionState(root, Check); V101Tests.Run(root, Check); V101GuiTests.Run(root, Check); Console.WriteLine($"SELF_TEST_OK {count} tests; fixture={root}"); return 0; }
            V101Tests.CompressionState(root, Check);
            var data = new AppData(Path.Combine(root, "state")); var log = new AppLog(data, "test"); var store = new Store(data);
            var sampleBase = Path.Combine(Path.GetTempPath(), "WACM-path-samples");
            var sampleA = Path.Combine(sampleBase, "日本語 空白;名前.txt");
            var sampleB = Path.Combine(sampleBase, "別.txt");
            Check(Targets.Parse($" "{sampleA}"
{sampleB} ").SequenceEqual(new[] { sampleA, sampleB }), "paste quotes Japanese semicolon");
            var parent = Path.Combine(sampleBase, "A");
            var child = Path.Combine(parent, "b");
            var sibling = Path.Combine(sampleBase, "AB");
            Check(Targets.Reduce([child, parent, parent.ToUpperInvariant(), sibling]).Count == 2, "duplicate and parent exclusion boundary");
            Check(!Targets.Contains(parent, sibling), "sibling not descendant");
            var settings = new Settings { FontSize = 99 }; data.Save(settings); Check(data.Load().FontSize == 17, "font upper clamp and settings restore");
            settings.FontSize = -1; data.Save(settings); Check(data.Load().FontSize == 9, "font lower clamp");
            foreach (var size in Enumerable.Range(9, 9)) { settings.FontSize = size; data.Save(settings); Check(data.Load().FontSize == size, "font persistence " + size); }
            var options = new JobOptions(Method.LZX, true, Settings.DefaultExtensions, 4096, 1);
            var state = new FileState(8192, 8192, 1, Method.None, 0, 1);
            Check(CompressionEngine.Excluded("x.JPG", state, options), "case insensitive extension skip");
            foreach (var ext in new[] { ".iso", ".img", ".bin", ".pdf", ".exe", ".dll", ".msi" }) Check(!CompressionEngine.Excluded("x" + ext, state, options), "content dependent allowed " + ext);
            Check(CompressionEngine.Excluded("x.txt", state with { Length = 1 }, options), "minimum size skip");
            Check(!CompressionEngine.Excluded("x.jpg", state, options with { Method = Method.None }), "decompress ignores exclusions");
            var job = store.CreateJob([root], options); var p = Path.Combine(root, "queue.txt"); store.Enqueue(job, "", p, false, options); store.Enqueue(job, "", p.ToUpperInvariant(), false, options);
            Check(store.Totals(job).Count == 1, "persistent queue dedup"); var item = store.Claim(job)!; Check(item != null && store.Claim(job) == null, "exclusive queue claim");
            var recovered = new Store(data); recovered.Recover(job); Check(recovered.Claim(job) != null, "crash running queue recovery"); recovered.Recover(job);
            item = recovered.Claim(job)!; recovered.Enqueue(job, "", p, false, options, due: DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(), refresh: true); recovered.Complete(item, new(Outcome.Success, p));
            Check(recovered.Claim(job) == null && recovered.Totals(job).Done == 0, "event during running retained and debounced");
            var racingJob = store.CreateJob([root], options); store.Enqueue(racingJob, "", p, false, options);
            var racing = store.Claim(racingJob)!;
            store.Enqueue(racingJob, "", p, true, options, refresh: true);
            store.Retry(racing with { Retry = 7 }, "old attempt failed after new change");
            var newer = store.Claim(racingJob);
            Check(newer != null && newer.Directory && newer.Retry == 0, "new event survives stale final retry and path type replacement");
            store.RetryFailed(racingJob); Check(store.Claim(racingJob) == null, "retry failed does not requeue running work");
            recovered.Checkpoint("r", ulong.MaxValue, 1234); Check(recovered.Checkpoint("r") == (ulong.MaxValue, 1234), "USN durable checkpoint");
            recovered.Seen("r", p, state); Check(recovered.Unchanged("r", p, state) && !recovered.Unchanged("r", p, state with { WriteTicks = 2 }), "metadata change only");
            using (var stream = new MemoryStream()) { Ipc.Write(stream, "status 日本語", default).GetAwaiter().GetResult(); stream.Position = 0; Check(Ipc.Read(stream, default).GetAwaiter().GetResult() == "status 日本語", "IPC frame roundtrip"); }
            using (var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue))) { bool rejected = false; try { Ipc.Read(stream, default).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; } Check(rejected, "IPC oversize reject"); }
            var acl = Ipc.Security(WindowsIdentity.GetCurrent().User!.Value).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access); Check(acl.Contains("NU") && acl.Contains("SY") && acl.Contains("BA"), "IPC local network deny and authorized ACL");
            var usn = new byte[72]; BitConverter.GetBytes(64u).CopyTo(usn, 8); BitConverter.GetBytes((ushort)2).CopyTo(usn, 12); BitConverter.GetBytes(123ul).CopyTo(usn, 16); BitConverter.GetBytes(1u).CopyTo(usn, 48); BitConverter.GetBytes((ushort)60).CopyTo(usn, 66);
            Check(UsnJournal.Parse(usn, usn.Length).Single().FileId == 123, "USN V2 parsing"); usn[12] = 3; bool bad = false; try { UsnJournal.Parse(usn, usn.Length); } catch (InvalidDataException) { bad = true; } Check(bad, "USN unknown version safe fallback");
            var files = Path.Combine(root, "専用 圧縮テスト"); Directory.CreateDirectory(files);
            var file = Path.Combine(files, "日本語 空白 & ; 文書.txt"); var payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("圧縮検証 archive test abcdefghijklmnopqrstuvwxyz
", 20000))); File.WriteAllBytes(file, payload);
            var hash = SHA256.HashData(payload); Native.EnsureLocalNtfs(file); using (Targets.Guard(file)) { }
            var engine = new CompressionEngine(log);
            foreach (var method in new[] { Method.NTFS, Method.XPRESS4K, Method.XPRESS8K, Method.XPRESS16K, Method.LZX })
            {
                var opt = options with { Method = method, Skip = false, MinimumBytes = 0 };
                var compressed = engine.Execute(file, false, opt, default).GetAwaiter().GetResult(); Console.WriteLine(compressed);
                Check(compressed.Outcome == Outcome.Success, method + " real compression");
                Check(Native.Inspect(file).Method == method, method + " actual state");
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), method + " logical SHA256");
                Check(engine.Execute(file, false, opt, default).GetAwaiter().GetResult().Outcome == Outcome.Already, method + " same method skip");
            }
            var plain = engine.Execute(file, false, options with { Method = Method.None }, default).GetAwaiter().GetResult(); Check(plain.Outcome == Outcome.Success && Native.Inspect(file).Method == Method.None, "WOF decompression"); Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "decompression logical SHA256");
            var dirResult = engine.Execute(files, true, options with { Method = Method.NTFS }, default).GetAwaiter().GetResult(); Check(dirResult.Outcome == Outcome.Success, "NTFS directory inheritance set");
            var inherited = Path.Combine(files, "inherited.txt"); File.WriteAllBytes(inherited, payload); Check(Native.Inspect(inherited).Method == Method.NTFS, "new file inherits NTFS");
            Check(engine.Execute(inherited, false, options with { Method = Method.None }, default).GetAwaiter().GetResult().Outcome == Outcome.Success, "NTFS decompress");
            Check(engine.Execute(files, true, options with { Method = Method.None }, default).GetAwaiter().GetResult().Outcome == Outcome.Success, "directory inheritance clear");
            var longDir = files; for (int i = 0; i < 8; i++) longDir = Path.Combine(longDir, "長い名前012345678901234567890123456789"); Directory.CreateDirectory(longDir); var longFile = Path.Combine(longDir, "日本語.txt"); File.WriteAllBytes(longFile, payload);
            Check(longFile.Length > 260, "long path fixture"); var lr = engine.Execute(longFile, false, options, default).GetAwaiter().GetResult(); Console.WriteLine(lr); Check(lr.Outcome == Outcome.Success, "long path LZX"); Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(longFile))), "long path SHA256");
            var analysis = Analysis.Run([files, file], options, log, _ => { }, default); Check(analysis.Files == 3 && analysis.Logical == 3L * payload.Length, "streaming analysis dedup totals");
            Check(analysis.Allocated > 0 && analysis.Allocated < analysis.Logical, "actual compressed allocation statistics");
            using var cts = new CancellationTokenSource(); cts.Cancel(); Check(engine.Execute(file, false, options, cts.Token).GetAwaiter().GetResult().Outcome == Outcome.Cancelled, "cancel safe state");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            using (var form = new MainForm(new AppData(Path.Combine(root, "ui")), true)) { form.Show(); form.Insert([file, longFile]); foreach (var size in Enumerable.Range(9, 9)) { form.SetFont(size); Application.DoEvents(); Check(form.Font.Size == size, "GUI font " + size); } using (var image = new System.Drawing.Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new System.Drawing.Rectangle(0,0,image.Width,image.Height)); image.Save(Path.Combine(root,"gui-17.png")); } form.CloseForTest(); }
            LayoutTests.Run(root, Check);
            V101Tests.Run(root, Check);
            V101GuiTests.Run(root, Check);
            IntegrationTests.Run(root, payload, Check).GetAwaiter().GetResult();
            Console.WriteLine($"SELF_TEST_OK {count} tests; fixture={root}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixture retained: " + root); return 1; }
    }
}
