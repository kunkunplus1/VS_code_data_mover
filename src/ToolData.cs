using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VSCodeDataMover
{
    /// <summary>
    /// 工具自身的数据（state / logs / backups）。默认在 %LOCALAPPDATA%，
    /// 迁移时移动到用户选择的目标目录下的 VSCodeDataMover 文件夹，
    /// 这样 C 盘不会留下备份，只留一个注册表指针。
    /// </summary>
    public static class ToolData
    {
        private const string RegPath = @"Software\VSCodeDataMover";
        private const string RegValue = "DataRoot";

        public static string DefaultRoot
        {
            get
            {
                if (Paths.SimRoot != null) return Path.Combine(Paths.SimRoot, "state");
                return Path.Combine(Paths.LocalAppData, "VSCodeDataMover");
            }
        }

        public static string Root
        {
            get
            {
                if (Paths.SimRoot != null) return DefaultRoot;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                    {
                        if (k != null)
                        {
                            string v = k.GetValue(RegValue) as string;
                            if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
                        }
                    }
                }
                catch { }
                return DefaultRoot;
            }
        }

        public static string StatePath { get { return Path.Combine(Root, "state.json"); } }
        public static string LogsDir { get { return Path.Combine(Root, "logs"); } }
        public static string BackupsDir { get { return Path.Combine(Root, "backups"); } }

        public static void Ensure()
        {
            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(LogsDir);
            }
            catch { }
        }

        public static bool TrySetRoot(string newRoot)
        {
            if (Paths.SimRoot != null) return false;
            if (string.IsNullOrEmpty(newRoot)) return false;

            try
            {
                string target = FsUtil.Norm(newRoot);
                string old = Root;
                if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                {
                    Ensure();
                    return true;
                }

                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(target, "logs"));
                Directory.CreateDirectory(Path.Combine(target, "backups"));

                MoveFileIfExists(Path.Combine(old, "state.json"), Path.Combine(target, "state.json"));
                MoveDirectoryIfExists(Path.Combine(old, "logs"), Path.Combine(target, "logs"));
                MoveDirectoryIfExists(Path.Combine(old, "backups"), Path.Combine(target, "backups"));

                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    k.SetValue(RegValue, target, RegistryValueKind.String);
                }

                TryRemoveEmpty(old);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Heal()
        {
            if (Paths.SimRoot != null) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (k == null) return;
                    string v = k.GetValue(RegValue) as string;
                    if (string.IsNullOrEmpty(v))
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(RegPath, false);
                        return;
                    }
                    if (Directory.Exists(v)) return;

                    string drive = Path.GetPathRoot(v);
                    if (!string.IsNullOrEmpty(drive) && Directory.Exists(drive))
                        Registry.CurrentUser.DeleteSubKeyTree(RegPath, false);
                }
            }
            catch { }
        }

        public static void PruneLogs(int days)
        {
            try
            {
                if (!Directory.Exists(LogsDir)) return;
                DateTime limit = DateTime.Now.AddDays(-days);
                foreach (string f in Directory.GetFiles(LogsDir, "*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        private static void MoveFileIfExists(string src, string dst)
        {
            try
            {
                if (!File.Exists(src)) return;
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
            }
            catch { }
        }

        private static void MoveDirectoryIfExists(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            try
            {
                Directory.CreateDirectory(dst);
                string robocopy = Path.Combine(Environment.SystemDirectory, "Robocopy.exe");
                if (!File.Exists(robocopy)) robocopy = "robocopy.exe";

                ProcessStartInfo psi = new ProcessStartInfo(robocopy);
                psi.Arguments = "\"" + src + "\" \"" + dst + "\" /E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS /NP";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) p.WaitForExit();

                if (Directory.Exists(src) && Directory.GetFileSystemEntries(src).Length == 0)
                    Directory.Delete(src);
            }
            catch { }
        }

        private static void TryRemoveEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
        }
    }
}
