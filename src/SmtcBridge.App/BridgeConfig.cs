using System.Runtime.InteropServices;
using System.Text;

namespace SmtcBridge;

/// <summary>
/// 读写 PotPlayerSmtcHook.ini。
///
/// [meta] 段由注入的 DLL 读取（标签优先开关、文件名伪正则）；
/// [app]  段只给界面自己用（是否已完成首次注册、PotPlayer 路径等）。
/// 用 Win32 的 ini API，保证与 C++ 侧 GetPrivateProfileString 完全一致。
/// </summary>
internal sealed class BridgeConfig
{
    public const string DefaultPattern = "%artist% - %title%";

    public bool TagFirst { get; set; } = true;
    public string NamePattern { get; set; } = DefaultPattern;

    public bool Registered { get; set; }
    public string PotPlayerPath { get; set; } = "";
    public bool AutoStart { get; set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(string section, string key, string def,
        StringBuilder retVal, int size, string filePath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string? val,
        string filePath);

    private static int GetInt(string section, string key, int fallback, string ini)
    {
        var buffer = new StringBuilder(64);
        var text = GetPrivateProfileString(section, key, "", buffer, buffer.Capacity, ini) > 0
            ? buffer.ToString()
            : "";
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private static string GetString(string section, string key, string fallback, string ini)
    {
        var buffer = new StringBuilder(1024);
        var length = GetPrivateProfileString(section, key, fallback, buffer, buffer.Capacity, ini);
        var text = length > 0 ? buffer.ToString() : fallback;
        return string.IsNullOrEmpty(text) ? fallback : text;
    }

    public static BridgeConfig Load()
    {
        var ini = AppPaths.Ini;
        var config = new BridgeConfig
        {
            TagFirst = GetInt("meta", "tag_first", 1, ini) != 0,
            NamePattern = GetString("meta", "name_pattern", DefaultPattern, ini),
            Registered = GetInt("app", "registered", 0, ini) != 0,
            PotPlayerPath = GetString("app", "potplayer_path", "", ini),
            AutoStart = GetInt("app", "autostart", 0, ini) != 0,
        };
        return config;
    }

    public void Save()
    {
        var ini = AppPaths.Ini;
        WritePrivateProfileString("meta", "tag_first", TagFirst ? "1" : "0", ini);
        WritePrivateProfileString("meta", "name_pattern", NamePattern, ini);
        WritePrivateProfileString("app", "registered", Registered ? "1" : "0", ini);
        WritePrivateProfileString("app", "potplayer_path", PotPlayerPath, ini);
        WritePrivateProfileString("app", "autostart", AutoStart ? "1" : "0", ini);
    }

    /// <summary>把 DLL 的同名 ini 也写一份，保证界面和 DLL 读的是同一份。</summary>
    public void SaveForDll() => Save();
}
