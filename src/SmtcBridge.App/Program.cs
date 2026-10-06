namespace SmtcBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Loc.AutoApply();

        // 提权后的一次性操作：给 PotPlayer 打导入表补丁 / 还原
        // 新的安装方式：IFEO 启动注入（不改动 PotPlayer 的任何文件）
        if (args.Length >= 2 && args[0] == "--install-ifeo")
        {
            Environment.Exit(RunOneShot(() => IfeoInstaller.Install(args[1]),
                "Injection installed.  ·  IFEO 启动注入已安装",
                quiet: args.Contains("--quiet")));
            return;
        }
        if (args.Length >= 2 && args[0] == "--uninstall-ifeo")
        {
            Environment.Exit(RunOneShot(() => IfeoInstaller.Remove(args[1]),
                "Injection removed.  ·  IFEO 启动注入已卸载",
                quiet: args.Contains("--quiet")));
            return;
        }

        if (args.Length >= 2 && args[0] == "--patch")
        {
            Environment.Exit(RunOneShot(() => PePatcher.Install(args[1]),
                "Patch written.  ·  补丁已写入",
                quiet: args.Contains("--quiet")));
            return;
        }
        if (args.Length >= 2 && args[0] == "--restore")
        {
            Environment.Exit(RunOneShot(() => PePatcher.Restore(args[1]),
                "Restored to the original file.  ·  已还原为原始文件",
                quiet: args.Contains("--quiet")));
            return;
        }

        // .NET 9+ 的 WinForms 深色模式：让系统绘制的部分（滚动条、复选框等）也跟随深色。
        // 老系统上不可用，忽略即可。
        try { Application.SetColorMode(SystemColorMode.Dark); }
        catch { /* 不支持就退化为纯手工配色 */ }

        var config = BridgeConfig.Load();

        // 只有第一次使用（或组件缺失）才弹向导；之后一律静默驻留。
        var justRegistered = false;
        if (!config.Registered || !Injector.ComponentReady)
        {
            using var wizard = new FirstRunForm(config);
            if (wizard.ShowDialog() != DialogResult.OK) return;
            config = BridgeConfig.Load();
            justRegistered = true;
        }

        // 双击（无参数）默认把主界面开出来——用户手动运行就该看到东西。
        // 只有开机自启项才带 --silent，那种情况才真正静默。
        var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        // 只允许一个后台实例；已有实例在跑时，--ui 只是把它叫出来
        var mutex = SilentApp.TryAcquireSingleInstance();
        if (mutex is null)
        {
            // 手动运行（非 --silent）时，把已在跑的后台实例的主界面叫出来
            if (!silent) SilentApp.SignalExistingInstance();
            return;
        }

        Application.Run(new SilentApp(config, mutex, showWindow: !silent || justRegistered));
    }

    /// <summary>
    /// 弹窗文案双语：成功提示直接写成双语字面量；这里只兜异常消息
    /// （异常来自 IfeoInstaller / PePatcher，正文是中文）。
    /// 表里没有的原样显示，漏翻只会保持中文、不会出错。
    /// </summary>
    private static readonly (string Zh, string Both)[] DialogPhrases =
    {
        ("找不到 PotPlayer 主程序", "PotPlayer executable not found.  ·  找不到 PotPlayer 主程序"),
        ("找不到启动注入器", "Injector not found.  ·  找不到启动注入器"),
        ("找不到注入模块", "Injection module not found.  ·  找不到注入模块"),
        ("写入 HKLM 失败，请用管理员身份运行。",
         "Failed to write HKLM — please run as administrator.  ·  写入 HKLM 失败，请用管理员身份运行。"),
        ("这个程序名的 IFEO 项不是本工具写的，已跳过（避免误删别人的设置）。",
         "This program's IFEO entry was not created by this tool — skipped, nothing was deleted.  ·  该程序名的 IFEO 项不是本工具写的，已跳过。"),
        ("主程序补丁方式已停用",
         "The executable-patch install mode is disabled (PotPlayer verifies its own files; patching breaks startup). Use the IFEO injection instead.  ·  主程序补丁方式已停用（PotPlayer 会自校验，改文件会导致无法启动），请改用 IFEO 启动注入。"),
    };

    private static string Bilingual(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        foreach (var (zh, both) in DialogPhrases)
        {
            // 异常正文常带路径（"…：C:\…"），按前缀匹配后把尾巴接回去
            if (text.StartsWith(zh, StringComparison.Ordinal)) return both + text[zh.Length..];
        }
        return text;
    }

    private static int RunOneShot(Action action, string successText, bool quiet)
    {
        try
        {
            action();
            if (!quiet)
            {
                MessageBox.Show(Bilingual(successText), "PotPlayer SMTC Bridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (quiet) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(Bilingual(ex.Message), "Operation failed  ·  操作失败",
                                 MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
