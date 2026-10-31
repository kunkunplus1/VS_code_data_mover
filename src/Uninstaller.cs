using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VSCodeDataMover
{
    /// <summary>
    /// 卸载/清理：还原数据（可选）、删除工具数据、删除注册表指针、按需删除程序本身。
    /// </summary>
    public static class Uninstaller
    {
        public static OpResult Run(List<Entry> entries, bool restoreFirst, bool deleteProgram, Log log)
        {
            OpResult result = new OpResult();
            string root = ToolData.Root;

            if (restoreFirst)
            {
                List<Entry> todo = entries.Where(delegate(Entry e)
                {
                    return e.State == EntryState.Migrated || e.State == EntryState.BrokenTargetMissing ||
                           e.State == EntryState.InterruptedRename || e.State == EntryState.CopyLeftover ||
                           e.State == EntryState.RestoreLeftover;
                }).ToList();

                if (todo.Count > 0)
                {
                    Mover mover = new Mover();
                    mover.Log = log;
                    OpResult restore = mover.RestoreAll(entries);
                    foreach (string line in restore.Lines) result.Add(line);
                    if (!restore.Ok)
                    {
                        result.Ok = false;
                        result.Summary = "还原失败，已停止卸载";
                        return result;
                    }
                    result.Add("数据已还原到 C 盘。");
                }
            }

            TryDeleteDir(Path.Combine(root, "backups"), result);
            TryDeleteDir(Path.Combine(root, "logs"), result);

            string state = Path.Combine(root, "state.json");
            try
            {
                if (File.Exists(state)) { File.Delete(state); result.Add("已删除状态文件: " + state); }
            }
            catch (Exception ex)
            {
                result.Add("状态文件删除失败: " + ex.Message);
            }

            TryDeleteDir(root, result);

            string legacy = Path.Combine(Paths.LocalAppData, "VSCodeDataMover");
            if (!string.Equals(FsUtil.Norm(legacy), FsUtil.Norm(root), StringComparison.OrdinalIgnoreCase))
                TryDeleteDir(legacy, result);

            if (Paths.SimRoot == null)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\VSCodeDataMover", false);
                    result.Add("已删除注册表项 HKCU\\Software\\VSCodeDataMover。");
                }
                catch (Exception ex)
                {
                    result.Add("注册表清理失败: " + ex.Message);
                }
            }

            if (deleteProgram && Paths.SimRoot == null)
            {
                if (ScheduleSelfDelete())
                    result.Add("程序将在退出后自动删除。");
                else
                    result.Add("程序自动删除失败，请手动删除 exe。");
            }

            if (result.Lines.Count == 0) result.Add("没有需要清理的内容。");
            result.Summary = result.Ok ? "卸载清理完成" : "卸载清理部分失败";
            return result;
        }

        private static void TryDeleteDir(string dir, OpResult result)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                FsUtil.DeleteTree(dir);
                result.Add("已删除: " + dir);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Add("删除失败: " + dir + "（" + ex.Message + "）");
            }
        }

        private static bool ScheduleSelfDelete()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe");
                psi.Arguments = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + exe + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
