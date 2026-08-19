using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class OverviewPage : PageBase
{
    private readonly Action _navigateToProxies;
    private readonly ModeBadge _modeBadge = new();

    // Per-label fade state: tracks the target color so we can animate toward it
    private readonly Dictionary<Label, FadeState> _labelFades = [];
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 16 }; // ~60fps
    private readonly Label _statusDetail = new();
    private readonly Label _updatedValue = new();
    private readonly Label _nextAction = new();
    private readonly Label _tunValue = new();
    private readonly Label _directValue = new();
    private readonly Label _proxyValue = new();
    private readonly FlowDiagram _flow = new();
    private readonly AdapterCard _directCard;
    private readonly AdapterCard _proxyCard;
    private readonly MetricCard _directMetric = new("直连出口");
    private readonly MetricCard _proxyMetric = new("代理出口");
    private readonly MetricCard _nodeMetric = new("当前节点");
    private readonly MetricCard _poolMetric = new("代理池");
    private readonly BandwidthChart _bandwidthChart = new();
    private readonly Label _chartDirectValue = new();
    private readonly Label _chartProxyValue = new();
    private readonly Label _nodeValue = new();
    private readonly Label _nodeDelay = new();

    public OverviewPage(NamedPipeRpcClient client, Action navigateToProxies)
        : base(
            client,
            "概览",
            "集中查看透明分流、物理出口和当前代理链路的实时运行状态。")
    {
        _navigateToProxies = navigateToProxies;
        _directCard = new AdapterCard("网卡1  ·  国内直连");
        _proxyCard = new AdapterCard("网卡2  ·  代理出口");
        _fadeTimer.Tick += OnFadeTick;
        BuildUi();
    }

    private void BuildUi()
    {
        var theme = ThemeManager.Current;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // At minimum width the active exit can wrap onto a second line. Keep
        // room for the real font metrics at 96 DPI; UiLayout scales this on
        // high-DPI displays.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); // status
        root.RowStyles.Add(new RowStyle(
            SizeType.Absolute,
            Math.Max(
                128,
                UiFonts.Caption.Height
                + UiFonts.Metric.Height
                + UiFonts.Caption.Height
                + (UiMetrics.SpaceMd * 4)))); // metrics
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));  // bandwidth chart
        // FlowDiagram has a 116px minimum height, and the card also needs
        // room for its heading and vertical padding. A fixed base row keeps
        // TableLayout from collapsing this section inside the scroll host.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));   // flow + health
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // adapter cards (compact)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // node bar
        root.Controls.Add(BuildStatusSection(), 0, 0);
        root.Controls.Add(BuildMetricsSection(), 0, 1);
        root.Controls.Add(BuildChartSection(), 0, 2);
        root.Controls.Add(BuildFlowAndHealthSection(), 0, 3);
        root.Controls.Add(BuildAdapterSection(), 0, 4);
        root.Controls.Add(BuildNodeSection(), 0, 5);
        Content.Controls.Add(root);
    }

    private Card BuildChartSection()
    {
        var theme = ThemeManager.Current;
        var section = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceXl,
                UiMetrics.SpaceSm,
                UiMetrics.SpaceXl,
                UiMetrics.SpaceSm),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(UiStyle.SectionTitle("实时带宽"), 0, 0);

        ConfigureChartLegendLabel(
            _chartDirectValue,
            "━━ 直连 —",
            theme.ChartDirect);
        _chartDirectValue.Margin = new Padding(0, 2, UiMetrics.SpaceLg, 0);
        heading.Controls.Add(_chartDirectValue, 1, 0);

        ConfigureChartLegendLabel(
            _chartProxyValue,
            "┄┄ 代理 —",
            theme.ChartProxy);
        _chartProxyValue.Margin = new Padding(0, 2, 0, 0);
        heading.Controls.Add(_chartProxyValue, 2, 0);
        table.Controls.Add(heading, 0, 0);

        _bandwidthChart.Dock = DockStyle.Fill;
        _bandwidthChart.BackColor = theme.BackgroundSurface;
        table.Controls.Add(_bandwidthChart, 0, 1);
        section.Controls.Add(table);
        return section;
    }

    private static void ConfigureChartLegendLabel(
        Label label,
        string text,
        Color color)
    {
        label.Text = text;
        label.Font = UiFonts.Mono;
        label.ForeColor = color;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Right;
    }

    private Card BuildStatusSection()
    {
        var theme = ThemeManager.Current;
        var section = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd,
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var state = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        _modeBadge.Margin = Padding.Empty;
        state.Controls.Add(_modeBadge);

        _statusDetail.AutoSize = true;
        _statusDetail.Font = UiFonts.BodyStrong;
        _statusDetail.ForeColor = theme.TextPrimary;
        _statusDetail.Text = "正在读取运行状态…";
        _statusDetail.Margin = new Padding(0, UiMetrics.SpaceSm, 0, 0);
        state.Controls.Add(_statusDetail);

        _updatedValue.AutoSize = true;
        _updatedValue.Font = UiFonts.Caption;
        _updatedValue.ForeColor = theme.TextMuted;
        _updatedValue.Text = "最后更新 —";
        _updatedValue.Margin = new Padding(0, 2, 0, 0);
        state.Controls.Add(_updatedValue);

        _nextAction.AutoSize = true;
        _nextAction.Font = UiFonts.Caption;
        _nextAction.ForeColor = theme.TextMuted;
        _nextAction.Text = "下一步：正在读取服务建议…";
        _nextAction.MaximumSize = new Size(620, 0);
        _nextAction.Margin = new Padding(0, 2, 0, 0);
        state.Controls.Add(_nextAction);
        table.Controls.Add(state, 0, 0);
        table.Controls.Add(BuildStatusActions(), 1, 0);
        section.Controls.Add(table);
        return section;
    }

    private Card BuildRuntimeHealthSection()
    {
        var theme = ThemeManager.Current;
        var section = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd,
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.334f));

        var heading = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        heading.Controls.Add(UiStyle.SectionTitle("接口健康"));
        heading.Controls.Add(UiStyle.MutedText("物理出口、TUN 与 DNS 状态"));
        table.Controls.Add(heading, 0, 0);
        table.Controls.Add(
            HealthLine("TUN 与 DNS", "Fake-IP · IPv4 严格路由", _tunValue),
            0,
            1);
        table.Controls.Add(
            HealthLine("网卡1 · 主宽带", "国内直连出口", _directValue),
            0,
            2);
        table.Controls.Add(
            HealthLine("网卡2 · F50", "代理节点物理出口", _proxyValue),
            0,
            3);
        section.Controls.Add(table);
        return section;
    }

    private static TableLayoutPanel HealthLine(string label, string detail, Label value)
    {
        var theme = ThemeManager.Current;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiMetrics.SpaceXs, 0, UiMetrics.SpaceXs)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        row.Controls.Add(new Label
        {
            Text = label,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        }, 0, 0);

        value.Text = "读取中";
        value.Font = UiFonts.CaptionStrong;
        value.ForeColor = theme.TextSecondary;
        value.AutoSize = true;
        value.Anchor = AnchorStyles.Right;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.AccessibleName = $"{label}状态";
        value.Margin = new Padding(UiMetrics.SpaceSm, 0, 0, 0);
        row.Controls.Add(value, 1, 0);

        var detailLabel = new Label
        {
            Text = detail,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 2, 0, 0)
        };
        row.Controls.Add(detailLabel, 0, 1);
        row.SetColumnSpan(detailLabel, 2);
        return row;
    }

    private TableLayoutPanel BuildStatusActions()
    {
        var theme = ThemeManager.Current;
        var actions = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddAction(
            actions,
            "校验配置",
            ButtonKind.Accent,
            (_, _) => _ = ValidateAsync(),
            UiGlyphs.Validate,
            0,
            0);
        AddAction(
            actions,
            "刷新",
            ButtonKind.Secondary,
            (_, _) => _ = RefreshAsync(),
            UiGlyphs.Refresh,
            1,
            0);
        AddAction(
            actions,
            "修复网络",
            ButtonKind.Link,
            (_, _) => _ = RepairAsync(),
            UiGlyphs.Repair,
            0,
            1);
        AddAction(
            actions,
            "回退配置",
            ButtonKind.Link,
            (_, _) => _ = RollbackAsync(),
            UiGlyphs.Rollback,
            1,
            1);
        return actions;
    }

    private TableLayoutPanel BuildMetricsSection()
    {
        var theme = ThemeManager.Current;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 7,
            RowCount = 1,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.SpaceMd));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.SpaceMd));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.SpaceMd));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddMetric(grid, _directMetric, 0);
        AddMetric(grid, _proxyMetric, 2);
        AddMetric(grid, _nodeMetric, 4);
        AddMetric(grid, _poolMetric, 6);
        return grid;
    }

    private static void AddMetric(TableLayoutPanel grid, MetricCard metric, int column)
    {
        metric.Dock = DockStyle.Fill;
        metric.Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd);
        grid.Controls.Add(metric, column, 0);
    }

    private TableLayoutPanel BuildFlowAndHealthSection()
    {
        var theme = ThemeManager.Current;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.SpaceMd));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(BuildFlowSection(), 0, 0);
        grid.Controls.Add(BuildRuntimeHealthSection(), 2, 0);
        return grid;
    }

    private Card BuildFlowSection()
    {
        var theme = ThemeManager.Current;
        var section = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd,
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(UiStyle.SectionTitle("实时分流路径"), 0, 0);
        heading.Controls.Add(new Label
        {
            Text = "绿色为直连  ·  蓝色为代理",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 1, 0, 0)
        }, 1, 0);
        table.Controls.Add(heading, 0, 0);

        _flow.Dock = DockStyle.Fill;
        _flow.Margin = Padding.Empty;
        table.Controls.Add(_flow, 0, 1);
        section.Controls.Add(table);
        return section;
    }

    private TableLayoutPanel BuildAdapterSection()
    {
        var theme = ThemeManager.Current;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.SpaceMd));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _directCard.Dock = DockStyle.Fill;
        _directCard.Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd);
        _proxyCard.Dock = DockStyle.Fill;
        _proxyCard.Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd);
        grid.Controls.Add(_directCard, 0, 0);
        grid.Controls.Add(_proxyCard, 2, 0);
        return grid;
    }

    private Card BuildNodeSection()
    {
        var theme = ThemeManager.Current;
        var section = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd,
                UiMetrics.SpaceXl,
                UiMetrics.SpaceMd),
            Margin = Padding.Empty
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));

        table.Controls.Add(new Label
        {
            Text = "当前节点",
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _nodeValue.Font = UiFonts.BodyStrong;
        _nodeValue.ForeColor = theme.TextPrimary;
        _nodeValue.Text = "未连接";
        _nodeValue.AutoEllipsis = true;
        _nodeValue.Dock = DockStyle.Fill;
        _nodeValue.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(_nodeValue, 1, 0);

        _nodeDelay.Font = UiFonts.Mono;
        _nodeDelay.ForeColor = theme.TextSecondary;
        _nodeDelay.Text = "—";
        _nodeDelay.Dock = DockStyle.Fill;
        _nodeDelay.TextAlign = ContentAlignment.MiddleLeft;
        table.Controls.Add(_nodeDelay, 2, 0);

        var switchButton = CreateButton(
            "切换节点",
            ButtonKind.Secondary,
            (_, _) => _navigateToProxies(),
            100,
            UiGlyphs.Proxies);
        switchButton.Anchor = AnchorStyles.Right;
        table.Controls.Add(switchButton, 3, 0);
        section.Controls.Add(table);
        return section;
    }

    private static void AddAction(
        TableLayoutPanel parent,
        string text,
        ButtonKind kind,
        EventHandler handler,
        string glyph,
        int column,
        int row)
    {
        var button = CreateButton(text, kind, handler, 104, glyph);
        button.Margin = new Padding(UiMetrics.SpaceXs, UiMetrics.SpaceXs, 0, 0);
        parent.Controls.Add(button, column, row);
    }

    private async Task ValidateAsync()
    {
        await RunActionAsync(async () =>
        {
            var result = await Client.SendAsync<ConfigurationValidationResult>(
                RpcCommands.Validate,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            var text = result?.IsValid == true
                ? "配置校验通过。"
                : string.Join(Environment.NewLine, result?.Errors ?? []);
            if (result is { Warnings.Count: > 0 })
            {
                text += Environment.NewLine
                    + Environment.NewLine
                    + "警告："
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, result.Warnings);
            }

            MessageBox.Show(
                this,
                text,
                "配置校验",
                MessageBoxButtons.OK,
                result?.IsValid == true
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Error);
        }).ConfigureAwait(true);
    }

    private async Task RepairAsync()
    {
        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.Repair,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            ShowInfo("已重建配置并重新检查 Mihomo。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RollbackAsync()
    {
        var answer = MessageBox.Show(
            this,
            "将停止当前 Mihomo，并恢复上次验证成功的配置。订阅与网卡设置不会改变。",
            "回退配置",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.Rollback,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            ShowInfo("已恢复上次可用配置。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            var statusTask = Client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);
            var adaptersTask = Client.SendAsync<IReadOnlyList<NetworkAdapterSnapshot>>(
                RpcCommands.Discover,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);
            var historyTask = TryGetTrafficHistoryAsync(cancellationToken);

            var status = await statusTask.ConfigureAwait(true) ?? new RuntimeStatus();
            var adapters = await adaptersTask.ConfigureAwait(true) ?? [];

            var history = await historyTask.ConfigureAwait(true);
            if (history is not null)
            {
                _bandwidthChart.SetHistory(history);
            }

            _modeBadge.SetMode(status.Mode);
            var delayText = !string.IsNullOrWhiteSpace(status.CurrentProxy)
                            && status.ProxyDelayMilliseconds is { } delay
                ? $"{delay} ms"
                : "—";
            var theme = ThemeManager.Current;
            var directRate = UiFormat.Rate(
                status.DirectTraffic.ReceiveBytesPerSecond
                + status.DirectTraffic.SendBytesPerSecond);
            var proxyRate = UiFormat.Rate(
                status.ProxyTraffic.ReceiveBytesPerSecond
                + status.ProxyTraffic.SendBytesPerSecond);
            _directMetric.SetValue(
                directRate,
                string.IsNullOrWhiteSpace(status.DirectAdapterName)
                    ? "网卡1 · 尚未绑定"
                    : $"网卡1 · {status.DirectAdapterName}",
                status.DirectAdapterAvailable ? theme.ChartDirect : theme.Danger);
            _proxyMetric.SetValue(
                proxyRate,
                string.IsNullOrWhiteSpace(status.ProxyAdapterName)
                    ? "网卡2 · 尚未绑定"
                    : $"网卡2 · {status.ProxyAdapterName}",
                status.ProxyAdapterAvailable
                    && (!status.ProxyRouteHealthKnown || status.ProxyRouteAvailable)
                    ? theme.ChartProxy
                    : theme.Danger);
            _chartDirectValue.Text = $"━━ 直连 {directRate}";
            _chartProxyValue.Text = $"┄┄ 代理 {proxyRate}";
            _nodeMetric.SetValue(
                delayText,
                $"{DisplayProxy(EffectiveProxy(status))} · "
                + (status.CurrentProxy.Equals(
                       MihomoConfigGenerator.AutoProxyGroupName,
                       StringComparison.Ordinal)
                    ? "自动选择"
                    : "手动选择"),
                status.ProxyDelayMilliseconds is null
                    ? theme.TextMuted
                    : status.ProxyDelayMilliseconds <= 120
                        ? theme.Success
                        : theme.Warning);
            var availableNodeCount = status.AvailableProxies.Count(name =>
                !name.Equals(
                    MihomoConfigGenerator.AutoProxyGroupName,
                    StringComparison.Ordinal));
            var poolValue = status.ProxyRouteHealthKnown
                ? $"{status.HealthyProxyCount}/{availableNodeCount}"
                : $"{availableNodeCount}";
            var poolCaption = !status.MihomoRunning
                ? "Mihomo 未运行"
                : status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable
                    ? "无可用节点 · 国外阻断"
                    : "URL-test 自动选择";
            if (status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable)
            {
                poolCaption = ModeVisuals.ProxyRouteText(status.ProxyRouteFailure);
            }
            _poolMetric.SetValue(
                poolValue,
                poolCaption,
                status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable
                    ? theme.Danger
                    : availableNodeCount > 0
                        ? theme.TextPrimary
                        : theme.Warning);
            var dnsHealthy = status.DnsStatusKnown && status.DnsEnabled;
            _statusDetail.Text = status.ProxyRouteHealthKnown
                && !status.ProxyRouteAvailable
                && status.ProxyAdapterAvailable
                ? "代理组暂无健康节点，国外流量已阻断  ·  国内直连仍可用"
                : status.TunEnabled && dnsHealthy
                    ? $"透明分流已接管 IPv4 流量  ·  {DisplayProxy(EffectiveProxy(status))}"
                    : status.TunEnabled && !status.DnsStatusKnown
                        ? "TUN 已接管，但 DNS 状态尚未确认"
                        : status.TunEnabled
                            ? "TUN 已接管，但 DNS 未启用"
                    : "系统流量未被接管，当前保持普通网络模式";
            _updatedValue.Text = $"最后更新 {status.UpdatedAt.ToLocalTime():HH:mm:ss}";
            SetNextAction(status);
            SetTunDnsHealthValue(_tunValue, status);
            var directTargetColor = status.DirectAdapterAvailable
                ? theme.Success : theme.TextMuted;
            SetHealthValue(_directValue, status.DirectAdapterAvailable,
                status.DirectAdapterAvailable ? "可用" : "不可用");
            FadeToColor(_directValue, directTargetColor);
            if (status.ProxyAdapterAvailable
                && status.ProxyRouteHealthKnown
                && !status.ProxyRouteAvailable)
            {
                _proxyValue.Text = "△  节点不可用";
                FadeToColor(_proxyValue, theme.Danger);
            }
            else
            {
                var proxyTargetColor = status.ProxyAdapterAvailable
                    ? theme.Success : theme.TextMuted;
                SetHealthValue(_proxyValue, status.ProxyAdapterAvailable,
                    status.ProxyAdapterAvailable ? "可用" : "不可用");
                FadeToColor(_proxyValue, proxyTargetColor);
            }
            _flow.SetState(
                status.TunEnabled,
                status.DirectAdapterAvailable,
                status.ProxyAdapterAvailable
                && (!status.ProxyRouteHealthKnown || status.ProxyRouteAvailable),
                status.DirectDelayMilliseconds,
                status.ProxyDelayMilliseconds);

            var direct = adapters.FirstOrDefault(adapter =>
                adapter.Name.Equals(
                    status.DirectAdapterName,
                    StringComparison.OrdinalIgnoreCase));
            var proxy = adapters.FirstOrDefault(adapter =>
                adapter.Name.Equals(
                    status.ProxyAdapterName,
                    StringComparison.OrdinalIgnoreCase));
            _directCard.SetData(
                direct,
                status.DirectAdapterAvailable,
                status.DirectTraffic.ReceiveBytesPerSecond,
                status.DirectTraffic.SendBytesPerSecond);
            _proxyCard.SetData(
                proxy,
                status.ProxyAdapterAvailable,
                status.ProxyTraffic.ReceiveBytesPerSecond,
                status.ProxyTraffic.SendBytesPerSecond);

            _nodeValue.Text = DisplayProxy(EffectiveProxy(status));
            _nodeDelay.Text = delayText;
            if (!string.IsNullOrWhiteSpace(status.LastError)
                && status.Mode is not RuntimeMode.Healthy
                && status.Mode is not RuntimeMode.Disabled)
            {
                ShowWarning(status.LastError);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法连接服务：{exception.Message}");
        }
    }

    private async Task<IReadOnlyList<TrafficPoint>?> TryGetTrafficHistoryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await Client.SendAsync<IReadOnlyList<TrafficPoint>>(
                RpcCommands.GetTrafficHistory,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"net-split traffic history refresh timed out: {exception.Message}");
            return null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"net-split traffic history refresh skipped: {exception.Message}");
            return null;
        }
    }

    private static void SetHealthValue(Label label, bool healthy, string text)
    {
        label.Text = healthy ? $"●  {text}" : $"○  {text}";
        label.ForeColor = healthy
            ? ThemeManager.Current.Success
            : ThemeManager.Current.TextMuted;
    }

    // Animate label ForeColor toward targetColor over ~150ms
    private void FadeToColor(Label label, Color targetColor)
    {
        if (label.ForeColor == targetColor)
        {
            return;
        }

        _labelFades[label] = new FadeState(label.ForeColor, targetColor, 0f);
        if (!_fadeTimer.Enabled)
        {
            _fadeTimer.Start();
        }
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        const float step = 0.12f; // ~150ms at 16ms interval
        var done = true;
        foreach (var (label, state) in _labelFades.ToList())
        {
            var t = Math.Min(1f, state.T + step);
            label.ForeColor = UiDrawing.Blend(state.Target, state.From, 1f - t);
            if (t >= 1f)
            {
                _labelFades.Remove(label);
            }
            else
            {
                _labelFades[label] = state with { T = t };
                done = false;
            }
        }

        if (done)
        {
            _fadeTimer.Stop();
        }
    }

    private record FadeState(Color From, Color Target, float T);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Stop();
            _fadeTimer.Tick -= OnFadeTick;
            _fadeTimer.Dispose();
            _labelFades.Clear();
        }

        base.Dispose(disposing);
    }

    private static void SetTunDnsHealthValue(Label label, RuntimeStatus status)
    {
        var theme = ThemeManager.Current;
        if (!status.TunEnabled)
        {
            SetHealthValue(label, false, "未启用");
            return;
        }

        if (!status.DnsStatusKnown)
        {
            label.Text = "△  DNS 未确认";
            label.ForeColor = theme.Warning;
            return;
        }

        label.Text = status.DnsEnabled
            ? "●  已接管"
            : "○  DNS 未启用";
        label.ForeColor = status.DnsEnabled ? theme.Success : theme.Danger;
    }

    private void SetNextAction(RuntimeStatus status)
    {
        var theme = ThemeManager.Current;
        var (text, color) = status.Mode switch
        {
            RuntimeMode.Healthy when status.ProxyRouteHealthKnown
                && !status.ProxyRouteAvailable
                => (
                    "下一步：检查代理节点或更新订阅；国外流量会继续阻断。",
                    theme.Warning),
            RuntimeMode.Healthy when !status.DnsStatusKnown
                => (
                    "下一步：等待 DNS 状态确认，再进行无泄漏验收。",
                    theme.Warning),
            RuntimeMode.Healthy
                => (
                    "下一步：无需操作；国内直连与国外代理正在按规则分流。",
                    theme.Success),
            RuntimeMode.Disabled
                => (
                    "下一步：确认两张网卡角色后，可从顶部开关开启分流。",
                    theme.TextMuted),
            RuntimeMode.Starting or RuntimeMode.Stopping
                => (
                    "下一步：等待服务完成状态同步。",
                    theme.Warning),
            RuntimeMode.DirectUnavailable
                => (
                    "下一步：检查网卡1连接；不会自动改用网卡2承载国内直连。",
                    theme.Warning),
            RuntimeMode.ProxyUnavailable when !status.ProxyAdapterAvailable
                => (
                    "下一步：重新连接 F50；国外流量保持阻断，不会绕过代理。",
                    theme.Danger),
            RuntimeMode.ProxyUnavailable
                => (
                    "下一步：打开“代理节点”页测速或更新订阅。",
                    theme.Warning),
            RuntimeMode.CoreUnavailable or RuntimeMode.Misconfigured
                => (
                    "下一步：先运行配置校验；仍失败时打开“诊断”导出报告。",
                    theme.Danger),
            _ => (
                "下一步：打开“诊断”查看服务、网卡和运行文件状态。",
                theme.TextMuted)
        };
        _nextAction.Text = text;
        _nextAction.ForeColor = color;
    }

    private static string DisplayProxy(string proxy)
    {
        if (proxy.Equals(
                MihomoConfigGenerator.ResidentialProxyName,
                StringComparison.Ordinal))
        {
            return "\u4F4F\u5B85 SOCKS5";
        }

        return string.IsNullOrWhiteSpace(proxy) ? "未连接" : proxy;
    }

    private static string EffectiveProxy(RuntimeStatus status)
    {
        return string.IsNullOrWhiteSpace(status.EffectiveProxy)
            ? status.CurrentProxy
            : status.EffectiveProxy;
    }
}
