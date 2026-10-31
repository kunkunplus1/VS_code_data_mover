using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VSCodeDataMover
{
    public class HistoryCandidate
    {
        public string EntryKey;
        public string EntryName;
        public string Kind;
        public string Path;
        public string Source;
        public string Confidence;
        public string Note;
        public long Size;
        public DateTime LastWrite;
    }

    /// <summary>
    /// 从系统/软件留下的历史痕迹里，寻找“用户手动移动之前”的 VS Code 数据目录。
    /// 只做线索收集和内容判定，是否恢复由用户决定；恢复前一定会备份当前数据。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only application: registry, WScript.Shell.")]
    public static class HistoryScanner
    {
        private static readonly Regex PathRegex = new Regex(@"[A-Za-z]:\\[^""'<>\|\r\n\t]*", RegexOptions.Compiled);

        private class Raw
        {
            public string Path;
            public string Source;
            public string Confidence;
            public string Note;
        }

        public static string KindOf(Entry e)
        {
            string k = e.Key;
            if (k.EndsWith("-userdata", StringComparison.OrdinalIgnoreCase)) return "UserData";
            if (k.EndsWith("-ext", StringComparison.OrdinalIgnoreCase)) return "Extensions";
            if (k.EndsWith("-shared", StringComparison.OrdinalIgnoreCase)) return "Shared";
            if (string.Equals(k, "cpptools", StringComparison.OrdinalIgnoreCase)) return "CppTools";
            return "";
        }

        public static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "UserData": return "用户数据";
                case "Extensions": return "扩展目录";
                case "Shared": return "共享存储";
                case "CppTools": return "C++ 缓存";
                default: return kind;
            }
        }

        public static string NormalizeForKind(string kind, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Trim().Trim('"').TrimEnd(',', ';').TrimEnd('\\');
            if (p.Length < 4) return null;

            try
            {
                if (File.Exists(p)) p = Path.GetDirectoryName(p);
            }
            catch { return null; }

            if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return null;

            for (int i = 0; i < 3; i++)
            {
                string name = Path.GetFileName(p);
                string parent = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(parent)) break;

                bool up = false;
                if (kind == "UserData" &&
                    (string.Equals(name, "User", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "workspaceStorage", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "globalStorage", StringComparison.OrdinalIgnoreCase)))
                    up = true;
                if (kind == "Extensions" && string.Equals(name, "extensions", StringComparison.OrdinalIgnoreCase)) up = true;
                if (kind == "Shared" && string.Equals(name, "sharedStorage", StringComparison.OrdinalIgnoreCase)) up = true;
                if (kind == "CppTools" && string.Equals(name, "ipch", StringComparison.OrdinalIgnoreCase)) up = true;

                if (!up) break;
                p = parent;
            }
            return p;
        }

        public static bool Matches(string kind, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                switch (kind)
                {
                    case "UserData":
                        return File.Exists(Path.Combine(dir, @"User\settings.json"))
                            || Directory.Exists(Path.Combine(dir, @"User\workspaceStorage"))
                            || Directory.Exists(Path.Combine(dir, @"User\globalStorage"))
                            || File.Exists(Path.Combine(dir, "storage.json"));
                    case "Extensions":
                        return File.Exists(Path.Combine(dir, @"extensions\extensions.json"))
                            || (Directory.Exists(Path.Combine(dir, "extensions")) && File.Exists(Path.Combine(dir, "argv.json")))
                            || File.Exists(Path.Combine(dir, "extensions.json"));
                    case "Shared":
                        return File.Exists(Path.Combine(dir, @"sharedStorage\state.vscdb"));
                    case "CppTools":
                        return File.Exists(Path.Combine(dir, "browse.vc.db"))
                            || Directory.Exists(Path.Combine(dir, "ipch"));
                }
            }
            catch { }
            return false;
        }

        public static List<HistoryCandidate> ScanAll(List<Entry> entries)
        {
            Dictionary<string, bool> present = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in entries)
            {
                if (e.State != EntryState.Missing) present[e.Product] = true;
            }

            List<HistoryCandidate> all = new List<HistoryCandidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Entry e in entries)
            {
                if (KindOf(e) == "") continue;
                bool isStable = string.Equals(e.Product, "VS Code", StringComparison.OrdinalIgnoreCase);
                if (!isStable && !present.ContainsKey(e.Product)) continue;

                foreach (HistoryCandidate c in Scan(e))
                {
                    string key = c.Kind + "|" + FsUtil.Norm(c.Path);
                    if (!seen.Add(key)) continue;
                    all.Add(c);
                }
            }

            all.Sort(delegate(HistoryCandidate x, HistoryCandidate y)
            {
                int rx = Rank(x.Confidence);
                int ry = Rank(y.Confidence);
                if (rx != ry) return ry - rx;
                return y.LastWrite.CompareTo(x.LastWrite);
            });
            return all;
        }

        public static List<HistoryCandidate> Scan(Entry entry)
        {
            List<HistoryCandidate> result = new List<HistoryCandidate>();
            string kind = KindOf(entry);
            if (kind == "") return result;

            List<Raw> raw = new List<Raw>();
            AddPowerShellHistory(raw);
            AddRecentShortcuts(raw);
            AddVSCodeLogs(raw);
            AddRegistryPaths(raw);
            AddStateTargets(raw, entry);
            AddBackupFolders(raw, entry);

            Dictionary<string, HistoryCandidate> map = new Dictionary<string, HistoryCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (Raw r in raw)
            {
                string path = NormalizeForKind(kind, r.Path);
                if (path == null) continue;
                if (!Matches(kind, path)) continue;
                if (IsSameOrInside(path, entry.Source)) continue;
                if (!string.IsNullOrEmpty(entry.Target) && IsSameOrInside(path, entry.Target)) continue;
                if (Paths.SimRoot != null && !IsUnderSim(path)) continue;

                if (!map.ContainsKey(path))
                {
                    HistoryCandidate c = new HistoryCandidate();
                    c.EntryKey = entry.Key;
                    c.EntryName = entry.DisplayName;
                    c.Kind = kind;
                    c.Path = path;
                    c.Source = r.Source;
                    c.Confidence = r.Confidence;
                    c.Note = r.Note;
                    try { c.LastWrite = Directory.GetLastWriteTime(path); }
                    catch { c.LastWrite = DateTime.MinValue; }

                    int fileCount;
                    try { c.Size = FsUtil.DirSize(path, out fileCount); }
                    catch { c.Size = 0; }

                    if (kind == "UserData" &&
                        File.Exists(Path.Combine(path, @"User\settings.json")) &&
                        Directory.Exists(Path.Combine(path, @"User\workspaceStorage")))
                        c.Confidence = "高";

                    map[path] = c;
                }
            }

            result = map.Values.ToList();
            result.Sort(delegate(HistoryCandidate x, HistoryCandidate y)
            {
                int rx = Rank(x.Confidence);
                int ry = Rank(y.Confidence);
                if (rx != ry) return ry - rx;
                return y.LastWrite.CompareTo(x.LastWrite);
            });
            if (result.Count > 40) result = result.GetRange(0, 40);
            return result;
        }

        private static int Rank(string confidence)
        {
            if (confidence == "高") return 3;
            if (confidence == "中") return 2;
            return 1;
        }

        private static bool IsSameOrInside(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(FsUtil.Norm(a), FsUtil.Norm(b), StringComparison.OrdinalIgnoreCase)) return true;
            return FsUtil.IsInside(a, b) || FsUtil.IsInside(b, a);
        }

        private static bool IsUnderSim(string path)
        {
            string sim = FsUtil.Norm(Paths.SimRoot);
            string p = FsUtil.Norm(path);
            return string.Equals(p, sim, StringComparison.OrdinalIgnoreCase) || FsUtil.IsInside(p, sim);
        }

        private static void Add(List<Raw> raw, string path, string source, string confidence, string note)
        {
            if (string.IsNullOrEmpty(path)) return;
            string p = path.Trim().Trim('"').TrimEnd(',', ';').Trim();
            if (p.Length < 4) return;
            Raw r = new Raw();
            r.Path = p;
            r.Source = source;
            r.Confidence = confidence;
            r.Note = note;
            raw.Add(r);
        }

        // ------------------------------------------------------------------
        // 1. PowerShell 命令历史（mklink / Move-Item / robocopy 等）
        // ------------------------------------------------------------------
        private static void AddPowerShellHistory(List<Raw> raw)
        {
            string file = Path.Combine(Paths.AppData, @"Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt");
            if (!File.Exists(file)) return;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { return; }

            int count = 0;
            foreach (string line in lines)
            {
                if (count++ > 20000) break;
                if (line.IndexOf("mklink", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("move-item", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("robocopy", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("junction", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("code", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (Match m in PathRegex.Matches(line))
                {
                    Add(raw, m.Value, "PowerShell 历史", "高", "命令历史中出现过该路径");
                }
            }
        }

        // ------------------------------------------------------------------
        // 2. Windows“最近使用”（.lnk）指向的目录/文件
        // ------------------------------------------------------------------
        private static void AddRecentShortcuts(List<Raw> raw)
        {
            string recent = Path.Combine(Paths.AppData, @"Microsoft\Windows\Recent");
            if (!Directory.Exists(recent)) return;

            string[] files;
            try { files = Directory.GetFiles(recent, "*.lnk"); }
            catch { return; }

            int count = 0;
            foreach (string f in files)
            {
                if (count++ > 800) break;
                string target = GetShortcutTarget(f);
                if (string.IsNullOrEmpty(target)) continue;
                if (target.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) < 0 &&
                    target.IndexOf("code", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Add(raw, target, "最近使用记录", "中", Path.GetFileName(f));
            }
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

        // ------------------------------------------------------------------
        // 3. VS Code 自身日志里出现过的绝对路径
        // ------------------------------------------------------------------
        private static void AddVSCodeLogs(List<Raw> raw)
        {
            string[] products = new string[] { "Code", "Code - Insiders", "VSCodium" };
            foreach (string product in products)
            {
                string logs = Path.Combine(Paths.AppData, product, "logs");
                if (!Directory.Exists(logs)) continue;

                string[] files;
                try { files = Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories); }
                catch { continue; }

                int count = 0;
                foreach (string f in files)
                {
                    if (count++ > 300) break;
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.Length > 8L * 1024 * 1024) continue;
                        string text = File.ReadAllText(f);
                        foreach (Match m in PathRegex.Matches(text))
                        {
                            string v = m.Value;
                            if (v.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) < 0 &&
                                v.IndexOf("code", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            Add(raw, v, "VS Code 日志", "中", Path.GetFileName(f));
                        }
                    }
                    catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // 4. 注册表：资源管理器地址栏历史、运行对话框历史
        // ------------------------------------------------------------------
        private static void AddRegistryPaths(List<Raw> raw)
        {
            if (Paths.SimRoot != null) return; // 测试沙盒不读取真实注册表

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths"))
                {
                    if (k != null)
                    {
                        foreach (string name in k.GetValueNames())
                        {
                            string v = k.GetValue(name) as string;
                            if (!string.IsNullOrEmpty(v)) Add(raw, v, "注册表 TypedPaths", "中", "资源管理器地址栏输入历史");
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"))
                {
                    if (k != null)
                    {
                        foreach (string name in k.GetValueNames())
                        {
                            string v = k.GetValue(name) as string;
                            if (string.IsNullOrEmpty(v)) continue;
                            foreach (Match m in PathRegex.Matches(v))
                            {
                                Add(raw, m.Value, "注册表 RunMRU", "中", "运行对话框命令历史");
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 5. 工具自己记录过的历史目标位置
        // ------------------------------------------------------------------
        private static void AddStateTargets(List<Raw> raw, Entry entry)
        {
            try
            {
                Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
                if (state.ContainsKey(entry.Key) && state[entry.Key].ContainsKey("Target"))
                {
                    string t = state[entry.Key]["Target"];
                    if (!string.IsNullOrEmpty(t)) Add(raw, t, "本工具历史记录", "高", "上次迁移使用过的目标");
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 6. 备份/迁移目录扫描（名称像 Code / vscode / 备份 / 副本 的目录）
        // ------------------------------------------------------------------
        private static void AddBackupFolders(List<Raw> raw, Entry entry)
        {
            string leaf = Path.GetFileName(entry.Source);
            List<string> bases = new List<string>();
            string parent = Path.GetDirectoryName(entry.Source);
            if (!string.IsNullOrEmpty(parent)) bases.Add(parent);
            bases.Add(Paths.UserProfile);
            bases.Add(Paths.AppData);
            bases.Add(Paths.LocalAppData);

            if (Paths.SimRoot != null)
            {
                bases.Add(Paths.SimRoot);
            }
            else
            {
                try
                {
                    foreach (DriveInfo di in DriveInfo.GetDrives())
                    {
                        if (!di.IsReady) continue;
                        if (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable) continue;
                        bases.Add(di.RootDirectory.FullName);
                    }
                }
                catch { }
            }

            HashSet<string> probes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string b in bases)
            {
                if (string.IsNullOrEmpty(b) || !Directory.Exists(b)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(b); }
                catch { continue; }

                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (!LooksInteresting(name, leaf)) continue;
                    AddProbes(probes, d);
                }
            }

            foreach (string p in probes)
            {
                if (!Directory.Exists(p)) continue;
                Add(raw, p, "备份目录扫描", "高", "目录名或结构与 VS Code 数据相似");
            }
        }

        private static bool LooksInteresting(string name, string leaf)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n.Contains("vscode") || n.Contains("code")) return true;
            if (n.Contains("备份") || n.Contains("迁移") || n.Contains("副本")) return true;
            if (n.Contains("backup") || n.Contains("bak") || n.Contains("old")) return true;
            if (!string.IsNullOrEmpty(leaf) && n.Contains(leaf.ToLowerInvariant())) return true;
            return false;
        }

        private static void AddProbes(HashSet<string> set, string baseDir)
        {
            set.Add(baseDir);
            set.Add(Path.Combine(baseDir, @"Roaming\Code"));
            set.Add(Path.Combine(baseDir, @"User\.vscode"));
            set.Add(Path.Combine(baseDir, @"Local\Microsoft\vscode-cpptools"));
            set.Add(Path.Combine(baseDir, "Code"));
            set.Add(Path.Combine(baseDir, ".vscode"));
            set.Add(Path.Combine(baseDir, @"data\Roaming\Code"));
            set.Add(Path.Combine(baseDir, @"data\User\.vscode"));
            set.Add(Path.Combine(baseDir, @"backup\Code"));
        }
    }
}
