using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VSCodeDataMover
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--repair-window", StringComparison.OrdinalIgnoreCase))
            {
                ToolData.Heal();
                ToolData.Ensure();
                ToolData.PruneLogs(30);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RepairForm());
                return 0;
            }

            if (args.Length > 0 && args[0].StartsWith("-")) return Cli.Run(args);

            bool created;
            using (Mutex mutex = new Mutex(true, "VSCodeDataMover_SingleInstance_v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show("VS Code 数据迁移工具已经在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                ToolData.Heal();
                ToolData.Ensure();
                ToolData.PruneLogs(30);
                Application.Run(new MainForm());
            }
            return 0;
        }

        internal static void EnsureConsole()
        {
            if (!AttachConsole(-1)) AllocConsole();
            try
            {
                Encoding enc = Console.OutputEncoding;
                StreamWriter o = new StreamWriter(Console.OpenStandardOutput(), enc);
                o.AutoFlush = true;
                Console.SetOut(o);
                StreamWriter e = new StreamWriter(Console.OpenStandardError(), enc);
                e.AutoFlush = true;
                Console.SetError(e);
            }
            catch { }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only application: registry, WScript.Shell.")]
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            Program.EnsureConsole();

            string command = args[0].ToLowerInvariant();
            string simRoot = GetOption(args, "--sim-root");
            if (!string.IsNullOrEmpty(simRoot))
            {
                Paths.SimRoot = Path.GetFullPath(simRoot);
            }

            bool deep = HasFlag(args, "--deep");
            bool quiet = HasFlag(args, "--quiet");

            if (command == "--version")
            {
                Console.WriteLine("VS Code Data Mover 1.0.0");
                return 0;
            }

            if (command == "--help" || command == "-h" || command == "/?")
            {
                PrintUsage();
                return 0;
            }

            ToolData.Heal();
            ToolData.Ensure();
            ToolData.PruneLogs(30);

            string logFile = Path.Combine(Paths.StateDir, "logs", "cli-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Log log = new Log(logFile);

            try
            {
                if (command == "--list" || command == "--detect")
                {
                    return List(deep, quiet, log);
                }

                if (command == "--migrate")
                {
                    string target = GetPositional(args, 1);
                    if (string.IsNullOrEmpty(target))
                    {
                        Console.WriteLine("用法: --migrate <目标文件夹> [--deep] [--sim-root <路径>]");
                        return 2;
                    }
                    return Migrate(target, deep, quiet, log);
                }

                if (command == "--restore")
                {
                    return Restore(deep, quiet, log);
                }

                if (command == "--recover")
                {
                    return Recover(quiet, log);
                }

                if (command == "--history")
                {
                    return History(quiet, log);
                }

                if (command == "--doctor")
                {
                    return DoctorCmd(HasFlag(args, "--history"), quiet, log);
                }

                if (command == "--fix")
                {
                    return FixCmd(quiet, log);
                }

                if (command == "--uninstall")
                {
                    return UninstallCmd(args, quiet, log);
                }

                if (command == "--restore-history")
                {
                    List<string> pos = GetPositionals(args, 1);
                    if (pos.Count < 2)
                    {
                        Console.WriteLine("用法: --restore-history <数据项key> <候选目录>");
                        return 2;
                    }
                    return RestoreHistory(pos[0], pos[1], deep, quiet, log);
                }

                if (command == "--diag")
                {
                    return Diag();
                }

                PrintUsage();
                return 2;
            }
            catch (MoverException ex)
            {
                Console.WriteLine("错误: " + ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.WriteLine("异常: " + ex.Message);
                return 1;
            }
        }

        private static int List(bool deep, bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state);
            foreach (Entry e in entries) Locator.Measure(e);

            string install = Locator.DetectInstallPath("VS Code");
            Console.WriteLine("INSTALL\t" + (install == null ? "" : install));
            foreach (Entry e in entries)
            {
                Console.WriteLine(string.Join("\t", new string[]
                {
                    e.Key,
                    e.State.ToString(),
                    e.Source,
                    e.Target == null ? "" : e.Target,
                    e.SizeBytes.ToString(),
                    e.FileCount.ToString()
                }));
            }
            Console.WriteLine("TOTAL\t" + entries.Count);
            return 0;
        }

        private static int Migrate(string target, bool deep, bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state);

            Mover mover = new Mover();
            mover.Log = log;
            mover.Deep = deep;
            if (!quiet)
            {
                mover.Progress = delegate(int p, string m) { Console.WriteLine("[" + p + "%] " + m); };
            }

            OpResult result = mover.MigrateAll(entries, target);
            foreach (string line in result.Lines) Console.WriteLine(line);
            if (!quiet) Console.WriteLine(result.Summary);
            return result.Ok ? 0 : 1;
        }

        private static int Restore(bool deep, bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state);

            Mover mover = new Mover();
            mover.Log = log;
            mover.Deep = deep;
            if (!quiet)
            {
                mover.Progress = delegate(int p, string m) { Console.WriteLine("[" + p + "%] " + m); };
            }

            OpResult result = mover.RestoreAll(entries);
            foreach (string line in result.Lines) Console.WriteLine(line);
            if (!quiet) Console.WriteLine(result.Summary);
            return result.Ok ? 0 : 1;
        }

        private static int Recover(bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state);

            Mover mover = new Mover();
            mover.Log = log;
            OpResult result = mover.Recover(entries);
            foreach (string line in result.Lines) Console.WriteLine(line);
            Console.WriteLine(result.Summary);
            return 0;
        }

        private static int History(bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state, true);
            List<HistoryCandidate> list = HistoryScanner.ScanAll(entries);

            Console.WriteLine("ENTRY\tKIND\tCONF\tSOURCE\tPATH");
            foreach (HistoryCandidate c in list)
            {
                Console.WriteLine(c.EntryKey + "\t" + c.Kind + "\t" + c.Confidence + "\t" + c.Source + "\t" + c.Path);
            }
            Console.WriteLine("TOTAL\t" + list.Count);
            return 0;
        }

        private static int RestoreHistory(string key, string path, bool deep, bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state, true);
            Entry entry = entries.FirstOrDefault(delegate(Entry x) { return string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase); });
            if (entry == null)
            {
                Console.WriteLine("未找到数据项: " + key);
                return 2;
            }

            Mover mover = new Mover();
            mover.Log = log;
            mover.Deep = deep;
            OpResult result = mover.RestoreHistory(entry, path);
            foreach (string line in result.Lines) Console.WriteLine(line);
            Console.WriteLine(result.Summary);
            return result.Ok ? 0 : 1;
        }

        private static int DoctorCmd(bool scanHistory, bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state, true);
            DoctorReport report = Doctor.Diagnose(entries, scanHistory);

            Console.WriteLine("SEVERITY\tACTION\tTITLE\tDETAIL");
            foreach (DoctorFinding f in report.Findings)
            {
                Console.WriteLine(f.Severity + "\t" + f.Action + "\t" + f.Title + "\t" +
                    f.Detail.Replace("\r", " ").Replace("\n", " "));
            }
            Console.WriteLine("AUTO\t" + report.AutoFixable);
            Console.WriteLine("HISTORY\t" + report.HistoryPending);
            foreach (HistoryCandidate c in report.Candidates)
            {
                Console.WriteLine("CANDIDATE\t" + c.EntryKey + "\t" + c.Confidence + "\t" + c.Source + "\t" + c.Path);
            }
            return 0;
        }

        private static int FixCmd(bool quiet, Log log)
        {
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state, true);
            DoctorReport report = Doctor.Diagnose(entries);
            OpResult result = Doctor.Fix(report, entries, null, log);
            foreach (string line in result.Lines) Console.WriteLine(line);
            Console.WriteLine(result.Summary);
            return result.Ok ? 0 : 1;
        }

        private static int UninstallCmd(string[] args, bool quiet, Log log)
        {
            bool keepMigrated = HasFlag(args, "--keep-migrated");
            bool deleteProgram = HasFlag(args, "--delete-program");

            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state, true);
            OpResult result = Uninstaller.Run(entries, !keepMigrated, deleteProgram, log);
            foreach (string line in result.Lines) Console.WriteLine(line);
            Console.WriteLine(result.Summary);
            return result.Ok ? 0 : 1;
        }

        private static List<string> GetPositionals(string[] args, int start)
        {
            List<string> list = new List<string>();
            for (int i = start; i < args.Length; i++)
            {
                if (args[i].StartsWith("--")) continue;
                list.Add(args[i]);
            }
            return list;
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetPositional(string[] args, int start)
        {
            for (int i = start; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--")) continue;
                return a;
            }
            return null;
        }

        private static int Diag()
        {
            Console.WriteLine("64bit=" + Environment.Is64BitProcess);
            Dictionary<string, Dictionary<string, string>> state = StateStore.Load();
            List<Entry> entries = Locator.Detect(state);
            foreach (Entry e in entries)
            {
                Console.WriteLine("ENTRY " + e.Key + "  " + e.Source);
                Console.WriteLine("  exists=" + FsUtil.Exists(e.Source) + " isDir=" + FsUtil.IsDir(e.Source) + " reparse=" + Reparse.IsReparsePoint(e.Source));
                string t;
                bool j;
                bool ok = Reparse.TryGetTarget(e.Source, out t, out j);
                Console.WriteLine("  target ok=" + ok + " isJunction=" + j + " target=" + t);
                try { Console.WriteLine("  attrs=" + File.GetAttributes(e.Source)); }
                catch (Exception ex) { Console.WriteLine("  attrs error: " + ex.GetType().Name + " " + ex.Message); }
                try { Console.WriteLine("  attrsLP=" + File.GetAttributes(Reparse.Lp(e.Source))); }
                catch (Exception ex) { Console.WriteLine("  attrsLP error: " + ex.GetType().Name + " " + ex.Message); }
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    Console.WriteLine("HKCU uninstall key=" + (key != null));
                    if (key != null)
                    {
                        foreach (string name in key.GetSubKeyNames())
                        {
                            using (RegistryKey item = key.OpenSubKey(name))
                            {
                                if (item == null) continue;
                                object dn = item.GetValue("DisplayName");
                                if (dn == null || dn.ToString().IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                Console.WriteLine("  " + dn + " | loc=" + item.GetValue("InstallLocation"));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("registry error: " + ex.Message);
            }
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("VS Code Data Mover 1.0.0");
            Console.WriteLine();
            Console.WriteLine("图形界面: 直接双击 VSCodeDataMover.exe");
            Console.WriteLine();
            Console.WriteLine("命令行:");
            Console.WriteLine("  --list                         列出检测结果");
            Console.WriteLine("  --migrate <目标文件夹>         迁移");
            Console.WriteLine("  --restore                      还原");
            Console.WriteLine("  --recover                      修复中断的操作");
            Console.WriteLine("  --history                      检索手动移动前的历史数据位置");
            Console.WriteLine("  --restore-history <key> <目录> 用历史候选目录恢复某一项数据");
            Console.WriteLine("  --doctor                       体检：程序本体/快捷方式/注册表/PATH/数据");
            Console.WriteLine("  --fix                          体检并自动修复可修复项");
            Console.WriteLine("  --uninstall                    卸载清理（默认先还原数据）");
            Console.WriteLine("  --uninstall --keep-migrated    保留迁移状态，只清理工具数据和注册表");
            Console.WriteLine("  --delete-program               连同 VSCodeDataMover.exe 一起删除");
            Console.WriteLine("  --deep                         深度校验（逐文件 SHA256）");
            Console.WriteLine("  --sim-root <路径>              测试用：模拟用户目录");
            Console.WriteLine("  --quiet                        安静模式");
            Console.WriteLine("  --version                      显示版本");
        }
    }
}
