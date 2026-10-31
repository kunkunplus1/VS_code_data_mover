using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace VSCodeDataMover
{
    public class Mover
    {
        public Log Log;
        public bool Deep;
        public Action<int, string> Progress;
        public List<string> Extra = new List<string>();

        private static readonly string[] CodeProcessNames = new string[] { "Code", "Code - Insiders", "VSCodium" };

        private static readonly string[] KeyFiles = new string[]
        {
            @"User\settings.json",
            @"User\globalStorage\storage.json",
            @"extensions\extensions.json",
            "argv.json",
            @"sharedStorage\state.vscdb"
        };

        public static List<string> RunningProcesses()
        {
            List<string> found = new List<string>();
            foreach (string name in CodeProcessNames)
            {
                try
                {
                    Process[] ps = Process.GetProcessesByName(name);
                    if (ps.Length > 0)
                    {
                        found.Add(name);
                        foreach (Process p in ps) p.Dispose();
                    }
                }
                catch { }
            }
            return found;
        }

        public static bool CloseRunning(int timeoutMs)
        {
            foreach (string name in CodeProcessNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.CloseMainWindow(); }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }
            DateTime end = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < end)
            {
                if (RunningProcesses().Count == 0) return true;
                Thread.Sleep(400);
            }
            return RunningProcesses().Count == 0;
        }

        public OpResult MigrateAll(List<Entry> entries, string targetRoot)
        {
            OpResult result = new OpResult();
            List<Entry> todo = new List<Entry>();
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Normal) todo.Add(e);
            }
            if (todo.Count == 0)
            {
                result.Summary = "没有需要迁移的项目";
                result.Add("没有处于“未迁移”状态的数据（可能已经迁移过了）。");
                return result;
            }

            CheckNoRunningCode();

            foreach (Entry e in todo)
            {
                if (e.SizeBytes == 0 && e.FileCount == 0) Locator.Measure(e);
            }

            string root = ValidateTargetRoot(targetRoot, todo);
            long totalBytes = 0;
            foreach (Entry e in todo) totalBytes += e.SizeBytes;
            CheckFreeSpace(root, totalBytes * 2);

            foreach (Entry e in todo)
            {
                string planned = Path.Combine(root, e.RelTarget);
                if (FsUtil.Exists(planned))
                {
                    if (Reparse.IsReparsePoint(planned))
                        throw new MoverException("目标位置已经是一个链接: " + planned);
                    if (!FsUtil.IsEmptyDir(planned))
                        throw new MoverException("目标目录已存在且不为空，请换一个目标文件夹或先清空: " + planned);
                }
            }

            if (Paths.SimRoot == null)
            {
                ToolData.TrySetRoot(Path.Combine(root, "VSCodeDataMover"));
                if (Log != null && !string.IsNullOrEmpty(Log.FilePath))
                    Log.Relocate(Path.Combine(ToolData.LogsDir, Path.GetFileName(Log.FilePath)));
            }

            int index = 0;
            foreach (Entry e in todo)
            {
                index++;
                ReportProgress(((index - 1) * 100) / todo.Count, "准备迁移: " + e.DisplayName);
                try
                {
                    MigrateOne(e, root, index, todo.Count);
                    result.Add("成功  " + e.DisplayName + "  ->  " + e.Target);
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Add("失败  " + e.DisplayName + ": " + ex.Message);
                }
            }
            result.Summary = result.Ok ? "迁移完成" : "部分项目失败";
            result.Lines.AddRange(Extra);
            return result;
        }

        public OpResult RestoreAll(List<Entry> entries)
        {
            OpResult result = new OpResult();
            List<Entry> todo = new List<Entry>();
            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing ||
                    e.State == EntryState.RestoreLeftover || e.State == EntryState.CopyLeftover)
                    todo.Add(e);
            }
            if (todo.Count == 0)
            {
                result.Summary = "没有需要还原的项目";
                result.Add("当前没有处于迁移状态的数据。");
                return result;
            }

            CheckNoRunningCode();

            int index = 0;
            foreach (Entry e in todo)
            {
                index++;
                try
                {
                    RestoreOne(e, index, todo.Count);
                    result.Add("成功  已还原 " + e.DisplayName);
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Add("失败  " + e.DisplayName + ": " + ex.Message);
                }
            }
            result.Summary = result.Ok ? "还原完成" : "部分项目失败";
            result.Lines.AddRange(Extra);
            return result;
        }

        public OpResult Recover(List<Entry> entries)
        {
            OpResult result = new OpResult();
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            int fixedCount = 0;

            foreach (Entry e in entries)
            {
                if (e.State == EntryState.InterruptedRename && e.Backup != null)
                {
                    string target = null;
                    if (state.ContainsKey(e.Key) && state[e.Key].ContainsKey("Target")) target = state[e.Key]["Target"];
                    if (!string.IsNullOrEmpty(target) && FsUtil.IsDir(target))
                    {
                        string output;
                        if (FsUtil.CreateJunction(e.Source, target, out output))
                        {
                            StateStore.Set(e.Key, target, e.Backup, "migrated");
                            result.Add("已补建联接: " + e.Source);
                            fixedCount++;
                        }
                        else
                        {
                            result.Add("补建联接失败: " + e.Source + " " + output);
                        }
                    }
                    else
                    {
                        try
                        {
                            MoveDirRobust(e.Backup, e.Source);
                            StateStore.Remove(e.Key);
                            result.Add("已回退中断的迁移: " + e.Source);
                            fixedCount++;
                        }
                        catch (Exception ex)
                        {
                            result.Add("回退失败: " + e.Source + " " + ex.Message);
                        }
                    }
                }
                else if (e.State == EntryState.CopyLeftover && !string.IsNullOrEmpty(e.Target))
                {
                    try
                    {
                        FsUtil.DeleteTree(e.Target);
                        StateStore.Remove(e.Key);
                        result.Add("已清理未完成的副本: " + e.Target);
                        fixedCount++;
                    }
                    catch (Exception ex)
                    {
                        result.Add("清理未完成副本失败: " + e.Target + " " + ex.Message);
                    }
                }
                else if (e.State == EntryState.RestoreLeftover && e.Backup != null)
                {
                    try
                    {
                        FsUtil.DeleteTree(e.Backup);
                        StateStore.Remove(e.Key);
                        result.Add("已清理残留备份: " + e.Backup);
                        fixedCount++;
                    }
                    catch (Exception ex)
                    {
                        result.Add("清理残留备份失败: " + e.Backup + " " + ex.Message);
                    }
                }
            }

            result.Summary = fixedCount > 0 ? ("已处理 " + fixedCount + " 项") : "没有需要修复的项目";
            return result;
        }

        public OpResult RepairData(List<Entry> entries)
        {
            OpResult result = Recover(entries);

            foreach (Entry e in entries)
            {
                if (e.State != EntryState.BrokenTargetMissing) continue;

                string fallback = e.Backup != null ? e.Backup : Locator.FindAnyBackup(e);
                if (fallback == null)
                {
                    result.Ok = false;
                    result.Add("无法恢复 " + e.DisplayName + "：目标丢失且没有备份，请用“历史恢复”。");
                    continue;
                }

                try
                {
                    if (Reparse.IsReparsePoint(e.Source)) FsUtil.DeleteJunction(e.Source);
                    MoveDirRobust(fallback, e.Source);
                    StateStore.Remove(e.Key);
                    e.State = EntryState.Normal;
                    e.StateText = "未迁移";
                    result.Add("已从备份恢复 " + e.DisplayName + "（迁移后的新改动可能不在备份中）。");
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Add("恢复失败 " + e.DisplayName + ": " + ex.Message);
                }
            }

            result.Summary = result.Ok ? "数据修复完成" : "部分数据修复失败";
            return result;
        }

        public OpResult RestoreHistory(Entry e, string candidatePath)
        {
            OpResult result = new OpResult();
            string kind = HistoryScanner.KindOf(e);
            if (kind == "") throw new MoverException("未知的数据类型: " + e.Key);
            if (e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing)
                throw new MoverException("当前数据处于已迁移状态，请先点“一键还原”，再使用历史恢复。");

            string candidate = HistoryScanner.NormalizeForKind(kind, candidatePath);
            if (candidate == null || !HistoryScanner.Matches(kind, candidate))
                throw new MoverException("所选目录不像“" + HistoryScanner.KindLabel(kind) + "”目录: " + candidatePath);
            if (string.Equals(FsUtil.Norm(candidate), FsUtil.Norm(e.Source), StringComparison.OrdinalIgnoreCase) ||
                FsUtil.IsInside(candidate, e.Source))
                throw new MoverException("不能从当前数据目录自身恢复。");

            CheckNoRunningCode();

            int count;
            long size = FsUtil.DirSize(candidate, out count);
            CheckSourceSpace(e.Source, size);

            string backup = null;
            string temp = null;
            bool renamed = false;
            if (FsUtil.Exists(e.Source))
            {
                if (Reparse.IsReparsePoint(e.Source))
                    throw new MoverException("源路径是目录联接，请先点“一键还原”。");
                backup = MakeBackupName(e.Source, e.Key);
                temp = e.Source + ".vscode-mover-tmp-" + FsUtil.Timestamp();
                FsUtil.MoveDir(e.Source, temp);
                renamed = true;
            }

            try
            {
                FsUtil.CreateDir(e.Source);
                CopyTree(candidate, e.Source, e.Key + "-history");
                VerifyCopy(candidate, e.Source, e);
            }
            catch
            {
                TryDelete(e.Source);
                if (renamed && !FsUtil.Exists(e.Source) && FsUtil.Exists(temp)) FsUtil.MoveDir(temp, e.Source);
                throw;
            }

            if (renamed) backup = MoveTempToBackups(temp, backup, e);

            StateStore.Set(e.Key, "", backup, "history-restored");
            e.State = EntryState.Normal;
            e.StateText = "未迁移";
            e.Target = null;
            e.Backup = backup;

            result.Add("已从历史位置恢复: " + candidate);
            if (backup != null) result.Add("恢复前的数据已备份到: " + backup);
            result.Add("确认无误后，可以点“一键迁移”把它安全迁到其它盘。");
            result.Summary = "历史恢复完成";
            return result;
        }

        private void MigrateOne(Entry e, string root, int index, int total)
        {
            string target = Path.Combine(root, e.RelTarget);
            if (FsUtil.Exists(target))
            {
                if (Reparse.IsReparsePoint(target)) throw new MoverException("目标位置是一个链接: " + target);
                if (!FsUtil.IsEmptyDir(target))
                {
                    Dictionary<string, Dictionary<string, string>> st = StateStore.Load();
                    bool leftover = st.ContainsKey(e.Key) && st[e.Key].ContainsKey("Phase") && st[e.Key]["Phase"] == "copying";
                    if (leftover)
                    {
                        TryDelete(target);
                        StateStore.Remove(e.Key);
                    }
                    else
                    {
                        throw new MoverException("目标目录已存在且不为空: " + target);
                    }
                }
            }
            FsUtil.CreateDir(target);
            StateStore.Set(e.Key, target, "", "copying");

            try
            {
                Step(index, total, 8, "复制 " + e.DisplayName + " ...");
                CopyTree(e.Source, target, e.Key);
                Step(index, total, 55, "校验 " + e.DisplayName + " ...");
                VerifyCopy(e.Source, target, e);
            }
            catch
            {
                StateStore.Remove(e.Key);
                TryDelete(target);
                throw;
            }

            string backup = MakeBackupName(e.Source, e.Key);
            string temp = e.Source + ".vscode-mover-tmp-" + FsUtil.Timestamp();
            StateStore.Set(e.Key, target, backup, "migrating");

            try
            {
                FsUtil.MoveDir(e.Source, temp);
            }
            catch (Exception ex)
            {
                StateStore.Remove(e.Key);
                TryDelete(target);
                throw new MoverException("重命名原目录失败（可能被占用）: " + ex.Message);
            }

            try
            {
                Step(index, total, 80, "创建目录联接 " + e.DisplayName + " ...");
                string output;
                if (!FsUtil.CreateJunction(e.Source, target, out output))
                    throw new MoverException("创建目录联接失败: " + output);
                VerifyJunction(e.Source, target);
            }
            catch
            {
                if (Reparse.IsReparsePoint(e.Source)) FsUtil.DeleteJunction(e.Source);
                if (!FsUtil.Exists(e.Source) && FsUtil.Exists(temp)) FsUtil.MoveDir(temp, e.Source);
                StateStore.Remove(e.Key);
                TryDelete(target);
                throw;
            }

            Step(index, total, 92, "保存迁移前备份 " + e.DisplayName + " ...");
            backup = MoveTempToBackups(temp, backup, e);

            StateStore.Set(e.Key, target, backup, "migrated");
            e.State = EntryState.Migrated;
            e.StateText = "已迁移";
            e.Target = target;
            e.Backup = backup;
            DeleteOldBackups(e.Key, backup);
            Step(index, total, 100, "完成 " + e.DisplayName);
        }

        private void RestoreOne(Entry e, int index, int total)
        {
            if (e.State == EntryState.CopyLeftover)
            {
                TryDelete(e.Target);
                StateStore.Remove(e.Key);
                e.State = EntryState.Normal;
                e.StateText = "未迁移";
                e.Target = null;
                AddLine("已清理上次未完成的副本（源数据未受影响）。");
                return;
            }

            if (e.State == EntryState.RestoreLeftover)
            {
                if (e.Backup != null) FsUtil.DeleteTree(e.Backup);
                StateStore.Remove(e.Key);
                e.Backup = null;
                e.State = EntryState.Normal;
                e.StateText = "未迁移";
                return;
            }

            string target = e.Target;
            if (string.IsNullOrEmpty(target) && StateStore.Load().ContainsKey(e.Key))
                target = StateStore.Load()[e.Key]["Target"];

            bool targetOk = !string.IsNullOrEmpty(target) && FsUtil.IsDir(target) && !Reparse.IsReparsePoint(target);

            if (!targetOk)
            {
                string fallback = e.Backup != null ? e.Backup : Locator.FindAnyBackup(e);
                if (fallback == null)
                    throw new MoverException("目标路径不可用，也没有迁移前的备份，无法自动还原: " + target);

                if (Reparse.IsReparsePoint(e.Source)) FsUtil.DeleteJunction(e.Source);
                MoveDirRobust(fallback, e.Source);
                StateStore.Remove(e.Key);
                e.State = EntryState.Normal;
                e.StateText = "未迁移";
                e.Target = null;
                e.Backup = null;
                AddLine("注意: 目标不可用，已用迁移前的备份恢复（迁移后产生的新改动不在备份中）。");
                return;
            }

            int count;
            long size = FsUtil.DirSize(target, out count);
            CheckSourceSpace(e.Source, size);

            if (Reparse.IsReparsePoint(e.Source))
            {
                if (!FsUtil.DeleteJunction(e.Source))
                    throw new MoverException("删除目录联接失败（可能被占用）: " + e.Source);
            }

            try
            {
                Step(index, total, 10, "复制回 C 盘 " + e.DisplayName + " ...");
                CopyTree(target, e.Source, e.Key + "-restore");
                Step(index, total, 60, "校验 " + e.DisplayName + " ...");
                VerifyCopy(target, e.Source, e);
                Step(index, total, 85, "清理 D 盘副本 " + e.DisplayName + " ...");

                if (e.Backup != null && FsUtil.Exists(e.Backup))
                {
                    string backupParent = Path.GetDirectoryName(e.Backup);
                    FsUtil.DeleteTree(e.Backup);
                    TryRemoveEmptyDir(backupParent);
                }
                FsUtil.DeleteTree(target);
                StateStore.Remove(e.Key);
            }
            catch
            {
                TryDelete(e.Source);
                if (!FsUtil.Exists(e.Source) && FsUtil.IsDir(target))
                {
                    string output;
                    FsUtil.CreateJunction(e.Source, target, out output);
                }
                throw;
            }

            e.State = EntryState.Normal;
            e.StateText = "未迁移";
            e.Target = null;
            e.Backup = null;
            Step(index, total, 100, "完成 " + e.DisplayName);
        }

        private void CopyTree(string source, string destination, string key)
        {
            string logDir = Path.Combine(Paths.StateDir, "logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "robocopy-" + key + "-" + FsUtil.Timestamp() + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".log");

            string robocopy = Path.Combine(Environment.SystemDirectory, "Robocopy.exe");
            if (!File.Exists(robocopy)) robocopy = "robocopy.exe";

            ProcessStartInfo psi = new ProcessStartInfo(robocopy);
            psi.Arguments = "\"" + source + "\" \"" + destination + "\" /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /MT:16 /XJ /NFL /NDL /NJH /NJS /NP /LOG:\"" + logFile + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode >= 8)
                    throw new MoverException("复制失败，Robocopy 退出码 " + p.ExitCode + "，日志: " + logFile);
            }
        }

        private void VerifyCopy(string source, string destination, Entry e)
        {
            Dictionary<string, long> a = FsUtil.Snapshot(source);
            Dictionary<string, long> b = FsUtil.Snapshot(destination);

            List<string> problems = new List<string>();
            foreach (KeyValuePair<string, long> kv in a)
            {
                if (!b.ContainsKey(kv.Key)) problems.Add("缺少 " + kv.Key);
                else if (b[kv.Key] != kv.Value) problems.Add("大小不一致 " + kv.Key);
                if (problems.Count >= 8) break;
            }
            if (problems.Count < 8)
            {
                foreach (KeyValuePair<string, long> kv in b)
                {
                    if (!a.ContainsKey(kv.Key)) problems.Add("多出 " + kv.Key);
                    if (problems.Count >= 8) break;
                }
            }
            if (problems.Count > 0)
                throw new MoverException("复制校验失败: " + string.Join("；", problems.ToArray()));

            List<string> check = new List<string>();
            if (Deep)
            {
                check.AddRange(a.Keys);
            }
            else
            {
                foreach (string key in KeyFiles)
                {
                    if (a.ContainsKey(key)) check.Add(key);
                }
            }

            foreach (string rel in check)
            {
                string left = Path.Combine(source, rel);
                string right = Path.Combine(destination, rel);
                string h1 = FsUtil.Hash(left);
                string h2 = FsUtil.Hash(right);
                if (!string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase))
                    throw new MoverException("文件内容校验失败: " + rel);
            }

            if (e != null)
            {
                e.SizeBytes = 0;
                foreach (long v in b.Values) e.SizeBytes += v;
                e.FileCount = b.Count;
            }
        }

        private static void VerifyJunction(string link, string target)
        {
            if (!Reparse.IsReparsePoint(link))
                throw new MoverException("联接未生效: " + link);

            string actual;
            bool isJunction;
            if (!Reparse.TryGetTarget(link, out actual, out isJunction) || !isJunction)
                throw new MoverException("无法读取联接目标: " + link);

            if (!string.Equals(FsUtil.Norm(actual), FsUtil.Norm(target), StringComparison.OrdinalIgnoreCase))
                throw new MoverException("联接指向了错误的位置: " + actual);

            if (!FsUtil.IsDir(link))
                throw new MoverException("通过联接无法访问目录: " + link);
        }

        private string ValidateTargetRoot(string targetRoot, List<Entry> entries)
        {
            if (targetRoot == null || targetRoot.Trim().Length == 0)
                throw new MoverException("请先选择迁移目标文件夹。");

            string root = targetRoot.Trim().Trim('"').Trim();
            if (!Path.IsPathRooted(root))
                throw new MoverException("目标必须是绝对路径: " + root);

            root = FsUtil.Norm(root);

            if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
                throw new MoverException("不能直接选择磁盘根目录，请在其下新建一个文件夹。");

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string[] forbidden = new string[] { windows, programFiles, programFilesX86, system };
            foreach (string f in forbidden)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (string.Equals(root, FsUtil.Norm(f), StringComparison.OrdinalIgnoreCase) || FsUtil.IsInside(root, f))
                    throw new MoverException("不能选择系统目录作为迁移目标: " + root);
            }

            foreach (Entry e in entries)
            {
                if (string.Equals(root, FsUtil.Norm(e.Source), StringComparison.OrdinalIgnoreCase))
                    throw new MoverException("目标不能和源目录相同: " + root);
                if (FsUtil.IsInside(root, e.Source))
                    throw new MoverException("目标不能放在源目录内部: " + root);
                if (FsUtil.IsInside(e.Source, root))
                    throw new MoverException("源目录不能放在目标目录内部: " + e.Source);
            }

            if (FsUtil.Exists(root) && Reparse.IsReparsePoint(root))
                throw new MoverException("目标路径本身是一个链接，请换一个普通文件夹。");

            try
            {
                FsUtil.CreateDir(root);
                string probe = Path.Combine(root, ".vsmover-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new MoverException("目标目录不可写: " + ex.Message);
            }

            return root;
        }

        private void CheckFreeSpace(string targetRoot, long neededBytes)
        {
            string rootPath = Path.GetPathRoot(targetRoot);
            if (string.IsNullOrEmpty(rootPath) || rootPath.StartsWith("\\\\")) return;
            try
            {
                DriveInfo di = new DriveInfo(rootPath);
                long required = (long)(neededBytes * 1.05) + 100L * 1024 * 1024;
                if (di.AvailableFreeSpace < required)
                    throw new MoverException("目标磁盘剩余空间不足，需要约 " + (required / 1024 / 1024) + " MB，当前可用 " + (di.AvailableFreeSpace / 1024 / 1024) + " MB。");
            }
            catch (MoverException) { throw; }
            catch { }
        }

        private void CheckSourceSpace(string sourcePath, long neededBytes)
        {
            string rootPath = Path.GetPathRoot(sourcePath);
            if (string.IsNullOrEmpty(rootPath) || rootPath.StartsWith("\\\\")) return;
            try
            {
                DriveInfo di = new DriveInfo(rootPath);
                long required = (long)(neededBytes * 1.05) + 100L * 1024 * 1024;
                if (di.AvailableFreeSpace < required)
                    throw new MoverException("源磁盘剩余空间不足，还原需要约 " + (required / 1024 / 1024) + " MB。");
            }
            catch (MoverException) { throw; }
            catch { }
        }

        private static void CheckNoRunningCode()
        {
            List<string> procs = RunningProcesses();
            if (procs.Count > 0)
                throw new MoverException("检测到 VS Code 正在运行（" + string.Join("、", procs.ToArray()) + "），请先完全退出后再操作。");
        }

        private string MoveTempToBackups(string temp, string backup, Entry e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                if (string.Equals(Path.GetPathRoot(temp), Path.GetPathRoot(backup), StringComparison.OrdinalIgnoreCase))
                {
                    if (FsUtil.Exists(backup)) FsUtil.DeleteTree(backup);
                    FsUtil.MoveDir(temp, backup);
                }
                else
                {
                    CopyTree(temp, backup, e.Key + "-backup");
                    VerifyCopy(temp, backup, e);
                    FsUtil.DeleteTree(temp);
                }
                return backup;
            }
            catch (Exception ex)
            {
                string legacy = Path.Combine(Path.GetDirectoryName(temp),
                    Path.GetFileName(e.Source) + ".vscode-mover-backup-" + FsUtil.Timestamp());
                try
                {
                    if (FsUtil.Exists(temp)) FsUtil.MoveDir(temp, legacy);
                    AddLine("提示: 备份未能移动到目标目录，已保留在 " + legacy + "（" + ex.Message + "）");
                    return legacy;
                }
                catch
                {
                    AddLine("提示: 备份仍保留在临时目录 " + temp + "（" + ex.Message + "）");
                    return temp;
                }
            }
        }

        private void MoveDirRobust(string src, string dst)
        {
            if (!FsUtil.Exists(src)) return;
            if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase))
            {
                FsUtil.MoveDir(src, dst);
                return;
            }

            try
            {
                CopyTree(src, dst, Path.GetFileName(src));
                VerifyCopy(src, dst, null);
                FsUtil.DeleteTree(src);
            }
            catch
            {
                TryDelete(dst);
                throw;
            }
        }

        private static string MakeBackupName(string source, string key)
        {
            string dir = Path.Combine(ToolData.BackupsDir, key);
            Directory.CreateDirectory(dir);
            string leaf = Path.GetFileName(source);
            string baseName = Path.Combine(dir, leaf + ".vscode-mover-backup-" + FsUtil.Timestamp());
            string name = baseName;
            int i = 2;
            while (FsUtil.Exists(name))
            {
                name = baseName + "-" + i;
                i++;
            }
            return name;
        }

        private static void DeleteOldBackups(string key, string keep)
        {
            try
            {
                string dir = Path.Combine(ToolData.BackupsDir, key);
                if (!Directory.Exists(dir)) return;
                foreach (string d in Directory.GetDirectories(dir, "*.vscode-mover-backup-*"))
                {
                    if (string.Equals(FsUtil.Norm(d), FsUtil.Norm(keep), StringComparison.OrdinalIgnoreCase)) continue;
                    try { FsUtil.DeleteTree(d); }
                    catch { }
                }
            }
            catch { }
        }

        private static void TryRemoveEmptyDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                    Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (FsUtil.Exists(path) && !Reparse.IsReparsePoint(path)) FsUtil.DeleteTree(path);
            }
            catch { }
        }

        private void Step(int index, int total, int phasePercent, string message)
        {
            int overall = ((index - 1) * 100 + phasePercent) / Math.Max(total, 1);
            ReportProgress(overall, message);
        }

        private void ReportProgress(int percent, string message)
        {
            if (Log != null) Log.Write(message);
            if (Progress != null) Progress(percent, message);
        }

        private void AddLine(string message)
        {
            Extra.Add(message);
            if (Log != null) Log.Write(message);
        }
    }
}
