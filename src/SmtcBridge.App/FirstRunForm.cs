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
    private readonly Label _componentState =
        Theme.Label("", Theme.TextDim, Theme.FontSmall, 660);

    public FirstRunForm(BridgeConfig config)
    {
        _config = config;

        Text = "PotPlayer SMTC Bridge — First Run 首次使用";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(900, 675);
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
        _stack.Controls.Add(BuildButtons());

        Controls.Add(_stack);

        _potPlayer.Text = string.IsNullOrEmpty(_config.PotPlayerPath)
            ? Injector.DetectPotPlayer() ?? ""
            : _config.PotPlayerPath;
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
            "完成注入，使 PotPlayer 向 Windows 输出完整媒体信息",
            Theme.TextDim, Theme.FontSmall));
        return panel;
    }

    private static Card BuildIntroCard()
    {
        var card = new Card("它做什么");
        card.AddRow(Theme.Label(
            "PotPlayer 默认只把文件名交给 Windows\n"
            + "这个工具会补齐这些字段，让系统媒体面板、Discord、歌词工具都能读到正确信息。",
            Theme.Text, Theme.FontBase, 660), 0);
        return card;
    }

    private Card BuildPathCard()
    {
        var card = new Card("① 定位 PotPlayer");
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
                _componentState.Text = "未找到 PotPlayer，请手动浏览选择。";
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

    private Control BuildButtons()
    {
        var row = Theme.Row(Theme.Window);
        row.FlowDirection = FlowDirection.RightToLeft;
        row.Margin = new Padding(0, 6, 0, 0);
        var finish = Theme.PrimaryButton("继续注入");
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
        var row = new Panel
        {
            Height = input.Height,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            MinimumSize = new Size(320, input.Height),
            Width = 660,
            MaximumSize = new Size(660, input.Height),
            Anchor = AnchorStyles.Left,
        };

        var caption = Theme.Label(Loc.T(label), Theme.TextDim);
        var captionWidth = Math.Max(120,
            TextRenderer.MeasureText(caption.Text, caption.Font).Width + 14);
        caption.BackColor = Theme.Surface;
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


        DialogResult = DialogResult.OK;
        Close();
    }
}
