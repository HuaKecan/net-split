using System.Drawing.Drawing2D;
using System.Diagnostics.CodeAnalysis;
using NetSplit.Core;

namespace NetSplit.Tray;

public enum ButtonKind
{
    Accent,
    Secondary,
    Danger,
    Link
}

public static class UiGlyphs
{
    public const string Overview = "\uE80F";
    public const string Proxies = "\uE968";
    public const string ResidentialProxy = "\uE839";
    public const string Subscriptions = "\uE8A5";
    public const string Rules = "\uE71C";
    public const string Logs = "\uE81C";
    public const string Settings = "\uE713";
    public const string Validate = "\uE73E";
    public const string Repair = "\uE90F";
    public const string Rollback = "\uE777";
    public const string Refresh = "\uE72C";
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string Save = "\uE74E";
    public const string Search = "\uE721";
    public const string Copy = "\uE8C8";
    public const string Export = "\uEDE1";
    public const string Network = "\uE968";
    public const string Shield = "\uEA18";
}

internal static class UiDrawing
{
    public static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return path;
        }

        if (rect.Width < 3 || rect.Height < 3)
        {
            path.AddRectangle(rect);
            return path;
        }

        var maxRadius = Math.Min(rect.Width, rect.Height) / 2;
        if (maxRadius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        radius = Math.Clamp(radius, 1, maxRadius);
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Backdrop(Control control)
    {
        return control.Parent?.BackColor ?? ThemeManager.Current.BackgroundSurface;
    }

    public static Color WithAlpha(Color color, int alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    public static Color Blend(Color foreground, Color background, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(background.R + ((foreground.R - background.R) * amount)),
            (int)(background.G + ((foreground.G - background.G) * amount)),
            (int)(background.B + ((foreground.B - background.B) * amount)));
    }
}

public static class ModeVisuals
{
    public static string Text(RuntimeMode mode)
    {
        return mode switch
        {
            RuntimeMode.Healthy => "分流正常",
            RuntimeMode.Disabled => "已关闭",
            RuntimeMode.Starting => "启动中",
            RuntimeMode.Stopping => "停止中",
            RuntimeMode.DirectUnavailable => "主宽带不可用",
            RuntimeMode.ProxyUnavailable => "F50 / 代理不可用",
            RuntimeMode.CoreUnavailable => "Mihomo 不可用",
            RuntimeMode.Misconfigured => "配置异常",
            _ => mode.ToString()
        };
    }

    public static string ProxyRouteText(ProxyRouteFailureReason reason)
    {
        return reason switch
        {
            ProxyRouteFailureReason.None => "\u4EE3\u7406\u8DEF\u7531\u6B63\u5E38",
            ProxyRouteFailureReason.Starting => "\u4EE3\u7406\u8DEF\u7531\u542F\u52A8\u4E2D",
            ProxyRouteFailureReason.CoreUnavailable => "Mihomo \u672A\u8FD0\u884C",
            ProxyRouteFailureReason.ControllerUnavailable =>
                "Mihomo \u63A7\u5236\u5668\u4E0D\u53EF\u7528",
            ProxyRouteFailureReason.ProxyAdapterUnavailable =>
                "\u7F51\u53612\u4E0D\u53EF\u7528",
            ProxyRouteFailureReason.ResidentialProxyUnavailable =>
                "\u4F4F\u5B85 SOCKS5 \u4E0D\u53EF\u7528",
            ProxyRouteFailureReason.NoHealthyProxy =>
                "\u673A\u573A\u8282\u70B9\u5168\u90E8\u5931\u6548",
            ProxyRouteFailureReason.HealthCheckPending =>
                "\u6B63\u5728\u68C0\u67E5\u4EE3\u7406\u5065\u5EB7",
            ProxyRouteFailureReason.ConfigurationInvalid =>
                "\u4EE3\u7406\u914D\u7F6E\u65E0\u6548",
            _ => "\u4EE3\u7406\u72B6\u6001\u672A\u77E5"
        };
    }

