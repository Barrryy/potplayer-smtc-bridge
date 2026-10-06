using Microsoft.Win32;

namespace SmtcBridge;

/// <summary>
/// IFEO(Image File Execution Options) 启动注入的安装/卸载。
///
/// 安装做两件事：
///   1. 把注入需要的 4 个文件复制到固定安装目录 AppPaths.InstallDir
///      （%LOCALAPPDATA%\PotPlayerSmtcBridge）——注入器、注入模块、规则 ini、文件挂接开关。
///      这样本程序目录随便挪、随便删都不影响 PotPlayer。
///   2. 在 HKLM 里写一个键，Debugger 指向安装目录里的注入器：
///      ...\Image File Execution Options\&lt;PotPlayer 可执行文件名&gt;\Debugger
///
/// 为什么不改 PotPlayer 的文件：实测 PotPlayerMini64.exe 是 Themida 加壳，
/// 自查文件长度与内容，任何改动都会让它拒绝启动（Cannot find or init PotPlayer64.dll）；
/// PotPlayer64.dll / MediaDB64.dll 又都带 Kakao 签名并被 WinVerifyTrust 校验，
/// 替身 DLL 会被判定为 modified or hacked。
/// </summary>
internal static class IfeoInstaller
{
    private const string IfeoRoot =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string MarkerValue = "SmtcBridgeInjector";

    private const string HookDllName = "PotPlayerSmtcHook.dll";
    private const string IniName = "PotPlayerSmtcHook.ini";
    private const string MarkerName = "PotPlayerSmtcHook.hookfiles";

    public static string SubKeyFor(string exePath) =>
        Path.Combine(IfeoRoot, Path.GetFileName(exePath));

    private static string Normalize(string? path) => (path ?? string.Empty).Trim().Trim('"');

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

    /// <summary>装好了、而且注册表指向的正是当前安装目录。</summary>
    public static bool IsInstalledByUs(string exePath) =>
        string.Equals(Normalize(DebuggerFor(exePath)), AppPaths.InstalledInjector,
                      StringComparison.OrdinalIgnoreCase) &&
        File.Exists(AppPaths.InstalledInjector);

    /// <summary>
    /// 装过，但注册表指向的路径已经不是当前安装目录（本程序目录挪过，或者装的是别的副本）
    /// 时返回那条陈旧路径 —— 这种情况下 PotPlayer 会去拉起一个不存在的注入器，也就打不开。
    /// </summary>
    public static string? StaleDebugger(string exePath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath));
            if (key?.GetValue(MarkerValue) is null) return null;
            var debugger = Normalize(key.GetValue("Debugger") as string);
            if (string.Equals(debugger, AppPaths.InstalledInjector,
                              StringComparison.OrdinalIgnoreCase)) return null;
            return debugger.Length == 0 ? null : debugger;
        }
        catch
        {
            return null;
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

    /// <summary>复制文件 + 写 IFEO 键。需要管理员权限。</summary>
    public static void Install(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到 PotPlayer 主程序", exePath);
        if (!File.Exists(AppPaths.InjectorExe))
            throw new FileNotFoundException("找不到启动注入器", AppPaths.InjectorExe);
        if (!File.Exists(AppPaths.HookDll))
            throw new FileNotFoundException("找不到注入模块", AppPaths.HookDll);

        // 1) 固定安装目录：注入器 + 注入模块 + 规则 + 文件挂接开关
        Directory.CreateDirectory(AppPaths.InstallDir);
        File.Copy(AppPaths.InjectorExe, AppPaths.InstalledInjector, overwrite: true);
        File.Copy(AppPaths.HookDll, Path.Combine(AppPaths.InstallDir, HookDllName), overwrite: true);

        // 规则 ini 只在安装目录里没有时才从本程序目录带过去（别覆盖用户后来改的）
        var iniSrc = Path.Combine(AppPaths.AppDir, IniName);
        var iniDst = Path.Combine(AppPaths.InstallDir, IniName);
        if (!File.Exists(iniDst) && File.Exists(iniSrc)) File.Copy(iniSrc, iniDst);

        File.WriteAllBytes(Path.Combine(AppPaths.InstallDir, MarkerName), Array.Empty<byte>());

        // 2) IFEO 指向安装目录里的注入器
        using var key = Registry.LocalMachine.CreateSubKey(SubKeyFor(exePath), writable: true)
            ?? throw new UnauthorizedAccessException("写入 HKLM 失败，请用管理员身份运行。");

        key.SetValue("Debugger", "\"" + AppPaths.InstalledInjector + "\"", RegistryValueKind.String);
        key.SetValue(MarkerValue, AppPaths.InstallDir, RegistryValueKind.String);
    }

    /// <summary>删掉自己写的 IFEO 键与安装目录。别人的占用不动。需要管理员权限。</summary>
    public static bool Remove(string exePath)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(SubKeyFor(exePath), writable: true))
        {
            if (key is null) return true;
            if (key.GetValue(MarkerValue) is null)
                throw new InvalidOperationException(
                    "这个程序名的 IFEO 项不是本工具写的，已跳过（避免误删别人的设置）。");
        }

        Registry.LocalMachine.DeleteSubKeyTree(SubKeyFor(exePath), throwOnMissingSubKey: false);

        // 顺手把固定安装目录一起清掉；删不掉也不影响 PotPlayer（键已经没了）
        try
        {
            if (Directory.Exists(AppPaths.InstallDir))
                Directory.Delete(AppPaths.InstallDir, recursive: true);
        }
        catch
        {
            // 文件被占用（比如 PotPlayer 正开着）就先留着，下次安装会覆盖
        }

        return true;
    }
}
