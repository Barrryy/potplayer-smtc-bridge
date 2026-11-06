using System.Diagnostics;

namespace SmtcBridge;

/// <summary>
/// v0.8.4 的原地升级收尾：
///   1. 结束另一个目录里的旧版后台实例，避免旧 DLL 继续抢先注入；
///   2. 如果旧版启用了后台自启动，把启动项迁移到当前版本；
///   3. 如果 IFEO 已安装，且 PotPlayer 没在运行，自动刷新固定安装目录里的模块。
/// </summary>
internal static class UpgradeManager
{
    private static string _report = "";

    public static string TakeReport()
    {
        var report = _report;
        _report = "";
        return report;
    }

    public static bool Prepare(bool silent)
    {
        using var current = Process.GetCurrentProcess();
        var currentPath = CurrentExecutablePath();
        var others = Process.GetProcessesByName(current.ProcessName)
            .Where(process => process.Id != current.Id)
            .ToArray();

        if (others.Length == 0) return true;

        var candidates = others
            .Where(process => !IsSameVersionAndPath(process, currentPath))
            .ToArray();
        if (candidates.Length == 0)
        {
            if (!silent) SilentApp.SignalExistingInstance();
            DisposeAll(others);
            return false;
        }

        if (!silent)
        {
            var answer = MessageBox.Show(
                "Detected another PotPlayer SMTC Bridge instance, likely an older version.\n"
                + "Close it and continue with v0.8.4?\n\n"
                + "检测到另一个 PotPlayer SMTC Bridge 实例，可能是旧版本。\n"
                + "要关闭旧版并继续使用 v0.8.4 吗？",
                "Upgrade · 升级", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                DisposeAll(others);
                return false;
            }
        }

        foreach (var process in candidates)
        {
            try
            {
                var path = ProcessPath(process);
                process.Kill();
                process.WaitForExit(3000);
                _report += $"已关闭旧版后台：{path ?? process.ProcessName}（PID {process.Id}）"
                         + Environment.NewLine;
            }
            catch (Exception ex)
            {
                _report += $"关闭旧版后台失败：PID {process.Id}，{ex.Message}"
                         + Environment.NewLine;
            }
        }
        DisposeAll(others);

        var remaining = Process.GetProcessesByName(current.ProcessName)
            .Where(process => process.Id != current.Id)
            .ToArray();
        var blocked = remaining.Any(process => !IsSameVersionAndPath(process, currentPath));
        DisposeAll(remaining);
        return !blocked;
    }

    public static void Integrate(BridgeConfig config)
    {
        if (Injector.IsAutoStartEnabled())
        {
            if (Injector.SetAutoStart(true))
            {
                config.AutoStart = true;
                config.Save();
                _report += "已把旧版后台自启动迁移到 v0.8.4。"
                         + Environment.NewLine;
            }
        }

        var path = config.PotPlayerPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !IfeoInstaller.IsInstalledByUs(path)) return;

        if (Injector.IsPotPlayerRunning())
        {
            _report += "启动注入仍指向旧文件；请完全退出 PotPlayer 后重新运行 v0.8.4。"
                     + Environment.NewLine;
            return;
        }

        if (IfeoInstaller.RefreshPayload())
        {
            _report += "已自动刷新固定安装目录里的 v0.8.4 注入文件。"
                     + Environment.NewLine;
        }
        else
        {
            _report += "自动刷新注入文件失败；请在界面里重新点击「安装启动注入」。"
                     + Environment.NewLine;
        }
    }

    private static bool IsSameVersionAndPath(Process process, string currentPath)
    {
        var path = ProcessPath(process);
        if (path is null ||
            !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        try
        {
            // 同一个 exe 路径也可能已经被新版本覆盖，但旧进程仍映射着旧映像。
            return process.StartTime >= File.GetLastWriteTime(currentPath).AddSeconds(-1);
        }
        catch
        {
            return true;
        }
    }

    private static string CurrentExecutablePath()
    {
        try
        {
            return Path.GetFullPath(Environment.ProcessPath ??
                                    Process.GetCurrentProcess().MainModule?.FileName ?? "");
        }
        catch
        {
            return AppPaths.SelfExe;
        }
    }

    private static string? ProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }
}
