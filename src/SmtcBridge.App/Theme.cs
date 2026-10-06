using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace SmtcBridge;

/// <summary>
/// 深色主题：统一调色板 + 自绘控件。
/// WinForms 原生控件默认是浅色的，这里全部手工上色，并用 DWM 把标题栏也切成深色。
/// </summary>
internal static class Theme
{
    public static readonly Color Window = Color.FromArgb(0x17, 0x17, 0x1B);
    public static readonly Color Surface = Color.FromArgb(0x21, 0x21, 0x27);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x2C, 0x2C, 0x35);
    public static readonly Color Field = Color.FromArgb(0x1B, 0x1B, 0x21);
    public static readonly Color FieldFocus = Color.FromArgb(0x22, 0x22, 0x2A);
    public static readonly Color Border = Color.FromArgb(0x36, 0x36, 0x41);
    public static readonly Color BorderFocus = Color.FromArgb(0xFF, 0xA5, 0x00);

    public static readonly Color Text = Color.FromArgb(0xEC, 0xEC, 0xF2);
    public static readonly Color TextDim = Color.FromArgb(0x9A, 0x9A, 0xA8);

    // 主题色用橙色 #FFA500
    public static readonly Color Accent = Color.FromArgb(0xFF, 0xA5, 0x00);
    public static readonly Color AccentHover = Color.FromArgb(0xFF, 0xB8, 0x33);
    public static readonly Color AccentDim = Color.FromArgb(0x3A, 0x2A, 0x00);
    public static readonly Color Good = Color.FromArgb(0x4A, 0xDE, 0x80);
    public static readonly Color Warn = Color.FromArgb(0xF5, 0xB1, 0x4E);
    public static readonly Color Bad = Color.FromArgb(0xF8, 0x71, 0x71);

    public static readonly Font FontBase = new("Microsoft YaHei UI", 9F);
    public static readonly Font FontSmall = new("Microsoft YaHei UI", 8.5F);
    public static readonly Font FontBold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font FontTitle = new("Microsoft YaHei UI", 15F, FontStyle.Bold);
    public static readonly Font FontMono = new("Consolas", 9F);

    // ---------------------------------------------------------------- 图形

    public static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || rect.Width <= d || rect.Height <= d)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void ApplyRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0) return;
        var old = control.Region;
        control.Region =
            new Region(RoundRect(new Rectangle(0, 0, control.Width, control.Height), radius));
        old?.Dispose();
    }

    /// <summary>把窗口标题栏切成深色（Win10 1809+ / Win11 都支持）。</summary>
    public static void UseDarkTitleBar(Form form)
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        const int DwmwaUseImmersiveDarkModeLegacy = 19;
        try
        {
            var value = 1;
            if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref value,
                    sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy, ref value,
                    sizeof(int));
            }
        }
        catch
        {
            // 老系统没有这个属性，忽略即可
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value,
        int size);

    // ---------------------------------------------------------------- 控件工厂

    public static Label Label(string text, Color? color = null, Font? font = null,
        int maxWidth = 0)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? Text,
            Font = font ?? FontBase,
            BackColor = Color.Transparent,
        };
        if (maxWidth > 0) label.MaximumSize = new Size(maxWidth, 0);
        return label;
    }

    public static Button PrimaryButton(string text) =>
        BuildButton(text, Accent, Color.White, AccentHover, FontBold);

    public static Button GhostButton(string text) =>
        BuildButton(text, SurfaceAlt, Text, Color.FromArgb(0x39, 0x39, 0x45), FontBase);

    private static Button BuildButton(string text, Color back, Color fore, Color hover, Font font)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 7, 14, 7),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = font,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        button.Resize += (_, _) => ApplyRegion(button, 8);
        return button;
    }

    public static CheckBox CheckBox(string text, bool isChecked) => new()
    {
        Text = text,
        Checked = isChecked,
        AutoSize = true,
        ForeColor = Text,
        Font = FontBase,
        BackColor = Color.Transparent,
        Cursor = Cursors.Hand,
    };

    /// <summary>
    /// 单行按钮容器。FlowLayoutPanel 默认 WrapContents=true，
    /// AutoSize 时会按"可能换行"估算高度，导致卡片下面多出一块空白。
    /// </summary>
    public static FlowLayoutPanel Row(Color background) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight,
        BackColor = background,
        Margin = new Padding(0),
    };
}

/// <summary>
/// 圆角卡片。基类用 TableLayoutPanel，AutoSize 才能正确跟随内容高度；
/// 圆角靠 Region 裁切，子控件设成 Theme.Surface 就能无缝贴合。
/// </summary>
internal sealed class Card : TableLayoutPanel
{
    private readonly int _radius;

    public Card(string title, int radius = 12)
    {
        title = Loc.T(title);
        _radius = radius;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Surface;
        Padding = new Padding(18, 14, 18, 16);
        Margin = new Padding(0, 0, 0, 12);
        DoubleBuffered = true;
        RowStyles.Clear();

        var header = Theme.Label(title, Theme.TextDim, Theme.FontBold);
        header.BackColor = Theme.Surface;
        header.Margin = new Padding(0, 0, 0, 10);
        Controls.Add(header);
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Resize += (_, _) => Theme.ApplyRegion(this, _radius);
    }

    public void AddRow(Control control, int topMargin = 8)
    {
        Loc.ApplyDeep(control);
        control.Margin = new Padding(0, topMargin, 0, 0);
        if (control is Label label) label.BackColor = Theme.Surface;
        Controls.Add(control);
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), _radius);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyRegion(this, _radius);
    }
}

/// <summary>深色输入框：外层画圆角描边，内部 TextBox 去边框。</summary>
internal sealed class DarkInput : Panel
{
    public TextBox Box { get; }

    public DarkInput(bool multiline = false, int height = 36)
    {
        BackColor = Theme.Field;
        Padding = new Padding(10, 0, 10, 0);
        Height = height;
        DoubleBuffered = true;
        Margin = new Padding(0);

        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.FontBase,
            Multiline = multiline,
            Dock = DockStyle.Fill,
        };
        if (multiline)
        {
            Box.ScrollBars = ScrollBars.Vertical;
            Box.ReadOnly = true;
        }

        Box.GotFocus += (_, _) => SetFocusVisual(true);
        Box.LostFocus += (_, _) => SetFocusVisual(false);
        Controls.Add(Box);

        Resize += (_, _) => Theme.ApplyRegion(this, 8);
    }

    private void SetFocusVisual(bool focused)
    {
        var color = focused ? Theme.FieldFocus : Theme.Field;
        BackColor = color;
        Box.BackColor = color;
        Invalidate();
    }

    [AllowNull]
    public override string Text
    {
        get => Box.Text;
        set => Box.Text = value ?? string.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        using var pen = new Pen(Box.Focused ? Theme.BorderFocus : Theme.Border);
        e.Graphics.DrawPath(pen, path);
    }
}
