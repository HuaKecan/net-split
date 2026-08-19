using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class ProxiesPage : PageBase
{
    private readonly DataGridView _grid = UiGrid.Create();
    private readonly Label _delayLabel = new();
    private readonly Label _currentLabel = new();
    private readonly Label _emptyLabel = new();
    private readonly Label _testSummaryLabel = new();
    private readonly ThemedButton _airportExitButton = new();
    private readonly ThemedButton _residentialExitButton = new();
    private readonly ThemedButton _testAllButton = new();
    private readonly ThemedButton _autoButton = new();
    private readonly ThemedButton _applyButton = new();
    private readonly Dictionary<string, ProxyDelayResult> _delayResults =
        new(StringComparer.Ordinal);
    private ProxyExitMode _exitMode = ProxyExitMode.Airport;
    private DateTimeOffset? _delayMeasuredAt;
    private bool _delayFromCache;
    private bool _sortByDelay;
    private bool _delaySortAscending = true;
    private string _delayNodeSignature = string.Empty;

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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
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
            ColumnCount = 2,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty,
            AccessibleName = "代理节点操作栏"
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
        toolbar.Controls.Add(BuildDelayStatus(), 0, 1);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(UiMetrics.SpaceLg, 0, 0, 0)
        };
        _testAllButton.Text = "测速全部";
        _testAllButton.Kind = ButtonKind.Secondary;
        _testAllButton.Glyph = UiGlyphs.Validate;
        _testAllButton.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _testAllButton.SizeToContent(104);
        _testAllButton.Click += (_, _) => _ = MeasureAllAsync();
        actions.Controls.Add(_testAllButton);

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
        toolbar.Controls.Add(actions, 1, 1);
        UpdateExitModeControls();
        card.Controls.Add(toolbar);
        return card;
    }

    private FlowLayoutPanel BuildDelayStatus()
    {
        var theme = ThemeManager.Current;
        var status = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        status.Controls.Add(new Label
        {
            Text = "节点测速",
            AutoSize = true,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            Margin = Padding.Empty
        });
        _testSummaryLabel.Text = "尚未测速";
        _testSummaryLabel.AutoSize = true;
        _testSummaryLabel.Font = UiFonts.Caption;
        _testSummaryLabel.ForeColor = theme.TextMuted;
        _testSummaryLabel.Margin = new Padding(UiMetrics.SpaceMd, 0, 0, 0);
        status.Controls.Add(_testSummaryLabel);
        return status;
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
        var delayColumn = UiGrid.TextColumn("Delay", "延迟", 94);
        delayColumn.SortMode = DataGridViewColumnSortMode.Programmatic;
        _grid.Columns.Add(delayColumn);
        _grid.Columns.Add(UiGrid.TextColumn("State", "状态", 90));
        _grid.CellDoubleClick += (_, _) => _ = ApplySelectionAsync();
        _grid.CellFormatting += OnCellFormatting;
        _grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;

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
        if (cellStyle is null)
        {
            return;
        }

        if (row.DelayMeasured && !row.DelayMilliseconds.HasValue)
        {
            cellStyle.ForeColor = ThemeManager.Current.TextMuted;
        }

        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName == "Delay"
            && row.DelayMeasured
            && !row.DelayMilliseconds.HasValue)
        {
            cellStyle.ForeColor = ThemeManager.Current.Danger;
        }
        else if (columnName == "State")
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

    private void OnColumnHeaderMouseClick(
        object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0
            || _grid.Columns[e.ColumnIndex].Name != "Delay"
            || _delayMeasuredAt is null)
        {
            return;
        }

        _delaySortAscending = !_sortByDelay || !_delaySortAscending;
        _sortByDelay = true;
        var rows = _grid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<ProxyRow>()
            .ToArray();
        BindRows(rows);
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

        if (row.HealthKnown && !row.IsHealthy)
        {
            ShowWarning("该节点当前不可用，请选择健康节点或重新测速。");
            return;
        }

        await SelectAsync(row.Name).ConfigureAwait(true);
    }

    private async Task MeasureAllAsync()
    {
        _testAllButton.Text = "测速中…";
        _testAllButton.SizeToContent(104);
        try
        {
            await RunActionAsync(async () =>
            {
                var batch = await Client.SendAsync<ProxyDelayBatchResult>(
                    RpcCommands.MeasureProxyDelays,
                    timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("服务未返回节点测速结果。");
                _delayResults.Clear();
                foreach (var result in batch.Results)
                {
                    _delayResults[result.Name] = result;
                }

                _delayMeasuredAt = batch.MeasuredAt;
                _delayFromCache = batch.FromCache;
                _sortByDelay = batch.Results.Count > 0;
                _delaySortAscending = true;
                _delayNodeSignature = CreateDelaySignature(
                    batch.Results.Select(result => result.Name));
                await RefreshAsync().ConfigureAwait(true);

                var available = batch.Results.Count(result =>
                    result.DelayMilliseconds.HasValue);
                ShowInfo(batch.Results.Count == 0
                    ? "当前代理组中没有可测速的真实节点。"
                    : batch.FromCache
                        ? $"已载入 5 分钟内的测速结果：{available}/{batch.Results.Count} 个节点可用。"
                        : $"测速完成：{available}/{batch.Results.Count} 个节点可用。");
            }).ConfigureAwait(true);
        }
        finally
        {
            _testAllButton.Text = "测速全部";
            _testAllButton.SizeToContent(104);
        }
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

            var availableNames = status.AvailableProxies ?? [];
            var delayNodeSignature = CreateDelaySignature(
                availableNames.Where(IsMeasurableProxy));
            if (_delayMeasuredAt is not null
                && !_delayNodeSignature.Equals(
                    delayNodeSignature,
                    StringComparison.Ordinal))
            {
                ResetDelayResults();
            }

            var availableSet = availableNames.ToHashSet(StringComparer.Ordinal);
            foreach (var staleName in _delayResults.Keys
                         .Where(name => !availableSet.Contains(name))
                         .ToArray())
            {
                _delayResults.Remove(staleName);
            }

            var rows = availableNames
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
                    DelayText(name),
                    name.Equals(status.CurrentProxy, StringComparison.Ordinal),
                    status.ProxyRouteHealthKnown,
                    IsHealthy(status, name),
                    _delayResults.TryGetValue(name, out var delay)
                        ? delay.DelayMilliseconds
                        : null,
                    _delayResults.ContainsKey(name)))
                .ToList();
            UpdateDelaySummary(rows);
            var preferred = _grid.CurrentRow is null ? status.CurrentProxy : null;
            BindRows(rows, preferred);
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

    private string DelayText(string name)
    {
        if (!_delayResults.TryGetValue(name, out var result))
        {
            return "—";
        }

        return result.DelayMilliseconds is { } delay
            ? $"{delay} ms"
            : "不可用";
    }

    private void UpdateDelaySummary(IReadOnlyList<ProxyRow> rows)
    {
        var measurableCount = rows.Count(row =>
            IsMeasurableProxy(row.Name));
        if (_delayMeasuredAt is null)
        {
            _testSummaryLabel.Text = measurableCount == 0
                ? "暂无真实节点"
                : $"{measurableCount} 个节点 · 尚未测速";
            return;
        }

        var availableCount = rows.Count(row => row.DelayMilliseconds.HasValue);
        var source = _delayFromCache ? " · 缓存" : string.Empty;
        _testSummaryLabel.Text =
            $"{_delayMeasuredAt.Value.ToLocalTime():HH:mm:ss} · "
            + $"{availableCount}/{measurableCount} 可用{source}";
    }

    private void BindRows(
        IEnumerable<ProxyRow> source,
        string? preferred = null)
    {
        var rows = OrderRows(source);
        UiGrid.BindRowsPreservingSelection(
            _grid,
            rows,
            row => row.Name,
            preferred);
        _grid.Columns["Delay"].HeaderCell.SortGlyphDirection = _sortByDelay
            ? _delaySortAscending
                ? SortOrder.Ascending
                : SortOrder.Descending
            : SortOrder.None;
    }

    private ProxyRow[] OrderRows(IEnumerable<ProxyRow> source)
    {
        var rows = source.ToArray();
        if (!_sortByDelay)
        {
            return rows;
        }

        var automatic = rows.Where(row => row.Name.Equals(
            MihomoConfigGenerator.AutoProxyGroupName,
            StringComparison.Ordinal));
        var measured = rows.Where(row =>
            !row.Name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal)
            && row.DelayMilliseconds.HasValue);
        measured = _delaySortAscending
            ? measured.OrderBy(row => row.DelayMilliseconds)
            : measured.OrderByDescending(row => row.DelayMilliseconds);
        var remaining = rows.Where(row =>
                !row.Name.Equals(
                    MihomoConfigGenerator.AutoProxyGroupName,
                    StringComparison.Ordinal)
                && !row.DelayMilliseconds.HasValue)
            .OrderBy(row => row.DelayMeasured ? 1 : 0)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        return automatic.Concat(measured).Concat(remaining).ToArray();
    }

    private static string CreateDelaySignature(IEnumerable<string> names)
    {
        return string.Join(
            "\u001F",
            names.Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    private static bool IsMeasurableProxy(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && !name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.ProxyGroupName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.DirectProxyName,
                StringComparison.Ordinal)
            && !name.Equals(
                MihomoConfigGenerator.ResidentialProxyName,
                StringComparison.Ordinal);
    }

    private void ResetDelayResults()
    {
        _delayResults.Clear();
        _delayMeasuredAt = null;
        _delayFromCache = false;
        _sortByDelay = false;
        _delaySortAscending = true;
        _delayNodeSignature = string.Empty;
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
        string Delay,
        bool IsCurrent,
        bool HealthKnown,
        bool IsHealthy,
        int? DelayMilliseconds,
        bool DelayMeasured);
}
