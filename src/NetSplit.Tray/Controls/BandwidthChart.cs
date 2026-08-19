using NetSplit.Core;
using System.Drawing.Drawing2D;

namespace NetSplit.Tray;

/// <summary>
/// Custom WinForms control that paints a dual-line bandwidth history chart
/// for the direct (NIC1) and proxy (NIC2) adapters.
/// </summary>
public sealed class BandwidthChart : Control
{
    private const int PadLeft = 56;
    private const int PadRight = 8;
    private const int PadTop = 4;
    private const int PadBottom = 4;
    private const int GridLines = 2;
    private const long MinPeakBps = 512L * 1024; // 512 KB/s floor so the chart never looks flat

    private IReadOnlyList<TrafficPoint> _history = [];

    public BandwidthChart()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw,
            true);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "最近网络带宽趋势";
        ThemeManager.Changed += OnThemeChanged;
    }

    public void SetHistory(IReadOnlyList<TrafficPoint> history)
    {
        _history = history;
        if (history.Count > 0)
        {
            var current = history[^1];
            AccessibleDescription =
                $"直连 {FormatBps(SumRates(current.DirectReceiveBps, current.DirectSendBps))}，"
                + $"代理 {FormatBps(SumRates(current.ProxyReceiveBps, current.ProxySendBps))}。";
        }
        else
        {
            AccessibleDescription = "尚无带宽历史数据。";
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var theme = ThemeManager.Current;
        var bounds = ClientRectangle;
        var padLeft = UiMetrics.Scale(this, PadLeft);
        var padRight = UiMetrics.Scale(this, PadRight);
        var padTop = UiMetrics.Scale(this, PadTop);
        var padBottom = UiMetrics.Scale(this, PadBottom);

        using var bgBrush = new SolidBrush(theme.BackgroundSurface);
        g.FillRectangle(bgBrush, bounds);

        var chartArea = new Rectangle(
            bounds.Left + padLeft,
            bounds.Top + padTop,
            bounds.Width - padLeft - padRight,
            bounds.Height - padTop - padBottom);

        if (chartArea.Width < 4 || chartArea.Height < 4)
        {
            return;
        }

        var pts = _history;
        var peak = MinPeakBps;
        foreach (var p in pts)
        {
            peak = Math.Max(
                peak,
                Math.Max(
                    SumRates(p.DirectReceiveBps, p.DirectSendBps),
                    SumRates(p.ProxyReceiveBps, p.ProxySendBps)));
        }

        peak = NiceCeiling(peak);

        DrawGrid(g, theme, chartArea, peak);

        if (pts.Count >= 2)
        {
            DrawLine(g, theme.ChartDirect, DashStyle.Solid, pts, chartArea, peak,
                p => SumRates(p.DirectReceiveBps, p.DirectSendBps));
            DrawLine(g, theme.ChartProxy, DashStyle.Dash, pts, chartArea, peak,
                p => SumRates(p.ProxyReceiveBps, p.ProxySendBps));
        }
    }

    private static void DrawGrid(
        Graphics g,
        UiTheme theme,
        Rectangle chart,
        long peak)
    {
        using var gridPen = new Pen(theme.Border)
        {
            DashStyle = DashStyle.Dot
        };
        using var labelBrush = new SolidBrush(theme.TextMuted);
        var labelFont = UiFonts.Badge;

        for (var i = 1; i <= GridLines; i++)
        {
            var fraction = (float)i / (GridLines + 1);
            var y = chart.Bottom - (int)(chart.Height * fraction);
            g.DrawLine(gridPen, chart.Left, y, chart.Right, y);

            var bps = peak * (double)fraction;
            var label = FormatBps(bps);
            var sz = g.MeasureString(label, labelFont);
            g.DrawString(
                label,
                labelFont,
                labelBrush,
                chart.Left - sz.Width - 3,
                y - sz.Height / 2);
        }
    }

    private static void DrawLine(
        Graphics g,
        Color color,
        DashStyle dashStyle,
        IReadOnlyList<TrafficPoint> pts,
        Rectangle chart,
        long peak,
        Func<TrafficPoint, long> selector)
    {
        var points = new PointF[pts.Count];
        var firstTimestamp = pts[0].Timestamp;
        var totalSeconds = (pts[^1].Timestamp - firstTimestamp).TotalSeconds;
        var useTimestamps = totalSeconds > 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var fraction = useTimestamps
                ? Math.Clamp(
                    (pts[i].Timestamp - firstTimestamp).TotalSeconds / totalSeconds,
                    0,
                    1)
                : (double)i / (pts.Count - 1);
            var x = chart.Left + (float)fraction * chart.Width;
            var val = Math.Max(0, selector(pts[i]));
            var y = chart.Bottom - (float)(val / (double)peak * chart.Height);
            y = Math.Clamp(y, chart.Top, chart.Bottom);
            points[i] = new PointF(x, y);
        }

        using var pen = new Pen(color, 1.6f)
        {
            DashStyle = dashStyle
        };
        g.DrawLines(pen, points);

        var fillPoints = new PointF[pts.Count + 2];
        fillPoints[0] = new PointF(chart.Left, chart.Bottom);
        for (var i = 0; i < pts.Count; i++)
        {
            fillPoints[i + 1] = points[i];
        }
        fillPoints[pts.Count + 1] = new PointF(chart.Right, chart.Bottom);

        using var fillBrush = new SolidBrush(Color.FromArgb(18, color));
        g.FillPolygon(fillBrush, fillPoints);
    }

    private static string FormatBps(double bps)
    {
        return bps switch
        {
            >= 1024 * 1024 => $"{bps / (1024 * 1024):F1} MB/s",
            >= 1024 => $"{bps / 1024:F0} KB/s",
            _ => $"{bps:F0} B/s"
        };
    }

    internal static long NiceCeiling(long value)
    {
        if (value <= MinPeakBps)
        {
            return MinPeakBps;
        }

        // Round up to nearest 1, 2, 5 × 10^n
        long magnitude = 1;
        while (magnitude <= value / 10)
        {
            magnitude *= 10;
        }

        foreach (var multiplier in new[] { 1L, 2L, 5L, 10L })
        {
            if (magnitude > long.MaxValue / multiplier)
            {
                return long.MaxValue;
            }

            var candidate = magnitude * multiplier;
            if (candidate >= value)
            {
                return candidate;
            }
        }

        return long.MaxValue;
    }

    internal static long SumRates(long receive, long send)
    {
        var safeReceive = Math.Max(0L, receive);
        var safeSend = Math.Max(0L, send);
        return safeReceive > long.MaxValue - safeSend
            ? long.MaxValue
            : safeReceive + safeSend;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }
}
