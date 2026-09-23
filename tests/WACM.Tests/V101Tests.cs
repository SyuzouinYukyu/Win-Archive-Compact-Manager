using System.Text;
using Microsoft.Data.Sqlite;
using System.ServiceProcess;
using WACM;

internal static class V101Tests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan span) => Now += span;
    }
    public static void Run(string root, Action<bool,string> check)
    {
        var folder = Path.Combine(root, "v101"); Directory.CreateDirectory(folder);
        var guid = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\";
        var watched = guid + @"Users\監視";
        check(UsnJournal.NeedsResync((7, 100, 200), null), "USN no checkpoint resync");
        check(!UsnJournal.NeedsResync((7, 100, 200), (7, 150)), "USN saved checkpoint resumes");
        check(UsnJournal.NeedsResync((8, 100, 200), (7, 150)) && UsnJournal.NeedsResync((7, 160, 200), (7, 150)) && UsnJournal.NeedsResync((7, 100, 200), (7, 250)), "USN ID change Wrap and invalid checkpoint");
        var raw = new byte[80]; BitConverter.GetBytes(72u).CopyTo(raw, 8); BitConverter.GetBytes((ushort)2).CopyTo(raw, 12); BitConverter.GetBytes(12ul).CopyTo(raw, 16); BitConverter.GetBytes(11ul).CopyTo(raw, 24); BitConverter.GetBytes(1u).CopyTo(raw, 48); BitConverter.GetBytes((ushort)10).CopyTo(raw, 64); BitConverter.GetBytes((ushort)60).CopyTo(raw, 66); Encoding.Unicode.GetBytes("a.txt").CopyTo(raw, 68);
        var parsed = UsnJournal.Parse(raw, raw.Length).Single();
        check(parsed.FileId == 12 && parsed.ParentId == 11 && parsed.Name == "a.txt", "USN V2 parent and name parsing");
        var entries = new[] { parsed, parsed with { FileId = 13, ParentId = 21 }, parsed with { FileId = 14, ParentId = 22 } };
        var calls = new List<ulong>(); var changed = new List<string>();
        (string? Path, int Error) Resolve(ulong id) { calls.Add(id); return id switch { 11 => (watched, 0), 12 => (watched + @"\a.txt", 0), 21 => (guid + @"Elsewhere", 0), 22 => (null, 5), _ => (null, 5) }; }
        var uncertain = UsnJournal.ProcessEntries(entries, watched, Resolve, (p, _, _) => changed.Add(p), default);
        check(uncertain && changed.SequenceEqual([watched + @"\a.txt"]) && !calls.Contains(13) && !calls.Contains(14), "USN outside root IDs never opened; protected parent reconciled");
        calls.Clear(); changed.Clear();
        var insideDenied = UsnJournal.ProcessEntries([parsed], watched, id => id == 11 ? (watched, 0) : (null, 5), (p, _, _) => changed.Add(p), default);
        check(!insideDenied && changed.Single() == watched + @"\a.txt", "USN protected watched file still queued");
        check(UsnJournal.WithinRoot(watched, watched + @"\nested\a.txt") && !UsnJournal.WithinRoot(watched, watched + @"Extra\a.txt"), "USN Volume GUID containment boundary");

        var targetedFile = Path.Combine(folder, "USN_日本語.txt"); File.WriteAllText(targetedFile, "専用テスト");
        using (var ownHandle = Native.Open(targetedFile, 0x80000000))
        {
            var ownRecord = new byte[4096];
            if (Native.DeviceIoControl(ownHandle, 0x900EB, null, 0, ownRecord, ownRecord.Length, out var ownLength, IntPtr.Zero) && ownLength >= 60 && BitConverter.ToUInt16(ownRecord, 4) == 2)
            {
                var ownId = BitConverter.ToUInt64(ownRecord, 8); var parentId = BitConverter.ToUInt64(ownRecord, 16);
                var volumeName = Native.Volume(targetedFile).Guid;
                check(ownId != 0 && parentId != 0 && volumeName.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase), "actual own-file USN V2 IDs on Volume GUID");
                try
                {
                    using var volumeHandle = Native.Open(volumeName.TrimEnd('\'), 0x80000000);
                    var descriptor = new Native.FileId { Size = 24, Type = 0, Value = ownId };
                    using var found = Native.OpenFileById(volumeHandle, ref descriptor, 0, 7, IntPtr.Zero, 0x02200000);
                    var parentDescriptor = new Native.FileId { Size = 24, Type = 0, Value = parentId };
                    using var foundParent = Native.OpenFileById(volumeHandle, ref parentDescriptor, 0, 7, IntPtr.Zero, 0x02200000);
                    check(!found.IsInvalid && !foundParent.IsInvalid && Native.Final(found).Equals(Targets.Canonical(targetedFile), StringComparison.OrdinalIgnoreCase) && Native.Final(foundParent).Equals(Targets.Canonical(folder), StringComparison.OrdinalIgnoreCase), "actual own-file USN IDs resolve on Volume GUID");
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) { Console.WriteLine("USN_VOLUME_ID_RESOLVE_UNVERIFIED: volume handle requires service privileges"); }
            }
            else Console.WriteLine("USN_TARGETED_ACTUAL_UNVERIFIED: FSCTL_READ_FILE_USN_DATA unavailable");
        }
        var protectedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WACM", "WACM.exe");
        var quoted = $""{protectedExe}" --service";
        check(ServiceControl.ParseImagePath(quoted) == protectedExe, "SCM quoted ImagePath with service argument");
        check(!ServiceControl.DescribeExecutable(ServiceControllerStatus.Running, quoted, 5).Contains("ありません") && ServiceControl.DescribeExecutable(ServiceControllerStatus.Running, quoted, 5).Contains("保護"), "running protected EXE not missing");
        check(ServiceControl.DescribeExecutable(ServiceControllerStatus.Stopped, quoted, 2).Contains("ありません") && !ServiceControl.DescribeExecutable(ServiceControllerStatus.Running, quoted, 2).Contains("ありません"), "real missing EXE versus running state");
        check(ServiceControl.IpcUnavailableMessage("未インストール") == "監視サービスはインストールされていません。" && ServiceControl.IpcUnavailableMessage("Stopped") == "監視サービスは停止しています。" && ServiceControl.IpcUnavailableMessage("Running") == "監視サービスへ接続できませんでした。", "IPC user messages Japanese by service state");

        bool timedOut = false;
        var silentPipe = "WACM.Test.Silent." + Guid.NewGuid().ToString("N");
        using (var silentServer = new System.IO.Pipes.NamedPipeServerStream(silentPipe, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            var accepted = silentServer.WaitForConnectionAsync();
            var call = Ipc.Call("status", silentPipe, TimeSpan.FromMilliseconds(300));
            accepted.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            try { _ = call.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { timedOut = true; }
            catch (System.TimeoutException) { timedOut = true; }
        }
        check(timedOut && ServiceControl.IpcUnavailableMessage("Running") == "監視サービスへ接続できませんでした。", "connected silent Named Pipe timeout has Japanese UI message");

        var time = new ManualTime(); var logsData = new AppData(Path.Combine(folder, "logs")); var log = new AppLog(logsData, "gui", time);
        log.Write("WARN", "p", "a", "同一"); log.Write("WARN", "p", "a", "同一"); log.Write("WARN", "p2", "a", "同一"); log.Write("ERROR", "p", "a", "エラー"); log.Write("ERROR", "p", "a", "エラー");
        var logFile = Path.Combine(log.Folder, "gui.log");
        check(File.ReadAllLines(logFile).Length == 4, "WARN initial and duplicate suppression; ERROR never suppressed");
        time.Advance(TimeSpan.FromMinutes(10)); log.Write("WARN", "p", "a", "同一");
        check(File.ReadAllText(logFile).Contains("同一警告を 1 回抑制しました。") && File.ReadAllLines(logFile).Length == 6, "WARN ten minute summary and new warning");
        using (var stream = new FileStream(logFile, FileMode.Open, FileAccess.Write)) stream.SetLength(AppLog.Limit - 2);
        log.Write("INFO", "日本語", "Success", "行を分断しない");
        check(new FileInfo(logFile).Length < AppLog.Limit && new FileInfo(logFile + ".1").Length == AppLog.Limit - 2 && File.ReadAllText(logFile).Contains("行を分断しない"), "10MiB prospective rotation and UTF8");
        for (int i = 0; i < 6; i++) { using (var stream = new FileStream(logFile, FileMode.Open, FileAccess.Write)) stream.SetLength(AppLog.Limit - 2); log.Write("INFO", "p", "Success", "rotate" + i); }
        check(Enumerable.Range(1,5).All(i => File.Exists(logFile + "." + i)) && !File.Exists(logFile + ".6"), "rotation keeps exactly five historical generations");
        Parallel.For(0, 100, i => log.Write("INFO", "p", "Success", "parallel-" + i));
        var utf8 = new UTF8Encoding(false, true); var current = utf8.GetString(File.ReadAllBytes(logFile));
        check(current.Split("parallel-", StringSplitOptions.None).Length - 1 == 100 && current.EndsWith(Environment.NewLine), "concurrent UTF8 lines intact");

        var dbData = new AppData(Path.Combine(folder, "db")); var store = new Store(dbData); var options = new JobOptions(Method.LZX, false, "", 0, 1);
        var old = store.CreateJob([folder], options); var oldPath = Path.Combine(folder, "old.txt"); store.Enqueue(old, "", oldPath, false, options); store.Complete(store.Claim(old)!, new(Outcome.Success, oldPath)); store.Staged(old);
        store.Exec("UPDATE work SET completedUtc=$t WHERE job=$j", ("$t", DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeSeconds()), ("$j", old));
        var recent = store.CreateJob([folder], options); var recentPath = Path.Combine(folder, "recent.txt"); store.Enqueue(recent, "", recentPath, false, options); store.Complete(store.Claim(recent)!, new(Outcome.Success, recentPath));
        var pending = store.CreateJob([folder], options); store.Enqueue(pending, "", Path.Combine(folder, "pending.txt"), false, options);
        var running = store.CreateJob([folder], options); store.Enqueue(running, "", Path.Combine(folder, "running.txt"), false, options); _ = store.Claim(running);
        var failed = store.CreateJob([folder], options); store.Enqueue(failed, "", Path.Combine(folder, "failed.txt"), false, options); store.Retry(store.Claim(failed)! with { Retry = 7 }, "retry later");
        var cpRoot = "checkpoint"; store.Checkpoint(cpRoot, 77, 123); store.Seen(cpRoot, oldPath, new FileState(1,1,1,Method.LZX,0,1), Method.LZX);
        check(store.CleanupCompleted(DateTimeOffset.UtcNow) == 1, "SQLite deletes only old completed history");
        check(store.Totals(recent).Count == 1 && store.Totals(pending).Count == 1 && store.Totals(running).Count == 1 && store.Totals(failed).Count == 1 && store.Checkpoint(cpRoot) == (77ul,123) && store.Known(cpRoot, oldPath), "SQLite retains recent pending running retry checkpoint seen");
        var known = new FileState(8192,4096,123,Method.LZX,0,1); store.Seen(cpRoot, recentPath, known, Method.LZX);
        check(store.Unchanged(cpRoot, recentPath, known, Method.LZX) && !store.Unchanged(cpRoot, recentPath, known with { Method = Method.None }, Method.LZX) && !store.Unchanged(cpRoot, recentPath, known, Method.XPRESS4K), "same metadata changed actual or required compression reprocesses");
        var oldDb = new AppData(Path.Combine(folder,"legacy")); using (var legacy = new SqliteConnection("Data Source=" + Path.Combine(oldDb.Root,"state.db"))) { legacy.Open(); using var cmd=legacy.CreateCommand(); cmd.CommandText="CREATE TABLE seen(root TEXT NOT NULL,path TEXT NOT NULL COLLATE NOCASE,length INTEGER NOT NULL,ticks INTEGER NOT NULL,PRIMARY KEY(root,path)); INSERT INTO seen VALUES('r','p',1,1); CREATE TABLE work(id INTEGER PRIMARY KEY,job TEXT NOT NULL,root TEXT NOT NULL,path TEXT NOT NULL COLLATE NOCASE,directory INTEGER NOT NULL,state TEXT NOT NULL DEFAULT 'pending',due INTEGER NOT NULL DEFAULT 0,retry INTEGER NOT NULL DEFAULT 0,generation INTEGER NOT NULL DEFAULT 1,options TEXT NOT NULL,logical INTEGER NOT NULL DEFAULT 0,beforeBytes INTEGER NOT NULL DEFAULT 0,afterBytes INTEGER NOT NULL DEFAULT 0,detail TEXT NOT NULL DEFAULT '',UNIQUE(job,path));";cmd.ExecuteNonQuery(); }
        var migrated = new Store(oldDb);
        check(migrated.Known("r","p") && !migrated.Unchanged("r","p",new FileState(1,1,1,Method.LZX,0,1),Method.LZX), "legacy DB migrated without false unchanged result");

        using (var ui = new MainForm(new AppData(Path.Combine(folder, "uninstall-ui")), smoke: true))
        {
            ui.Show(); Application.DoEvents();
            var factory = typeof(MainForm).GetMethod("BuildUninstallDialog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            foreach (var size in new[] { 9f, 13f, 17f })
            {
                ui.SetFont(size);
                using var dialog = (Form)factory.Invoke(ui, null)!;
                dialog.Show(); Application.DoEvents();
                var buttons = dialog.Controls.OfType<FlowLayoutPanel>().SelectMany(x => x.Controls.OfType<Button>()).ToArray();
                check(buttons.Length == 3 && buttons[0].Text == "サービスのみ削除" && buttons[0].DialogResult == DialogResult.Yes && buttons[1].Text == "完全削除" && buttons[1].DialogResult == DialogResult.No && buttons[2].Text == "キャンセル" && buttons[2].DialogResult == DialogResult.Cancel, "uninstall three explicit choices font " + size);
                check(buttons.All(x => x.Width >= x.GetPreferredSize(Size.Empty).Width && x.Height >= x.GetPreferredSize(Size.Empty).Height), "uninstall buttons fit font " + size);
                dialog.Close();
            }
            ui.CloseForTest();
        }
        var cleanupRoot = Path.Combine(folder,"uninstall"); Directory.CreateDirectory(Path.Combine(cleanupRoot,"Logs")); File.WriteAllText(Path.Combine(cleanupRoot,"settings.json"),"settings"); File.WriteAllText(Path.Combine(cleanupRoot,"state.db"),"db"); File.WriteAllText(Path.Combine(cleanupRoot,"Logs","gui.log.5"),"log"); File.WriteAllText(Path.Combine(cleanupRoot,"keep.txt"),"user");
        var outside = Path.Combine(folder,"original-WACM.exe"); File.WriteAllText(outside,"original"); var archive=Path.Combine(folder,"archive.txt"); File.WriteAllText(archive,"compressed data");
        File.SetAttributes(Path.Combine(cleanupRoot,"state.db"), FileAttributes.ReadOnly);
        var partial=ServiceControl.RemoveOwnedData(cleanupRoot);
        check(!partial.Success && File.Exists(Path.Combine(cleanupRoot,"state.db")) && File.Exists(outside) && File.Exists(archive), "uninstall partial failure reported and user files retained");
        File.SetAttributes(Path.Combine(cleanupRoot,"state.db"), FileAttributes.Normal);
        var cleaned=ServiceControl.RemoveOwnedData(cleanupRoot);
        check(cleaned.Success && !File.Exists(Path.Combine(cleanupRoot,"state.db")) && !File.Exists(Path.Combine(cleanupRoot,"Logs","gui.log.5")) && File.Exists(Path.Combine(cleanupRoot,"keep.txt")) && File.Exists(outside) && File.Exists(archive), "full cleanup removes owned files only");
    }
    public static void CompressionState(string root, Action<bool,string> check)
    {
        var folder = Path.Combine(root, "method-state"); Directory.CreateDirectory(folder);
        var dbData = new AppData(Path.Combine(folder, "db")); var store = new Store(dbData);
        var options = new JobOptions(Method.NTFS, false, "", 0, 1); var cpRoot = "checkpoint";
        var modeFile = Path.Combine(folder, "same-metadata-method.txt");
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("same metadata compression state 日本語\r\n", 10000)));
        File.WriteAllBytes(modeFile, bytes); var expectedHash = System.Security.Cryptography.SHA256.HashData(bytes);
        var compressor = new CompressionEngine(new AppLog(dbData, "test-compression"));
        var modeOptions = options with { Method = Method.NTFS, MinimumBytes = 0 };
        check(compressor.Execute(modeFile, false, modeOptions, default).GetAwaiter().GetResult().Outcome == Outcome.Success, "monitor state fixture NTFS compression");
        var compressedState = Native.Inspect(modeFile); store.Seen(cpRoot, modeFile, compressedState, Method.NTFS);
        check(compressor.Execute(modeFile, false, modeOptions with { Method = Method.None }, default).GetAwaiter().GetResult().Outcome == Outcome.Success, "monitor state fixture decompression");
        File.SetLastWriteTimeUtc(modeFile, new DateTime(compressedState.WriteTicks, DateTimeKind.Utc));
        var changedState = Native.Inspect(modeFile);
        check(changedState.Length == compressedState.Length && changedState.WriteTicks == compressedState.WriteTicks && changedState.Method == Method.None && !store.Unchanged(cpRoot, modeFile, changedState, Method.NTFS), "same path length timestamp but wrong actual method detected");
        var restored = compressor.Execute(modeFile, false, modeOptions, default).GetAwaiter().GetResult();
        check(restored.Outcome == Outcome.Success && Native.Inspect(modeFile).Method == Method.NTFS && expectedHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modeFile))), "required method restored without content change");
        check(compressor.Execute(modeFile, false, modeOptions, default).GetAwaiter().GetResult().Outcome == Outcome.Already, "correct actual method avoids recompression");
    }
}
