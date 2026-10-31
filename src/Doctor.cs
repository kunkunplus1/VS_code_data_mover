using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace VSCodeDataMover
{
    public class DoctorFinding
    {
        public string Severity;   // 错误 / 警告 / 正常 / 信息
        public string Title;
        public string Detail;
        public string Action;     // none / shortcut / registry / path / data
        public string Arg1;
        public string Arg2;
    }

    public class DoctorReport
    {
        public List<DoctorFinding> Findings = new List<DoctorFinding>();
        public List<HistoryCandidate> Candidates = new List<HistoryCandidate>();
        public string InstallPath;
        public string CodeExe;
        public string ShortcutTarget;
        public int Problems;
        public int AutoFixable;
        public int HistoryPending;
    }

    /// <summary>
    /// 检查“手动迁移后坏掉”的常见原因：程序找不到、快捷方式失效、PATH 失效、数据联接断裂。
    /// 能修的直接给修复动作，不能修的明确告诉用户原因。
    /// </summary>
    public static class Doctor
    {
        public static DoctorReport Diagnose(List<Entry> entries)
        {
            return Diagnose(entries, false);
        }

        public static DoctorReport Diagnose(List<Entry> entries, bool scanHistory)
        {
            DoctorReport report = new DoctorReport();

            if (Paths.SimRoot == null)
            {
            List<string> shortcutTargets;
            List<string> shortcuts = FindShortcuts(out shortcutTargets);
            report.ShortcutTarget = FirstExisting(shortcutTargets);

            string registryInstall = ReadRegistryInstall();
            report.InstallPath = FirstValidInstall(registryInstall, shortcutTargets);

            if (report.InstallPath == null)
            {
                string found = SearchForInstall(shortcutTargets);
                if (found != null) report.InstallPath = found;
            }

            if (report.InstallPath != null)
                report.CodeExe = Path.Combine(report.InstallPath, "Code.exe");

            // 1. 程序本体
            if (report.InstallPath != null && File.Exists(report.CodeExe))
            {
                Add(report, "正常", "程序本体存在", report.CodeExe, "none", null, null);
            }
            else
            {
                string detail = "没有找到可用的 Code.exe。数据可以修，但程序本体无法凭空恢复。";
                Add(report, "错误", "找不到 VS Code 程序本体", detail, "none", null, null);
                Add(report, "信息", "需要重新安装 VS Code",
                    "请从 https://code.visualstudio.com/download 重新安装（装到任意位置都行），装完再运行本工具修数据。",
                    "none", null, null);
            }

            // 2. 快捷方式
            int brokenShortcuts = 0;
            foreach (string lnk in shortcuts)
            {
                string target = GetShortcutTarget(lnk);
                if (string.IsNullOrEmpty(target)) continue;
                if (target.IndexOf("Code.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (File.Exists(target)) continue;

                brokenShortcuts++;
                string fixTo = report.CodeExe != null && File.Exists(report.CodeExe) ? report.CodeExe : null;
                Add(report, "错误", "快捷方式指向的文件不存在",
                    lnk + Environment.NewLine + "  -> " + target,
                    fixTo != null ? "shortcut" : "none", lnk, fixTo);
            }
            if (brokenShortcuts == 0)
                Add(report, "正常", "快捷方式检查通过", "桌面 / 开始菜单 / 任务栏中没有发现失效的 VS Code 快捷方式。", "none", null, null);

            // 3. 注册表安装位置
            if (!string.IsNullOrEmpty(registryInstall))
            {
                bool ok = File.Exists(Path.Combine(registryInstall, "Code.exe"));
                if (!ok)
                {
                    string fixTo = report.InstallPath;
                    Add(report, "错误", "注册表里的安装位置无效",
                        "注册表记录: " + registryInstall + Environment.NewLine + "  该目录下没有 Code.exe。",
                        fixTo != null ? "registry" : "none", null, fixTo);
                }
                else
                {
                    Add(report, "正常", "注册表安装位置正常", registryInstall, "none", null, null);
                }
            }

            // 4. 用户 PATH 里的 code 命令
            List<string> badPathEntries = FindBadPathEntries();
            foreach (string bad in badPathEntries)
            {
                string newBin = report.InstallPath != null ? Path.Combine(report.InstallPath, "bin") : null;
                Add(report, "警告", "PATH 里的 VS Code 目录不存在",
                    bad,
                    newBin != null && Directory.Exists(newBin) ? "path" : "none", bad, newBin);
            }
            if (badPathEntries.Count == 0)
                Add(report, "正常", "PATH 检查通过", "没有发现失效的 VS Code bin 目录。", "none", null, null);

            }

            // 5. 数据目录
            Dictionary<string, bool> present = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in entries)
            {
                if (e.State != EntryState.Missing) present[e.Product] = true;
            }

            List<Entry> brokenEntries = new List<Entry>();

            foreach (Entry e in entries)
            {
                if (e.State == EntryState.Missing &&
                    !string.Equals(e.Product, "VS Code", StringComparison.OrdinalIgnoreCase) &&
                    !present.ContainsKey(e.Product))
                    continue;

                if (e.State == EntryState.Migrated)
                {
                    Add(report, "正常", "数据已迁移: " + e.DisplayName, e.Target, "none", null, null);
                    continue;
                }
                if (e.State == EntryState.Normal)
                {
                    if (e.Key.EndsWith("-userdata", StringComparison.OrdinalIgnoreCase) && IsFreshUserData(e.Source))
                    {
                        Add(report, "信息", "当前用户数据像是新建的空配置",
                            e.Source + Environment.NewLine + "  没有 settings.json / workspaceStorage，可能已被清空或替换。",
                            "pending-fresh", e.Key, null);
                        brokenEntries.Add(e);
                    }
                    else
                    {
                        Add(report, "正常", "数据正常: " + e.DisplayName, e.Source, "none", null, null);
                    }
                    continue;
                }

                string severity;
                string title;
                string detail;
                string action;
                switch (e.State)
                {
                    case EntryState.BrokenTargetMissing:
                        severity = "错误";
                        title = "数据联接指向的目标不存在: " + e.DisplayName;
                        detail = e.Source + Environment.NewLine + "  目标: " + (e.Target == null ? "未知" : e.Target) +
                                 (e.Backup != null ? Environment.NewLine + "  可从备份恢复: " + e.Backup : "");
                        action = e.Backup != null ? "data" : "pending-history";
                        break;
                    case EntryState.InterruptedRename:
                        severity = "错误";
                        title = "上次迁移中断: " + e.DisplayName;
                        detail = e.Source + Environment.NewLine + "  源目录缺失，备份: " + e.Backup;
                        action = "data";
                        break;
                    case EntryState.CopyLeftover:
                        severity = "警告";
                        title = "上次复制未完成: " + e.DisplayName;
                        detail = "目标残留半副本: " + e.Target + Environment.NewLine + "  源数据未受影响。";
                        action = "data";
                        break;
                    case EntryState.RestoreLeftover:
                        severity = "警告";
                        title = "残留备份: " + e.DisplayName;
                        detail = e.Backup;
                        action = "data";
                        break;
                    default:
                        severity = "警告";
                        title = "未检测到数据: " + e.DisplayName;
                        detail = e.Source;
                        action = "pending-history";
                        break;
                }

                Add(report, severity, title, detail, action, e.Key, null);
                brokenEntries.Add(e);
            }

            if (scanHistory && brokenEntries.Count > 0)
            {
                report.Candidates = HistoryScanner.ScanAll(brokenEntries);
            }

            foreach (DoctorFinding f in report.Findings)
            {
                if (!string.Equals(f.Action, "pending-history", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(f.Action, "pending-fresh", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool fresh = string.Equals(f.Action, "pending-fresh", StringComparison.OrdinalIgnoreCase);
                bool has = false;
                foreach (HistoryCandidate c in report.Candidates)
                {
                    if (string.Equals(c.EntryKey, f.Arg1, StringComparison.OrdinalIgnoreCase)) { has = true; break; }
                }

                f.Action = has ? "history" : "none";
                if (has)
                {
                    if (fresh) f.Severity = "警告";
                    f.Detail = f.Detail + Environment.NewLine + "  找到历史候选目录，可在下方勾选。";
                }
            }

            foreach (DoctorFinding f in report.Findings)
            {
                if (f.Severity == "错误" || f.Severity == "警告") report.Problems++;
                if (string.Equals(f.Action, "shortcut", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Action, "registry", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Action, "path", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.Action, "data", StringComparison.OrdinalIgnoreCase))
                    report.AutoFixable++;
                if (string.Equals(f.Action, "history", StringComparison.OrdinalIgnoreCase)) report.HistoryPending++;
            }
            return report;
        }

        public static OpResult Fix(DoctorReport report, List<Entry> entries, Dictionary<string, string> chosen, Log log)
        {
            OpResult result = new OpResult();
            Mover mover = new Mover();
            mover.Log = log;

            foreach (DoctorFinding f in report.Findings)
            {
                if (string.Equals(f.Action, "shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    if (SetShortcutTarget(f.Arg1, f.Arg2))
                    {
                        result.Add("已修复快捷方式: " + f.Arg1 + " -> " + f.Arg2);
                        if (log != null) log.Write("fix shortcut " + f.Arg1 + " -> " + f.Arg2);
                    }
                    else
                    {
                        result.Ok = false;
                        result.Add("快捷方式修复失败: " + f.Arg1);
                    }
                }
                else if (string.Equals(f.Action, "registry", StringComparison.OrdinalIgnoreCase))
                {
                    if (FixRegistryInstall(f.Arg2))
                    {
                        result.Add("已修复注册表安装位置: " + f.Arg2);
                        if (log != null) log.Write("fix registry install -> " + f.Arg2);
                    }
                    else
                    {
                        result.Ok = false;
                        result.Add("注册表修复失败（可能需要管理员权限）。");
                    }
                }
                else if (string.Equals(f.Action, "path", StringComparison.OrdinalIgnoreCase))
                {
                    if (FixUserPath(f.Arg1, f.Arg2))
                    {
                        result.Add("已修复 PATH: " + f.Arg1 + " -> " + f.Arg2);
                        if (log != null) log.Write("fix path " + f.Arg1 + " -> " + f.Arg2);
                    }
                    else
                    {
                        result.Ok = false;
                        result.Add("PATH 修复失败。");
                    }
                }
            }

            bool needData = false;
            foreach (DoctorFinding f in report.Findings)
            {
                if (string.Equals(f.Action, "data", StringComparison.OrdinalIgnoreCase)) { needData = true; break; }
            }

            if (needData)
            {
                OpResult data = mover.RepairData(entries);
                foreach (string line in data.Lines) result.Add(line);
                if (!data.Ok) result.Ok = false;
            }

            if (chosen != null && chosen.Count > 0)
            {
                foreach (KeyValuePair<string, string> kv in chosen)
                {
                    Entry entry = null;
                    foreach (Entry e in entries)
                    {
                        if (string.Equals(e.Key, kv.Key, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
                    }
                    if (entry == null) continue;

                    Locator.Analyze(entry, StateStore.Load());
                    if (entry.State == EntryState.Normal || entry.State == EntryState.Migrated) continue;

                    try
                    {
                        OpResult one = mover.RestoreHistory(entry, kv.Value);
                        foreach (string line in one.Lines) result.Add(line);
                        if (!one.Ok) result.Ok = false;
                    }
                    catch (Exception ex)
                    {
                        result.Ok = false;
                        result.Add("历史恢复失败 " + entry.DisplayName + ": " + ex.Message);
                    }
                }
            }

            if (result.Lines.Count == 0) result.Add("没有需要修复的项目。");
            result.Summary = result.Ok ? "修复完成" : "部分项目修复失败";
            return result;
        }

        // ------------------------------------------------------------------
        private static bool IsFreshUserData(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                if (File.Exists(Path.Combine(dir, @"User\settings.json"))) return false;
                if (Directory.Exists(Path.Combine(dir, @"User\workspaceStorage"))) return false;
                if (File.Exists(Path.Combine(dir, @"User\globalStorage\storage.json"))) return false;
                return true;
            }
            catch { return false; }
        }

        private static void Add(DoctorReport r, string severity, string title, string detail, string action, string arg1, string arg2)
        {
            DoctorFinding f = new DoctorFinding();
            f.Severity = severity;
            f.Title = title;
            f.Detail = detail;
            f.Action = action;
            f.Arg1 = arg1;
            f.Arg2 = arg2;
            r.Findings.Add(f);
        }

        private static List<string> FindShortcuts(out List<string> targets)
        {
            targets = new List<string>();
            List<string> result = new List<string>();
            List<string> roots = new List<string>();

            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs)); } catch { }
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)); } catch { }
            roots.Add(Path.Combine(Paths.AppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"));
            roots.Add(Path.Combine(Paths.AppData, @"Microsoft\Windows\Start Menu\Programs"));

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (string f in files)
                {
                    if (!seen.Add(f)) continue;
                    result.Add(f);

                    string t = GetShortcutTarget(f);
                    if (!string.IsNullOrEmpty(t)) targets.Add(t);
                }
            }
            return result;
        }

        private static string FirstExisting(List<string> paths)
        {
            foreach (string p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p; }
                catch { }
            }
            return null;
        }

        private static string FirstValidInstall(string registryInstall, List<string> shortcutTargets)
        {
            if (!string.IsNullOrEmpty(registryInstall) && LooksLikeInstall(registryInstall)) return registryInstall;

            foreach (string t in shortcutTargets)
            {
                try
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    string dir = Path.GetDirectoryName(t);
                    if (LooksLikeInstall(dir)) return dir;
                }
                catch { }
            }

            string local = Path.Combine(Paths.LocalAppData, @"Programs\Microsoft VS Code");
            if (LooksLikeInstall(local)) return local;

            return null;
        }

        private static bool LooksLikeInstall(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                if (!File.Exists(Path.Combine(dir, "Code.exe"))) return false;
                if (Directory.Exists(Path.Combine(dir, "resources"))) return true;
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (File.Exists(Path.Combine(sub, @"resources\app\package.json"))) return true;
                }
            }
            catch { }
            return false;
        }

        private static string SearchForInstall(List<string> shortcutTargets)
        {
            Stopwatch watch = Stopwatch.StartNew();

            List<string> roots = new List<string>();
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
            roots.Add(Paths.LocalAppData);
            roots.Add(Paths.UserProfile);

            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                    roots.Add(di.RootDirectory.FullName);
                }
            }
            catch { }

            foreach (string root in roots)
            {
                if (watch.ElapsedMilliseconds > 12000) break;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                string[] level1;
                try { level1 = Directory.GetDirectories(root); }
                catch { continue; }

                foreach (string d1 in level1)
                {
                    if (watch.ElapsedMilliseconds > 12000) break;
                    string name = Path.GetFileName(d1);
                    if (!NameLooksLikeCode(name)) continue;

                    if (LooksLikeInstall(d1)) return d1;

                    string[] level2;
                    try { level2 = Directory.GetDirectories(d1); }
                    catch { continue; }
                    foreach (string d2 in level2)
                    {
                        if (watch.ElapsedMilliseconds > 12000) break;
                        if (LooksLikeInstall(d2)) return d2;
                    }
                }
            }
            return null;
        }

        private static bool NameLooksLikeCode(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("code") || n.Contains("vscode") || n.Contains("vs code");
        }

        private static string ReadRegistryInstall()
        {
            try
            {
                RegistryKey key = FindUninstallKey();
                if (key == null) return null;
                using (key)
                {
                    object loc = key.GetValue("InstallLocation");
                    if (loc == null) return null;
                    return loc.ToString().TrimEnd('\\');
                }
            }
            catch { return null; }
        }

        private static RegistryKey FindUninstallKey()
        {
            string[] roots = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string root in roots)
            {
                RegistryKey baseKey = Registry.CurrentUser.OpenSubKey(root);
                if (baseKey == null) continue;
                foreach (string name in baseKey.GetSubKeyNames())
                {
                    RegistryKey item = baseKey.OpenSubKey(name);
                    if (item == null) continue;
                    object dn = item.GetValue("DisplayName");
                    if (dn != null)
                    {
                        string s = dn.ToString();
                        if (s.IndexOf("Visual Studio Code", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            s.IndexOf("Insiders", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            baseKey.Close();
                            return item;
                        }
                    }
                    item.Close();
                }
                baseKey.Close();
            }
            return null;
        }

        private static bool FixRegistryInstall(string installDir)
        {
            if (string.IsNullOrEmpty(installDir)) return false;
            try
            {
                RegistryKey key = FindUninstallKey();
                if (key == null) return false;
                using (key)
                {
                    key.SetValue("InstallLocation", installDir.TrimEnd('\\') + "\\", RegistryValueKind.String);
                    string exe = Path.Combine(installDir, "Code.exe");
                    if (File.Exists(exe)) key.SetValue("DisplayIcon", exe, RegistryValueKind.String);
                    string unins = Path.Combine(installDir, "unins000.exe");
                    if (File.Exists(unins)) key.SetValue("UninstallString", "\"" + unins + "\"", RegistryValueKind.String);
                }
                return true;
            }
            catch { return false; }
        }

        private static List<string> FindBadPathEntries()
        {
            List<string> bad = new List<string>();
            try
            {
                string path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
                if (string.IsNullOrEmpty(path)) return bad;
                foreach (string part in path.Split(';'))
                {
                    string p = part.Trim();
                    if (p.Length == 0) continue;
                    if (p.IndexOf("Microsoft VS Code", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.IndexOf("VS Code\\bin", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!Directory.Exists(p)) bad.Add(p);
                }
            }
            catch { }
            return bad;
        }

        private static bool FixUserPath(string oldEntry, string newBin)
        {
            if (string.IsNullOrEmpty(newBin) || !Directory.Exists(newBin)) return false;
            try
            {
                string path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
                if (path == null) path = "";
                List<string> parts = new List<string>();
                bool replaced = false;
                foreach (string raw in path.Split(';'))
                {
                    string p = raw.Trim();
                    if (p.Length == 0) continue;
                    if (!replaced && string.Equals(p, oldEntry, StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(newBin);
                        replaced = true;
                    }
                    else
                    {
                        parts.Add(p);
                    }
                }
                if (!replaced) parts.Add(newBin);
                string updated = string.Join(";", parts.ToArray());
                Registry.SetValue(@"HKEY_CURRENT_USER\Environment", "Path", updated, RegistryValueKind.ExpandString);
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
                return true;
            }
            catch { return false; }
        }

        private static string GetShortcutTarget(string lnk)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                object target = shellType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                return target as string;
            }
            catch { return null; }
        }

        private static bool SetShortcutTarget(string lnk, string target)
        {
            if (string.IsNullOrEmpty(lnk) || string.IsNullOrEmpty(target)) return false;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                shellType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shellType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return true;
            }
            catch { return false; }
        }
    }
}
