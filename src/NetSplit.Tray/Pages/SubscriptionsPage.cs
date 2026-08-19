using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class SubscriptionsPage : PageBase
{
    private readonly DataGridView _grid = UiGrid.Create();
    private readonly RoundedTextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly RoundedTextBox _source = new();
    private readonly ThemedButton _browseButton = new();
    private readonly Label _emptyLabel = new();

    public SubscriptionsPage(NamedPipeRpcClient client)
        : base(
            client,
            "订阅",
            "管理 Clash / Mihomo YAML 订阅。来源地址会加密保存，界面和日志只显示脱敏信息。")
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
            RowCount = 3,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildEditor(), 0, 0);
        root.Controls.Add(BuildListToolbar(), 0, 1);
        root.Controls.Add(BuildGrid(), 0, 2);
        Content.Controls.Add(root);
    }

    private Card BuildEditor()
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
            ColumnCount = 4,
            RowCount = 3,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight));

        table.Controls.Add(UiStyle.SectionTitle("添加订阅"), 0, 0);
        table.SetColumnSpan(table.GetControlFromPosition(0, 0)!, 2);

        var import = CreateButton(
            "读取 Clash 当前配置",
            ButtonKind.Link,
            (_, _) => ImportClashProfile(),
            0,
            UiGlyphs.Subscriptions);
        import.Anchor = AnchorStyles.Right;
        table.Controls.Add(import, 2, 0);
        table.SetColumnSpan(import, 2);

        table.Controls.Add(UiStyle.FieldLabel("名称"), 0, 1);
        table.Controls.Add(UiStyle.FieldLabel("来源类型"), 1, 1);
        table.Controls.Add(UiStyle.FieldLabel("订阅地址或文件"), 2, 1);

        _name.PlaceholderText = "例如：日常节点";
        _name.InputAccessibleName = "订阅名称";
        _name.CornerRadius = 7;
        _name.Dock = DockStyle.Fill;
        _name.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        table.Controls.Add(_name, 0, 2);

        UiStyle.Apply(_type);
        _type.DropDownStyle = ComboBoxStyle.DropDownList;
        _type.Items.AddRange(["HTTPS URL", "本地 YAML"]);
        _type.SelectedIndex = 0;
        _type.Dock = DockStyle.Fill;
        _type.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _type.SelectedIndexChanged += (_, _) => UpdateSourceMode();
        table.Controls.Add(_type, 1, 2);

        var sourceHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0)
        };
        sourceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        _source.PlaceholderText = "https://…";
        _source.InputAccessibleName = "订阅地址或文件";
        _source.CornerRadius = 7;
        _source.Dock = DockStyle.Fill;
        _source.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        sourceHost.Controls.Add(_source, 0, 0);

        _browseButton.Text = "选择文件";
        _browseButton.Kind = ButtonKind.Secondary;
        _browseButton.Enabled = false;
        _browseButton.Dock = DockStyle.Fill;
        _browseButton.SizeToContent(78);
        _browseButton.Click += (_, _) => BrowseFile();
        sourceHost.Controls.Add(_browseButton, 1, 0);
        table.Controls.Add(sourceHost, 2, 2);

        var add = CreateButton(
            "添加订阅",
            ButtonKind.Accent,
            (_, _) => _ = AddAsync(),
            112,
            UiGlyphs.Add);
        add.Dock = DockStyle.Fill;
        table.Controls.Add(add, 3, 2);
        card.Controls.Add(table);
        return card;
    }

    private TableLayoutPanel BuildListToolbar()
    {
        var theme = ThemeManager.Current;
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = UiStyle.SectionTitle("已保存订阅");
        title.Anchor = AnchorStyles.Left;
        toolbar.Controls.Add(title, 0, 0);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        var refresh = CreateButton(
            "更新全部",
            ButtonKind.Secondary,
            (_, _) => _ = RefreshAllAsync(),
            0,
            UiGlyphs.Refresh);
        refresh.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        actions.Controls.Add(refresh);
        actions.Controls.Add(CreateButton(
            "删除选中",
            ButtonKind.Danger,
            (_, _) => _ = RemoveAsync(),
            0,
            UiGlyphs.Delete));
        toolbar.Controls.Add(actions, 1, 0);
        return toolbar;
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
        _grid.AccessibleName = "订阅列表";
        _grid.Columns.Add(UiGrid.TextColumn("Name", "名称", 180));
        _grid.Columns.Add(UiGrid.TextColumn("Kind", "类型", 100));
        _grid.Columns.Add(UiGrid.TextColumn("Source", "来源", fill: true));
        _grid.Columns.Add(UiGrid.TextColumn("Updated", "上次更新", 120));

        _emptyLabel.Text = "还没有订阅。可从 Clash 读取当前配置，或在上方添加 YAML 来源。";
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

    private void UpdateSourceMode()
    {
        var fileMode = _type.SelectedIndex == 1;
        _browseButton.Enabled = fileMode;
        _source.PlaceholderText = fileMode
            ? "选择 Clash / Mihomo YAML 文件"
            : "https://…";
    }

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "YAML 配置 (*.yaml;*.yml)|*.yaml;*.yml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _source.Text = dialog.FileName;
        }
    }

    private void ImportClashProfile()
    {
        var profile = ClashVergeDiscovery.FindCurrentProfilePath();
        if (profile is null)
        {
            ShowWarning("未找到 Clash Verge 当前订阅文件。");
            return;
        }

        _name.Text = "Clash 当前订阅";
        _type.SelectedIndex = 1;
        _source.Text = profile;
        ShowInfo("已读取 Clash 当前配置路径，确认后点击“添加订阅”。");
    }

    private async Task AddAsync()
    {
        var name = _name.Text.Trim();
        var source = _source.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
        {
            ShowWarning("请填写订阅名称和来源。");
            return;
        }

        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.AddSubscription,
                new SubscriptionInput
                {
                    Name = name,
                    SourceKind = _type.SelectedIndex == 1
                        ? SubscriptionSourceKind.File
                        : SubscriptionSourceKind.Url,
                    Source = source
                },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _name.Clear();
            _source.Clear();
            ShowInfo($"已添加订阅“{name}”。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RemoveAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not SubscriptionRow row)
        {
            ShowWarning("请先选择一个订阅。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定删除订阅“{row.Name}”吗？缓存会在后续配置更新时清理。",
            "删除订阅",
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
                RpcCommands.RemoveSubscription,
                new RemoveItemRequest { Id = row.Id },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            ShowInfo($"已删除订阅“{row.Name}”。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RefreshAllAsync()
    {
        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.RefreshSubscriptions,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            ShowInfo("订阅已更新。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            var settings = await Client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? new ClientSettingsSnapshot();

            var rows = settings.Subscriptions
                .Select(item => new SubscriptionRow(
                    item.Id,
                    item.Name,
                    item.SourceKind == SubscriptionSourceKind.File
                        ? "本地文件"
                        : "HTTPS URL",
                    item.DisplaySource,
                    UiFormat.RelativeTime(item.LastUpdated)))
                .ToList();
            UiGrid.BindRowsPreservingSelection(
                _grid,
                rows,
                row => row.Id.ToString("N"));
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

    private sealed record SubscriptionRow(
        Guid Id,
        string Name,
        string Kind,
        string Source,
        string Updated);
}
