namespace SmtcBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 提权后的一次性操作：给 PotPlayer 打导入表补丁 / 还原
        // 新的安装方式：IFEO 启动注入（不改动 PotPlayer 的任何文件）
        if (args.Length >= 2 && args[0] == "--install-ifeo")
        {
            Environment.Exit(RunOneShot(() => IfeoInstaller.Install(args[1]), "IFEO 启动注入已安装",
                quiet: args.Contains("--quiet")));
            return;
        }
        if (args.Length >= 2 && args[0] == "--uninstall-ifeo")
        {
            Environment.Exit(RunOneShot(() => IfeoInstaller.Remove(args[1]), "IFEO 启动注入已卸载",
                quiet: args.Contains("--quiet")));
            return;
        }

        if (args.Length >= 2 && args[0] == "--patch")
        {
            Environment.Exit(RunOneShot(() => PePatcher.Install(args[1]), "补丁已写入",
                quiet: args.Contains("--quiet")));
            return;
        }
        if (args.Length >= 2 && args[0] == "--restore")
        {
            Environment.Exit(RunOneShot(() => PePatcher.Restore(args[1]), "已还原为原始文件",
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

    private static int RunOneShot(Action action, string successText, bool quiet)
    {
        try
        {
            action();
            if (!quiet)
            {
                MessageBox.Show(successText, "PotPlayer SMTC Bridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (quiet) Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
