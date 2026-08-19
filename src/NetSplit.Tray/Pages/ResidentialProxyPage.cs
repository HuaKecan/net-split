using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class ResidentialProxyPage : PageBase
{
    private readonly ToggleSwitch _enabled = new();
    private readonly ComboBox _routeMode = new();
    private readonly RoundedTextBox _host = new();
    private readonly RoundedTextBox _port = new();
    private readonly ToggleSwitch _authentication = new();
    private readonly RoundedTextBox _username = new();
    private readonly RoundedTextBox _password = new();
    private readonly CheckBox _showPassword = new();
    private readonly Badge _credentialBadge = new();
    private readonly Label _credentialHint = new();
    private readonly FlowLayoutPanel _pathPreview = new();
    private readonly Label _pathDetail = new();
    private readonly ThemedButton _saveButton = new();

    private bool _loaded;
    private bool _loading;
    private bool _dirty;
    private bool _hasCredentials;

    public ResidentialProxyPage(NamedPipeRpcClient client)
        : base(
            client,
            "住宅代理",
            "将境外流量的最终出口切换为固定 SOCKS5 住宅 IP；机场节点仍可作为前置链路。")
    {
        BuildUi();
        WireChangeTracking();
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 284));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.Controls.Add(BuildPolicyCard(), 0, 0);
        root.Controls.Add(BuildConnectionCard(), 0, 1);
        root.Controls.Add(BuildPathCard(), 0, 2);
        Content.Controls.Add(root);
    }

    private Card BuildPolicyCard()
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
            ColumnCount = 3,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var copy = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        copy.Controls.Add(UiStyle.SectionTitle("用作境外最终出口"));
        copy.Controls.Add(new Label
        {
            Text = "关闭时仍使用机场节点；开启后目标网站看到住宅代理的公网 IP。",
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = new Padding(0, UiMetrics.SpaceXs, 0, 0)
        });
        table.Controls.Add(copy, 0, 0);
        table.SetColumnSpan(copy, 2);

        _enabled.CheckedAccessibleName = "关闭住宅代理";
        _enabled.UncheckedAccessibleName = "启用住宅代理";
        _enabled.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _enabled.Margin = new Padding(0, 2, 0, 0);
        table.Controls.Add(_enabled, 2, 0);

        var routeLabel = new Label
        {
            Text = "连接路径",
            Dock = DockStyle.Fill,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        table.Controls.Add(routeLabel, 0, 1);

        UiStyle.Apply(_routeMode);
        _routeMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _routeMode.Items.AddRange(
        [
            new RouteModeItem("经机场节点连接（推荐）", ResidentialProxyRouteMode.ThroughAirport),
            new RouteModeItem("从网卡2直接连接", ResidentialProxyRouteMode.DirectNic2)
        ]);
        _routeMode.SelectedIndex = 0;
        _routeMode.Dock = DockStyle.Fill;
        _routeMode.Margin = new Padding(0, UiMetrics.SpaceSm, 0, 0);
        table.Controls.Add(_routeMode, 1, 1);
        table.SetColumnSpan(_routeMode, 2);
        card.Controls.Add(table);
        return card;
    }

    private Card BuildConnectionCard()
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
            ColumnCount = 1,
            RowCount = 3,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        table.Controls.Add(BuildConnectionHeader(), 0, 0);
        table.Controls.Add(BuildConnectionFields(), 0, 1);
        table.Controls.Add(BuildConnectionFooter(), 0, 2);
        card.Controls.Add(table);
        return card;
    }

    private TableLayoutPanel BuildConnectionHeader()
    {
        var theme = ThemeManager.Current;
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var copy = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        copy.Controls.Add(UiStyle.SectionTitle("SOCKS5 连接"));
        copy.Controls.Add(new Label
        {
            Text = "配置住宅出口的服务器端点与登录凭据",
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            Margin = new Padding(0, 3, 0, 0)
        });
        header.Controls.Add(copy, 0, 0);

        _credentialBadge.Set("未保存凭据", theme.TextMuted, theme.BackgroundSurface2);
        _credentialBadge.Anchor = AnchorStyles.Right;
        _credentialBadge.Margin = new Padding(0, 8, UiMetrics.SpaceLg, 0);
        header.Controls.Add(_credentialBadge, 1, 0);

        var authenticationControl = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        authenticationControl.Controls.Add(new Label
        {
            Text = "身份认证",
            AutoSize = true,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextSecondary,
            Margin = new Padding(0, 7, UiMetrics.SpaceSm, 0)
        });
        _authentication.CheckedAccessibleName = "关闭 SOCKS5 身份认证";
        _authentication.UncheckedAccessibleName = "启用 SOCKS5 身份认证";
        _authentication.Checked = true;
        _authentication.Margin = new Padding(0, 4, 0, 0);
        authenticationControl.Controls.Add(_authentication);
        header.Controls.Add(authenticationControl, 2, 0);
        return header;
    }

    private TableLayoutPanel BuildConnectionFields()
    {
        var theme = ThemeManager.Current;
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        fields.Controls.Add(BuildEndpointFields(), 0, 0);
        fields.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.Border,
            Margin = new Padding(15, UiMetrics.SpaceSm, 16, UiMetrics.SpaceMd)
        }, 1, 0);
        fields.Controls.Add(BuildCredentialFields(), 2, 0);
        return fields;
    }

    private TableLayoutPanel BuildEndpointFields()
    {
        var theme = ThemeManager.Current;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(GroupLabel("服务器端点"), 0, 0);
        table.SetColumnSpan(table.GetControlFromPosition(0, 0)!, 2);
        table.Controls.Add(UiStyle.FieldLabel("服务器地址"), 0, 1);
        table.Controls.Add(UiStyle.FieldLabel("端口"), 1, 1);
        ConfigureTextBox(_host, "proxy.example.com", "SOCKS5 服务器地址");
        ConfigureTextBox(_port, "1080", "SOCKS5 端口");
        _host.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        table.Controls.Add(_host, 0, 2);
        table.Controls.Add(_port, 1, 2);
        table.Controls.Add(new Label
        {
            Text = "支持域名或 IPv4 地址。",
            Dock = DockStyle.Fill,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, 3);
        table.SetColumnSpan(table.GetControlFromPosition(0, 3)!, 2);
        return table;
    }

    private TableLayoutPanel BuildCredentialFields()
    {
        var theme = ThemeManager.Current;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.ControlHeight));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        table.Controls.Add(GroupLabel("登录凭据"), 0, 0);
        ConfigureCheckBox(_showPassword, "显示本次输入");
        _showPassword.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _showPassword.Margin = new Padding(0, 1, 0, 0);
        table.Controls.Add(_showPassword, 1, 0);

        table.Controls.Add(UiStyle.FieldLabel("用户名"), 0, 1);
        table.Controls.Add(UiStyle.FieldLabel("密码"), 1, 1);
        ConfigureTextBox(_username, "留空则保留", "SOCKS5 用户名");
        ConfigureTextBox(_password, "留空则保留", "SOCKS5 密码");
        _password.UseSystemPasswordChar = true;
        _username.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        table.Controls.Add(_username, 0, 2);
        table.Controls.Add(_password, 1, 2);

        _credentialHint.Text = "留空将继续使用已保存的凭据。";
        _credentialHint.Dock = DockStyle.Fill;
        _credentialHint.Font = UiFonts.Caption;
        _credentialHint.ForeColor = theme.TextMuted;
        _credentialHint.TextAlign = ContentAlignment.MiddleLeft;
        _credentialHint.Margin = Padding.Empty;
        table.Controls.Add(_credentialHint, 0, 3);
        table.SetColumnSpan(_credentialHint, 2);
        return table;
    }

    private TableLayoutPanel BuildConnectionFooter()
    {
        var theme = ThemeManager.Current;
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface2,
            Padding = new Padding(
                UiMetrics.SpaceMd,
                UiMetrics.SpaceXs,
                UiMetrics.SpaceSm,
                UiMetrics.SpaceXs),
            Margin = new Padding(0, UiMetrics.SpaceSm, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var securityNote = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface2,
            Margin = Padding.Empty
        };
        securityNote.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        securityNote.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        securityNote.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = UiGlyphs.Shield,
            Font = UiFonts.Icon,
            ForeColor = theme.AccentText,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleRole = AccessibleRole.None,
            Margin = Padding.Empty
        }, 0, 0);
        securityNote.Controls.Add(new Label
        {
            Text = "凭据由 Windows DPAPI 加密且不会回显；保存会先验证配置，再安全重载 Mihomo。",
            Dock = DockStyle.Fill,
            Font = UiFonts.Caption,
            ForeColor = theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(UiMetrics.SpaceXs, 0, UiMetrics.SpaceMd, 0)
        }, 1, 0);
        footer.Controls.Add(securityNote, 0, 0);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface2,
            Margin = Padding.Empty
        };
        var validate = CreateButton(
            "验证当前配置",
            ButtonKind.Secondary,
            (_, _) => _ = ValidateAsync(),
            0,
            UiGlyphs.Validate);
        validate.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        actions.Controls.Add(validate);
        _saveButton.Text = "保存配置";
        _saveButton.Kind = ButtonKind.Accent;
        _saveButton.Glyph = UiGlyphs.Save;
        _saveButton.SizeToContent(104);
        _saveButton.Click += (_, _) => _ = SaveAsync();
        actions.Controls.Add(_saveButton);
        footer.Controls.Add(actions, 1, 0);
        return footer;
    }

    private Card BuildPathCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
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
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(UiStyle.SectionTitle("流量路径"), 0, 0);

        _pathPreview.Dock = DockStyle.Fill;
        _pathPreview.FlowDirection = FlowDirection.LeftToRight;
        _pathPreview.WrapContents = false;
        _pathPreview.BackColor = theme.BackgroundSurface;
        _pathPreview.Margin = Padding.Empty;
        table.Controls.Add(_pathPreview, 0, 1);

        _pathDetail.Dock = DockStyle.Fill;
        _pathDetail.Font = UiFonts.Caption;
        _pathDetail.ForeColor = theme.TextMuted;
        _pathDetail.TextAlign = ContentAlignment.MiddleLeft;
        _pathDetail.Margin = Padding.Empty;
        table.Controls.Add(_pathDetail, 0, 2);
        card.Controls.Add(table);
        UpdatePathPreview();
        return card;
    }

    private void WireChangeTracking()
    {
        _enabled.CheckedChanged += (_, _) =>
        {
            MarkDirty();
            UpdateEnabledState();
            UpdatePathPreview();
        };
        _routeMode.SelectedIndexChanged += (_, _) =>
        {
            MarkDirty();
            UpdatePathPreview();
        };
        _authentication.CheckedChanged += (_, _) =>
        {
            MarkDirty();
            UpdateEnabledState();
        };
        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;
        _host.TextChanged += (_, _) => MarkDirty();
        _port.TextChanged += (_, _) => MarkDirty();
        _username.TextChanged += (_, _) => MarkDirty();
        _password.TextChanged += (_, _) => MarkDirty();
    }

    private async Task SaveAsync()
    {
        var host = _host.Text.Trim();
        if (_enabled.Checked && string.IsNullOrWhiteSpace(host))
        {
            ShowWarning("启用住宅代理前请填写服务器地址。");
            return;
        }

        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowWarning("端口必须是 1 到 65535 之间的数字。");
            return;
        }

        var username = _username.Text.Trim();
        var password = _password.Text;
        var replacingCredentials = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
        if (_authentication.Checked
            && replacingCredentials
            && (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)))
        {
            ShowWarning("如需更新凭据，请同时填写用户名和密码。");
            return;
        }

        if (_enabled.Checked
            && _authentication.Checked
            && !_hasCredentials
            && !replacingCredentials)
        {
            ShowWarning("启用住宅代理前请填写用户名和密码。");
            return;
        }

        var routeMode = (_routeMode.SelectedItem as RouteModeItem)?.Value
            ?? ResidentialProxyRouteMode.ThroughAirport;
        await RunActionAsync(async () =>
        {
            await Client.SendAsync(
                RpcCommands.UpdateResidentialProxy,
                new UpdateResidentialProxyRequest
                {
                    Enabled = _enabled.Checked,
                    Host = host,
                    Port = port,
                    AuthenticationEnabled = _authentication.Checked,
                    Username = username,
                    Password = password,
                    ReplaceCredentials = replacingCredentials,
                    RouteMode = routeMode
                },
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            _username.Clear();
            _password.Clear();
            _dirty = false;
            ShowInfo(_enabled.Checked
                ? "住宅代理已保存，并设为境外最终出口。"
                : "住宅代理配置已保存，当前未启用。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ValidateAsync()
    {
        await RunActionAsync(async () =>
        {
            var result = await Client.SendAsync<ConfigurationValidationResult>(
                RpcCommands.Validate,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true)
                ?? new ConfigurationValidationResult();
            if (!result.IsValid)
            {
                ShowError(string.Join(Environment.NewLine, result.Errors));
                return;
            }

            ShowInfo(result.Warnings.Count == 0
                ? "当前配置已通过应用校验和 Mihomo 语法检查。"
                : $"配置有效；提示：{string.Join("；", result.Warnings)}");
        }).ConfigureAwait(true);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await Client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true)
                ?? new ClientSettingsSnapshot();
            _hasCredentials = settings.ResidentialProxy.HasCredentials;
            UpdateCredentialBadge();
            if (_loaded && _dirty)
            {
                return;
            }

            _loading = true;
            try
            {
                var proxy = settings.ResidentialProxy;
                _enabled.Checked = proxy.Enabled;
                _host.Text = proxy.Host;
                _port.Text = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _authentication.Checked = proxy.AuthenticationEnabled;
                SelectRouteMode(proxy.RouteMode);
                _username.Clear();
                _password.Clear();
                _loaded = true;
                _dirty = false;
                UpdateEnabledState();
                UpdatePathPreview();
            }
            finally
            {
                _loading = false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法连接服务：{exception.Message}");
        }
    }

    private void UpdateEnabledState()
    {
        _routeMode.Enabled = true;
        _host.Enabled = true;
        _port.Enabled = true;
        _authentication.Enabled = true;
        var authenticationEnabled = _authentication.Checked;
        _username.Enabled = authenticationEnabled;
        _password.Enabled = authenticationEnabled;
        _showPassword.Enabled = authenticationEnabled;
        _saveButton.Enabled = true;
        UpdateCredentialBadge();
    }

    private void UpdateCredentialBadge()
    {
        var theme = ThemeManager.Current;
        if (!_authentication.Checked)
        {
            _credentialBadge.Set("无需认证", theme.TextSecondary, theme.BackgroundSurface2);
            _credentialHint.Text = "当前连接不会发送用户名或密码。";
        }
        else if (_hasCredentials)
        {
            _credentialBadge.Set("凭据已保存", theme.Success, theme.BackgroundSurface2);
            _credentialHint.Text = "留空将继续使用已保存的凭据。";
        }
        else
        {
            _credentialBadge.Set("未保存凭据", theme.Warning, theme.BackgroundSurface2);
            _credentialHint.Text = "请输入用户名和密码后保存。";
        }
    }

    private void UpdatePathPreview()
    {
        var theme = ThemeManager.Current;
        _pathPreview.SuspendLayout();
        try
        {
            foreach (Control control in _pathPreview.Controls)
            {
                control.Dispose();
            }

            _pathPreview.Controls.Clear();
            AddPathStage("境外流量");
            if (!_enabled.Checked)
            {
                AddArrow(theme);
                AddPathStage("机场节点");
                _pathDetail.Text = "住宅代理关闭，境外网站继续使用当前机场节点出口。";
                return;
            }

            var throughAirport = (_routeMode.SelectedItem as RouteModeItem)?.Value
                != ResidentialProxyRouteMode.DirectNic2;
            if (throughAirport)
            {
                AddArrow(theme);
                AddPathStage("机场节点");
            }

            AddArrow(theme);
            AddPathStage("住宅 SOCKS5");
            AddArrow(theme);
            AddPathStage("目标网站");
            _pathDetail.Text = throughAirport
                ? "机场负责连接住宅代理，目标网站最终看到住宅 IP。"
                : "住宅代理连接固定从网卡2出站，不经过机场节点。";
        }
        finally
        {
            _pathPreview.ResumeLayout();
        }
    }

    private void AddPathStage(string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFonts.CaptionStrong,
            ForeColor = ThemeManager.Current.TextPrimary,
            BackColor = ThemeManager.Current.BackgroundSurface2,
            Padding = new Padding(UiMetrics.SpaceSm, 4, UiMetrics.SpaceSm, 4),
            Margin = new Padding(0, 2, 0, 0)
        };
        _pathPreview.Controls.Add(label);
    }

    private void AddArrow(UiTheme theme)
    {
        _pathPreview.Controls.Add(new Label
        {
            Text = "→",
            AutoSize = true,
            Font = UiFonts.Section,
            ForeColor = theme.TextMuted,
            Margin = new Padding(UiMetrics.SpaceSm, 4, UiMetrics.SpaceSm, 0)
        });
    }

    private void SelectRouteMode(ResidentialProxyRouteMode value)
    {
        for (var index = 0; index < _routeMode.Items.Count; index++)
        {
            if (_routeMode.Items[index] is RouteModeItem item && item.Value == value)
            {
                _routeMode.SelectedIndex = index;
                return;
            }
        }

        _routeMode.SelectedIndex = 0;
    }

    private void MarkDirty()
    {
        if (!_loading)
        {
            _dirty = true;
        }
    }

    private static Label GroupLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = UiFonts.CaptionStrong,
            ForeColor = ThemeManager.Current.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
    }

    private static void ConfigureTextBox(
        RoundedTextBox textBox,
        string placeholder,
        string accessibleName)
    {
        textBox.PlaceholderText = placeholder;
        textBox.InputAccessibleName = accessibleName;
        textBox.CornerRadius = 7;
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = Padding.Empty;
    }

    private static void ConfigureCheckBox(CheckBox checkBox, string text)
    {
        checkBox.Text = text;
        checkBox.AutoSize = true;
        checkBox.Font = UiFonts.Caption;
        checkBox.ForeColor = ThemeManager.Current.TextSecondary;
        checkBox.BackColor = ThemeManager.Current.BackgroundSurface;
        checkBox.FlatStyle = FlatStyle.Flat;
        checkBox.Margin = Padding.Empty;
    }

    private sealed record RouteModeItem(
        string Label,
        ResidentialProxyRouteMode Value)
    {
        public override string ToString()
        {
            return Label;
        }
    }
}