    public static string Text(RuntimeStatus status)
    {
        return status.Mode == RuntimeMode.ProxyUnavailable
            && status.ProxyRouteFailure != ProxyRouteFailureReason.None
            ? $"{Text(status.Mode)} - {ProxyRouteText(status.ProxyRouteFailure)}"
            : Text(status.Mode);
    }

    public static Color Color(RuntimeMode mode, UiTheme theme)
    {
        return mode switch
        {
            RuntimeMode.Healthy => theme.Success,
            RuntimeMode.Disabled => theme.TextMuted,
            RuntimeMode.Starting or RuntimeMode.Stopping => theme.Warning,
            RuntimeMode.DirectUnavailable => theme.Warning,
            RuntimeMode.ProxyUnavailable
                or RuntimeMode.CoreUnavailable
                or RuntimeMode.Misconfigured => theme.Danger,
            _ => theme.TextSecondary
        };
    }
}

public sealed class SplitMark : Control
{
    public SplitMark()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(UiMetrics.Scale(this, 26), UiMetrics.Scale(this, 26));
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "net-split";
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = new Size(UiMetrics.Scale(this, 26), UiMetrics.Scale(this, 26));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);
        using var fill = new SolidBrush(theme.Accent);
        graphics.FillPath(fill, path);

        using var pen = new Pen(theme.OnAccent, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var inset = UiMetrics.Scale(this, 7);
        graphics.DrawLine(pen, inset, inset, inset, Height - inset);
        graphics.DrawLine(pen, Width - inset, inset, Width - inset, Height - inset);
        graphics.DrawLine(pen, inset, inset, Width - inset, Height - inset);
    }
}

public sealed class ThemedButton : Control
{
    private const int IconSize = 16;
    private const int IconGap = 6;
    private const int HorizontalPadding = 12;
    private const int MeasurementSlack = 4;

    private bool _hovered;
    private bool _pressed;
    private ButtonKind _kind = ButtonKind.Accent;
    private Image? _icon;
    private string _glyph = string.Empty;
    private bool _fitToContent;
    private int _contentMinimumWidth;

