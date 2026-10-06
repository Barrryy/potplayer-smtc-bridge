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
        ["欢迎使用"] = "Welcome  ·  欢迎使用",
        ["完成注入，使 PotPlayer 向 Windows 输出完整媒体信息"] =
            "Complete the setup so PotPlayer delivers full media metadata to Windows  ·  完成注入，使 PotPlayer 向 Windows 输出完整媒体信息",
        ["① 定位 PotPlayer"] = "① Locate PotPlayer  ·  ① 定位 PotPlayer",
        ["② 启动方式"] = "② Startup mode  ·  启动方式",
        ["主程序路径"] = "Player path  ·  主程序路径",
        ["自动检测"] = "Auto-detect  ·  自动检测",
        ["重新检测 PotPlayer"] = "Re-detect  ·  重新检测 PotPlayer",
        ["浏览…"] = "Browse…  ·  浏览…",
        ["选择 PotPlayer 主程序"] = "Select the PotPlayer executable  ·  选择 PotPlayer 主程序",
        ["继续注入"] = "Continue  ·  继续注入",
        ["保存并应用"] = "Save & apply  ·  保存并应用",
        ["退出"] = "Exit  ·  退出",
        ["路径无效"] = "Invalid path  ·  路径无效",
        ["指定的 PotPlayer 路径不存在。"] = "That PotPlayer path does not exist.  ·  指定的 PotPlayer 路径不存在。",
        ["未找到 PotPlayer，请手动浏览选择。"] =
            "PotPlayer was not found — please pick it manually.  ·  未找到 PotPlayer，请手动浏览选择。",
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
        ["文件名匹配范例"] = "Filename sample  ·  文件名匹配范例",
        ["未指定"] = "not set  ·  未指定",
        ["检查中…"] = "checking…  ·  检查中…",
        ["运行记录"] = "Log  ·  运行记录",

        // 状态栏
        ["尚未定位 PotPlayer。"] = "PotPlayer is not located yet.  ·  尚未定位 PotPlayer。",
        ["状态：已安装（PotPlayer 每次启动将自动加载）"] =
            "State: installed (PotPlayer loads this tool on every start)  ·  状态：已安装",
        ["状态：未安装（当前依赖后台进程自动注入）"] =
            "State: not installed (relying on the background process)  ·  状态：未安装",
        ["状态：未安装（检测到备份文件，可还原）"] =
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
        ["操作未完成。"] =
            "The operation did not finish.  ·  操作未完成。",
        ["IFEO 启动注入已安装：以后每次启动 PotPlayer 都会自动注入，不需要任何常驻进程。"] =
            "Injection installed: PotPlayer gets injected on every start, with no resident process.",
        ["IFEO 启动注入已卸载：PotPlayer 恢复成原样。"] =
            "Injection removed: PotPlayer is back to stock.",
    };

    /// <summary>
    /// 长段落按「前缀」识别：这些说明本来就是多段字面量拼起来的，逐字对齐容易失手；
    /// 而且这一版要收紧冗长说明，所以前缀命中就整条换成精简后的双语版本。
    /// </summary>
    private static readonly (string Prefix, string Text)[] Paragraphs =
    {
        ("在 Windows 里登记一条启动规则（IFEO），每次启动 PotPlayer 时",
         "Registers a Windows startup rule (IFEO): on every PotPlayer launch the injector loads this tool's module, then exits.\n"
         + "PotPlayer's own files are never modified.\n"
         + "在 Windows 中登记一条 IFEO 启动规则：每次启动 PotPlayer 时加载本注入模块后即退出，不改动 PotPlayer 的任何文件。"),
        ("点「安装启动注入」后",
         "Once installed, PotPlayer is injected on every launch: no files touched, no resident process, no startup entry.\n"
         + "点「安装启动注入」后，每次启动 PotPlayer 都会自动注入：不改动它的任何文件，不需要常驻进程，也不需要开机自启。"),
        ("② 注入方式",
         "② Injection mode  ·  ② 注入方式"),
        ("每次启动 PotPlayer 时自动注入，不碰它的任何文件，也不需要常驻进程。",
         "Injects on every PotPlayer launch without touching its files and without a resident process.\n"
         + "每次启动 PotPlayer 时自动注入，不改动其文件，也无需常驻进程。"),
        ("备选：用后台进程自动注入（需要开机自启）",
         "Alternative: background auto-inject (requires auto-start)\n"
         + "备选：后台进程自动注入（需开机自启）"),
        ("程序在后台静默待命：没有窗口、没有托盘图标。每次 PotPlayer 启动时自动注入。",
         "Runs silently in the background: no window, no tray icon.\n"
         + "后台静默运行：无窗口、无托盘图标，PotPlayer 每次启动时自动注入。"),
        ("代价是常驻一个隐藏进程、并且需要开机自启。",
         "Trade-off: one hidden resident process plus a startup entry.\n"
         + "代价：常驻一个隐藏进程，并需要开机自启。"),
        ("PotPlayer 默认只把文件名交给 Windows",
         "PotPlayer only hands Windows the file name by default; this tool fills in the rest of the metadata,\n"
         + "so the system media panel, Discord and lyric tools read the correct info.\n"
         + "PotPlayer 默认只把文件名交给 Windows；本工具补齐其余字段，供系统媒体面板、Discord 与歌词工具读取。"),
        ("这个工具会补齐这些字段",
         "This tool fills them in, so the system media panel, Discord and lyric tools read the correct metadata.\n"
         + "本工具补齐这些字段，供系统媒体面板、Discord、歌词工具读取。"),
        ("文件名伪正则：%title% %artist% %album% %albumArtist% %track% 是占位符，",
         "Filename pattern: %title% %artist% %album% %albumArtist% %track% are placeholders; every other character matches literally (%% = a literal percent sign).\n"
         + "文件名伪正则：上述为占位符，其余字符按字面量匹配，%% 为字面量百分号。"),
        ("匹配时按顺序查找字面量，它之前的文本归给上一个占位符，末尾占位符吃掉剩余全部。",
         "Literals are searched in order; the text before a literal belongs to the previous placeholder, and the last placeholder takes the remainder.\n"
         + "按顺序查找字面量：其之前的文本归属上一个占位符，末尾占位符取剩余全部。"),
        ("其余字符按字面量匹配，%% 表示一个字面量百分号。",
         "Every other character matches literally; %% is a literal percent sign.\n"
         + "其余字符按字面量匹配，%% 为字面量百分号。"),
        ("把完整的媒体信息交给 Windows",
         "Delivers the complete media metadata to Windows, for the system media panel, Discord and lyric tools.\n"
         + "向 Windows 提交完整媒体信息，供系统媒体面板、Discord 与歌词工具读取。"),
        ("本程序目录随便挪", ""),   // 动态串由 Loc.Fmt 处理，这里只是兜底
        ("装一次之后：无后台进程、无开机启动项、重启照样生效、PotPlayer 自己更新也不用重装。",
         "After one install: no background process, no startup entry, survives reboots and PotPlayer updates.\n"
         + "安装一次后：无后台进程、无开机启动项，重启与 PotPlayer 更新后均继续生效。"),
    };

    /// <summary>查表（先精确、再长段落前缀）；都没有就原样返回。</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (Map.TryGetValue(text, out var hit)) return hit;
        foreach (var (prefix, replacement) in Paragraphs)
        {
            if (prefix.Length > 0 && replacement.Length > 0 &&
                text.StartsWith(prefix, StringComparison.Ordinal))
                return replacement;
        }
        return text;
    }

    /// <summary>带占位符的模板：Loc.Fmt(中文模板, ("{dir}", 实际值))。</summary>
    public static string Fmt(string template, params (string Key, string Value)[] args)
    {
        var text = T(template);
        foreach (var (key, value) in args) text = text.Replace(key, value);
        return text;
    }

    /// <summary>
    /// 全局兜底：启动时挂到 Application.Idle 上，每次空闲把「当前所有已打开窗体」的控件树过一遍。
    /// 直接挂在窗体上的标题、按钮（没走 Card.AddRow 的那些）也能覆盖；
    /// 已经翻译过的文本不再是词典键，重复扫描是空操作。
    /// </summary>
    public static void AutoApply()
    {
        Application.Idle += (_, _) =>
        {
            foreach (Form form in Application.OpenForms) ApplyDeep(form);
        };
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
