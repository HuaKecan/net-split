using System.Diagnostics;
using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class SettingsPage : PageBase
{
    private readonly ComboBox _directAdapter = new();
    private readonly ComboBox _proxyAdapter = new();
    private readonly Label _directDetail = new();
    private readonly Label _proxyDetail = new();
    private readonly ThemedButton _saveBindings = new();
    private readonly ComboBox _theme = new();
    private readonly ToggleSwitch _silentNotificationsToggle = new();
    private readonly Label _mihomoValue = new();
    private readonly Label _geodataValue = new();

    private string _adapterSignature = string.Empty;
    private string _savedDirectId = string.Empty;
    private string _savedProxyId = string.Empty;
    private bool _syncingAdapters;
    private bool _syncingUserPreferences;

    public SettingsPage(NamedPipeRpcClient client)
        : base(
            client,
            "设置",
            "确认国内直连与代理出口使用的物理网卡，并检查本机运行环境。")
    {
        BuildUi();
    }

    private void BuildUi()
    {
        var theme = ThemeManager.Current;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 236));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 224));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.Controls.Add(BuildAdapterCard(), 0, 0);
        root.Controls.Add(BuildEnvironmentCard(), 0, 1);
        root.Controls.Add(BuildRecoveryCard(), 0, 2);
        Content.Controls.Add(root);
    }

    private Card BuildAdapterCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

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
        var titleBlock = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Left,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        titleBlock.Controls.Add(UiStyle.SectionTitle("网卡角色"));
        titleBlock.Controls.Add(new Label
        {
            Text = "系统只提供建议，点击保存后才会生效",
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = new Padding(UiMetrics.SpaceMd, 2, 0, 0)
        });
        heading.Controls.Add(titleBlock, 0, 0);
        var rescan = CreateButton(
            "重新扫描",
            ButtonKind.Secondary,
            (_, _) => _ = RescanAsync(),
            0,
            UiGlyphs.Refresh);
        rescan.Anchor = AnchorStyles.Right;
        heading.Controls.Add(rescan, 1, 0);
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);

        table.Controls.Add(
            BuildAdapterField(
                "网卡1  ·  国内直连",
                "国内域名、国内 IP 与私网流量从这里直接出站。",
                _directAdapter,
                _directDetail),
            0,
            1);
        table.Controls.Add(
            BuildAdapterField(
                "网卡2  ·  代理出口",
                "机场节点的物理连接固定从这里出站，通常选择 F50。",
                _proxyAdapter,
                _proxyDetail),
            1,
            1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            Text = "保存时会按接口 GUID 建立稳定映射；网卡重命名不影响绑定。",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _saveBindings.Text = "保存网卡角色";
        _saveBindings.Kind = ButtonKind.Accent;
        _saveBindings.Glyph = UiGlyphs.Save;
        _saveBindings.Enabled = false;
        _saveBindings.SizeToContent(112);
        _saveBindings.Anchor = AnchorStyles.Right;
        _saveBindings.Click += (_, _) => _ = SaveBindingsAsync();
        footer.Controls.Add(_saveBindings, 1, 0);
        table.Controls.Add(footer, 0, 2);
        table.SetColumnSpan(footer, 2);
        card.Controls.Add(table);
        return card;
    }

    private Panel BuildAdapterField(
        string title,
        string description,
        ComboBox comboBox,
        Label detail)
    {
        var theme = ThemeManager.Current;
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundSurface,
            Padding = new Padding(0, UiMetrics.SpaceXs, UiMetrics.SpaceLg, 0),
            Margin = Padding.Empty
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
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label
        {
            Text = title,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 0);
        table.Controls.Add(new Label
        {
            Text = description,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 1);

        UiStyle.Apply(comboBox);
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.Dock = DockStyle.Fill;
        comboBox.DropDownWidth = 560;
        comboBox.Margin = Padding.Empty;
        comboBox.SelectedIndexChanged += (_, _) => OnAdapterSelectionChanged();
        table.Controls.Add(comboBox, 0, 2);

        detail.Text = "尚未选择";
        detail.Font = UiFonts.Caption;
        detail.ForeColor = theme.TextMuted;
        detail.Dock = DockStyle.Fill;
        detail.TextAlign = ContentAlignment.MiddleLeft;
        detail.AutoEllipsis = true;
        detail.Margin = Padding.Empty;
        table.Controls.Add(detail, 0, 3);
        panel.Controls.Add(table);
        return panel;
    }

    private Card BuildEnvironmentCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
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
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        table.Controls.Add(BuildAppearanceSection(), 0, 0);
        table.Controls.Add(BuildRuntimeSection(), 1, 0);
        card.Controls.Add(table);
        return card;
    }

    private Panel BuildAppearanceSection()
    {
        var theme = ThemeManager.Current;
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundSurface,
            Padding = new Padding(0, 0, UiMetrics.Space2xl, 0)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(UiStyle.SectionTitle("界面"), 0, 0);
        table.Controls.Add(UiStyle.FieldLabel("主题"), 0, 1);

        UiStyle.Apply(_theme);
        _theme.DropDownStyle = ComboBoxStyle.DropDownList;
        _theme.Items.AddRange(["跟随系统", "深色", "浅色"]);
        _theme.SelectedIndex = ThemeManager.Mode switch
        {
            UiThemeMode.Dark => 1,
            UiThemeMode.Light => 2,
            _ => 0
        };
        _theme.Dock = DockStyle.Fill;
        _theme.Margin = Padding.Empty;
        _theme.SelectedIndexChanged += (_, _) =>
        {
            ThemeManager.Mode = _theme.SelectedIndex switch
            {
                1 => UiThemeMode.Dark,
                2 => UiThemeMode.Light,
                _ => UiThemeMode.FollowSystem
            };
        };
        table.Controls.Add(_theme, 0, 2);
        table.Controls.Add(BuildNotificationPreference(), 0, 3);
        table.Controls.Add(new Label
        {
            Text = "界面与通知偏好只保存在当前 Windows 用户下。",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 4);
        panel.Controls.Add(table);
        return panel;
    }

    private TableLayoutPanel BuildNotificationPreference()
    {
        var theme = ThemeManager.Current;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var copy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(0, UiMetrics.SpaceMd, 0, 0)
        };
        copy.Controls.Add(new Label
        {
            Text = "静默通知",
            AutoSize = true,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            Margin = Padding.Empty
        });
        copy.Controls.Add(new Label
        {
            Text = "关闭自动状态弹窗，手动操作错误仍会提示。",
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = new Padding(0, 2, 0, 0)
        });
        row.Controls.Add(copy, 0, 0);

        _silentNotificationsToggle.AccessibleName = "静默通知";
        _silentNotificationsToggle.Anchor = AnchorStyles.Right;
        _silentNotificationsToggle.Margin = new Padding(UiMetrics.SpaceLg, 0, 0, 0);
        _silentNotificationsToggle.SetCheckedSilently(
            UserPreferences.SilentNotifications);
        _silentNotificationsToggle.CheckedChanged += (_, _) =>
        {
            if (!_syncingUserPreferences)
            {
                UserPreferences.SilentNotifications =
                    _silentNotificationsToggle.Checked;
            }
        };
        row.Controls.Add(_silentNotificationsToggle, 1, 0);
        return row;
    }

    private Panel BuildRuntimeSection()
    {
        var theme = ThemeManager.Current;
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundSurface,
            Padding = new Padding(UiMetrics.SpaceLg, 0, 0, 0)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.Controls.Add(UiStyle.SectionTitle("运行环境"), 0, 0);
        table.Controls.Add(RuntimeRow("Mihomo", _mihomoValue), 0, 1);
        table.Controls.Add(RuntimeRow("GeoData", _geodataValue), 0, 2);
        panel.Controls.Add(table);
        return panel;
    }

    private Card BuildRecoveryCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(UiMetrics.SpaceLg, UiMetrics.SpaceMd, UiMetrics.SpaceLg, UiMetrics.SpaceMd),
            Margin = Padding.Empty
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
        var copy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        copy.Controls.Add(new Label
        {
            Text = "紧急恢复",
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Margin = Padding.Empty
        });
        copy.Controls.Add(new Label
        {
            Text = "仅在 TUN 或 DNS 异常、关闭程序后网络未恢复时使用。",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0)
        });
        table.Controls.Add(copy, 0, 0);
        var recovery = CreateButton(
            "运行恢复工具",
            ButtonKind.Danger,
            (_, _) => RunRecovery(),
            0,
            UiGlyphs.Repair);
        recovery.Anchor = AnchorStyles.Right;
        table.Controls.Add(recovery, 1, 0);
        card.Controls.Add(table);
        return card;
    }

    private static TableLayoutPanel RuntimeRow(string label, Label value)
    {
        var theme = ThemeManager.Current;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label
        {
            Text = label,
            Font = UiFonts.Caption,
            ForeColor = theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        value.Text = "正在检查…";
        value.Font = UiFonts.Mono;
        value.ForeColor = theme.TextSecondary;
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.AutoEllipsis = true;
        row.Controls.Add(value, 1, 0);
        return row;
    }

    private async Task RescanAsync()
    {
        _adapterSignature = string.Empty;
        await RunActionAsync(async () =>
        {
            await RefreshAsync().ConfigureAwait(true);
            ShowInfo("网卡列表已重新扫描。");
        }).ConfigureAwait(true);
    }

    private async Task SaveBindingsAsync()
    {
        if (_directAdapter.SelectedItem is not AdapterOption direct
            || _proxyAdapter.SelectedItem is not AdapterOption proxy
            || !direct.Available
            || !proxy.Available)
        {
            ShowWarning("请选择两张当前可用的物理网卡。");
            return;
        }

        if (direct.Id.Equals(proxy.Id, StringComparison.OrdinalIgnoreCase))
        {
            ShowWarning("网卡1和网卡2必须是不同接口。");
            return;
        }

        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.UpdateBindings,
                new UpdateBindingsRequest
                {
                    DirectAdapterId = direct.Id,
                    ProxyAdapterId = proxy.Id
                },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _savedDirectId = direct.Id;
            _savedProxyId = proxy.Id;
            UpdateBindingActionState();
            ShowInfo("网卡角色已保存。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void OnAdapterSelectionChanged()
    {
        if (_syncingAdapters)
        {
            return;
        }

        UpdateAdapterDetail(_directAdapter, _directDetail);
        UpdateAdapterDetail(_proxyAdapter, _proxyDetail);
        UpdateBindingActionState();
    }

    private void UpdateBindingActionState()
    {
        if (_directAdapter.SelectedItem is not AdapterOption direct
            || _proxyAdapter.SelectedItem is not AdapterOption proxy)
        {
            _saveBindings.Enabled = false;
            return;
        }

        var changed = !direct.Id.Equals(_savedDirectId, StringComparison.OrdinalIgnoreCase)
            || !proxy.Id.Equals(_savedProxyId, StringComparison.OrdinalIgnoreCase);
        _saveBindings.Enabled = direct.Available
            && proxy.Available
            && !direct.Id.Equals(proxy.Id, StringComparison.OrdinalIgnoreCase)
            && changed;
    }

    private static void UpdateAdapterDetail(ComboBox comboBox, Label detail)
    {
        if (comboBox.SelectedItem is not AdapterOption option)
        {
            detail.Text = "尚未选择";
            detail.ForeColor = ThemeManager.Current.TextMuted;
            return;
        }

        if (option.Snapshot is not { } adapter)
        {
            detail.Text = "该接口当前离线，无法重新保存";
            detail.ForeColor = ThemeManager.Current.Warning;
            return;
        }

        var gateway = adapter.Gateways.Count > 0
            ? adapter.Gateways[0]
            : "无网关";
        detail.Text = $"ifIndex {adapter.InterfaceIndex}  ·  网关 {gateway}  ·  MAC {adapter.MacAddress}";
        detail.ForeColor = adapter.IsUp
            ? ThemeManager.Current.TextMuted
            : ThemeManager.Current.Warning;
    }

    private void PopulateAdapters(
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        ClientSettingsSnapshot settings)
    {
        var signature = string.Join(
            "|",
            adapters.Select(adapter =>
                $"{adapter.Id}:{adapter.Name}:{adapter.IsUp}:{(adapter.Ipv4Addresses.Count > 0 ? adapter.Ipv4Addresses[0] : string.Empty)}"));
        signature += $"|bindings:{settings.DirectAdapter?.Id}:{settings.ProxyAdapter?.Id}";
        _savedDirectId = settings.DirectAdapter?.Id ?? string.Empty;
        _savedProxyId = settings.ProxyAdapter?.Id ?? string.Empty;

        if (signature.Equals(_adapterSignature, StringComparison.Ordinal)
            && _directAdapter.Items.Count > 0
            && _proxyAdapter.Items.Count > 0)
        {
            UpdateBindingActionState();
            return;
        }

        var previousDirect = (_directAdapter.SelectedItem as AdapterOption)?.Id;
        var previousProxy = (_proxyAdapter.SelectedItem as AdapterOption)?.Id;
        var directOptions = BuildOptions(adapters, settings.DirectAdapter);
        var proxyOptions = BuildOptions(adapters, settings.ProxyAdapter);

        _syncingAdapters = true;
        try
        {
            _directAdapter.Items.Clear();
            _directAdapter.Items.AddRange(directOptions.Cast<object>().ToArray());
            _proxyAdapter.Items.Clear();
            _proxyAdapter.Items.AddRange(proxyOptions.Cast<object>().ToArray());

            SelectOption(
                _directAdapter,
                previousDirect,
                settings.DirectAdapter?.Id,
                directOptions.FirstOrDefault(option =>
                    option.Snapshot is { IsSelectable: true, IsF50Candidate: false })?.Id);
            SelectOption(
                _proxyAdapter,
                previousProxy,
                settings.ProxyAdapter?.Id,
                proxyOptions.FirstOrDefault(option =>
                    option.Snapshot is { IsSelectable: true, IsF50Candidate: true })?.Id);
        }
        finally
        {
            _syncingAdapters = false;
        }

        _adapterSignature = signature;
        UpdateAdapterDetail(_directAdapter, _directDetail);
        UpdateAdapterDetail(_proxyAdapter, _proxyDetail);
        UpdateBindingActionState();
    }

    private static List<AdapterOption> BuildOptions(
        IReadOnlyList<NetworkAdapterSnapshot> adapters,
        AdapterBinding? binding)
    {
        var options = adapters
            .Where(adapter =>
                !adapter.IsTunnelOrLoopback
                && (adapter.IsSelectable
                    || adapter.Id.Equals(binding?.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(adapter => adapter.IsSelectable)
            .ThenByDescending(adapter => adapter.IsF50Candidate)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(adapter => new AdapterOption(
                adapter.Id,
                AdapterDisplayName(adapter),
                adapter.IsSelectable,
                adapter))
            .ToList();

        if (binding is not null
            && options.All(option =>
                !option.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase)))
        {
            options.Insert(
                0,
                new AdapterOption(
                    binding.Id,
                    $"{binding.LastKnownName}  ·  当前离线",
                    false,
                    null));
        }

        return options;
    }

    private static string AdapterDisplayName(NetworkAdapterSnapshot adapter)
    {
        var ip = adapter.Ipv4Addresses.Count > 0
            ? adapter.Ipv4Addresses[0]
            : "无 IPv4";
        var suffix = adapter.IsF50Candidate ? "  ·  F50 候选" : string.Empty;
        return $"{adapter.Name}  ·  {ip}  ·  {adapter.Description}{suffix}";
    }

    private static void SelectOption(
        ComboBox comboBox,
        params string?[] candidateIds)
    {
        foreach (var candidateId in candidateIds)
        {
            if (string.IsNullOrWhiteSpace(candidateId))
            {
                continue;
            }

            for (var index = 0; index < comboBox.Items.Count; index++)
            {
                if (comboBox.Items[index] is AdapterOption option
                    && option.Id.Equals(candidateId, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = index;
                    return;
                }
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private void RunRecovery()
    {
        var answer = MessageBox.Show(
            this,
            "将请求服务关闭 TUN、停止托管 Mihomo 并刷新 DNS。订阅与网卡设置会保留。",
            "紧急恢复",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var recoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "net-split",
            "recovery",
            "NetSplit.Recovery.exe");
        if (!File.Exists(recoveryPath))
        {
            ShowError($"找不到恢复工具：{recoveryPath}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(recoveryPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            ShowError($"无法启动恢复工具：{exception.Message}");
        }
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            var settingsTask = Client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);
            var adaptersTask = Client.SendAsync<IReadOnlyList<NetworkAdapterSnapshot>>(
                RpcCommands.Discover,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken);
            var settings = await settingsTask.ConfigureAwait(true)
                ?? new ClientSettingsSnapshot();
            var adapters = await adaptersTask.ConfigureAwait(true) ?? [];

            _syncingUserPreferences = true;
            try
            {
                _silentNotificationsToggle.SetCheckedSilently(
                    UserPreferences.SilentNotifications);
            }
            finally
            {
                _syncingUserPreferences = false;
            }

            PopulateAdapters(adapters, settings);
            SetRuntimeValue(
                _mihomoValue,
                settings.MihomoPath,
                settings.MihomoAvailable);
            SetRuntimeValue(
                _geodataValue,
                settings.GeoDataDirectory,
                settings.GeoDataAvailable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法连接服务：{exception.Message}");
        }
    }

    private static void SetRuntimeValue(Label label, string path, bool available)
    {
        label.Text = available
            ? $"可用  ·  {path}"
            : $"缺失  ·  {path}";
        label.ForeColor = available
            ? ThemeManager.Current.Success
            : ThemeManager.Current.Danger;
    }

    private sealed record AdapterOption(
        string Id,
        string DisplayName,
        bool Available,
        NetworkAdapterSnapshot? Snapshot)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }
}
