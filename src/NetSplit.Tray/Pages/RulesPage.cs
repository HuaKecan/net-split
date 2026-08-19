using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class RulesPage : PageBase
{
    private readonly DataGridView _grid = UiGrid.Create();
    private readonly ComboBox _ruleType = new();
    private readonly RoundedTextBox _ruleValue = new();
    private readonly ComboBox _ruleAction = new();
    private readonly Label _emptyLabel = new();

    public RulesPage(NamedPipeRpcClient client)
        : base(
            client,
            "自定义规则",
            "自定义规则优先于内置国内外分流。可按域名、IPv4 网段或进程强制直连、代理或阻断。")
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
            RowCount = 4,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildPolicySummary(), 0, 0);
        root.Controls.Add(BuildEditor(), 0, 1);
        root.Controls.Add(BuildListToolbar(), 0, 2);
        root.Controls.Add(BuildGrid(), 0, 3);
        Content.Controls.Add(root);
    }

    private static Card BuildPolicySummary()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(UiMetrics.SpaceLg, UiMetrics.SpaceMd, UiMetrics.SpaceLg, UiMetrics.SpaceMd),
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceMd)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        table.Controls.Add(new Label
        {
            Text = "内置顺序",
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        table.Controls.Add(PolicyStep("私网与回环", "直接连接"), 1, 0);
        table.Controls.Add(PolicyStep("中国域名 / IP", "网卡1"), 2, 0);
        table.Controls.Add(PolicyStep("其余公网流量", "代理节点"), 3, 0);
        card.Controls.Add(table);
        return card;
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
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight + 2));
        table.Controls.Add(UiStyle.FieldLabel("匹配类型"), 0, 0);
        table.Controls.Add(UiStyle.FieldLabel("匹配值"), 1, 0);
        table.Controls.Add(UiStyle.FieldLabel("动作"), 2, 0);

        UiStyle.Apply(_ruleType);
        _ruleType.DropDownStyle = ComboBoxStyle.DropDownList;
        _ruleType.Items.AddRange(["域名", "域名后缀", "IPv4 CIDR", "进程名", "进程路径"]);
        _ruleType.SelectedIndex = 1;
        _ruleType.Dock = DockStyle.Fill;
        _ruleType.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _ruleType.SelectedIndexChanged += (_, _) => UpdateRulePlaceholder();
        table.Controls.Add(_ruleType, 0, 1);

        _ruleValue.Dock = DockStyle.Fill;
        _ruleValue.PlaceholderText = "例如：github.com";
        _ruleValue.InputAccessibleName = "规则匹配值";
        _ruleValue.CornerRadius = 7;
        _ruleValue.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        table.Controls.Add(_ruleValue, 1, 1);

        UiStyle.Apply(_ruleAction);
        _ruleAction.DropDownStyle = ComboBoxStyle.DropDownList;
        _ruleAction.Items.AddRange(["直连", "走代理", "阻断"]);
        _ruleAction.SelectedIndex = 1;
        _ruleAction.Dock = DockStyle.Fill;
        _ruleAction.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        table.Controls.Add(_ruleAction, 2, 1);

        var add = CreateButton(
            "添加规则",
            ButtonKind.Accent,
            (_, _) => _ = AddAsync(),
            92,
            UiGlyphs.Add);
        add.Dock = DockStyle.Fill;
        table.Controls.Add(add, 3, 1);
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
        var title = UiStyle.SectionTitle("已启用规则");
        title.Anchor = AnchorStyles.Left;
        toolbar.Controls.Add(title, 0, 0);

        var remove = CreateButton(
            "删除选中",
            ButtonKind.Danger,
            (_, _) => _ = RemoveAsync(),
            0,
            UiGlyphs.Delete);
        remove.Anchor = AnchorStyles.Right;
        toolbar.Controls.Add(remove, 1, 0);
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
        _grid.AccessibleName = "自定义规则列表";
        _grid.Columns.Add(UiGrid.TextColumn("MatchType", "匹配类型", 130));
        _grid.Columns.Add(UiGrid.TextColumn("Value", "匹配值", fill: true));
        _grid.Columns.Add(UiGrid.TextColumn("Action", "动作", 100));
        _grid.CellFormatting += OnCellFormatting;

        _emptyLabel.Text = "暂无自定义规则，当前仅使用内置分流顺序。";
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

    private static FlowLayoutPanel PolicyStep(string match, string action)
    {
        var theme = ThemeManager.Current;
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
            Text = match,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Margin = Padding.Empty
        });
        copy.Controls.Add(new Label
        {
            Text = action,
            Font = UiFonts.BodyStrong,
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 1, 0, 0)
        });
        return copy;
    }

    private void UpdateRulePlaceholder()
    {
        _ruleValue.PlaceholderText = _ruleType.SelectedIndex switch
        {
            0 => "例如：www.example.com",
            1 => "例如：example.com",
            2 => "例如：192.0.2.0/24",
            3 => "例如：game.exe",
            4 => @"例如：C:\Apps\game.exe",
            _ => string.Empty
        };
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0
            || _grid.Columns[e.ColumnIndex].Name != "Action"
            || _grid.Rows[e.RowIndex].DataBoundItem is not RuleRow row)
        {
            return;
        }

        var cellStyle = e.CellStyle;
        if (cellStyle is null)
        {
            return;
        }

        cellStyle.ForeColor = row.ActionKind switch
        {
            RuleAction.Direct => ThemeManager.Current.Success,
            RuleAction.Proxy => ThemeManager.Current.AccentText,
            RuleAction.Block => ThemeManager.Current.Danger,
            _ => ThemeManager.Current.TextPrimary
        };
        cellStyle.Font = UiFonts.BodyStrong;
    }

    private async Task AddAsync()
    {
        var value = _ruleValue.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowWarning("请填写规则匹配值。");
            return;
        }

        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.AddRule,
                new CustomRule
                {
                    MatchType = (RuleMatchType)_ruleType.SelectedIndex,
                    Value = value,
                    Action = _ruleAction.SelectedIndex switch
                    {
                        0 => RuleAction.Direct,
                        2 => RuleAction.Block,
                        _ => RuleAction.Proxy
                    }
                },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _ruleValue.Clear();
            ShowInfo("规则已添加并写入配置。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RemoveAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not RuleRow row)
        {
            ShowWarning("请先选择一条规则。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定删除规则“{row.Value}”吗？",
            "删除规则",
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
                RpcCommands.RemoveRule,
                new RemoveItemRequest { Id = row.Id },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            ShowInfo("规则已删除。");
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

            var rows = settings.Rules
                .Where(item => item.Enabled)
                .Select(item => new RuleRow(
                    item.Id,
                    FriendlyMatchType(item.MatchType),
                    item.Value,
                    FriendlyAction(item.Action),
                    item.Action))
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

    private static string FriendlyMatchType(RuleMatchType matchType)
    {
        return matchType switch
        {
            RuleMatchType.Domain => "域名",
            RuleMatchType.DomainSuffix => "域名后缀",
            RuleMatchType.IpCidr => "IPv4 CIDR",
            RuleMatchType.ProcessName => "进程名",
            RuleMatchType.ProcessPath => "进程路径",
            _ => matchType.ToString()
        };
    }

    private static string FriendlyAction(RuleAction action)
    {
        return action switch
        {
            RuleAction.Direct => "直连",
            RuleAction.Proxy => "走代理",
            RuleAction.Block => "阻断",
            _ => action.ToString()
        };
    }

    private sealed record RuleRow(
        Guid Id,
        string MatchType,
        string Value,
        string Action,
        RuleAction ActionKind);
}
