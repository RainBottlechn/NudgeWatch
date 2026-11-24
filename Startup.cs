using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NudgeWatch
{
    internal sealed class StartupStatus
    {
        public bool Enabled, DisabledBySystem, PointsToOtherExe;
        public string RegisteredTarget, Method;
    }

    internal static class StartupManager
    {
        internal const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string DefaultApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        internal const string DefaultFolderApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        internal const string ValueName = "NudgeWatch.Native";
        internal const string LegacyDesktopValueName = "local.NudgeWatch.Desktop";
        internal const string ShortcutName = "NudgeWatch.lnk";

        public static string CurrentExePath()
        {
            string candidate = null;
            try
            {
                using (var proc = Process.GetCurrentProcess())
                    if (proc.MainModule != null) candidate = proc.MainModule.FileName;
            }
            catch { }
            if (String.IsNullOrWhiteSpace(candidate))
                candidate = typeof(Shell).Assembly.Location;
            return NormalizePath(candidate);
        }

        internal static string DefaultStartupDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        public static string ExtractExecutablePath(string commandLine)
        {
            if (String.IsNullOrWhiteSpace(commandLine)) return "";
            string trimmed = commandLine.Trim();
            if (trimmed[0] == '"')
            {
                int close = trimmed.IndexOf('"', 1);
                return close > 1 ? trimmed.Substring(1, close - 1).Trim() : trimmed.Trim('"').Trim();
            }
            int exeEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd >= 0)
            {
                int after = exeEnd + 4;
                if (after == trimmed.Length || Char.IsWhiteSpace(trimmed[after]))
                    return trimmed.Substring(0, after).Trim();
            }
            return trimmed;
        }

        public static bool IsApprovedEnabled(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return true;
            return (raw[0] & 1) == 0;
        }

        static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFullPath(path.Trim()); }
            catch { return path.Trim(); }
        }

        static bool SamePath(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            return String.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        public static StartupStatus QueryStatus(string currentExePath)
        {
            return QueryStatus(currentExePath, DefaultRunSubKey, DefaultApprovedSubKey, DefaultFolderApprovedSubKey, DefaultStartupDirectory());
        }

        internal static StartupStatus QueryStatus(string currentExePath, string runSubKey, string approvedSubKey, string folderApprovedSubKey, string startupDir)
        {
            var status = new StartupStatus();
            string expected = NormalizePath(currentExePath);

            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(runSubKey))
                {
                    if (run != null)
                    {
                        var raw = run.GetValue(ValueName) as string;
                        if (!String.IsNullOrWhiteSpace(raw))
                        {
                            string target = ExtractExecutablePath(raw);
                            bool approved = ReadApproved(approvedSubKey, ValueName);
                            status.RegisteredTarget = target;
                            if (!approved) status.DisabledBySystem = true;
                            if (SamePath(target, expected) && File.Exists(target))
                            {
                                if (approved)
                                {
                                    status.Enabled = true;
                                    status.Method = "registry";
                                    return status;
                                }
                            }
                            else
                            {
                                status.PointsToOtherExe = true;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!String.IsNullOrWhiteSpace(startupDir))
            {
                string shortcutPath = Path.Combine(startupDir, ShortcutName);
                if (File.Exists(shortcutPath))
                {
                    string target = ReadShortcutTarget(shortcutPath);
                    bool approved = ReadApproved(folderApprovedSubKey, ShortcutName);
                    if (String.IsNullOrEmpty(status.RegisteredTarget)) status.RegisteredTarget = target;
                    if (!approved) status.DisabledBySystem = true;
                    if (SamePath(target, expected) && File.Exists(target))
                    {
                        if (approved)
                        {
                            status.Enabled = true;
                            status.Method = "startup_folder";
                            status.RegisteredTarget = target;
                            return status;
                        }
                    }
                    else if (!String.IsNullOrWhiteSpace(target))
                    {
                        status.PointsToOtherExe = true;
                    }
                }
            }

            return status;
        }

        public static bool SetEnabled(bool enable, string currentExePath, out string detail)
        {
            return SetEnabled(enable, currentExePath, DefaultRunSubKey, DefaultApprovedSubKey, DefaultFolderApprovedSubKey, DefaultStartupDirectory(), out detail);
        }

        internal static bool SetEnabled(bool enable, string currentExePath, string runSubKey, string approvedSubKey, string folderApprovedSubKey, string startupDir, out string detail)
        {
            string normalized = NormalizePath(currentExePath);
            string shortcutPath = String.IsNullOrWhiteSpace(startupDir) ? null : Path.Combine(startupDir, ShortcutName);

            if (!enable)
            {
                DeleteRegistryValue(runSubKey, ValueName);
                DeleteRegistryValue(approvedSubKey, ValueName);
                DeleteRegistryValue(folderApprovedSubKey, ShortcutName);
                DeleteFileQuiet(shortcutPath);
                var afterOff = QueryStatus(normalized, runSubKey, approvedSubKey, folderApprovedSubKey, startupDir);
                if (!afterOff.Enabled)
                {
                    detail = "已关闭随 Windows 登录启动";
                    return true;
                }
                detail = "未能清理启动项，请检查系统权限或安全软件拦截";
                return false;
            }

            if (String.IsNullOrWhiteSpace(normalized) || !File.Exists(normalized))
            {
                detail = "找不到当前程序路径：" + normalized;
                return false;
            }

            CleanupStaleLegacyDesktopEntry(runSubKey);

            string regError = null;
            try
            {
                using (var run = Registry.CurrentUser.CreateSubKey(runSubKey))
                    run.SetValue(ValueName, "\"" + normalized + "\"", RegistryValueKind.String);
                WriteApprovedEnabled(approvedSubKey, ValueName);
                var afterReg = QueryStatus(normalized, runSubKey, approvedSubKey, folderApprovedSubKey, startupDir);
                if (afterReg.Enabled && afterReg.Method == "registry")
                {
                    DeleteFileQuiet(shortcutPath);
                    DeleteRegistryValue(folderApprovedSubKey, ShortcutName);
                    detail = "已开启随 Windows 登录启动";
                    return true;
                }
            }
            catch (Exception ex) { regError = ex.Message; }

            if (!String.IsNullOrWhiteSpace(shortcutPath))
            {
                try
                {
                    CreateShortcut(shortcutPath, normalized);
                    WriteApprovedEnabled(folderApprovedSubKey, ShortcutName);
                    var afterFolder = QueryStatus(normalized, runSubKey, approvedSubKey, folderApprovedSubKey, startupDir);
                    if (afterFolder.Enabled && afterFolder.Method == "startup_folder")
                    {
                        detail = "已通过系统启动文件夹开启登录启动";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    detail = "启动项写入失败：" + (regError ?? ex.Message);
                    return false;
                }
            }

            detail = "随 Windows 登录启动校验未通过" + (String.IsNullOrEmpty(regError) ? "，可能被系统或安全软件拦截" : "：" + regError);
            return false;
        }

        static void CleanupStaleLegacyDesktopEntry(string runSubKey)
        {
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(runSubKey, true))
                {
                    if (run == null) return;
                    var raw = run.GetValue(LegacyDesktopValueName) as string;
                    if (String.IsNullOrWhiteSpace(raw)) return;
                    string target = ExtractExecutablePath(raw);
                    if (!String.IsNullOrWhiteSpace(target) && !File.Exists(target))
                        run.DeleteValue(LegacyDesktopValueName, false);
                }
            }
            catch { }
        }

        static bool ReadApproved(string subKey, string valueName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    if (key == null) return true;
                    return IsApprovedEnabled(key.GetValue(valueName) as byte[]);
                }
            }
            catch { return true; }
        }

        static void WriteApprovedEnabled(string subKey, string valueName)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(subKey))
                {
                    if (key != null)
                        key.SetValue(valueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
            }
            catch { }
        }

        static void DeleteRegistryValue(string subKey, string valueName)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey, true))
                    if (key != null) key.DeleteValue(valueName, false);
            }
            catch { }
        }

        static void DeleteFileQuiet(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public static void CreateShortcut(string shortcutPath, string targetPath)
        {
            string dir = Path.GetDirectoryName(shortcutPath);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
            object wsh = Activator.CreateInstance(shellType);
            object sc = null;
            try
            {
                sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wsh, new object[] { shortcutPath });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                string workDir = Path.GetDirectoryName(targetPath);
                if (!String.IsNullOrEmpty(workDir))
                    scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "NudgeWatch" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            finally
            {
                if (sc != null && Marshal.IsComObject(sc)) Marshal.ReleaseComObject(sc);
                if (wsh != null && Marshal.IsComObject(wsh)) Marshal.ReleaseComObject(wsh);
            }
        }

        public static string ReadShortcutTarget(string shortcutPath)
        {
            if (String.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath)) return "";
            object wsh = null;
            object sc = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return "";
                wsh = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wsh, new object[] { shortcutPath });
                if (sc == null) return "";
                var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return target == null ? "" : target.Trim();
            }
            catch { return ""; }
            finally
            {
                if (sc != null && Marshal.IsComObject(sc)) Marshal.ReleaseComObject(sc);
                if (wsh != null && Marshal.IsComObject(wsh)) Marshal.ReleaseComObject(wsh);
            }
        }
    }
}
