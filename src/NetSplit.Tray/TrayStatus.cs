using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NetSplit.Core;

namespace NetSplit.Tray;

internal enum TrayHealthLevel
{
    Disabled,
    Transitioning,
    Healthy,
    Degraded,
    Critical
}

internal enum TrayNotificationKind
{
    None,
    Problem,
    Recovered
}

internal sealed class TrayStartupGrace
{
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMinutes(3);

    private readonly DateTimeOffset _expiresAt;
    private bool _serviceConnected;

    public TrayStartupGrace(
        bool enabled,
        DateTimeOffset startedAt,
        TimeSpan? duration = null)
    {
        _expiresAt = enabled
            ? startedAt + (duration ?? DefaultDuration)
            : DateTimeOffset.MinValue;
    }

    public bool ShouldSuppressOffline(DateTimeOffset observedAt)
    {
        return !_serviceConnected && observedAt < _expiresAt;
    }

    public void ObserveConnected()
    {
        _serviceConnected = true;
    }
}

internal sealed record TrayStatusPresentation(
    TrayHealthLevel Health,
    string IssueKey,
    string HeaderText,
    string ToolTipText,
    string NotificationText)
{
    public static TrayStatusPresentation Offline { get; } = new(
        TrayHealthLevel.Critical,
        "service-offline",
        "net-split - 服务离线",
        "net-split · 服务离线",
        "本地管理服务暂时无法连接，托盘会继续自动重试。");
}

internal static class TrayStatusPresenter
{
    private const int NotifyIconTextLimit = 63;

    public static TrayStatusPresentation FromStatus(RuntimeStatus status)
    {
        var health = status.Mode switch
        {
            RuntimeMode.Disabled => TrayHealthLevel.Disabled,
            RuntimeMode.Starting or RuntimeMode.Stopping => TrayHealthLevel.Transitioning,
            RuntimeMode.Healthy => TrayHealthLevel.Healthy,
            RuntimeMode.DirectUnavailable or RuntimeMode.ProxyUnavailable =>
                TrayHealthLevel.Degraded,
            RuntimeMode.CoreUnavailable or RuntimeMode.Misconfigured =>
                TrayHealthLevel.Critical,
            _ => TrayHealthLevel.Critical
        };
        var modeText = ModeVisuals.Text(status);
        var routeText = RouteText(status);
        return new TrayStatusPresentation(
            health,
            $"{status.Mode}:{status.ProxyRouteFailure}",
            $"net-split - {modeText} · {routeText}",
            TruncateNotifyText($"net-split · {ModeVisuals.Text(status.Mode)} · {routeText}"),
            ProblemText(status));
    }

    internal static string TruncateNotifyText(string text)
    {
        if (text.Length <= NotifyIconTextLimit)
        {
            return text;
        }

        return text[..(NotifyIconTextLimit - 1)] + "…";
    }

    private static string RouteText(RuntimeStatus status)
    {
        if (!status.Enabled)
        {
            return "分流关闭";
        }

        if (status.Mode == RuntimeMode.ProxyUnavailable
            && status.ProxyRouteFailure != ProxyRouteFailureReason.None)
        {
            return ModeVisuals.ProxyRouteText(status.ProxyRouteFailure);
        }

        if (status.EffectiveProxy.Equals(
                MihomoConfigGenerator.ResidentialProxyName,
                StringComparison.Ordinal))
        {
            return "住宅 SOCKS5";
        }

        if (status.CurrentProxy.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(status.EffectiveProxy)
                ? "自动选择"
                : $"自动 · {DisplayProxy(status.EffectiveProxy)}";
        }

        var proxy = !string.IsNullOrWhiteSpace(status.CurrentProxy)
            ? status.CurrentProxy
            : status.EffectiveProxy;
        return string.IsNullOrWhiteSpace(proxy)
            ? "出口待定"
            : DisplayProxy(proxy);
    }

    private static string DisplayProxy(string name)
    {
        return name switch
        {
            MihomoConfigGenerator.AutoProxyGroupName => "自动选择",
            MihomoConfigGenerator.ResidentialProxyName => "住宅 SOCKS5",
            MihomoConfigGenerator.DirectProxyName => "国内直连",
            _ => name
        };
    }

