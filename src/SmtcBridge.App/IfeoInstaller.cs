using Microsoft.Win32;

namespace SmtcBridge;

/// <summary>
/// IFEO(Image File Execution Options) 启动注入的安装/卸载。
///
/// 只在 HKLM 里写一个键：
///   ...\Image File Execution Options\&lt;PotPlayer 可执行文件名&gt;\Debugger = "&lt;本程序目录&gt;\PotPlayerSmtcInjector.exe"
/// 之后每次启动 PotPlayer，系统会先拉起我们的注入器，由它把 DLL 注进去再退出。
///
/// 为什么不用「给主程序打导入表补丁」：实测 PotPlayerMini64.exe 是 Themida 加壳，
/// 自查文件长度与内容，任何改动都会让它拒绝启动（Cannot find or init PotPlayer64.dll）；
/// PotPlayer64.dll / MediaDB64.dll 又都带 Kakao 签名并被 WinVerifyTrust 校验，
/// 替身 DLL 会被判定为 modified or hacked。所以一个字节都不能碰。
/// </summary>
internal static class IfeoInstaller
{
    private const string IfeoRoot =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerValue = "SmtcBridgeInjector";

    /// <summary>安装后写入的标记值（卸载时只删自己写的）。</summary>
    public static string SubKeyFor(string exePath) =>
        Path.Combine(IfeoRoot, Path.GetFileName(exePath));

    /// <summary>读取当前 IFEO Debugger 值，没有则返回 null。</summary>
    public static string? DebuggerFor(string exePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath));
            return key?.GetValue("Debugger") as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsInstalledByUs(string exePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath));
            if (key is null) return false;
            if (key.GetValue(MarkerValue) is null) return false;
            // 注入器被挪走/删掉时算「没装好」，免得用户以为生效了
            return File.Exists(AppPaths.InjectorExe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>别人占用了这个程序名的 IFEO（比如调试器）时返回它，否则 null。</summary>
    public static string? ForeignDebugger(string exePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath));
            if (key is null || key.GetValue(MarkerValue) is not null) return null;
            return key.GetValue("Debugger") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写入 IFEO 键。需要管理员权限。</summary>
    public static void Install(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到 PotPlayer 主程序", exePath);
        if (!File.Exists(AppPaths.InjectorExe))
            throw new FileNotFoundException("找不到启动注入器", AppPaths.InjectorExe);
        if (!File.Exists(AppPaths.HookDll))
            throw new FileNotFoundException("找不到注入模块", AppPaths.HookDll);

        using var key = Registry.LocalMachine.CreateSubKey(SubKeyFor(exePath), writable: true)
            ?? throw new UnauthorizedAccessException("写入 HKLM 失败，请用管理员身份运行。");

        key.SetValue("Debugger", "\"" + AppPaths.InjectorExe + "\"", RegistryValueKind.String);
        key.SetValue(MarkerValue, AppPaths.AppDir, RegistryValueKind.String);
    }

    /// <summary>删除自己写的 IFEO 键。别人的占用不动。需要管理员权限。</summary>
    public static bool Remove(string exePath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath), writable: true);
        if (key is null) return true;
        if (key.GetValue(MarkerValue) is null)
            throw new InvalidOperationException(
                "这个程序名的 IFEO 项不是本工具写的，已跳过（避免误删别人的设置）。");
        key.Close();

        Registry.LocalMachine.DeleteSubKeyTree(SubKeyFor(exePath), throwOnMissingSubKey: false);
        return true;
    }
}