    public ButtonKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Font = value == ButtonKind.Accent
                ? UiFonts.BodyStrong
                : UiFonts.Body;
            Invalidate();
        }
    }

    public Image? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Invalidate();
        }
    }

    public string Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value ?? string.Empty;
            Invalidate();
        }
    }

    public ThemedButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.StandardClick,
            true);
        Font = UiFonts.Body;
        Height = UiMetrics.Scale(this, UiMetrics.ControlHeight);
        MinimumSize = new Size(
            UiMetrics.Scale(this, UiMetrics.ControlHeight),
            UiMetrics.Scale(this, UiMetrics.ControlHeight));
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public void SizeToContent(int minimumWidth = 0)
    {
        _fitToContent = true;
        _contentMinimumWidth = minimumWidth;
        var preferredWidth = MeasureContentWidth();
        MinimumSize = new Size(
            preferredWidth,
            UiMetrics.Scale(this, UiMetrics.ControlHeight));
        Width = preferredWidth;
        AccessibleName = Text;
    }

    private int MeasureContentWidth()
    {
        var textWidth = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var hasIcon = Icon is not null || !string.IsNullOrWhiteSpace(Glyph);
        var iconSize = UiMetrics.Scale(this, IconSize);
        var iconGap = UiMetrics.Scale(this, IconGap);
        var horizontalPadding = UiMetrics.Scale(this, HorizontalPadding);
        var measurementSlack = UiMetrics.Scale(this, MeasurementSlack);
        var contentWidth = textWidth
            + (hasIcon ? iconSize + iconGap : 0)
            + (horizontalPadding * 2)
            + measurementSlack;
        return Math.Max(UiMetrics.Scale(this, _contentMinimumWidth), contentWidth);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_fitToContent)
        {
            SizeToContent(_contentMinimumWidth);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_fitToContent && IsHandleCreated)
        {
            SizeToContent(_contentMinimumWidth);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, UiMetrics.ControlHeight);
        MinimumSize = new Size(
            UiMetrics.Scale(this, UiMetrics.ControlHeight),
            UiMetrics.Scale(this, UiMetrics.ControlHeight));
        if (_fitToContent)
        {
            SizeToContent(_contentMinimumWidth);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_fitToContent || !IsHandleCreated)
        {
            return;
        }

        var preferredWidth = MeasureContentWidth();
        if (MinimumSize.Width != preferredWidth)
        {
            MinimumSize = new Size(preferredWidth, MinimumSize.Height);
        }

        if (Width != preferredWidth)
        {
            Width = preferredWidth;
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        AccessibleName = Text;
        base.OnTextChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_kind == ButtonKind.Link)
        {
            DrawLink(graphics, ResolveText(theme), rect);
            return;
        }

        using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);
        using (var brush = new SolidBrush(ResolveFill(theme)))
        {
            graphics.FillPath(brush, path);
        }

        var border = ResolveBorder(theme);
        if (border.HasValue)
        {
            using var pen = new Pen(border.Value);
            graphics.DrawPath(pen, path);
        }

        if (Focused)
        {
            var focusRect = Rectangle.Inflate(rect, -3, -3);
            using var focusPath = UiDrawing.Rounded(focusRect, UiMetrics.RadiusSm);
            using var focusPen = new Pen(
                _kind == ButtonKind.Accent ? theme.OnAccent : theme.Accent)
            {
                DashStyle = DashStyle.Dot
            };
            graphics.DrawPath(focusPen, focusPath);
        }

        DrawContent(graphics, ResolveText(theme), rect);
    }

    private Color ResolveFill(UiTheme theme)
    {
        if (!Enabled)
        {
            return theme.BackgroundSurface2;
        }

        return _kind switch
        {
            ButtonKind.Accent => _pressed
                ? UiDrawing.Blend(Color.Black, theme.Accent, 0.14f)
                : _hovered
                    ? UiDrawing.Blend(Color.White, theme.Accent, 0.10f)
                    : theme.Accent,
            ButtonKind.Secondary => _pressed
                ? theme.Border
                : _hovered
                    ? theme.BackgroundSurface2
                    : theme.BackgroundSurface,
            ButtonKind.Danger => _pressed
                ? UiDrawing.WithAlpha(theme.Danger, 32)
                : _hovered
                    ? UiDrawing.WithAlpha(theme.Danger, 20)
                    : UiDrawing.Backdrop(this),
            _ => UiDrawing.Backdrop(this)
        };
    }

    private Color? ResolveBorder(UiTheme theme)
    {
        return _kind switch
        {
            ButtonKind.Secondary => _hovered ? theme.BorderStrong : theme.Border,
            ButtonKind.Danger => Enabled ? theme.Danger : theme.Border,
            _ => null
        };
    }

    private Color ResolveText(UiTheme theme)
    {
        if (!Enabled)
        {
            return theme.TextMuted;
        }

        return _kind switch
        {
            ButtonKind.Accent => theme.OnAccent,
            ButtonKind.Danger => theme.Danger,
            ButtonKind.Link => _hovered ? theme.AccentText : theme.TextSecondary,
            _ => theme.TextPrimary
        };
    }

    private void DrawLink(Graphics graphics, Color textColor, Rectangle rect)
    {
        if (_hovered || _pressed)
        {
            using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);
            using var fill = new SolidBrush(ThemeManager.Current.BackgroundSurface2);
            graphics.FillPath(fill, path);
        }

        DrawContent(graphics, textColor, rect);
        if (Focused)
        {
            using var pen = new Pen(ThemeManager.Current.Accent) { DashStyle = DashStyle.Dot };
            graphics.DrawRectangle(pen, Rectangle.Inflate(rect, -2, -2));
        }
    }

    private void DrawContent(Graphics graphics, Color textColor, Rectangle rect)
    {
        var textWidth = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var hasIcon = Icon is not null || !string.IsNullOrWhiteSpace(Glyph);
        var iconSize = UiMetrics.Scale(this, IconSize);
        var iconGap = UiMetrics.Scale(this, IconGap);
        var horizontalPadding = UiMetrics.Scale(this, HorizontalPadding);
        var totalWidth = textWidth + (hasIcon ? iconSize + iconGap : 0);
        var startX = rect.X + Math.Max(horizontalPadding, (rect.Width - totalWidth) / 2);
        var contentY = rect.Y + (rect.Height - iconSize) / 2;

        if (Icon is not null)
        {
            graphics.DrawImage(Icon, startX, contentY, iconSize, iconSize);
            startX += iconSize + iconGap;
        }
        else if (!string.IsNullOrWhiteSpace(Glyph))
        {
            TextRenderer.DrawText(
                graphics,
                Glyph,
                UiFonts.Icon,
                new Rectangle(startX, rect.Y, iconSize, rect.Height),
                textColor,
                TextFormatFlags.VerticalCenter
                    | TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPadding);
            startX += iconSize + iconGap;
        }

        var textRect = new Rectangle(startX, rect.Y, textWidth, rect.Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textRect,
            textColor,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix);
    }
}

