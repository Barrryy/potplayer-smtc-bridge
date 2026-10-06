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
        // 内容列固定 660，再加卡片与滚动条留白；高度留足（下方还有日志框）
        ClientSize = new Size(1200, 910);
        MinimumSize = new Size(900, 640);
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
        _sample.Text = "洛天依 - 上山岗.mp3";
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
        AppendLog("就绪。点击「启动并注入」会自动拉起 PotPlayer 并完成注入。");
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
            AppendLog("未找到 PotPlayer，请手动指定。");
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
            + "匹配时按顺序查找字面量，它之前的文本归给上一个占位符，末尾占位符匹配剩余全部。",
            Theme.TextDim, Theme.FontSmall, 660), 8);

        card.AddRow(Field("规则", _pattern), 12);
        _pattern.Box.TextChanged += (_, _) =>
        {
            if (!_loading) UpdatePreview();
        };

        card.AddRow(Field("文件名匹配范例", _sample), 10);
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
        // 标题现在是双语的（Filename sample · 文件名匹配范例），宽度按实际文本量出来；
        // 硬编码会让加长后的标题压到输入框上（和首次向导里同一个毛病）。
        var caption = Theme.Label(Loc.T(label), Theme.TextDim);
        caption.BackColor = Theme.Surface;
        var captionWidth = Math.Max(120,
            TextRenderer.MeasureText(caption.Text, caption.Font).Width + 14);

        var row = new Panel
        {
            Height = input.Height,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            MinimumSize = new Size(320, input.Height),
            // 与本窗口其余内容同宽（660），只往左锚定：
            // 左右锚定会让这一行超出内容列，输入框就甩到卡片外面去了。
            Width = 660,
            MaximumSize = new Size(660, input.Height),
            Anchor = AnchorStyles.Left,
        };

        caption.Location = new Point(0, Math.Max(0, (input.Height - caption.Height) / 2));
        row.Controls.Add(caption);

        input.Location = new Point(captionWidth, 0);
        input.Width = Math.Max(40, Math.Min(row.Width, 660) - captionWidth);
        row.Controls.Add(input);
        row.Resize += (_, _) =>
        {
            var width = Math.Min(row.Width, 660) - captionWidth;
            if (width > 40) input.Width = width;
        };
        return row;
    }

    private void UpdatePreview()
    {
        var result = MetaRules.Apply(_sample.Text, _pattern.Text);
        _preview.ForeColor = result.Matched ? Theme.Good : Theme.Warn;

        // 预览串：字段分隔符统一用「：」，标签英文在前；先换长标签，否则「专辑歌手」
        // 会被「专辑」先吃掉。
        var note = result.Note
            .Replace("匹配成功：", "Matched · 匹配成功：")
            .Replace("专辑歌手=", "Album artist：")
            .Replace("标题=", "Title：")
            .Replace("歌手=", "Artist：")
            .Replace("专辑=", "Album：")
            .Replace("音轨=", "Track：")
            .Replace("文件名为空", "Filename is empty · 文件名为空")
            .Replace("规则中没有占位符，整体作为标题",
                     "No placeholder in the pattern; the whole name is used as the title · 规则中没有占位符，整体作为标题")
            .Replace("规则未匹配，回退为「整体作为标题」",
                     "Pattern did not match; the whole name is used as the title · 规则未匹配，回退为「整体作为标题」")
            .Replace("规则未捕获到任何字段，回退为「整体作为标题」",
                     "No field captured; the whole name is used as the title · 规则未捕获到任何字段，回退为「整体作为标题」");

        _preview.Text = "▸ " + note + (_tagFirst.Checked ? "" : "（当前为仅文件名模式）");
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
            + "不碰 PotPlayer 任何文件。\n"
            + "装一次之后：无后台进程、无开机启动项；重启、PotPlayer更新仍有效。",
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
            "程序在后台静默待命：没有窗口、托盘图标。每次 PotPlayer 启动时自动注入。\n"
            + "代价是常驻一个隐藏进程、并且需要开机自启。",
            Theme.TextDim, Theme.FontSmall, 660), 6);

        var launch = Theme.GhostButton("启动并注入");
        launch.Click += (_, _) => LaunchAndInject();

        var inject = Theme.GhostButton("注入到运行中的 PotPlayer");
        inject.Click += (_, _) => InjectRunning();

        var watch = Theme.GhostButton("后台监听");
        watch.Click += (_, _) =>
        {
            var process = Injector.StartWatch();
            AppendLog(process is null
                ? "启动监听失败。"
                : $"监听已启动（PID {process.Id}）：PotPlayer 启动时自动注入。");
        };

        var openLog = Theme.GhostButton("打开日志目录");
        openLog.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDir,
                UseShellExecute = true
            });
        };

        // ==================== 第一排 ====================

        var row1 = new Panel
        {
            Width = 660,
            Height = 44,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        launch.Location = new Point(0, 0);
        inject.Location = new Point(launch.Right + 8, 0);
        watch.Location = new Point(inject.Right + 8, 0);

        row1.Controls.Add(launch);
        row1.Controls.Add(inject);
        row1.Controls.Add(watch);

        // ==================== 第二排 ====================

        var row2 = new Panel
        {
            Width = 660,
            Height = 44,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        openLog.Location = new Point(0, 0);

        row2.Controls.Add(openLog);

        if (_onExit is not null)
        {
            var exit = Theme.GhostButton("退出后台程序");
            exit.ForeColor = Theme.Bad;
            exit.Location = new Point(openLog.Right + 8, 0);

            exit.Click += (_, _) =>
            {
                AppendLog("正在退出后台进程…");
                _onExit();
            };

            row2.Controls.Add(exit);
        }

        // ==================== 两排容器 ====================

        var grid = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Width = 660,
            Height = 96,
            AutoSize = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 660));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        grid.Controls.Add(row1, 0, 0);
        grid.Controls.Add(row2, 0, 1);

        card.AddRow(grid, 8);

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
            _patchState.Text =
                $"State: injection installed — files live in {AppPaths.InstallDir}; "
                + "this program folder can be moved or deleted freely.\n"
                + $"状态：启动注入已安装 —— 文件位于 {AppPaths.InstallDir}，本程序目录可移动或删除。\n" + "若需卸载本服务，请保留程序并按程序说明卸载。";

            if (IfeoInstaller.MissingFiles() is { Length: > 0 } missing)
            {
                _patchState.Text += "\nIncomplete install — missing in the install folder: " + missing
                                  + "\n安装不完整 —— 安装目录里缺失文件：" + missing;
                _patchState.ForeColor = Theme.Warn;
                return;
            }

            _patchState.ForeColor = Theme.Good;
            if (PePatcher.IsPatched(path))
            {
                _patchState.Text += "\nNote: the old executable patch is still applied — uninstall it and restore manually."
                                  + "\n注意：主程序仍残留旧补丁，请卸载后手动还原。";
                _patchState.ForeColor = Theme.Warn;
            }
            return;
        }
        if (IfeoInstaller.StaleDebugger(path) is { Length: > 0 } stale)
        {
            _patchState.Text =
                $"State: the registry still points at {stale} — click Uninstall, then Install, to fix it.\n"
                + $"状态：注册表仍指向 {stale} —— 点击「卸载」、「安装」即可修正。";
            _patchState.ForeColor = Theme.Warn;
            return;
        }
        if (IfeoInstaller.ForeignDebugger(path) is { Length: > 0 } other)
        {
            _patchState.Text =
                $"State: not installed — but IFEO already carries another Debugger: {other}\n"
                + $"状态：未安装 —— 但 IFEO 中已有其他 Debugger：{other}";
            _patchState.ForeColor = Theme.Warn;
            return;
        }

        if (PePatcher.IsPatched(path))
        {
            _patchState.Text = "状态：已安装（PotPlayer 每次启动将自动加载）"
                             + (PePatcher.HasBackup(path) ? "，备份存在" : "");
            _patchState.ForeColor = Theme.Good;
        }
        else if (PePatcher.HasBackup(path))
        {
            _patchState.Text = "状态：未安装（检测到备份文件，可还原）";
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
            AppendLog("未定位到 PotPlayer。");
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
                    ? "IFEO 启动注入已安装：启动 PotPlayer 时会自动注入，无任何常驻进程。"
                    : "IFEO 启动注入已卸载：PotPlayer 已复原。")
                : "操作未完成。");
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
            AppendLog("未定位到 PotPlayer。");
            return;
        }
        if (Injector.IsPotPlayerRunning())
        {
            AppendLog("请完全退出 PotPlayer 后，再进行修改。");
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
                : "操作未完成。");
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
        // 这一排用普通 Panel + 手工定位：FlowLayoutPanel 在控件加入那一刻就定好位置，
        // 双语翻译让按钮变宽后它不会重排，相邻按钮就会互相压住（重叠的根因）。
        var row = new Panel { Height = 44, BackColor = Theme.Surface, Margin = new Padding(0) };

        // 按钮文字先翻译再交给容器：按钮是 AutoSize 的，容器按「加入那一刻的文字宽度」
        // 排位置；如果等加入之后再翻译，位置会按短中文排、按钮却变宽 -> 互相压住。
        var launch = Theme.PrimaryButton(Loc.T("启动并注入"));
        launch.Click += (_, _) => LaunchAndInject();

        var inject = Theme.GhostButton(Loc.T("注入到已运行的 PotPlayer"));
        inject.Click += (_, _) => InjectRunning();

        var watch = Theme.GhostButton(Loc.T("后台监听"));
        watch.Click += (_, _) =>
        {
            var process = Injector.StartWatch();
            AppendLog(process is null
                ? "启动监听失败。"
                : $"监听已启动（PID {process.Id}）：PotPlayer 启动时将自动注入。");
        };

        var openLog = Theme.GhostButton(Loc.T("打开日志目录"));
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

        // 第二排：打开日志目录 / 退出后台程序
        var row2 = new Panel { Height = 44, BackColor = Theme.Surface, Margin = new Padding(0) };
        row2.Controls.Add(openLog);
        if (_onExit is not null)
        {
            var exit = Theme.GhostButton(Loc.T("退出后台程序"));
            exit.ForeColor = Theme.Bad;
            exit.Click += (_, _) =>
            {
                AppendLog("正在退出后台进程…");
                _onExit();
            };
            row2.Controls.Add(exit);
        }

        // 两排各占一行：用显式单元格的 TableLayoutPanel。
        // 不能靠 FlowLayoutPanel 换行 —— AutoSize 且没有宽度约束时它会一直往右长，
        // WrapContents / SetFlowBreak 都不会生效（这三次试错都是这个原因）。
        foreach (var line in new[] { row, row2 })
        {
            // 每排自己按内容排：容器不设死宽度，里面的按钮按文字自适应、留固定间距，
            // 这样双语长文案（"Inject into running PotPlayer · 注入到运行中的 PotPlayer"）
            // 不会压到旁边的按钮上。
            line.AutoSize = false;
            line.MinimumSize = new Size(0, 44);
            line.Anchor = AnchorStyles.Left;
            var nextX = 0;
            foreach (Control button in line.Controls.Cast<Control>().ToArray())
            {
                // 显式按文字量宽度：AutoSize 在 FlowLayoutPanel 里不一定被采纳
                // （实测按钮仍按旧宽度排列，于是互相压住）。
                button.AutoSize = false;
                button.Size = new Size(
                    TextRenderer.MeasureText(Loc.T(button.Text), button.Font).Width + 36, 44);
                button.Margin = new Padding(0, 0, 8, 0);
                button.Location = new Point(nextX, 0);
                nextX += button.Width + 8;

                // 关键：FlowLayoutPanel 只在这个子控件「被加入」的那一刻排位置。
                // 先量好尺寸、从容器里摘掉再放回去，才能让位置按最终宽度算。
                line.Controls.Remove(button);
                line.Controls.Add(button);
            }
            // 按钮尺寸变了，FlowLayoutPanel 不会自己重排（它按加入时的旧宽度摆位置，
            // 于是双语长文案互相压住）——这里显式重排一次。
            line.Width = nextX;
            line.PerformLayout();
        }

        var grid = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.Controls.Add(row, 0, 0);
        grid.Controls.Add(row2, 0, 1);
        card.AddRow(grid, 0);

        // 重排必须放在 AddRow 之后：翻译就在 AddRow 内部发生，
        // 按钮是 AutoSize 的，翻译变宽后容器不会重算位置 —— 放在 AddRow 之前会被覆盖，
        // 这就是按钮重叠反复改不好的原因。
        foreach (var line in new[] { row, row2 })
        {
            var x = 0;
            foreach (Control button in line.Controls)
            {
                button.Location = new Point(x, 0);
                x += button.Width + 8;
            }
            line.Width = Math.Max(1, x);
        }

        return card;
    }

    private void LaunchAndInject()
    {
        var path = _config.PotPlayerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppendLog("未定位到 PotPlayer，请「重新检测 PotPlayer」。");
            return;
        }
        if (Injector.IsPotPlayerRunning())
        {
            AppendLog("PotPlayer 已在运行。同一个 DLL 重复注入不会执行新代码，请先退出。");
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
        if (ok) AppendLog("提示：若 PotPlayer 之前就在运行，请重启，否则注入不会生效。");
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