    private static string ProblemText(RuntimeStatus status)
    {
        return status.Mode switch
        {
            RuntimeMode.DirectUnavailable =>
                "网卡1当前不可用，国内直连流量已阻断；代理链路仍按当前状态工作。",
            RuntimeMode.ProxyUnavailable =>
                $"{ModeVisuals.ProxyRouteText(status.ProxyRouteFailure)}，"
                + "国外流量已阻断，国内直连继续工作。",
            RuntimeMode.CoreUnavailable =>
                "Mihomo、TUN 或 DNS 未就绪，分流服务正在尝试自动恢复。",
            RuntimeMode.Misconfigured =>
                "当前配置无法安全启用，请打开控制台查看诊断信息。",
            _ => string.Empty
        };
    }
}

internal sealed class TrayNotificationTracker
{
    private TrayObservation? _candidate;
    private int _candidateCount;
    private TrayObservation? _stable;

    public TrayNotificationKind Observe(TrayStatusPresentation presentation)
    {
        if (presentation.Health == TrayHealthLevel.Transitioning)
        {
            _candidate = null;
            _candidateCount = 0;
            return TrayNotificationKind.None;
        }

        var observation = new TrayObservation(
            presentation.Health,
            presentation.IssueKey);
        if (_candidate == observation)
        {
            _candidateCount++;
        }
        else
        {
            _candidate = observation;
            _candidateCount = 1;
        }

        var threshold = presentation.Health is TrayHealthLevel.Healthy
            or TrayHealthLevel.Degraded
            or TrayHealthLevel.Critical
            ? 2
            : 1;
        if (_candidateCount < threshold || _stable == observation)
        {
            return TrayNotificationKind.None;
        }

        var previous = _stable;
        _stable = observation;
        if (presentation.Health is TrayHealthLevel.Degraded or TrayHealthLevel.Critical)
        {
            return TrayNotificationKind.Problem;
        }

        return presentation.Health == TrayHealthLevel.Healthy
               && previous?.Health is TrayHealthLevel.Degraded or TrayHealthLevel.Critical
            ? TrayNotificationKind.Recovered
            : TrayNotificationKind.None;
    }

    private sealed record TrayObservation(
        TrayHealthLevel Health,
        string IssueKey);
}

internal sealed class TrayIconSet : IDisposable
{
    public Icon Disabled { get; } = Create(Color.FromArgb(0x7A, 0x87, 0x97));
    public Icon Transitioning { get; } = Create(Color.FromArgb(0xD3, 0x8B, 0x18));
    public Icon Healthy { get; } = Create(Color.FromArgb(0x1D, 0x9E, 0x75));
    public Icon Degraded { get; } = Create(Color.FromArgb(0xD3, 0x8B, 0x18));
    public Icon Critical { get; } = Create(Color.FromArgb(0xD8, 0x4C, 0x4C));

    public Icon Resolve(TrayHealthLevel health)
    {
        return health switch
        {
            TrayHealthLevel.Disabled => Disabled,
            TrayHealthLevel.Transitioning => Transitioning,
            TrayHealthLevel.Healthy => Healthy,
            TrayHealthLevel.Degraded => Degraded,
            TrayHealthLevel.Critical => Critical,
            _ => Critical
        };
    }

    public void Dispose()
    {
        Disabled.Dispose();
        Transitioning.Dispose();
        Healthy.Dispose();
        Degraded.Dispose();
        Critical.Dispose();
    }

    private static Icon Create(Color statusColor)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(0x0B, 0x6B, 0xD3));
            using var backgroundPath = UiDrawing.Rounded(
                new Rectangle(2, 2, 27, 27),
                6);
            graphics.FillPath(background, backgroundPath);

            using var mark = new Pen(Color.White, 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLine(mark, 9, 9, 9, 22);
            graphics.DrawLine(mark, 9, 9, 22, 22);
            graphics.DrawLine(mark, 22, 9, 22, 22);

            using var border = new SolidBrush(Color.White);
            graphics.FillEllipse(border, 19, 19, 12, 12);
            using var status = new SolidBrush(statusColor);
            graphics.FillEllipse(status, 21, 21, 8, 8);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);
}