public sealed class RoundedTextBox : UserControl
{
    private const int HorizontalInset = 12;

    private readonly TextBox _editor;
    private bool _hovered;
    private int _cornerRadius = UiMetrics.RadiusMd;

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(1, value);
            Invalidate();
        }
    }

    public string PlaceholderText
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value ?? string.Empty;
    }

    public bool UseSystemPasswordChar
    {
        get => _editor.UseSystemPasswordChar;
        set => _editor.UseSystemPasswordChar = value;
    }

    public string InputAccessibleName
    {
        get => _editor.AccessibleName ?? string.Empty;
        set => _editor.AccessibleName = value;
    }

    [AllowNull]
    public override string Text
    {
        get => _editor?.Text ?? base.Text;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(base.Text, value, StringComparison.Ordinal))
            {
                base.Text = value;
            }

            if (_editor is not null
                && !string.Equals(_editor.Text, value, StringComparison.Ordinal))
            {
                _editor.Text = value;
            }
        }
    }

    public RoundedTextBox()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = UiDrawing.Backdrop(this);
        Font = UiFonts.Body;
        Height = UiMetrics.Scale(this, UiMetrics.ControlHeight);
        MinimumSize = new Size(0, UiMetrics.Scale(this, UiMetrics.ControlHeight));
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        TabStop = false;
        Cursor = Cursors.IBeam;

        var theme = ThemeManager.Current;
        _editor = new TextBox
        {
            AutoSize = true,
            BorderStyle = BorderStyle.None,
            Font = Font,
            BackColor = theme.BackgroundSurface,
            ForeColor = theme.TextPrimary,
            Margin = Padding.Empty,
            TabIndex = 0
        };
        _editor.TextChanged += (_, _) =>
        {
            if (!string.Equals(base.Text, _editor.Text, StringComparison.Ordinal))
            {
                base.Text = _editor.Text;
            }
        };
        _editor.GotFocus += (_, _) => Invalidate();
        _editor.LostFocus += (_, _) => Invalidate();
        _editor.MouseEnter += (_, _) =>
        {
            _hovered = true;
            Invalidate();
        };
        _editor.MouseLeave += (_, _) =>
        {
            _hovered = false;
            Invalidate();
        };
        Controls.Add(_editor);
        LayoutEditor();
    }

    protected override bool ScaleChildren => false;

    public void Clear()
    {
        Text = string.Empty;
    }

    protected override void OnClick(EventArgs e)
    {
        _editor.Focus();
        base.OnClick(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyEditorColors();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_editor is not null)
        {
            _editor.Font = Font;
            LayoutEditor();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutEditor();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, UiMetrics.ControlHeight);
        MinimumSize = new Size(0, UiMetrics.Scale(this, UiMetrics.ControlHeight));
        LayoutEditor();
    }

    protected override void ScaleControl(
        SizeF factor,
        BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        LayoutEditor();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        if (Width < 3 || Height < 3)
        {
            return;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(
            rect,
            UiMetrics.Scale(this, CornerRadius));
        using var fill = new SolidBrush(
            Enabled ? theme.BackgroundSurface : theme.BackgroundSurface2);
        graphics.FillPath(fill, path);

        var border = !Enabled
            ? theme.Border
            : _editor.Focused
                ? theme.Accent
                : _hovered
                    ? theme.BorderStrong
                    : theme.Border;
        using var pen = new Pen(border, _editor.Focused ? 1.5f : 1f);
        graphics.DrawPath(pen, path);
    }

    private void LayoutEditor()
    {
        if (_editor is null)
        {
            return;
        }

        var inset = UiMetrics.Scale(this, HorizontalInset);
        var editorHeight = _editor.PreferredHeight;
        _editor.SetBounds(
            inset,
            Math.Max(1, (Height - editorHeight) / 2),
            Math.Max(0, Width - (inset * 2)),
            editorHeight);
        ApplyEditorColors();
    }

    private void ApplyEditorColors()
    {
        if (_editor is null)
        {
            return;
        }

        var theme = ThemeManager.Current;
        _editor.BackColor = Enabled
            ? theme.BackgroundSurface
            : theme.BackgroundSurface2;
        _editor.ForeColor = Enabled
            ? theme.TextPrimary
            : theme.TextMuted;
    }
}

