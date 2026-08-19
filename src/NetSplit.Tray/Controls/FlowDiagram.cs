using System.Drawing.Drawing2D;

namespace NetSplit.Tray;

public sealed class FlowDiagram : Control
{
    private const int NodeHeight = 34;
    private const int DesignWidth = 432;
    private const int NodeGap = 14;
    private bool _tun;
    private bool _direct;
    private bool _proxy;
    private int? _directDelayMs;
    private int? _proxyDelayMs;

    public FlowDiagram()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Height = UiMetrics.Scale(this, 116);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 116));
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = "分流路径";
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = UiMetrics.Scale(this, 116);
        MinimumSize = new Size(0, UiMetrics.Scale(this, 116));
    }

    public void SetState(bool tun, bool direct, bool proxy,
        int? directDelayMs = null, int? proxyDelayMs = null)
    {
        _tun = tun;
        _direct = direct;
        _proxy = proxy;
        _directDelayMs = directDelayMs;
        _proxyDelayMs = proxyDelayMs;
        AccessibleDescription = tun
            ? $"TUN 已启用，国内路径{(direct ? "可用" : "不可用")}，代理路径{(proxy ? "可用" : "不可用")}"
            : "TUN 未启用";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = ThemeManager.Current;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));

        var dpiScale = DeviceDpi / 96f;
        int Dpi(int value) => Math.Max(1, (int)Math.Round(value * dpiScale));
        var nodeHeight = Dpi(NodeHeight);
        var designWidth = Dpi(DesignWidth);
        var centerY = Height / 2;
        var topY = Dpi(8);
        var bottomY = Height - nodeHeight - Dpi(8);
        var widthScale = Math.Min(
            1f,
            Math.Max(0.86f, (Width - Dpi(8)) / (float)designWidth));
        int Scale(int value) => Math.Max(1, (int)Math.Round(Dpi(value) * widthScale));

        var originX = Math.Max(Dpi(4), (Width - Scale(DesignWidth)) / 2);
        var app = new Rectangle(
            originX,
            centerY - (nodeHeight / 2),
            Scale(110),
            nodeHeight);
        var tun = new Rectangle(app.Right + Scale(NodeGap), app.Y, Scale(130), nodeHeight);
        var branchX = tun.Right + Scale(28);
        var direct = new Rectangle(branchX, topY, Scale(150), nodeHeight);
        var proxy = new Rectangle(branchX, bottomY, Scale(150), nodeHeight);

        var tunColor = _tun ? theme.Accent : theme.TextMuted;
        var directColor = _tun
            ? (_direct ? theme.ChartDirect : theme.Danger)
            : theme.TextMuted;
        var proxyColor = _tun
            ? (_proxy ? theme.Accent : theme.Danger)
            : theme.TextMuted;

        // Draw pipe connectors first (behind nodes)
        DrawPipe(graphics, app, tun, tunColor);
        DrawBranchPipe(graphics, tun, direct, directColor);
        DrawBranchPipe(graphics, tun, proxy, proxyColor);

        DrawNode(graphics, app, "应用流量", tunColor, theme, _tun, false, null);
        DrawNode(graphics, tun, "TUN 核心", tunColor, theme, _tun, true, null);
        DrawNode(graphics, direct, "国内 · 网卡1", directColor, theme, _tun && _direct, true,
            _tun && _direct && _directDelayMs.HasValue ? $"{_directDelayMs} ms" : null);
        DrawNode(graphics, proxy, "境外 · 网卡2", proxyColor, theme, _tun && _proxy, true,
            _tun && _proxy && _proxyDelayMs.HasValue ? $"{_proxyDelayMs} ms" : null);
    }

    private static void DrawNode(
        Graphics graphics,
        Rectangle rect,
        string text,
        Color color,
        UiTheme theme,
        bool active,
        bool emphasized,
        string? badge)
    {
        using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);

        // Gradient background fill
        if (rect.Height > 2)
        {
            var topColor = active
                ? UiDrawing.Blend(color, theme.BackgroundSurface, emphasized ? 0.22f : 0.10f)
                : theme.BackgroundSurface2;
            var bottomColor = active
                ? UiDrawing.Blend(color, theme.BackgroundSurface, emphasized ? 0.08f : 0.04f)
                : theme.BackgroundSurface2;
            using var gradBrush = new LinearGradientBrush(
                new Point(rect.X, rect.Y),
                new Point(rect.X, rect.Bottom),
                topColor, bottomColor);
            graphics.FillPath(gradBrush, path);
        }

        // Border: glowing when active
        var borderAlpha = active ? (emphasized ? 200 : 120) : 50;
        using (var pen = new Pen(UiDrawing.WithAlpha(color, borderAlpha), 1.2f))
        {
            graphics.DrawPath(pen, path);
        }

        // Status dot
        var dotSize = Math.Max(5, (int)Math.Round(6 * graphics.DpiX / 96f));
        var textStartX = rect.X + UiMetrics.SpaceSm;
        if (active)
        {
            var dotX = rect.X + UiMetrics.SpaceSm;
            var dotY = rect.Y + (rect.Height - dotSize) / 2;
            using var dot = new SolidBrush(color);
            graphics.FillEllipse(dot, dotX, dotY, dotSize, dotSize);
            textStartX = dotX + dotSize + 5;
        }

        // Badge (delay) on right side
        if (!string.IsNullOrEmpty(badge))
        {
            var badgeFont = UiFonts.Mono;
            var badgeSize = TextRenderer.MeasureText(badge, badgeFont,
                new Size(int.MaxValue, rect.Height), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            var badgeRect = new Rectangle(
                rect.Right - badgeSize.Width - UiMetrics.SpaceSm,
                rect.Y,
                badgeSize.Width,
                rect.Height);
            TextRenderer.DrawText(graphics, badge, badgeFont, badgeRect,
                UiDrawing.WithAlpha(color, 200),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        var textRect = new Rectangle(textStartX, rect.Y,
            rect.Right - textStartX - (badge != null ? 52 : UiMetrics.SpaceXs), rect.Height);
        TextRenderer.DrawText(
            graphics,
            text,
            emphasized ? UiFonts.CaptionStrong : UiFonts.Caption,
            textRect,
            active ? theme.TextPrimary : theme.TextMuted,
            TextFormatFlags.VerticalCenter
                | TextFormatFlags.Left
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
    }

    private static void DrawPipe(Graphics graphics, Rectangle from, Rectangle to, Color color)
    {
        var start = new Point(from.Right, from.Top + (from.Height / 2));
        var end = new Point(to.Left, to.Top + (to.Height / 2));
        using var pen = new Pen(color, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.ArrowAnchor
        };
        graphics.DrawLine(pen, start, end);
    }

    private static void DrawBranchPipe(Graphics graphics, Rectangle from, Rectangle to, Color color)
    {
        var start = new Point(from.Right, from.Top + (from.Height / 2));
        var end = new Point(to.Left, to.Top + (to.Height / 2));
        var midX = start.X + ((end.X - start.X) / 2);
        using var pen = new Pen(color, 2.4f)
        {
            StartCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLine(pen, start.X, start.Y, midX, start.Y);
        graphics.DrawLine(pen, midX, start.Y, midX, end.Y);
        // Arrow tip
        using var arrowPen = new Pen(color, 2.4f) { EndCap = LineCap.ArrowAnchor };
        graphics.DrawLine(arrowPen, midX, end.Y, end.X, end.Y);
    }
}
