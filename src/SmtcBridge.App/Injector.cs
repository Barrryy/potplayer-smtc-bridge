using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace SmtcBridge;

internal static class Injector
{
    private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValue = "PotPlayerSmtcBridge";

    public static bool ComponentReady => File.Exists(AppPaths.HookDll) && File.Exists(AppPaths.Loader);

    /// <summary>调用 SmtcLoader.exe —— 注入逻辑复用已经验证过的那份代码。</summary>
    public static (bool Ok, string Output) RunLoader(params string[] args)
    {
        if (!File.Exists(AppPaths.Loader))
            return (false, $"找不到注入器: {AppPaths.Loader}");

        var info = new ProcessStartInfo
        {
            FileName = AppPaths.Loader,
            WorkingDirectory = AppPaths.AppDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (false, "无法启动注入器");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>后台监听：PotPlayer 一出现就注入。</summary>
    public static Process? StartWatch()
    {
        if (!File.Exists(AppPaths.Loader)) return null;
        var info = new ProcessStartInfo
        {
            FileName = AppPaths.Loader,
            WorkingDirectory = AppPaths.AppDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "--watch",
        };
        try { return Process.Start(info); } catch { return null; }
    }

    public static bool IsPotPlayerRunning() =>
        Process.GetProcessesByName("PotPlayerMini64").Length > 0 ||
        Process.GetProcessesByName("PotPlayer64").Length > 0 ||
        Process.GetProcessesByName("PotPlayerMini").Length > 0;

    /// <summary>先查注册表（安装时会写入 ProgramPath），再退回常见安装位置。</summary>
    public static string? DetectPotPlayer()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\DAUM\PotPlayer64")
                                ?? baseKey.OpenSubKey(@"SOFTWARE\DAUM\PotPlayer");
                var path = key?.GetValue("ProgramPath") as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            catch { /* 读不到就换下一个 */ }
        }

        var candidates = new[]
        {
            @"C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe",
            @"C:\Program Files\DAUM\PotPlayer\PotPlayer64.exe",
            @"C:\Program Files (x86)\DAUM\PotPlayer\PotPlayerMini.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, writable: true);
            if (key is null) return false;
            if (enable)
                // 注册界面程序自己：启动后静默驻留并自动注入，不弹任何窗口
                key.SetValue(AutoStartValue, $"\"{AppPaths.SelfExe}\"");
            else
                key.DeleteValue(AutoStartValue, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey);
            return key?.GetValue(AutoStartValue) is string;
        }
        catch
        {
            return false;
        }
    }
}
