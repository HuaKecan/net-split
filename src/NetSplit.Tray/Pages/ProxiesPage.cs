using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class ProxiesPage : PageBase
{
    private readonly DataGridView _grid = UiGrid.Create();
    private readonly Label _delayLabel = new();
    private readonly Label _currentLabel = new();
    private readonly Label _emptyLabel = new();
    private readonly ThemedButton _airportExitButton = new();
    private readonly ThemedButton _residentialExitButton = new();
    private readonly ThemedButton _autoButton = new();
    private readonly ThemedButton _applyButton = new();
    private ProxyExitMode _exitMode = ProxyExitMode.Airport;

    public ProxiesPage(NamedPipeRpcClient client)
        : base(
            client,
            "代理节点",
            "先选择境外流量的最终出口。机场模式直接使用所选节点；住宅模式下节点仅作为前置链路。")
    {
        BuildUi();
    }

    private void BuildUi()
    {
        var theme = ThemeManager.Current;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildGrid(), 0, 1);
        Content.Controls.Add(root);
    }

    private Card BuildToolbar()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                UiMetrics.SpaceLg,
                UiMetrics.SpaceMd,
                UiMetrics.SpaceLg,
                UiMetrics.SpaceMd),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var state = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Left,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        state.Controls.Add(new Label
        {
            Text = "当前最终出口",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Margin = Padding.Empty
        });
        var currentRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(0, 3, 0, 0)
        };
        _currentLabel.AutoSize = true;
        _currentLabel.Font = UiFonts.Section;
        _currentLabel.ForeColor = theme.TextPrimary;
        _currentLabel.Margin = Padding.Empty;
        _currentLabel.Text = "未连接";
        currentRow.Controls.Add(_currentLabel);

        _delayLabel.AutoSize = true;
        _delayLabel.Font = UiFonts.Mono;
        _delayLabel.ForeColor = theme.TextSecondary;
        _delayLabel.Margin = new Padding(UiMetrics.SpaceLg, 2, 0, 0);
        _delayLabel.Text = "—";
        currentRow.Controls.Add(_delayLabel);
        state.Controls.Add(currentRow);
        toolbar.Controls.Add(state, 0, 0);
        toolbar.Controls.Add(BuildExitModeSelector(), 1, 0);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(UiMetrics.SpaceLg, 0, 0, 0)
        };
        _autoButton.Text = "自动出口";
        _autoButton.Kind = ButtonKind.Secondary;
        _autoButton.Glyph = UiGlyphs.Refresh;
        _autoButton.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _autoButton.SizeToContent(104);
        _autoButton.Click += (_, _) =>
            _ = SelectAsync(MihomoConfigGenerator.AutoProxyGroupName);
        actions.Controls.Add(_autoButton);

        _applyButton.Text = "设为出口";
        _applyButton.Kind = ButtonKind.Accent;
        _applyButton.Glyph = UiGlyphs.Proxies;
        _applyButton.Margin = Padding.Empty;
        _applyButton.SizeToContent(104);
        _applyButton.Click += (_, _) => _ = ApplySelectionAsync();
        actions.Controls.Add(_applyButton);
        toolbar.Controls.Add(actions, 2, 0);
        UpdateExitModeControls();
        card.Controls.Add(toolbar);
        return card;
    }

    private FlowLayoutPanel BuildExitModeSelector()
    {
        var theme = ThemeManager.Current;
        var group = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(UiMetrics.SpaceLg, 0, 0, 0)
        };
        group.Controls.Add(new Label
        {
            Text = "最终出口模式",
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = Padding.Empty
        });

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(0, 3, 0, 0)
        };
        _airportExitButton.Text = "机场节点";
        _airportExitButton.Glyph = UiGlyphs.Proxies;
        _airportExitButton.Margin = new Padding(0, 0, UiMetrics.SpaceXs, 0);
        _airportExitButton.SizeToContent(104);
        _airportExitButton.Click += (_, _) =>
            _ = SetExitModeAsync(ProxyExitMode.Airport);
        buttons.Controls.Add(_airportExitButton);

        _residentialExitButton.Text = "住宅 SOCKS5";
        _residentialExitButton.Glyph = UiGlyphs.ResidentialProxy;
        _residentialExitButton.Margin = Padding.Empty;
        _residentialExitButton.SizeToContent(122);
        _residentialExitButton.Click += (_, _) =>
            _ = SetExitModeAsync(ProxyExitMode.Residential);
        buttons.Controls.Add(_residentialExitButton);
        group.Controls.Add(buttons);
        return group;
    }

    private Card BuildGrid()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            Margin = Padding.Empty
        };
        _grid.AccessibleName = "代理节点列表";
        _grid.Columns.Add(UiGrid.TextColumn("DisplayName", "节点", fill: true));
        _grid.Columns.Add(UiGrid.TextColumn("Strategy", "选择方式", 110));
        _grid.Columns.Add(UiGrid.TextColumn("State", "状态", 90));
        _grid.CellDoubleClick += (_, _) => _ = ApplySelectionAsync();
        _grid.CellFormatting += OnCellFormatting;

        _emptyLabel.Text = "暂无可用节点，请先添加或更新订阅。";
        _emptyLabel.Font = UiFonts.Body;
        _emptyLabel.ForeColor = theme.TextMuted;
        _emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        _emptyLabel.Dock = DockStyle.Fill;
        _emptyLabel.Visible = false;
        _emptyLabel.BackColor = theme.BackgroundSurface;

        card.Controls.Add(_grid);
        card.Controls.Add(_emptyLabel);
        return card;
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0
            || _grid.Rows[e.RowIndex].DataBoundItem is not ProxyRow row)
        {
            return;
        }

        var cellStyle = e.CellStyle;
        if (cellStyle is not null
            && _grid.Columns[e.ColumnIndex].Name == "State")
        {
            if (row.IsCurrent || row.IsHealthy)
            {
                cellStyle.ForeColor = ThemeManager.Current.Success;
                cellStyle.Font = UiFonts.BodyStrong;
            }
            else if (row.HealthKnown)
            {
                cellStyle.ForeColor = ThemeManager.Current.Danger;
            }
        }
    }

    private async Task SetExitModeAsync(ProxyExitMode mode)
    {
        if (_exitMode == mode)
        {
            ShowInfo(mode == ProxyExitMode.Residential
                ? "当前已经使用住宅 SOCKS5 作为最终出口。"
                : "当前已经使用机场节点作为最终出口。");
            return;
        }

        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.SetProxyExitMode,
                new SetProxyExitModeRequest { Mode = mode },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _exitMode = mode;
            UpdateExitModeControls();
            ShowInfo(mode == ProxyExitMode.Residential
                ? "已切换为住宅 SOCKS5 最终出口；节点列表现在用于选择前置机场。"
                : "已切换为机场节点最终出口；境外流量将直接使用当前节点。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task SelectAsync(string name)
    {
        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.SelectProxy,
                new SelectProxyRequest { Name = name },
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            var automatic = name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal);
            ShowInfo(_exitMode == ProxyExitMode.Residential
                ? automatic
                    ? "住宅出口的前置机场已恢复自动选择；最终出口仍为住宅 SOCKS5。"
                    : $"住宅出口的前置机场已切换到“{name}”；最终出口仍为住宅 SOCKS5。"
                : automatic
                    ? "机场最终出口已恢复自动选择。"
                    : $"机场最终出口已切换到“{name}”。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ApplySelectionAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not ProxyRow row)
        {
            ShowWarning("请先选择一个节点。");
            return;
        }

        await SelectAsync(row.Name).ConfigureAwait(true);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            var status = await Client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? new RuntimeStatus();
            var settings = await Client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? new ClientSettingsSnapshot();
            _exitMode = settings.ResidentialProxy.Enabled
                ? ProxyExitMode.Residential
                : ProxyExitMode.Airport;
            UpdateExitModeControls();

            var delayText = !string.IsNullOrWhiteSpace(status.CurrentProxy)
                            && status.ProxyDelayMilliseconds is { } delay
                ? $"{delay} ms"
                : "—";
            _delayLabel.Text = delayText;
            _currentLabel.Text = ResolveCurrentRouteText(status, _exitMode);

            if (status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable)
            {
                _currentLabel.Text = ModeVisuals.ProxyRouteText(status.ProxyRouteFailure);
            }

            if (status.ProxyRouteFailure == ProxyRouteFailureReason.ProxyAdapterUnavailable)
            {
                ShowWarning("网卡2当前不可用。国内直连继续工作，国外流量保持阻断。");
            }
            else if (status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable)
            {
                ShowWarning(_exitMode == ProxyExitMode.Residential
                    ? "住宅 SOCKS5 当前不可用。国内直连继续工作，国外流量保持阻断。"
                    : "机场代理组当前没有健康节点。国内直连继续工作，国外流量保持阻断。");
            }

            var rows = (status.AvailableProxies ?? [])
                .Select(name => new ProxyRow(
                    name,
                    DisplayName(name),
                    name.Equals(
                        MihomoConfigGenerator.AutoProxyGroupName,
                        StringComparison.Ordinal)
                        ? _exitMode == ProxyExitMode.Residential
                            ? "自动前置"
                            : "自动测速"
                        : _exitMode == ProxyExitMode.Residential
                            ? "前置节点"
                            : "最终出口",
                    name.Equals(status.CurrentProxy, StringComparison.Ordinal)
                        ? "当前"
                        : status.ProxyRouteHealthKnown
                            ? IsHealthy(status, name) ? "可用" : "不可用"
                            : string.Empty,
                    name.Equals(status.CurrentProxy, StringComparison.Ordinal),
                    status.ProxyRouteHealthKnown,
                    IsHealthy(status, name)))
                .ToList();
            var preferred = _grid.CurrentRow is null ? status.CurrentProxy : null;
            UiGrid.BindRowsPreservingSelection(
                _grid,
                rows,
                row => row.Name,
                preferred);
            _grid.Visible = rows.Count > 0;
            _emptyLabel.Visible = rows.Count == 0;
            if (_emptyLabel.Visible)
            {
                _emptyLabel.BringToFront();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法连接服务：{exception.Message}");
        }
    }

    internal static string ResolveCurrentRouteText(
        RuntimeStatus status,
        ProxyExitMode exitMode)
    {
        if (!status.Enabled)
        {
            return "分流已关闭";
        }

        if (!status.MihomoRunning)
        {
            return "Mihomo 未运行";
        }

        if (status.Mode == RuntimeMode.Starting)
        {
            return "出口启动中";
        }

        if (status.Mode == RuntimeMode.CoreUnavailable
            || !status.TunEnabled
            || !status.DnsStatusKnown
            || !status.DnsEnabled)
        {
            return "代理核心未就绪";
        }

        if (status.ProxyRouteFailure == ProxyRouteFailureReason.ProxyAdapterUnavailable
            || !status.ProxyAdapterAvailable)
        {
            return "网卡2不可用 · 国外流量已阻断";
        }

        if (status.ProxyRouteHealthKnown && !status.ProxyRouteAvailable)
        {
            return "无可用出口 · 国外流量已阻断";
        }

        if (exitMode == ProxyExitMode.Residential)
        {
            return "住宅 SOCKS5";
        }

        return status.CurrentProxy.Equals(
                   MihomoConfigGenerator.AutoProxyGroupName,
                   StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(status.EffectiveProxy)
            ? $"自动选择 · {DisplayName(status.EffectiveProxy)}"
            : DisplayName(status.CurrentProxy);
    }

    private void UpdateExitModeControls()
    {
        var residential = _exitMode == ProxyExitMode.Residential;
        _airportExitButton.Kind = residential
            ? ButtonKind.Secondary
            : ButtonKind.Accent;
        _residentialExitButton.Kind = residential
            ? ButtonKind.Accent
            : ButtonKind.Secondary;
        _airportExitButton.AccessibleName = residential
            ? "切换为机场节点最终出口"
            : "机场节点最终出口，当前模式";
        _residentialExitButton.AccessibleName = residential
            ? "住宅 SOCKS5 最终出口，当前模式"
            : "切换为住宅 SOCKS5 最终出口";
        _autoButton.Text = residential
            ? "自动前置"
            : "自动出口";
        _applyButton.Text = residential
            ? "设为前置"
            : "设为出口";
        _autoButton.SizeToContent(104);
        _applyButton.SizeToContent(104);
    }

    private static string DisplayName(string name)
    {
        if (name.Equals(
                MihomoConfigGenerator.ResidentialProxyName,
                StringComparison.Ordinal))
        {
            return "\u4F4F\u5B85 SOCKS5";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "未连接";
        }

        return name.Equals(
            MihomoConfigGenerator.AutoProxyGroupName,
            StringComparison.Ordinal)
            ? "自动选择"
            : name;
    }

    private static bool IsHealthy(RuntimeStatus status, string name)
    {
        return name.Equals(
                   MihomoConfigGenerator.AutoProxyGroupName,
                   StringComparison.Ordinal)
            ? status.ProxyRouteAvailable
            : status.HealthyProxies.Contains(name, StringComparer.Ordinal);
    }

    private sealed record ProxyRow(
        string Name,
        string DisplayName,
        string Strategy,
        string State,
        bool IsCurrent,
        bool HealthKnown,
        bool IsHealthy);
}
