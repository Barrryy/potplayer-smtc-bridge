namespace SmtcBridge;

/// <summary>
/// 界面双语（英文在前 / 中文在后）。
///
/// 文案本来就是中文写死在各个工厂函数里的，这里用一张表在 **控件加进界面之前**
/// 统一替换：<see cref="ApplyDeep"/> 递归扫 Label/Button/CheckBox… 的 Text。
/// 表里没收录的字符串原样保留（也就是中文），所以漏翻最多是没变，不会出错。
/// 键必须和源码里的字面量一字不差。
/// </summary>
internal static class Loc
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // 窗口 / 向导
        ["PotPlayer SMTC Bridge — 首次使用"] = "PotPlayer SMTC Bridge — First run  ·  首次使用",
        ["三步完成注册，之后就能让 PotPlayer 向 Windows 输出完整媒体信息"] =
            "Three steps and PotPlayer will feed Windows the full media info  ·  三步完成注册，之后就能让 PotPlayer 向 Windows 输出完整媒体信息",
        ["① 指定 PotPlayer"] = "① Locate PotPlayer  ·  指定 PotPlayer",
        ["② 启动方式"] = "② Startup mode  ·  启动方式",
        ["主程序路径"] = "Player path  ·  主程序路径",
        ["自动检测"] = "Auto-detect  ·  自动检测",
        ["重新检测 PotPlayer"] = "Re-detect  ·  重新检测 PotPlayer",
        ["浏览…"] = "Browse…  ·  浏览…",
        ["选择 PotPlayer 主程序"] = "Select the PotPlayer executable  ·  选择 PotPlayer 主程序",
        ["完成注册"] = "Finish  ·  完成注册",
        ["保存并应用"] = "Save & apply  ·  保存并应用",
        ["退出"] = "Exit  ·  退出",
        ["路径无效"] = "Invalid path  ·  路径无效",
        ["指定的 PotPlayer 路径不存在。"] = "That PotPlayer path does not exist.  ·  指定的 PotPlayer 路径不存在。",
        ["没有自动找到 PotPlayer，请手动浏览选择。"] =
            "PotPlayer was not found automatically — please pick it manually.  ·  没有自动找到 PotPlayer，请手动浏览选择。",
        ["还没定位到 PotPlayer，请先点「重新检测 PotPlayer」。"] =
            "PotPlayer is not located yet — click \"Re-detect\" first.  ·  还没定位到 PotPlayer，请先点「重新检测 PotPlayer」。",
        ["✔ 组件就位：PotPlayerSmtcHook.dll 与 SmtcLoader.exe 都在本程序目录"] =
            "✔ Components ready: PotPlayerSmtcHook.dll and SmtcLoader.exe are in this folder  ·  组件就位",
        ["✘ 缺少组件：请把 PotPlayerSmtcHook.dll 和 SmtcLoader.exe "] =
            "✘ Missing components: put PotPlayerSmtcHook.dll and SmtcLoader.exe ",
        ["放到本程序同一目录后重新运行"] = "next to this program and start it again  ·  放到本程序同一目录后重新运行",
        ["组件就位"] = "Components ready  ·  组件就位",
        ["组件缺失"] = "Components missing  ·  组件缺失",

        // 主界面
        ["把完整的媒体信息交给 Windows，供系统媒体面板、Discord、歌词工具读取"] =
            "Hand Windows the full media info, for the system media panel, Discord and lyric tools  ·  把完整的媒体信息交给 Windows",
        ["它做什么"] = "What it does  ·  它做什么",
        ["当前状态"] = "Status  ·  当前状态",
        ["刷新状态"] = "Refresh  ·  刷新状态",
        ["怎么让它生效（二选一）"] = "How to activate it (pick one)  ·  怎么让它生效（二选一）",
        ["方式一 · 一次性安装（推荐）"] = "Option 1 · One-time install (recommended)  ·  方式一 · 一次性安装（推荐）",
        ["方式二 · 后台自动注入（不改动任何文件）"] =
            "Option 2 · Background auto-inject (touches no files)  ·  方式二 · 后台自动注入（不改动任何文件）",
        ["安装启动注入"] = "Install injection  ·  安装启动注入",
        ["卸载启动注入"] = "Uninstall injection  ·  卸载启动注入",
        ["打开安装目录"] = "Open install folder  ·  打开安装目录",
        ["———————— 或者 ————————"] = "————————  or  ————————",

        // 操作
        ["操作"] = "Actions  ·  操作",
        ["启动并注入"] = "Launch & inject  ·  启动并注入",
        ["注入到已运行的 PotPlayer"] = "Inject into running PotPlayer  ·  注入到已运行的 PotPlayer",
        ["后台监听"] = "Background watch  ·  后台监听",
        ["打开日志目录"] = "Open log folder  ·  打开日志目录",
        ["退出后台程序"] = "Quit background app  ·  退出后台程序",
        ["PotPlayer 未运行"] = "PotPlayer is not running  ·  PotPlayer 未运行",
        ["PotPlayer 运行中"] = "PotPlayer is running  ·  PotPlayer 运行中",

        // 规则
        ["规则"] = "Rules  ·  规则",
        ["元数据规则"] = "Metadata rules  ·  元数据规则",
        ["标签优先"] = "Tags first  ·  标签优先",
        ["仅文件名"] = "Filename only  ·  仅文件名",
        ["（当前为仅文件名模式）"] = "(currently filename-only mode)  ·  （当前为仅文件名模式）",
        ["标签优先，文件名兜底"] = "Tags first, filename as fallback  ·  标签优先，文件名兜底",
        ["恢复默认规则"] = "Restore defaults  ·  恢复默认规则",
        ["试一个文件名"] = "Test a filename  ·  试一个文件名",
        ["未指定"] = "not set  ·  未指定",
        ["检查中…"] = "checking…  ·  检查中…",
        ["运行记录"] = "Log  ·  运行记录",

        // 状态栏
        ["尚未定位 PotPlayer。"] = "PotPlayer is not located yet.  ·  尚未定位 PotPlayer。",
        ["状态：已安装（PotPlayer 每次启动都会自动加载本工具）"] =
            "State: installed (PotPlayer loads this tool on every start)  ·  状态：已安装",
        ["状态：未安装（当前依赖后台进程自动注入）"] =
            "State: not installed (relying on the background process)  ·  状态：未安装",
        ["状态：未安装（检测到备份文件，可随时还原）"] =
            "State: not installed (a backup file was found)  ·  状态：未安装（检测到备份文件）",
        ["，备份存在"] = ", backup present  ·  ，备份存在",
        ["（注意：主程序还留着旧补丁，请点「卸载」后手动还原）"] =
            " (note: the old exe patch is still applied — uninstall and restore it manually)",
        ["状态：启动注入已安装 —— 文件在 {AppPaths.InstallDir}，本程序目录随便挪"] =
            "State: injection installed — files live in {AppPaths.InstallDir}; this program folder can be moved freely",
        ["状态：注册表还指向 {stale} —— 点「卸载」再「安装」就能修正"] =
            "State: the registry still points at {stale} — click Uninstall then Install to fix it",
        ["状态：未安装 —— 但 IFEO 里已有别的 Debugger：{other}"] =
            "State: not installed — but IFEO already has another Debugger: {other}",

        // 日志 / 提示
        ["就绪。点「启动并注入」会自动拉起 PotPlayer 并完成注入。"] =
            "Ready. \"Launch & inject\" will start PotPlayer and inject into it.  ·  就绪",
        ["已启动 PotPlayer 并完成注入。"] = "PotPlayer started and injected.  ·  已启动 PotPlayer 并完成注入。",
        ["注入成功。"] = "Injected.  ·  注入成功。",
        ["已还原为原始文件。"] = "Restored to the original file.  ·  已还原为原始文件。",
        ["补丁已写入 PotPlayer。"] = "Patch written to PotPlayer.  ·  补丁已写入 PotPlayer。",
        ["还没定位到 PotPlayer。"] = "PotPlayer is not located yet.  ·  还没定位到 PotPlayer。",
        ["正在退出后台进程…"] = "Quitting the background process…  ·  正在退出后台进程…",
        ["请先完全退出 PotPlayer 再改它的主程序。"] =
            "Fully exit PotPlayer before patching its executable.  ·  请先完全退出 PotPlayer 再改它的主程序。",
        ["PotPlayer 已在运行。同一个 DLL 重复注入不会执行新代码，请先退出它。"] =
            "PotPlayer is already running — injecting the same DLL again runs no new code, please exit it first.",
        ["规则已保存。DLL 每 2 秒重读一次配置，不必重启播放器。"] =
            "Rules saved. The DLL re-reads the config every 2 s; no need to restart the player.  ·  规则已保存",
        ["操作未完成（详情见弹出的提示框）。"] =
            "The operation did not finish (see the message box).  ·  操作未完成",
        ["IFEO 启动注入已安装：以后每次启动 PotPlayer 都会自动注入，不需要任何常驻进程。"] =
            "Injection installed: PotPlayer gets injected on every start, with no resident process.",
        ["IFEO 启动注入已卸载：PotPlayer 恢复成原样。"] =
            "Injection removed: PotPlayer is back to stock.",
    };

    /// <summary>查表；没有收录就原样返回。</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!Map.TryGetValue(text, out var hit)) return text;
        // 带 {xxx} 占位符的是「模板」，由调用方自己替换
        return hit;
    }

    /// <summary>把控件树里所有文本控件的 Text 过一遍词典。</summary>
    public static void ApplyDeep(Control? root)
    {
        if (root is null) return;

        switch (root)
        {
            case Label or Button or CheckBox or RadioButton or GroupBox:
                root.Text = T(root.Text);
                break;
        }

        foreach (Control child in root.Controls) ApplyDeep(child);
    }
}
