namespace SmtcBridge;

/// <summary>
/// 首次使用向导：确认组件就位、定位 PotPlayer、写入配置、可选开机自动注入。
/// 只负责「注册」，真正注入在主界面一键完成。
/// </summary>
internal sealed class FirstRunForm : Form
{
    private readonly BridgeConfig _config;
    private readonly FlowLayoutPanel _stack = new();
    private readonly DarkInput _potPlayer = new();
    private readonly CheckBox _autoStart =
        Theme.CheckBox("备选：用后台进程自动注入（需要开机自启）", false);
    private readonly Label _componentState =
        Theme.Label("", Theme.TextDim, Theme.FontSmall, 660);

    public FirstRunForm(BridgeConfig config)
    {
        _config = config;

        Text = "PotPlayer SMTC Bridge — 首次使用";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 540);
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
        _stack.Controls.Add(BuildIntroCard());
        _stack.Controls.Add(BuildPathCard());
        _stack.Controls.Add(BuildOptionCard());
        _stack.Controls.Add(BuildButtons());

        Controls.Add(_stack);

        _potPlayer.Text = string.IsNullOrEmpty(_config.PotPlayerPath)
            ? Injector.DetectPotPlayer() ?? ""
            : _config.PotPlayerPath;
        // 默认不勾：推荐走主界面里的「安装到 PotPlayer」——改一次主程序，零常驻。
        // 只有不想改动主程序时才需要这个后台方案。
        _autoStart.Checked = _config.AutoStart || Injector.IsAutoStartEnabled();
        RefreshComponentState();
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
    }

    private void ResizeCards()
    {
        var width = _stack.ClientSize.Width - _stack.Padding.Horizontal - 4;
        if (width < 200) return;
        foreach (Control control in _stack.Controls)
        {
            control.MinimumSize = new Size(width, 0);
            control.Width = width;
        }
    }

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
        panel.Controls.Add(Theme.Label("欢迎使用", Theme.Text, Theme.FontTitle));
        panel.Controls.Add(Theme.Label(
            "三步完成注册，之后就能让 PotPlayer 向 Windows 输出完整媒体信息",
            Theme.TextDim, Theme.FontSmall));
        return panel;
    }

    private static Card BuildIntroCard()
    {
        var card = new Card("它做什么");
        card.AddRow(Theme.Label(
            "PotPlayer 默认只把文件名交给 Windows：歌名、歌手、专辑、音轨号、流派都是空的。\n"
            + "这个工具会补齐这些字段，让系统媒体面板、Discord、歌词工具都能读到正确信息。",
            Theme.Text, Theme.FontBase, 660), 0);
        return card;
    }

    private Card BuildPathCard()
    {
        var card = new Card("① 指定 PotPlayer");
        card.AddRow(Field("主程序路径", _potPlayer), 0);

        var row = Theme.Row(Theme.Surface);
        var browse = Theme.GhostButton("浏览…");
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "PotPlayer|PotPlayer*.exe|所有程序|*.exe",
                Title = "选择 PotPlayer 主程序",
            };
            var dir = Path.GetDirectoryName(_potPlayer.Text);
            if (!string.IsNullOrEmpty(dir)) dialog.InitialDirectory = dir;
            if (dialog.ShowDialog(this) == DialogResult.OK) _potPlayer.Text = dialog.FileName;
        };
        var detect = Theme.GhostButton("自动检测");
        detect.Click += (_, _) =>
        {
            var found = Injector.DetectPotPlayer();
            if (found is null)
            {
                _componentState.Text = "没有自动找到 PotPlayer，请手动浏览选择。";
                _componentState.ForeColor = Theme.Warn;
                return;
            }
            _potPlayer.Text = found;
            RefreshComponentState();
        };
        row.Controls.Add(browse);
        row.Controls.Add(detect);
        card.AddRow(row, 10);

        _componentState.BackColor = Theme.Surface;
        card.AddRow(_componentState, 10);
        return card;
    }

    private Card BuildOptionCard()
    {
        var card = new Card("② 启动方式");
        card.AddRow(_autoStart, 0);
        card.AddRow(Theme.Label(
            "下一步在主界面点「安装启动注入」：登记一条 IFEO 启动规则，"
            + "每次启动 PotPlayer 时自动注入，不碰它的任何文件，也不需要常驻进程。\n"
            + "不想要这个规则时，点「卸载启动注入」即可，PotPlayer 恢复原样。",
            Theme.TextDim, Theme.FontSmall, 660), 8);
        return card;
    }

    private Control BuildButtons()
    {
        var row = Theme.Row(Theme.Window);
        row.FlowDirection = FlowDirection.RightToLeft;
        row.Margin = new Padding(0, 6, 0, 0);
        var finish = Theme.PrimaryButton("完成注册");
        finish.Click += (_, _) => Finish();
        var cancel = Theme.GhostButton("退出");
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        row.Controls.Add(finish);
        row.Controls.Add(cancel);
        return row;
    }

    private static Control Field(string label, DarkInput input)
    {
        // 与主界面一致：手工定位，避免嵌套 TableLayoutPanel 把输入框拉高
        const int captionWidth = 104;
        var row = new Panel
        {
            Height = input.Height,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            MinimumSize = new Size(320, input.Height),
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

    private void RefreshComponentState()
    {
        if (Injector.ComponentReady)
        {
            _componentState.ForeColor = Theme.Good;
            _componentState.Text = "✔ 组件就位：PotPlayerSmtcHook.dll 与 SmtcLoader.exe 都在本程序目录";
        }
        else
        {
            _componentState.ForeColor = Theme.Bad;
            _componentState.Text = "✘ 缺少组件：请把 PotPlayerSmtcHook.dll 和 SmtcLoader.exe "
                                 + "放到本程序同一目录后重新运行";
        }
    }

    private void Finish()
    {
        var path = _potPlayer.Text.Trim();
        if (path.Length > 0 && !File.Exists(path))
        {
            MessageBox.Show(this,
                "That PotPlayer path does not exist.  ·  指定的 PotPlayer 路径不存在。",
                "Invalid path  ·  路径无效",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.PotPlayerPath = path;
        _config.AutoStart = _autoStart.Checked;
        _config.Registered = true;
        _config.TagFirst = true;
        _config.NamePattern = BridgeConfig.DefaultPattern;
        _config.Save();

        // 没有这个标记文件，DLL 不会挂接 CreateFileW，也就拿不到当前播放文件路径
        if (!File.Exists(AppPaths.FileHookMarker))
        {
            try { File.WriteAllText(AppPaths.FileHookMarker, ""); }
            catch { /* 写不了也不影响启动 */ }
        }

        if (_autoStart.Checked) Injector.SetAutoStart(true);

        DialogResult = DialogResult.OK;
        Close();
    }
}