public sealed class ToggleSwitch : Control
{
    private const int TrackWidth = 40;
    private const int TrackHeight = 22;
    private const int ThumbSize = 16;

    private bool _checked;
    private string _checkedAccessibleName = "关闭分流";
    private string _uncheckedAccessibleName = "开启分流";

    public event EventHandler? CheckedChanged;

    public string CheckedAccessibleName
    {
        get => _checkedAccessibleName;
        set
        {
            _checkedAccessibleName = value;
            UpdateAccessibleName();
        }
    }

    public string UncheckedAccessibleName
    {
        get => _uncheckedAccessibleName;
        set
        {
            _uncheckedAccessibleName = value;
            UpdateAccessibleName();
        }
    }

    public bool Checked
    {
        get => _checked;
        set => SetChecked(value, notify: true);
    }

    public void SetCheckedSilently(bool value)
    {
        SetChecked(value, notify: false);
    }

    private void SetChecked(bool value, bool notify)
    {
        if (_checked == value)
        {
            return;
        }

        _checked = value;
        UpdateAccessibleName();
        Invalidate();
        if (notify)
        {
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.Selectable
            | ControlStyles.StandardClick,
            true);
        Size = new Size(
            UiMetrics.Scale(this, TrackWidth),
            UiMetrics.Scale(this, TrackHeight));
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        UpdateAccessibleName();
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            Checked = !Checked;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var trackRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var trackPath = UiDrawing.Rounded(trackRect, Height / 2);
        using (var trackBrush = new SolidBrush(
                   Enabled ? (_checked ? theme.Accent : theme.BorderStrong) : theme.Border))
        {
            graphics.FillPath(trackBrush, trackPath);
        }

        var thumbSize = UiMetrics.Scale(this, ThumbSize);
        var thumbInset = UiMetrics.Scale(this, 3);
        var thumbX = _checked ? Width - thumbSize - thumbInset : thumbInset;
        var thumbRect = new Rectangle(
            thumbX,
            (Height - thumbSize) / 2,
            thumbSize,
            thumbSize);
        using (var thumbBrush = new SolidBrush(Enabled ? Color.White : theme.TextMuted))
        {
            graphics.FillEllipse(thumbBrush, thumbRect);
        }

        if (Focused)
        {
            var focusRect = Rectangle.Inflate(trackRect, -1, -1);
            using var focusPath = UiDrawing.Rounded(focusRect, Height / 2);
            using var focusPen = new Pen(theme.Accent) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focusPen, focusPath);
        }
    }

    private void UpdateAccessibleName()
    {
        AccessibleName = _checked ? CheckedAccessibleName : UncheckedAccessibleName;
    }
}

