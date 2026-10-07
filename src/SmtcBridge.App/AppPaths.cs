namespace SmtcBridge;

/// <summary>程序各文件的固定位置：注入器、DLL、配置都放在一起。</summary>
internal static class AppPaths
{
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>
    /// 固定安装目录：注入要用的那 4 个文件会复制到这里，IFEO 指向这里的注入器。
    /// 放用户目录下有两个原因：改规则要写 ini（Program Files 每写一次都得 UAC）；
    /// 而且 PotPlayer 卸载/更新不会碰这里，不会把注入器删成死链。
    /// </summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PotPlayerSmtcBridge");

    /// <summary>安装目录里的注入器（IFEO 的 Debugger 值指向它）。</summary>
    public static string InstalledInjector => Path.Combine(InstallDir, "PotPlayerSmtcInjector.exe");

    /// <summary>是否已经装好（以安装目录里有没有注入模块为准）。</summary>
    public static bool Installed => File.Exists(Path.Combine(InstallDir, "PotPlayerSmtcHook.dll"));

    /// <summary>DLL 实际会被加载的目录：装过就是安装目录，没装就是本程序目录。</summary>
    public static string RuntimeDir => Installed ? InstallDir : AppDir;

    /// <summary>实际会被加载的那一份注入模块（与 RuntimeDir 同目录）。</summary>
    public static string RuntimeHookDll => Path.Combine(RuntimeDir, "PotPlayerSmtcHook.dll");

    /// <summary>本程序自身，用于注册开机自启。</summary>
    public static string SelfExe => Path.Combine(AppDir, "PotPlayerSmtcBridge.exe");

    public static string HookDll => Path.Combine(AppDir, "PotPlayerSmtcHook.dll");

    public static string Loader => Path.Combine(AppDir, "SmtcLoader.exe");

    /// <summary>IFEO 启动注入器：每次启动 PotPlayer 时由系统自动拉起。</summary>
    public static string InjectorExe => Path.Combine(AppDir, "PotPlayerSmtcInjector.exe");

    /// <summary>与 DLL 同目录，DLL 会读它；界面写它。</summary>
    public static string Ini => Path.Combine(RuntimeDir, "PotPlayerSmtcHook.ini");

    /// <summary>是否启用文件挂接的标记文件（DLL 靠它判断）。</summary>
    public static string FileHookMarker => Path.Combine(RuntimeDir, "PotPlayerSmtcHook.hookfiles");

    public static string LogDir => Path.Combine(Path.GetTempPath(), "potplayer-smtc-bridge");
}
