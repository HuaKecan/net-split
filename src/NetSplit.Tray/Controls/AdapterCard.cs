using System.Drawing.Drawing2D;
using NetSplit.Core;

namespace NetSplit.Tray;

public static class UiFormat
{
    public static string Rate(long bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            >= 1024 * 1024 => $"{bytesPerSecond / 1024d / 1024d:0.0} MiB/s",
            >= 1024 => $"{bytesPerSecond / 1024d:0.0} KiB/s",
            _ => $"{bytesPerSecond} B/s"
        };
    }

    public static string RelativeTime(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "从未";
        }

        var span = DateTimeOffset.UtcNow - timestamp.Value;
        return span switch
        {
            _ when span.TotalMinutes < 1 => "刚刚",
            _ when span.TotalHours < 1 => $"{(int)span.TotalMinutes} 分钟前",
            _ when span.TotalDays < 1 => $"{(int)span.TotalHours} 小时前",
            _ => $"{(int)span.TotalDays} 天前"
        };
    }
}

public sealed class Sparkline : Control
{
    private const int MaxPoints = 60;
    private readonly List<float> _values = [];
    private Color _color = ThemeManager.Current.Accent;

    public Sparkline()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    public void SetColor(Color color)
    {
        _color = color;
        Invalidate();
    }

    public void Add(float value)
    {
        _values.Add(value);
        while (_values.Count > MaxPoints)
        {
            _values.RemoveAt(0);
        }

        Invalidate();
    }

    public void Clear()
    {
        _values.Clear();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiDrawing.Backdrop(this));
        if (_values.Count < 2)
        {
            return;
        }

        var max = _values.Max();
        var min = _values.Min();
        var range = Math.Max(1, max - min);
        var points = new PointF[_values.Count];
        for (var i = 0; i < _values.Count; i++)
        {
            var x = i * (Width - 1f) / (_values.Count - 1f);
            var normalized = (_values[i] - min) / range;
            var y = Height - 2f - (normalized * (Height - 4f));
            points[i] = new PointF(x, y);
        }

        using var fillPath = new GraphicsPath();
        fillPath.AddLines(points);
        fillPath.AddLine(points[^1].X, points[^1].Y, Width, Height);
        fillPath.AddLine(Width, Height, 0, Height);
        fillPath.CloseFigure();
        // Vertical gradient fill: color at top → transparent at bottom
        if (Height > 1)
        {
            using var gradBrush = new LinearGradientBrush(
                new Point(0, 0),
                new Point(0, Height),
                UiDrawing.WithAlpha(_color, 55),
                UiDrawing.WithAlpha(_color, 0));
            graphics.FillPath(gradBrush, fillPath);
        }

        using var pen = new Pen(_color, 1.6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLines(pen, points);
    }
}

public sealed class AdapterCard : Card
{
    private readonly Label _statusValue;
    private readonly Label _nameValue;
    private readonly Label _ipValue;
    private readonly Label _receiveValue;
    private readonly Label _sendValue;
    private readonly Sparkline _sparkline;

    public AdapterCard(string role)
    {
        var theme = ThemeManager.Current;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = theme.BackgroundSurface;
        Padding = new Padding(UiMetrics.SpaceLg, UiMetrics.SpaceMd, UiMetrics.SpaceLg, UiMetrics.SpaceMd);

        // Compact horizontal layout: [role+status] [name] [IP] [↓rate ↑rate] [sparkline]
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); // role+status
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));  // name
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));  // IP
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // rates
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // sparkline
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Col 0: role label + status stacked
        var roleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        roleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        roleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        roleStack.Controls.Add(new Label
        {
            Text = role,
            Font = UiFonts.Badge,
            ForeColor = theme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty
        }, 0, 0);
        _statusValue = new Label
        {
            Text = "● 离线",
            Font = UiFonts.Badge,
            ForeColor = theme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };
        roleStack.Controls.Add(_statusValue, 0, 1);
        table.Controls.Add(roleStack, 0, 0);

        // Col 1: adapter name
        _nameValue = new Label
        {
            Text = "—",
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(UiMetrics.SpaceXs, 0, UiMetrics.SpaceXs, 0)
        };
        table.Controls.Add(_nameValue, 1, 0);

        // Col 2: IP address
        _ipValue = new Label
        {
            Text = "—",
            Font = UiFonts.Mono,
            ForeColor = theme.TextSecondary,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(UiMetrics.SpaceXs, 0, UiMetrics.SpaceXs, 0)
        };
        table.Controls.Add(_ipValue, 2, 0);

        // Col 3: ↓ receive / ↑ send stacked
        var rates = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        rates.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rates.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _receiveValue = new Label
        {
            Text = "↓ 0 B/s",
            Font = UiFonts.Mono,
            ForeColor = theme.ChartDirect,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty
        };
        _sendValue = new Label
        {
            Text = "↑ 0 B/s",
            Font = UiFonts.Mono,
            ForeColor = theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };
        rates.Controls.Add(_receiveValue, 0, 0);
        rates.Controls.Add(_sendValue, 0, 1);
        table.Controls.Add(rates, 3, 0);

        // Col 4: sparkline
        _sparkline = new Sparkline
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(UiMetrics.SpaceXs, 2, 0, 2)
        };
        table.Controls.Add(_sparkline, 4, 0);
        Controls.Add(table);
    }

    public void SetData(
        NetworkAdapterSnapshot? adapter,
        bool available,
        long receiveRate,
        long sendRate)
    {
        var theme = ThemeManager.Current;
        if (adapter is null)
        {
            _nameValue.Text = "未绑定";
            _nameValue.ForeColor = theme.TextMuted;
            _ipValue.Text = "—";
            _statusValue.Text = "● 离线";
            _statusValue.ForeColor = theme.TextMuted;
            _receiveValue.Text = "↓ 0 B/s";
            _sendValue.Text = "↑ 0 B/s";
            _sparkline.SetColor(theme.TextMuted);
            _sparkline.Clear();
            return;
        }

        var ip = adapter.Ipv4Addresses.Count > 0 ? adapter.Ipv4Addresses[0] : "无 IPv4";
        _nameValue.Text = adapter.Name;
        _nameValue.ForeColor = theme.TextPrimary;
        _ipValue.Text = ip;
        _statusValue.Text = adapter.IsUp ? "● 正常" : "● 离线";
        _statusValue.ForeColor = adapter.IsUp ? theme.Success : theme.Danger;
        _receiveValue.Text = $"↓ {UiFormat.Rate(receiveRate)}";
        _receiveValue.ForeColor = available ? theme.ChartDirect : theme.TextMuted;
        _sendValue.Text = $"↑ {UiFormat.Rate(sendRate)}";
        _sparkline.SetColor(available ? theme.ChartDirect : theme.Warning);
        _sparkline.Add(receiveRate + sendRate);
    }
}
