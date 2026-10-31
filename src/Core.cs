using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace VSCodeDataMover
{
    public enum EntryState
    {
        Missing,
        Normal,
        Migrated,
        BrokenTargetMissing,
        InterruptedRename,
        CopyLeftover,
        RestoreLeftover
    }

    public class Entry
    {
        public string Key;
        public string Product;
        public string Label;
        public string Source;
        public string RelTarget;
        public string Target;
        public string Backup;
        public long SizeBytes;
        public int FileCount;
        public EntryState State;
        public string StateText;

        public string DisplayName
        {
            get { return Product + " · " + Label; }
        }
    }

    public class OpResult
    {
        public bool Ok = true;
        public List<string> Lines = new List<string>();
        public string Summary = "";

        public void Add(string s)
        {
            Lines.Add(s);
        }

        public void Fail(string s)
        {
            Ok = false;
            Lines.Add("错误: " + s);
        }
    }

    public class MoverException : Exception
    {
        public MoverException(string message) : base(message) { }
    }

    internal static class Reparse
    {
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_ALL = 0x00000007;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
            uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static string Lp(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(Lp(path));
                return (attrs & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetTarget(string path, out string target, out bool isJunction)
        {
            target = null;
            isJunction = false;
            IntPtr h = CreateFileW(Lp(path), GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (h == new IntPtr(-1)) return false;

            IntPtr buf = Marshal.AllocHGlobal(16384);
            try
            {
                uint returned;
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buf, 16384, out returned, IntPtr.Zero))
                    return false;

                uint tag = (uint)Marshal.ReadInt32(buf, 0);
                if (tag != IO_REPARSE_TAG_MOUNT_POINT && tag != IO_REPARSE_TAG_SYMLINK) return false;

                isJunction = tag == IO_REPARSE_TAG_MOUNT_POINT;
                int baseOffset = isJunction ? 20 : 16;
                ushort subOffset = (ushort)Marshal.ReadInt16(buf, 8);
                ushort subLength = (ushort)Marshal.ReadInt16(buf, 10);
                ushort printOffset = (ushort)Marshal.ReadInt16(buf, 12);
                ushort printLength = (ushort)Marshal.ReadInt16(buf, 14);

                string printName = CleanPath(ReadString(buf, baseOffset + printOffset, printLength));
                string substitute = CleanPath(ReadString(buf, baseOffset + subOffset, subLength));
                string substitute16 = CleanPath(ReadString(buf, 16 + subOffset, subLength));
                string substitute20 = CleanPath(ReadString(buf, 20 + subOffset, subLength));

                string[] candidates = new string[] { printName, substitute, substitute16, substitute20 };
                foreach (string c in candidates)
                {
                    if (IsPathLike(c))
                    {
                        target = c;
                        return true;
                    }
                }
                target = printName != null ? printName : substitute;
                return target != null && target.Length > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        private static string ReadString(IntPtr buf, int offset, int byteLength)
        {
            if (byteLength <= 0) return null;
            return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + offset), byteLength / 2);
        }

        private static string CleanPath(string s)
        {
            if (s == null) return null;
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            s = s.Trim();
            if (s.StartsWith(@"\??\")) s = s.Substring(4);
            else if (s.StartsWith(@"??\")) s = s.Substring(3);
            else if (s.StartsWith(@"?\")) s = s.Substring(2);
            else if (s.StartsWith("?")) s = s.Substring(1);
            return s.Trim();
        }

        private static bool IsPathLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length >= 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
            if (s.StartsWith(@"\\")) return true;
            return false;
        }
    }

    public static class Paths
    {
        public static string SimRoot;

        public static string UserProfile
        {
            get
            {
                if (SimRoot != null) return SimRoot;
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        public static string AppData
        {
            get
            {
                if (SimRoot != null) return Path.Combine(SimRoot, @"AppData\Roaming");
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
        }

        public static string LocalAppData
        {
            get
            {
                if (SimRoot != null) return Path.Combine(SimRoot, @"AppData\Local");
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
        }

        public static string StateDir
        {
            get
            {
                if (SimRoot != null) return Path.Combine(SimRoot, "state");
                return Path.Combine(LocalAppData, "VSCodeDataMover");
            }
        }
    }

    public static class FsUtil
    {
        public static string Norm(string path)
        {
            string s = Path.GetFullPath(path);
            if (s.Length > 3) s = s.TrimEnd('\\');
            return s;
        }

        public static bool Exists(string path)
        {
            try
            {
                File.GetAttributes(Reparse.Lp(path));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsDir(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(Reparse.Lp(path));
                return (a & FileAttributes.Directory) != 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsEmptyDir(string path)
        {
            try
            {
                string lp = Reparse.Lp(path);
                if (Directory.EnumerateFileSystemEntries(lp).Any()) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static long DirSize(string root, out int count)
        {
            long total = 0;
            int c = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(Reparse.Lp(root), "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(f).Length;
                        c++;
                    }
                    catch { }
                }
            }
            catch { }
            count = c;
            return total;
        }

        public static Dictionary<string, long> Snapshot(string root)
        {
            Dictionary<string, long> map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string lp = Reparse.Lp(root);
            foreach (string f in Directory.EnumerateFiles(lp, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(lp.Length).TrimStart('\\');
                try
                {
                    map[rel] = new FileInfo(f).Length;
                }
                catch { }
            }
            return map;
        }

        public static string Hash(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(Reparse.Lp(file), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static void DeleteTree(string path)
        {
            if (!Exists(path)) return;
            if (Reparse.IsReparsePoint(path))
            {
                DeleteJunction(path);
                return;
            }
            foreach (string f in Directory.EnumerateFiles(Reparse.Lp(path), "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch { }
            }
            Directory.Delete(Reparse.Lp(path), true);
        }

        public static void MoveDir(string src, string dst)
        {
            Directory.Move(Reparse.Lp(src), Reparse.Lp(dst));
        }

        public static void CreateDir(string path)
        {
            Directory.CreateDirectory(Reparse.Lp(path));
        }

        public static bool IsInside(string child, string parent)
        {
            child = Norm(child);
            parent = Norm(parent);
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return false;
            return child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CreateJunction(string link, string target, out string output)
        {
            string args = "/c mklink /J \"" + link + "\" \"" + target + "\"";
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit();
                output = (o + " " + e).Trim();
                return p.ExitCode == 0;
            }
        }

        public static bool DeleteJunction(string link)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c rmdir \"" + link + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
        }

        public static string Timestamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }
    }

    public class Log
    {
        private readonly object gate = new object();
        public string FilePath;

        public Log(string path)
        {
            FilePath = path;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
        }

        public void Write(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (gate)
            {
                try { File.AppendAllText(FilePath, line + "\r\n", new UTF8Encoding(false)); }
                catch { }
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only application: registry, junctions, WinForms.")]
    public static class Locator
    {
        public static List<Entry> Detect(Dictionary<string, Dictionary<string, string>> state)
        {
            return Detect(state, false);
        }

        public static List<Entry> Detect(Dictionary<string, Dictionary<string, string>> state, bool includeMissing)
        {
            List<Entry> all = new List<Entry>();
            AddProduct(all, "VS Code", "Code", ".vscode");
            AddProduct(all, "VS Code Insiders", "Code - Insiders", ".vscode-insiders");
            AddProduct(all, "VSCodium", "VSCodium", ".vscode-oss");
            AddCppTools(all);

            List<Entry> result = new List<Entry>();
            foreach (Entry e in all)
            {
                Analyze(e, state);
                if (includeMissing || e.State != EntryState.Missing) result.Add(e);
            }
            return result;
        }

        private static void AddProduct(List<Entry> list, string product, string dataFolder, string dotFolder)
        {
            Add(list, product, "用户数据", product + "-userdata",
                Path.Combine(Paths.AppData, dataFolder), @"Roaming\" + dataFolder);
            Add(list, product, "扩展与 CLI", product + "-ext",
                Path.Combine(Paths.UserProfile, dotFolder), @"User\" + dotFolder);
            Add(list, product, "共享存储", product + "-shared",
                Path.Combine(Paths.UserProfile, dotFolder + "-shared"), @"User\" + dotFolder + "-shared");
        }

        private static void AddCppTools(List<Entry> list)
        {
            Add(list, "C++ 扩展", "IntelliSense 缓存", "cpptools",
                Path.Combine(Paths.LocalAppData, @"Microsoft\vscode-cpptools"), @"Local\Microsoft\vscode-cpptools");
        }

        private static void Add(List<Entry> list, string product, string label, string key, string source, string relTarget)
        {
            Entry e = new Entry();
            e.Key = key;
            e.Product = product;
            e.Label = label;
            e.Source = source;
            e.RelTarget = relTarget;
            list.Add(e);
        }

        public static void Analyze(Entry e, Dictionary<string, Dictionary<string, string>> state)
        {
            e.Target = null;
            e.Backup = null;

            bool isLink = Reparse.IsReparsePoint(e.Source);
            if (isLink)
            {
                string t;
                bool j;
                if (Reparse.TryGetTarget(e.Source, out t, out j) && j) e.Target = t;
            }
            else if (state != null && state.ContainsKey(e.Key))
            {
                e.Target = state[e.Key].ContainsKey("Target") ? state[e.Key]["Target"] : null;
            }

            e.Backup = FindLatestBackup(e.Source);

            bool exists = FsUtil.Exists(e.Source);
            bool targetExists = !string.IsNullOrEmpty(e.Target) && FsUtil.IsDir(e.Target) && !Reparse.IsReparsePoint(e.Target);

            if (isLink && targetExists)
            {
                e.State = EntryState.Migrated;
                e.StateText = "已迁移";
            }
            else if (isLink && !targetExists)
            {
                e.State = EntryState.BrokenTargetMissing;
                e.StateText = "目标丢失";
            }
            else if (!exists && e.Backup != null)
            {
                e.State = EntryState.InterruptedRename;
                e.StateText = "中断待恢复";
            }
            else if (exists && targetExists && GetPhase(state, e.Key) == "copying")
            {
                e.State = EntryState.CopyLeftover;
                e.StateText = "残留副本";
            }
            else if (exists && e.Backup != null && GetPhase(state, e.Key) == "history-restored")
            {
                e.State = EntryState.Normal;
                e.StateText = "未迁移";
            }
            else if (exists && e.Backup != null)
            {
                e.State = EntryState.RestoreLeftover;
                e.StateText = "残留备份";
            }
            else if (exists)
            {
                e.State = EntryState.Normal;
                e.StateText = "未迁移";
            }
            else
            {
                e.State = EntryState.Missing;
                e.StateText = "不存在";
                e.Backup = null;
            }
        }

        private static string GetPhase(Dictionary<string, Dictionary<string, string>> state, string key)
        {
            if (state == null || !state.ContainsKey(key)) return null;
            Dictionary<string, string> item = state[key];
            return item.ContainsKey("Phase") ? item["Phase"] : null;
        }

        public static string FindLatestBackup(string source)
        {
            return FindBackup(source, ".vscode-mover-backup-*");
        }

        public static string FindAnyBackup(string source)
        {
            string tool = FindLatestBackup(source);
            if (tool != null) return tool;
            return FindBackup(source, ".migrated-*");
        }

        private static string FindBackup(string source, string suffixPattern)
        {
            try
            {
                string dir = Path.GetDirectoryName(source);
                string leaf = Path.GetFileName(source);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                string[] found = Directory.GetDirectories(dir, leaf + suffixPattern);
                if (found.Length == 0) return null;
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                return found[found.Length - 1];
            }
            catch
            {
                return null;
            }
        }

        public static void Measure(Entry e)
        {
            int count;
            e.SizeBytes = FsUtil.DirSize(e.Source, out count);
            e.FileCount = count;
        }

        public static string DetectInstallPath(string product)
        {
            string match = product == "VS Code" ? "Visual Studio Code" : product;
            string install = FindInRegistry(match);
            if (install != null) return install;

            string[] candidates;
            if (product == "VS Code")
            {
                candidates = new string[]
                {
                    Path.Combine(Paths.LocalAppData, @"Programs\Microsoft VS Code"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code")
                };
            }
            else
            {
                candidates = new string[0];
            }

            foreach (string c in candidates)
            {
                if (File.Exists(Path.Combine(c, "Code.exe"))) return c;
            }
            return null;
        }

        private static string FindInRegistry(string product)
        {
            string[] roots = new string[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            RegistryKey[] hives = new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine };

            foreach (RegistryKey hive in hives)
            {
                foreach (string sub in roots)
                {
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            foreach (string name in key.GetSubKeyNames())
                            {
                                using (RegistryKey item = key.OpenSubKey(name))
                                {
                                    if (item == null) continue;
                                    object display = item.GetValue("DisplayName");
                                    object loc = item.GetValue("InstallLocation");
                                    if (display == null || loc == null) continue;
                                    string dn = display.ToString();
                                    string il = loc.ToString();
                                    if (dn.IndexOf(product, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    if (dn.IndexOf("Insiders", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                    if (il.Length > 0 && File.Exists(Path.Combine(il, "Code.exe"))) return il.TrimEnd('\\');
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        public static bool IsPortable(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return false;
            return Directory.Exists(Path.Combine(installPath, "data"));
        }
    }

    public static class StateStore
    {
        public static string StatePath
        {
            get { return Path.Combine(Paths.StateDir, "state.json"); }
        }

        public static Dictionary<string, Dictionary<string, string>> Load()
        {
            Dictionary<string, Dictionary<string, string>> result =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(StatePath)) return result;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatePath, Encoding.UTF8));
                if (root == null || !root.ContainsKey("Entries")) return result;
                Dictionary<string, object> entries = root["Entries"] as Dictionary<string, object>;
                if (entries == null) return result;
                foreach (KeyValuePair<string, object> kv in entries)
                {
                    Dictionary<string, object> raw = kv.Value as Dictionary<string, object>;
                    if (raw == null) continue;
                    Dictionary<string, string> item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, object> f in raw)
                    {
                        if (f.Value != null) item[f.Key] = f.Value.ToString();
                    }
                    result[kv.Key] = item;
                }
            }
            catch { }
            return result;
        }

        public static void Save(Dictionary<string, Dictionary<string, string>> data)
        {
            try
            {
                Directory.CreateDirectory(Paths.StateDir);
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["Version"] = 1;
                root["Entries"] = data;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(root);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(tmp, StatePath);
            }
            catch { }
        }

        public static void Set(string key, string target, string backup, string phase)
        {
            Dictionary<string, Dictionary<string, string>> data = Load();
            Dictionary<string, string> item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            item["Target"] = target;
            item["Backup"] = backup;
            item["Phase"] = phase;
            item["Updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            data[key] = item;
            Save(data);
        }

        public static void Remove(string key)
        {
            Dictionary<string, Dictionary<string, string>> data = Load();
            if (data.ContainsKey(key)) data.Remove(key);
            Save(data);
        }
    }
}
