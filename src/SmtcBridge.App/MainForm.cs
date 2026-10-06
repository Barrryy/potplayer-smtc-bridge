using System.Diagnostics;

namespace SmtcBridge;

internal sealed class MainForm : Form
{
    private readonly BridgeConfig _config;
    private readonly Action? _onExit;
    private readonly FlowLayoutPanel _stack = new();

    private readonly Label _pathValue = Theme.Label("未指定", Theme.TextDim);
    private readonly Label _stateValue = Theme.Label("检查中…", Theme.TextDim);
    private readonly CheckBox _tagFirst = Theme.CheckBox("标签优先，文件名兜底", true);
    private readonly DarkInput _pattern = new();
    private readonly DarkInput _sample = new();
    private readonly Label _preview = Theme.Label("", Theme.TextDim, Theme.FontSmall, 660);
    private readonly DarkInput _log = new(multiline: true, height: 132);
    private readonly Label _patchState = Theme.Label("", Theme.TextDim, Theme.FontSmall, 660);

    private bool _loading;
    private bool _allowClose;

    public MainForm(BridgeConfig config, Action? onExit = null)
    {
        _config = config;
        _onExit = onExit;

        Text = "PotPlayer SMTC Bridge";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 850);
        MinimumSize = new Size(700, 560);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.FontBase;

        _stack.Dock = DockStyle.Fill;
        _stack.FlowDirection = FlowDirection.TopDown;
        _stack.WrapContents = false;
        _stack.AutoScroll = true;
        _stack.BackColor = Theme.Window;
        _stack.Padding = new Padding(18, 16, 18, 16);
        _stack.Resize += (_, _) => ResizeCards();

        _stack.Controls.Add(BuildHeader());
        _stack.Controls.Add(BuildStatusCard());
        _stack.Controls.Add(BuildActivationCard());
        _stack.Controls.Add(BuildRulesCard());
        _stack.Controls.Add(BuildLogCard());

        Controls.Add(_stack);

        _loading = true;
        _tagFirst.Checked = _config.TagFirst;
        _pattern.Text = _config.NamePattern;
        _sample.Text = "周杰伦 - 夜曲.mp3";
        _loading = false;