public sealed class ModeBadge : Control
{
    private RuntimeMode _mode = RuntimeMode.Disabled;
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 40 };
    private float _pulsePhase;

    public ModeBadge()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Height = UiMetrics.Scale(this, 28);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 28));
        AccessibleRole = AccessibleRole.StaticText;
        _pulseTimer.Tick += (_, _) =>
        {
            _pulsePhase = (_pulsePhase + 0.06f) % (MathF.PI * 2);
            Invalidate();
        };
        Fit();
    }

    public void SetMode(RuntimeMode mode)
    {
        _mode = mode;
        AccessibleName = $"状态：{ModeVisuals.Text(mode)}";
        var shouldPulse = mode == RuntimeMode.Healthy;
        if (shouldPulse && !_pulseTimer.Enabled)
        {
            _pulsePhase = 0f;
            _pulseTimer.Start();
        }
        else if (!shouldPulse && _pulseTimer.Enabled)
        {
            _pulseTimer.Stop();
        }
        Fit();
    }

    public void Fit()
    {
        var text = ModeVisuals.Text(_mode);
        var width = TextRenderer.MeasureText(
            text,
            UiFonts.CaptionStrong,
            new Size(int.MaxValue, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        Width = Math.Max(UiMetrics.Scale(this, 78), width + UiMetrics.Scale(this, 34));
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, 28);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 28));
        Fit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulseTimer.Stop();
            _pulseTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var color = ModeVisuals.Color(_mode, theme);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(rect, Height / 2);
        using (var fill = new SolidBrush(UiDrawing.WithAlpha(color, 20)))
        {
            graphics.FillPath(fill, path);
        }

        using (var border = new Pen(UiDrawing.WithAlpha(color, 76)))
        {
            graphics.DrawPath(border, path);
        }

        var dotSize = UiMetrics.Scale(this, 7);
        var dotX = UiMetrics.Scale(this, 10);
        var dotY = (Height - dotSize) / 2;

        // Pulse glow halo for Healthy state
        if (_mode == RuntimeMode.Healthy && _pulseTimer.Enabled)
        {
            var pulse = (MathF.Sin(_pulsePhase) + 1f) / 2f; // 0..1
            var haloAlpha = (int)(pulse * 60);
            var haloSize = dotSize + UiMetrics.Scale(this, 5);
            var haloOffset = (haloSize - dotSize) / 2;
            using var haloBrush = new SolidBrush(UiDrawing.WithAlpha(color, haloAlpha));
            graphics.FillEllipse(haloBrush, dotX - haloOffset, dotY - haloOffset, haloSize, haloSize);
        }

        using (var dotBrush = new SolidBrush(color))
        {
            graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
        }

        var textGap = UiMetrics.Scale(this, 7);
        var trailingPadding = UiMetrics.Scale(this, 14);
        var textRect = new Rectangle(
            dotX + dotSize + textGap,
            0,
            Width - dotX - dotSize - trailingPadding,
            Height);
        TextRenderer.DrawText(
            graphics,
            ModeVisuals.Text(_mode),
            UiFonts.CaptionStrong,
            textRect,
            color,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix);
    }
}

public sealed class InlineBanner : Control
{
    private string _message = string.Empty;
    private Color _color = ThemeManager.Current.Warning;

    public event EventHandler? Dismissed;

