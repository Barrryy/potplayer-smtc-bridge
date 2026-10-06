namespace SmtcBridge;

/// <summary>程序各文件的固定位置：注入器、DLL、配置都放在一起。</summary>
internal static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>本程序自身，用于注册开机自启。</summary>
    public static string SelfExe => Path.Combine(AppDir, "PotPlayerSmtcBridge.exe");

    public static string HookDll => Path.Combine(AppDir, "PotPlayerSmtcHook.dll");

    public static string Loader => Path.Combine(AppDir, "SmtcLoader.exe");

    /// <summary>IFEO 启动注入器：每次启动 PotPlayer 时由系统自动拉起。</summary>
    public static string InjectorExe => Path.Combine(AppDir, "PotPlayerSmtcInjector.exe");

    /// <summary>与 DLL 同目录，DLL 会读它；界面写它。</summary>
    public static string Ini => Path.Combine(AppDir, "PotPlayerSmtcHook.ini");

    /// <summary>是否启用文件挂接的标记文件（DLL 靠它判断）。</summary>
    public static string FileHookMarker => Path.Combine(AppDir, "PotPlayerSmtcHook.hookfiles");

    public static string LogDir => Path.Combine(Path.GetTempPath(), "potplayer-smtc-bridge");
}