        UpdatePreview();
        RefreshStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ResizeCards();
        AppendLog("就绪。点「启动并注入」会自动拉起 PotPlayer 并完成注入。");
    }

    /// <summary>
    /// 关闭窗口只是收起，后台进程继续待命——这是「无感」的关键。
    /// 如果本实例没有后台职能（onExit 为空），关闭就是退出。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && _onExit is not null && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>真正退出时用这个，绕过「收起」逻辑。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ResizeCards()
    {
        var width = _stack.ClientSize.Width - _stack.Padding.Horizontal - 4;
        if (width < 200) return;
        foreach (Control control in _stack.Controls)
        {
            // 卡片是 AutoSize 的（高度跟随内容），如果不把最小宽度钉住，
            // 它会被内容宽度拉窄，几张卡片就宽窄不一了。
            control.MinimumSize = new Size(width, 0);
            control.Width = width;
        }
    }

    // ---------------------------------------------------------------- 顶部

    private static Control BuildHeader()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Window,
            Margin = new Padding(2, 0, 0, 14),
        };
        panel.Controls.Add(Theme.Label("PotPlayer SMTC Bridge", Theme.Text, Theme.FontTitle));
        panel.Controls.Add(Theme.Label(
            "把完整的媒体信息交给 Windows，供系统媒体面板、Discord、歌词工具读取",
            Theme.TextDim, Theme.FontSmall));
        return panel;
    }

    // ---------------------------------------------------------------- 状态

    private Card BuildStatusCard()
    {
        var card = new Card("状态");
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var pathCaption = Theme.Label("PotPlayer", Theme.TextDim);
        pathCaption.Margin = new Padding(0, 0, 16, 0);
        grid.Controls.Add(pathCaption, 0, 0);
        grid.Controls.Add(_pathValue, 1, 0);

        var stateCaption = Theme.Label("当前状态", Theme.TextDim);
        stateCaption.Margin = new Padding(0, 6, 16, 0);
        _stateValue.Margin = new Padding(0, 6, 0, 0);
        grid.Controls.Add(stateCaption, 0, 1);
        grid.Controls.Add(_stateValue, 1, 1);

        card.AddRow(grid, 0);

        var row = Theme.Row(Theme.Surface);
        var refresh = Theme.GhostButton("刷新状态");
        refresh.Click += (_, _) => RefreshStatus();
        var change = Theme.GhostButton("重新检测 PotPlayer");
        change.Click += (_, _) => DetectAndStore();
        row.Controls.Add(refresh);
        row.Controls.Add(change);
        card.AddRow(row, 12);
        return card;
    }

    private void RefreshStatus()
    {
        var path = _config.PotPlayerPath;
        _pathValue.Text = string.IsNullOrEmpty(path) ? "未指定" : path;
        _pathValue.ForeColor = string.IsNullOrEmpty(path) ? Theme.Warn : Theme.Text;

        var running = Injector.IsPotPlayerRunning();
        var ready = Injector.ComponentReady;
        _stateValue.Text = $"{(ready ? "组件就位" : "组件缺失")} · "
                         + $"{(running ? "PotPlayer 运行中" : "PotPlayer 未运行")} · "
                         + $"{(_config.TagFirst ? "标签优先" : "仅文件名")}";
        _stateValue.ForeColor = ready ? Theme.Good : Theme.Bad;
    }

    private void DetectAndStore()
    {
        var found = Injector.DetectPotPlayer();
        if (found is null)
        {
            AppendLog("没找到 PotPlayer，请手动指定（首次向导里也能改）。");
            return;
        }
        _config.PotPlayerPath = found;
        _config.Save();
        AppendLog($"已定位 PotPlayer：{found}");
        RefreshStatus();
    }

    // ---------------------------------------------------------------- 规则

    private Card BuildRulesCard()
    {
        var card = new Card("元数据规则");

        _tagFirst.CheckedChanged += (_, _) =>
        {
            if (!_loading) UpdatePreview();
        };
        card.AddRow(_tagFirst, 0);

        card.AddRow(Theme.Label(
            "文件名伪正则：%title% %artist% %album% %albumArtist% %track% 是占位符，"
            + "其余字符按字面量匹配，%% 表示一个字面量百分号。\n"
            + "匹配时按顺序查找字面量，它之前的文本归给上一个占位符，末尾占位符吃掉剩余全部。",
            Theme.TextDim, Theme.FontSmall, 660), 8);

        card.AddRow(Field("规则", _pattern), 12);
        _pattern.Box.TextChanged += (_, _) =>
        {
            if (!_loading) UpdatePreview();
        };

        card.AddRow(Field("试一个文件名", _sample), 10);
        _sample.Box.TextChanged += (_, _) =>
        {
            if (!_loading) UpdatePreview();
        };

        card.AddRow(_preview, 10);

        var buttons = Theme.Row(Theme.Surface);
        var reset = Theme.GhostButton("恢复默认规则");
        reset.Click += (_, _) =>
        {
            _loading = true;
            _pattern.Text = BridgeConfig.DefaultPattern;
            _tagFirst.Checked = true;
            _loading = false;
            UpdatePreview();
        };
        var save = Theme.PrimaryButton("保存并应用");
        save.Click += (_, _) => SaveRules();
        buttons.Controls.Add(reset);
        buttons.Controls.Add(save);
        card.AddRow(buttons, 14);

        return card;
    }

    private static Control Field(string label, DarkInput input)
    {
        // 手工定位：嵌套 TableLayoutPanel 的 AutoSize 在多层嵌套时不可靠，
        // 会出现输入框被拉高的问题，这里用固定高度 + Resize 里同步宽度。
        const int captionWidth = 104;
        var row = new Panel
        {
            Height = input.Height,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            MinimumSize = new Size(320, input.Height),
            // TableLayoutPanel 里默认按 Top|Left 锚定，不横向拉伸，
            // 必须显式声明左右锚定，输入框才能跟着卡片宽度走。
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

        var caption = Theme.Label(label, Theme.TextDim);
        caption.BackColor = Theme.Surface;
        caption.Location = new Point(0, Math.Max(0, (input.Height - caption.Height) / 2));
        row.Controls.Add(caption);

        input.Location = new Point(captionWidth, 0);
        row.Controls.Add(input);
        row.Resize += (_, _) =>
        {
            var width = row.Width - captionWidth;
            if (width > 40) input.Width = width;
        };
        return row;
    }

    private void UpdatePreview()
    {
        var result = MetaRules.Apply(_sample.Text, _pattern.Text);
        _preview.ForeColor = result.Matched ? Theme.Good : Theme.Warn;
        _preview.Text = "▸ " + result.Note + (_tagFirst.Checked ? "" : "（当前为仅文件名模式）");
        _preview.BackColor = Theme.Surface;
    }

    private void SaveRules()
    {
        _config.TagFirst = _tagFirst.Checked;
        _config.NamePattern = string.IsNullOrWhiteSpace(_pattern.Text)
            ? BridgeConfig.DefaultPattern
            : _pattern.Text.Trim();
        _config.Save();
        RefreshStatus();
        AppendLog("规则已保存。DLL 每 2 秒重读一次配置，不必重启播放器。");
    }

    // ---------------------------------------------------------------- 操作

    private Card BuildActivationCard()
    {
        var card = new Card("怎么让它生效（二选一）");

        // ---------------- 方式一：一次性安装
        card.AddRow(Theme.Label("方式一 · 一次性安装（推荐）", Theme.Accent, Theme.FontBold), 0);
        card.AddRow(Theme.Label(
            "在 Windows 里登记一条启动规则（IFEO），每次启动 PotPlayer 时\n"
            + "先拉起本工具的注入器，把注入模块塞进进程后立刻退出。\n"
            + "不碰 PotPlayer 任何文件（它的主程序带 Themida 壳 + 签名自校验，改一个字节就起不来）。\n"
            + "装一次之后：无后台进程、无开机启动项、重启照样生效、PotPlayer 自己更新也不用重装。",
            Theme.TextDim, Theme.FontSmall, 660), 6);

        _patchState.BackColor = Theme.Surface;
        card.AddRow(_patchState, 8);

        var installRow = Theme.Row(Theme.Surface);
        var install = Theme.PrimaryButton("安装启动注入");
        install.Click += (_, _) => DoIfeo(install: true);
        var restore = Theme.GhostButton("卸载启动注入");
        restore.Click += (_, _) => DoIfeo(install: false);
        installRow.Controls.Add(install);
        installRow.Controls.Add(restore);
        var openDir = Theme.GhostButton("打开安装目录");
        openDir.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.InstallDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.InstallDir,
                UseShellExecute = true,
            });
        };
        installRow.Controls.Add(openDir);
        card.AddRow(installRow, 8);

        card.AddRow(Theme.Label("———————— 或者 ————————", Theme.TextDim, Theme.FontSmall), 16);

        // ---------------- 方式二：后台自动注入
        card.AddRow(Theme.Label("方式二 · 后台自动注入（不改动任何文件）", Theme.Text, Theme.FontBold), 12);
        card.AddRow(Theme.Label(
            "程序在后台静默待命：没有窗口、没有托盘图标。每次 PotPlayer 启动时自动注入。\n"
            + "代价是常驻一个隐藏进程、并且需要开机自启。",
            Theme.TextDim, Theme.FontSmall, 660), 6);

        var actRow = Theme.Row(Theme.Surface);

        var launch = Theme.GhostButton("启动并注入");
        launch.Click += (_, _) => LaunchAndInject();
        var inject = Theme.GhostButton("注入到已运行的");
        inject.Click += (_, _) => InjectRunning();
        var watch = Theme.GhostButton("后台监听");
        watch.Click += (_, _) =>
        {
            var process = Injector.StartWatch();
            AppendLog(process is null
                ? "启动监听失败。"
                : $"监听已启动（PID {process.Id}）：PotPlayer 一出现就会自动注入。");
        };
        var openLog = Theme.GhostButton("打开日志目录");
        openLog.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true });
        };

        actRow.Controls.Add(launch);
        actRow.Controls.Add(inject);
        actRow.Controls.Add(watch);
        actRow.Controls.Add(openLog);

        if (_onExit is not null)
        {
            var exit = Theme.GhostButton("退出后台程序");
            exit.ForeColor = Theme.Bad;
            exit.Click += (_, _) =>
            {
                AppendLog("正在退出后台进程…");
                _onExit();
            };
            actRow.Controls.Add(exit);
        }
        card.AddRow(actRow, 8);

        return card;
    }

    private void RefreshPatchState()
    {
        var path = _config.PotPlayerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _patchState.Text = "尚未定位 PotPlayer。";
            _patchState.ForeColor = Theme.Warn;
            return;
        }

        // 新安装方式：IFEO 启动注入（不改动 PotPlayer 任何文件）
        if (IfeoInstaller.IsInstalledByUs(path))
        {
            _patchState.Text = $"状态：启动注入已安装 —— 文件在 {AppPaths.InstallDir}，本程序目录随便挪";
            _patchState.ForeColor = Theme.Good;
            if (PePatcher.IsPatched(path))
            {
                _patchState.Text += "（注意：主程序还留着旧补丁，请点「卸载」后手动还原）";
                _patchState.ForeColor = Theme.Warn;
            }
            return;
        }
        if (IfeoInstaller.StaleDebugger(path) is { Length: > 0 } stale)
        {
            _patchState.Text = $"状态：注册表还指向 {stale} —— 点「卸载」再「安装」就能修正";
            _patchState.ForeColor = Theme.Warn;
            return;
        }
        if (IfeoInstaller.ForeignDebugger(path) is { Length: > 0 } other)
        {
            _patchState.Text = $"状态：未安装 —— 但 IFEO 里已有别的 Debugger：{other}";
            _patchState.ForeColor = Theme.Warn;
            return;
        }

        if (PePatcher.IsPatched(path))
        {
            _patchState.Text = "状态：已安装（PotPlayer 每次启动都会自动加载本工具）"
                             + (PePatcher.HasBackup(path) ? "，备份存在" : "");
            _patchState.ForeColor = Theme.Good;
        }
        else if (PePatcher.HasBackup(path))
        {
            _patchState.Text = "状态：未安装（检测到备份文件，可随时还原）";
            _patchState.ForeColor = Theme.TextDim;
        }
        else
        {
            _patchState.Text = "状态：未安装（当前依赖后台进程自动注入）";
            _patchState.ForeColor = Theme.TextDim;
        }
    }

    /// <summary>安装/卸载 IFEO 启动注入（写 HKLM，需要管理员，走 UAC 提权）。</summary>
    private void DoIfeo(bool install)
    {
        var path = _config.PotPlayerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppendLog("还没定位到 PotPlayer。");
            return;
        }

        var verb = install ? "--install-ifeo" : "--uninstall-ifeo";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.SelfExe,
                Arguments = $"{verb} \"{path}\"",
                UseShellExecute = true,
                Verb = "runas",   // 只有这一步需要管理员
            });
            process?.WaitForExit();
            AppendLog(process?.ExitCode == 0
                ? (install
                    ? "IFEO 启动注入已安装：以后每次启动 PotPlayer 都会自动注入，不需要任何常驻进程。"
                    : "IFEO 启动注入已卸载：PotPlayer 恢复成原样。")
                : "操作未完成（详情见弹出的提示框）。");
        }
        catch (Exception ex)
        {
            AppendLog("操作失败：" + ex.Message);
        }
        RefreshPatchState();
    }

    private void DoPatch(bool install)
    {
        var path = _config.PotPlayerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppendLog("还没定位到 PotPlayer。");
            return;
        }
        if (Injector.IsPotPlayerRunning())
        {
            AppendLog("请先完全退出 PotPlayer 再改它的主程序。");
            return;
        }

        var verb = install ? "--patch" : "--restore";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.SelfExe,
                Arguments = $"{verb} \"{path}\"",
                UseShellExecute = true,
                Verb = "runas",   // 写 Program Files 需要管理员
            });
            process?.WaitForExit();
            AppendLog(process?.ExitCode == 0
                ? (install ? "补丁已写入 PotPlayer。" : "已还原为原始文件。")
                : "操作未完成（详情见弹出的提示框）。");
        }
        catch (Exception ex)
        {
            AppendLog("操作失败：" + ex.Message);
        }
        RefreshPatchState();
    }

    private Card BuildActionsCard()
    {
        var card = new Card("操作");
        var row = Theme.Row(Theme.Surface);

        var launch = Theme.PrimaryButton("启动并注入");
        launch.Click += (_, _) => LaunchAndInject();

        var inject = Theme.GhostButton("注入到已运行的 PotPlayer");
        inject.Click += (_, _) => InjectRunning();

        var watch = Theme.GhostButton("后台监听");
        watch.Click += (_, _) =>
        {
            var process = Injector.StartWatch();
            AppendLog(process is null
                ? "启动监听失败。"
                : $"监听已启动（PID {process.Id}）：PotPlayer 一出现就会自动注入。");
        };

        var openLog = Theme.GhostButton("打开日志目录");
        openLog.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDir,
                UseShellExecute = true,
            });
        };

        row.Controls.Add(launch);
        row.Controls.Add(inject);
        row.Controls.Add(watch);
        row.Controls.Add(openLog);

        if (_onExit is not null)
        {
            var exit = Theme.GhostButton("退出后台程序");
            exit.ForeColor = Theme.Bad;
            exit.Click += (_, _) =>
            {
                AppendLog("正在退出后台进程…");
                _onExit();
            };
            row.Controls.Add(exit);
        }

        card.AddRow(row, 0);
        return card;
    }

    private void LaunchAndInject()
    {
        var path = _config.PotPlayerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppendLog("还没定位到 PotPlayer，请先点「重新检测 PotPlayer」。");
            return;
        }
        if (Injector.IsPotPlayerRunning())
        {
            AppendLog("PotPlayer 已在运行。同一个 DLL 重复注入不会执行新代码，请先退出它。");
            return;
        }
        var (ok, output) = Injector.RunLoader("--launch", path);
        AppendLog(ok ? "已启动 PotPlayer 并完成注入。" : "启动注入失败：");
        AppendLog(output);
        RefreshStatus();
        RefreshPatchState();
    }

    private void InjectRunning()
    {
        var (ok, output) = Injector.RunLoader();
        AppendLog(ok ? "注入成功。" : "注入失败：");
        AppendLog(output);
        if (ok) AppendLog("提示：若 PotPlayer 之前就在运行，请重启它，否则新代码不会生效。");
    }

    // ---------------------------------------------------------------- 日志

    private Card BuildLogCard()
    {
        var card = new Card("运行记录");
        _log.Dock = DockStyle.Fill;
        card.AddRow(_log, 0);
        return card;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            _log.Box.AppendText($"[{DateTime.Now:HH:mm:ss}] {trimmed}{Environment.NewLine}");
        }
        _log.Box.SelectionStart = _log.Box.TextLength;
        _log.Box.ScrollToCaret();
    }
}