    public InlineBanner()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Height = UiMetrics.Scale(this, 44);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 44));
        Visible = false;
        Cursor = Cursors.Default;
        AccessibleRole = AccessibleRole.Alert;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, 44);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 44));
    }

    public void Show(string message, Color color)
    {
        _message = message;
        _color = color;
        AccessibleName = message;
        Visible = true;
        Invalidate();
    }

    public void Clear()
    {
        _message = string.Empty;
        Visible = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (GetCloseRectangle().Contains(e.Location))
        {
            Clear();
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle GetCloseRectangle()
    {
        var closeSize = UiMetrics.Scale(this, 18);
        return new Rectangle(
            Width - UiMetrics.Scale(this, 30),
            (Height - closeSize) / 2,
            closeSize,
            closeSize);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);
        using (var fill = new SolidBrush(UiDrawing.WithAlpha(_color, 18)))
        {
            graphics.FillPath(fill, path);
        }

        using (var pen = new Pen(UiDrawing.WithAlpha(_color, 88)))
        {
            graphics.DrawPath(pen, path);
        }

        using (var marker = new SolidBrush(_color))
        {
            var markerSize = UiMetrics.Scale(this, 7);
            graphics.FillEllipse(
                marker,
                UiMetrics.Scale(this, 12),
                (Height - markerSize) / 2,
                markerSize,
                markerSize);
        }

        var textRect = new Rectangle(
            UiMetrics.Scale(this, 28),
            0,
            Width - UiMetrics.Scale(this, 66),
            Height);
        TextRenderer.DrawText(
            graphics,
            _message,
            UiFonts.Body,
            textRect,
            theme.TextPrimary,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(
            graphics,
            "×",
            UiFonts.Section,
            GetCloseRectangle(),
            theme.TextSecondary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

public sealed class Badge : Control
{
    private string _text = string.Empty;
    private Color _foreground = ThemeManager.Current.TextSecondary;
    private Color _background = ThemeManager.Current.BackgroundSurface2;

    public Badge()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);
        Height = UiMetrics.Scale(this, 24);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 24));
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void Set(string text, Color foreground, Color background)
    {
        _text = text;
        _foreground = foreground;
        _background = background;
        AccessibleName = text;
        Fit();
    }

    public void Fit()
    {
        var width = TextRenderer.MeasureText(
            _text,
            UiFonts.Badge,
            new Size(int.MaxValue, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width
            + UiMetrics.Scale(this, 14);
        Width = Math.Max(UiMetrics.Scale(this, 20), width);
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, 24);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 24));
        Fit();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(rect, Height / 2);
        using (var brush = new SolidBrush(_background))
        {
            graphics.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            graphics,
            _text,
            UiFonts.Badge,
            rect,
            _foreground,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.HorizontalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix);
    }
}

public sealed class EmptyState : UserControl
{
    public EmptyState(string title, string description, ThemedButton? action = null)
    {
        var theme = ThemeManager.Current;
        Dock = DockStyle.Fill;
        BackColor = theme.BackgroundSurface;
        Padding = new Padding(UiMetrics.Space2xl);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = action is null ? 2 : 3,
            BackColor = theme.BackgroundSurface
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (action is not null)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var copy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Bottom,
            BackColor = theme.BackgroundSurface
        };
        copy.Controls.Add(new Label
        {
            Text = title,
            Font = UiFonts.Section,
            ForeColor = theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            Anchor = AnchorStyles.Top
        });
        copy.Controls.Add(new Label
        {
            Text = description,
            Font = UiFonts.Body,
            ForeColor = theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Anchor = AnchorStyles.Top,
            Margin = new Padding(0, UiMetrics.SpaceXs, 0, 0)
        });
        table.Controls.Add(copy, 0, 0);

        if (action is not null)
        {
            var wrapper = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Anchor = AnchorStyles.Top,
                BackColor = theme.BackgroundSurface,
                Margin = new Padding(0, UiMetrics.SpaceMd, 0, 0)
            };
            wrapper.Controls.Add(action);
            table.Controls.Add(wrapper, 0, 1);
        }

        Controls.Add(table);
    }
}

public class Card : Panel
{
    private int _cornerRadius = UiMetrics.RadiusXl;

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(1, value);
            UpdateRegion();
            Invalidate();
        }
    }

    public Card()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = ThemeManager.Current.BackgroundSurface;
        Padding = new Padding(UiMetrics.SpaceLg);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width < 3 || Height < 3)
        {
            Region?.Dispose();
            Region = null;
            return;
        }

        using var path = UiDrawing.Rounded(
            new Rectangle(0, 0, Width, Height),
            UiMetrics.Scale(this, CornerRadius));
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 3 || Height < 3)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDrawing.Rounded(
            rect,
            UiMetrics.Scale(this, CornerRadius));
        using var pen = new Pen(ThemeManager.Current.Border);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class MetricValueDisplay : Control
{
    private static readonly Font[] ValueFonts =
    [
        UiFonts.Metric,
        UiFonts.MetricCompact,
        UiFonts.MetricSmall,
        UiFonts.MetricTiny
    ];

    private Font _displayFont = UiFonts.Metric;

    internal Font DisplayFont => _displayFont;

    internal bool ContentFits => MeasureText(_displayFont) <= ClientSize.Width;

    public MetricValueDisplay()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = ThemeManager.Current.BackgroundSurface;
        AccessibleRole = AccessibleRole.StaticText;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        SelectDisplayFont();
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        SelectDisplayFont();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        SelectDisplayFont();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        SelectDisplayFont();
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            _displayFont,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.VerticalCenter
            | TextFormatFlags.Left
            | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.NoPadding
            | TextFormatFlags.EndEllipsis);
    }

    private void SelectDisplayFont()
    {
        if (ClientSize.Width <= 0 || string.IsNullOrEmpty(Text))
        {
            return;
        }

        var selected = ValueFonts[^1];
        foreach (var font in ValueFonts)
        {
            if (MeasureText(font) <= ClientSize.Width)
            {
                selected = font;
                break;
            }
        }

        _displayFont = selected;
    }

    private int MeasureText(Font font)
    {
        return TextRenderer.MeasureText(
            Text,
            font,
            new Size(int.MaxValue, Math.Max(1, ClientSize.Height)),
            TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.NoPadding).Width;
    }
}

public sealed class MetricCard : Card
{
    private readonly string _title;
    private readonly MetricValueDisplay _value;
    private readonly Label _detail;
    private readonly ToolTip _toolTip = new();
    private readonly Color _defaultValueColor;

    public MetricCard(string title)
    {
        var theme = ThemeManager.Current;
        _title = title;
        _defaultValueColor = theme.TextPrimary;
        Padding = new Padding(
            UiMetrics.SpaceSm,
            UiMetrics.SpaceMd,
            UiMetrics.SpaceSm,
            UiMetrics.SpaceSm);
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = title;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            Math.Max(20, UiFonts.Caption.Height + UiMetrics.SpaceXs)));
        table.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            Math.Max(36, UiFonts.Metric.Height + UiMetrics.SpaceXs)));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        table.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = Padding.Empty
        }, 0, 0);

        _value = new MetricValueDisplay
        {
            Text = "—",
            Dock = DockStyle.Fill,
            ForeColor = _defaultValueColor,
            AccessibleName = $"{title}数值",
            Margin = Padding.Empty
        };
        table.Controls.Add(_value, 0, 1);

        _detail = new Label
        {
            Text = "等待服务返回状态",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoEllipsis = false,
            AccessibleName = $"{title}说明",
            Margin = Padding.Empty
        };
        table.Controls.Add(_detail, 0, 2);
        Controls.Add(table);
    }

    public void SetValue(string value, string detail, Color? valueColor = null)
    {
        var newColor = valueColor ?? _defaultValueColor;
        _value.Text = value.Replace(' ', '\u00A0');
        _value.ForeColor = newColor;
        _detail.Text = detail;
        AccessibleDescription = $"{_title}：{value}。{detail}";
        _toolTip.SetToolTip(_value, value);
        _toolTip.SetToolTip(_detail, detail);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
