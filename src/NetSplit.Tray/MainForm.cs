using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NetSplit.Core;

namespace NetSplit.Tray;

internal enum PageKind
{
    Overview,
    Proxies,
    ResidentialProxy,
    Subscriptions,
    Rules,
    Logs,
    Diagnostics,
    Settings
}

public sealed class MainForm : Form
{
    private readonly NamedPipeRpcClient _client;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<NavItem> _navItems = [];
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private Panel _pageHost = new();
    private ModeBadge _topBadge = new();
    private ToggleSwitch _masterToggle = new();
    private Label _toggleLabel = new();
    private Label _sectionLabel = new();
    private Label _serviceStateLabel = new();
    private WindowButton? _maximizeButton;

    private PageBase? _activePage;
    private CancellationTokenSource? _pageRefreshCancellation;
    private PageKind _activeKind = PageKind.Overview;
    private RuntimeStatus? _lastStatus;
    private bool _syncingToggle;
    private bool _busy;

    public MainForm(NamedPipeRpcClient client)
    {
        _client = client;

        Text = "net-split 双网卡透明分流";
        Icon = SystemIcons.Shield;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1240, 820);
        Font = UiFonts.Body;
        BackColor = ThemeManager.Current.BackgroundPage;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);

        BuildShell();
        NavigateTo(_activeKind, force: true);

        _timer.Interval = 5000;
        _timer.Tick += async (_, _) => await RefreshPeriodicallyAsync(waitForTurn: false)
            .ConfigureAwait(true);
        _timer.Start();

        ThemeManager.Changed += OnThemeChanged;
        FormClosing += OnFormClosing;
        Shown += async (_, _) => await RefreshPeriodicallyAsync(waitForTurn: true)
            .ConfigureAwait(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.Apply(this);
        NormalizeLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        NormalizeLayout();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _maximizeButton?.SetMaximized(WindowState == FormWindowState.Maximized);
    }

    protected override void WndProc(ref Message m)
    {
        const int wmNcHitTest = 0x0084;
        const int htClient = 1;
        if (m.Msg == wmNcHitTest && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result != htClient)
            {
                return;
            }

            var screenX = (short)((long)m.LParam & 0xFFFF);
            var screenY = (short)(((long)m.LParam >> 16) & 0xFFFF);
            var point = PointToClient(new Point(screenX, screenY));
            const int grip = 6;
            var left = point.X <= grip;
            var right = point.X >= ClientSize.Width - grip;
            var top = point.Y <= grip;
            var bottom = point.Y >= ClientSize.Height - grip;
            m.Result = (nint)((left, right, top, bottom) switch
            {
                (true, _, true, _) => 13,
                (_, true, true, _) => 14,
                (true, _, _, true) => 16,
                (_, true, _, true) => 17,
                (true, _, _, _) => 10,
                (_, true, _, _) => 11,
                (_, _, true, _) => 12,
                (_, _, _, true) => 15,
                _ => htClient
            });
            return;
        }

        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && Visible)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void BuildShell()
    {
        CancelPageRefresh();
        _activePage = null;

        foreach (Control control in Controls)
        {
            control.Dispose();
        }

        Controls.Clear();
        var theme = ThemeManager.Current;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sidebar = BuildSidebar();
        root.Controls.Add(sidebar, 0, 0);
        root.SetRowSpan(sidebar, 2);
        root.Controls.Add(BuildTopBar(), 1, 0);

        _pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundPage,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        root.Controls.Add(_pageHost, 1, 1);
        Controls.Add(root);
    }

    private BottomBorderPanel BuildTopBar()
    {
        var theme = ThemeManager.Current;
        var bar = new BottomBorderPanel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundChrome,
            Padding = new Padding(
                UiMetrics.Space2xl,
                9,
                6,
                8)
        };
        AttachDragHandlers(bar);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundChrome,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AttachDragHandlers(table);

        var location = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Left,
            BackColor = theme.BackgroundChrome,
            Margin = Padding.Empty
        };
        location.Controls.Add(new Label
        {
            Text = "控制中心",
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 6, UiMetrics.SpaceSm, 0)
        });
        location.Controls.Add(new Label
        {
            Text = "/",
            Font = UiFonts.Caption,
            ForeColor = theme.BorderStrong,
            AutoSize = true,
            Margin = new Padding(0, 6, UiMetrics.SpaceSm, 0)
        });
        _sectionLabel = new Label
        {
            Text = PageTitle(_activeKind),
            Font = UiFonts.BodyStrong,
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0)
        };
        location.Controls.Add(_sectionLabel);
        AttachDragHandlers(location);
        table.Controls.Add(location, 0, 0);

        var controls = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundChrome,
            Margin = Padding.Empty
        };
        _topBadge = new ModeBadge
        {
            Margin = new Padding(0, 3, UiMetrics.SpaceLg, 0)
        };
        _toggleLabel = new Label
        {
            AutoSize = true,
            Font = UiFonts.BodyStrong,
            ForeColor = theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "分流已关闭",
            Margin = new Padding(0, 6, UiMetrics.SpaceSm, 0)
        };
        _masterToggle = new ToggleSwitch
        {
            Margin = new Padding(0, 4, 0, 0)
        };
        _masterToggle.CheckedChanged += async (_, _) =>
            await OnMasterToggleChangedAsync().ConfigureAwait(true);

        controls.Controls.Add(_topBadge);
        controls.Controls.Add(_toggleLabel);
        controls.Controls.Add(_masterToggle);
        table.Controls.Add(controls, 1, 0);

        var windowControls = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundChrome,
            Margin = new Padding(UiMetrics.SpaceMd, 0, 0, 0)
        };
        var minimize = new WindowButton(WindowAction.Minimize)
        {
            Margin = Padding.Empty
        };
        minimize.Click += (_, _) =>
            WindowState = FormWindowState.Minimized;
        windowControls.Controls.Add(minimize);
        _maximizeButton = new WindowButton(WindowAction.Maximize)
        {
            Margin = Padding.Empty
        };
        _maximizeButton.SetMaximized(WindowState == FormWindowState.Maximized);
        _maximizeButton.Click += (_, _) => ToggleMaximize();
        windowControls.Controls.Add(_maximizeButton);
        var close = new WindowButton(WindowAction.Close)
        {
            Margin = Padding.Empty
        };
        close.Click += (_, _) => Hide();
        windowControls.Controls.Add(close);
        table.Controls.Add(windowControls, 2, 0);
        bar.Controls.Add(table);
        return bar;
    }

    private RightBorderPanel BuildSidebar()
    {
        var theme = ThemeManager.Current;
        var sidebar = new RightBorderPanel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.SidebarBackground,
            Padding = new Padding(UiMetrics.SpaceMd, 0, UiMetrics.SpaceMd, UiMetrics.SpaceLg)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = theme.SidebarBackground,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.SidebarBackground,
            Margin = Padding.Empty,
            Padding = new Padding(UiMetrics.SpaceSm, UiMetrics.SpaceLg, 0, UiMetrics.SpaceMd)
        };
        brand.Height = UiMetrics.Scale(brand, 72);
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var mark = new SplitMark
        {
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left
        };
        mark.Size = new Size(UiMetrics.Scale(mark, 32), UiMetrics.Scale(mark, 32));
        brand.Controls.Add(mark, 0, 0);
        var brandCopy = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            BackColor = theme.SidebarBackground,
            Margin = new Padding(0, 0, 0, 0)
        };
        brandCopy.Controls.Add(new Label
        {
            Text = "net-split",
            Font = new Font(UiFonts.UiFamily, 12f, FontStyle.Bold),
            ForeColor = theme.SidebarText,
            AutoSize = true,
            Margin = Padding.Empty
        });
        brandCopy.Controls.Add(new Label
        {
            Text = "双网卡透明分流",
            Font = UiFonts.Caption,
            ForeColor = theme.SidebarMuted,
            AutoSize = true,
            Margin = new Padding(0, 1, 0, 0)
        });
        brand.Controls.Add(brandCopy, 1, 0);

        var nav = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = theme.SidebarBackground,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiMetrics.SpaceSm, 0, 0)
        };
        _navItems.Clear();
        nav.Controls.Add(NavGroupLabel("工作台"));
        AddNavItem(nav, "概览", PageKind.Overview, NavGlyph.Overview);
        AddNavItem(nav, "代理节点", PageKind.Proxies, NavGlyph.Proxies);
        AddNavItem(nav, "住宅代理", PageKind.ResidentialProxy, NavGlyph.ResidentialProxy);
        nav.Controls.Add(NavGroupLabel("管理"));
        AddNavItem(nav, "订阅", PageKind.Subscriptions, NavGlyph.Subscriptions);
        AddNavItem(nav, "规则", PageKind.Rules, NavGlyph.Rules);
        AddNavItem(nav, "运行日志", PageKind.Logs, NavGlyph.Logs);
        AddNavItem(nav, "诊断", PageKind.Diagnostics, NavGlyph.Diagnostics);
        AddNavItem(nav, "设置", PageKind.Settings, NavGlyph.Settings);
        nav.HandleCreated += (_, _) => ResizeNavigationItems(nav);
        nav.Resize += (_, _) => ResizeNavigationItems(nav);

        var footer = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.SidebarBackground,
            Margin = Padding.Empty,
            Padding = new Padding(
                UiMetrics.SpaceSm,
                UiMetrics.SpaceXs,
                0,
                0)
        };
        footer.Height = Math.Max(
            UiMetrics.Scale(footer, 52),
            UiFonts.CaptionStrong.Height
            + UiFonts.Caption.Height
            + UiMetrics.SpaceSm);
        footer.MinimumSize = new Size(0, footer.Height);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _serviceStateLabel = new Label
        {
            Text = "●  本地服务连接中",
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.SidebarMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            Margin = Padding.Empty
        };
        footer.Controls.Add(_serviceStateLabel, 0, 0);
        footer.Controls.Add(new Label
        {
            Text = "Windows 11 x64  ·  v1.0.0",
            Font = UiFonts.Caption,
            ForeColor = theme.SidebarMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
            Margin = Padding.Empty
        }, 0, 1);

        layout.Controls.Add(brand, 0, 0);
        layout.Controls.Add(nav, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        sidebar.Controls.Add(layout);
        return sidebar;
    }

    private void AddNavItem(
        Control nav,
        string text,
        PageKind kind,
        NavGlyph glyph)
    {
        var item = new NavItem(text, glyph)
        {
            Margin = new Padding(0, 1, 0, 1),
            Tag = kind
        };
        item.Width = UiMetrics.Scale(item, 207);
        item.Click += (_, _) => NavigateTo(kind);
        nav.Controls.Add(item);
        _navItems.Add(item);
    }

    private static void ResizeNavigationItems(FlowLayoutPanel nav)
    {
        var availableWidth = Math.Max(
            UiMetrics.Scale(nav, 120),
            nav.ClientSize.Width
            - nav.Padding.Horizontal
            - SystemInformation.VerticalScrollBarWidth
            - UiMetrics.SpaceXs);
        foreach (Control child in nav.Controls)
        {
            if (child.Width != availableWidth)
            {
                child.Width = availableWidth;
            }
        }
    }

    private static Label NavGroupLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            Font = UiFonts.Badge,
            ForeColor = ThemeManager.Current.SidebarMuted,
            AutoSize = false,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(UiMetrics.SpaceSm, 0, 0, 5),
            Margin = new Padding(0, UiMetrics.SpaceSm, 0, 2)
        };
        label.Width = UiMetrics.Scale(label, 207);
        label.Height = UiMetrics.Scale(label, 28);
        return label;
    }

    private static string PageTitle(PageKind kind)
    {
        return kind switch
        {
            PageKind.Overview => "概览",
            PageKind.Proxies => "代理节点",
            PageKind.ResidentialProxy => "住宅代理",
            PageKind.Subscriptions => "订阅",
            PageKind.Rules => "自定义规则",
            PageKind.Logs => "运行日志",
            PageKind.Diagnostics => "诊断",
            PageKind.Settings => "设置",
            _ => "概览"
        };
    }

    private void NavigateTo(PageKind kind, bool force = false)
    {
        if (!force && kind == _activeKind && _activePage is not null)
        {
            return;
        }

        _activeKind = kind;
        if (!_sectionLabel.IsDisposed)
        {
            _sectionLabel.Text = PageTitle(kind);
        }

        foreach (var item in _navItems)
        {
            item.Selected = item.Tag is PageKind tagged && tagged == kind;
        }

        CancelPageRefresh();
        _activePage?.Dispose();
        _pageHost.Controls.Clear();

        _pageRefreshCancellation = new CancellationTokenSource();
        _activePage = CreatePage(kind);
        _activePage.Dock = DockStyle.Fill;
        _pageHost.Controls.Add(_activePage);
        NormalizeLayout();
        _ = RefreshPeriodicallyAsync(waitForTurn: true);
    }

    private PageBase CreatePage(PageKind kind)
    {
        return kind switch
        {
            PageKind.Overview => new OverviewPage(_client, () => NavigateTo(PageKind.Proxies)),
            PageKind.Proxies => new ProxiesPage(_client),
            PageKind.ResidentialProxy => new ResidentialProxyPage(_client),
            PageKind.Subscriptions => new SubscriptionsPage(_client),
            PageKind.Rules => new RulesPage(_client),
            PageKind.Logs => new LogsPage(_client),
            PageKind.Diagnostics => new DiagnosticsPage(_client),
            PageKind.Settings => new SettingsPage(_client),
            _ => new OverviewPage(_client, () => NavigateTo(PageKind.Proxies))
        };
    }

    private async Task OnMasterToggleChangedAsync()
    {
        if (_syncingToggle || _busy)
        {
            return;
        }

        _busy = true;
        _masterToggle.Enabled = false;
        try
        {
            if (_masterToggle.Checked)
            {
                var answer = MessageBox.Show(
                    this,
                    "开启后会启动 Mihomo TUN 并接管本机 IPv4 流量。系统会先验证配置，验证失败时不会启用。",
                    "开启透明分流",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    SyncToggle(false);
                    return;
                }

                await _client.SendAsync(
                    RpcCommands.Enable,
                    timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }
            else
            {
                await _client.SendAsync(
                    RpcCommands.Disable,
                    timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "net-split",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _masterToggle.Enabled = true;
            await RefreshPeriodicallyAsync(waitForTurn: true).ConfigureAwait(true);
        }
    }

    private async Task RefreshPeriodicallyAsync(bool waitForTurn)
    {
        var entered = waitForTurn
            ? await _refreshGate.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(true)
            : await _refreshGate.WaitAsync(0).ConfigureAwait(true);
        if (!entered)
        {
            return;
        }

        try
        {
            var status = await _client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            if (status is not null)
            {
                _lastStatus = status;
                ApplyStatus(status);
            }

            var page = _activePage;
            var cancellation = _pageRefreshCancellation;
            if (page is not null
                && cancellation is not null
                && !page.IsDisposed
                && !cancellation.IsCancellationRequested)
            {
                try
                {
                    await page.RefreshAsync(cancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Page navigation or a page-level timeout should not mark the service offline.
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"net-split page refresh failed: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Page navigation cancels in-flight refreshes.
        }
        catch (Exception)
        {
            ApplyServiceOfflineState();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyStatus(RuntimeStatus status)
    {
        if (_topBadge.IsDisposed || _masterToggle.IsDisposed)
        {
            return;
        }

        _topBadge.SetMode(status.Mode);
        SyncToggle(status.Enabled);
        _toggleLabel.Text = status.Enabled ? "分流已开启" : "分流已关闭";
        _toggleLabel.ForeColor = status.Enabled
            ? ThemeManager.Current.AccentText
            : ThemeManager.Current.TextSecondary;
        _masterToggle.Enabled = !_busy
            && status.Mode is not RuntimeMode.Starting
            and not RuntimeMode.Stopping
            and not RuntimeMode.Misconfigured;
        ApplyServiceStateLabel(status);
    }

    private void ApplyServiceStateLabel(RuntimeStatus status)
    {
        if (_serviceStateLabel.IsDisposed)
        {
            return;
        }

        var theme = ThemeManager.Current;
        var state = status.Mode switch
        {
            RuntimeMode.Healthy => ("●  本地服务已连接", theme.Success),
            RuntimeMode.Disabled => ("●  本地服务已连接", theme.SidebarMuted),
            RuntimeMode.Starting => ("●  服务初始化中", theme.Warning),
            RuntimeMode.Stopping => ("●  服务停止中", theme.Warning),
            RuntimeMode.DirectUnavailable => ("●  网卡1不可用", theme.Warning),
            RuntimeMode.ProxyUnavailable =>
                ($"●  {ModeVisuals.ProxyRouteText(status.ProxyRouteFailure)}", theme.Danger),
            RuntimeMode.CoreUnavailable => ("●  Mihomo 不可用", theme.Danger),
            RuntimeMode.Misconfigured => ("●  需要恢复", theme.Danger),
            _ => ("●  服务状态未知", theme.SidebarMuted)
        };
        _serviceStateLabel.Text = state.Item1;
        _serviceStateLabel.ForeColor = state.Item2;
    }

    private void ApplyServiceOfflineState()
    {
        if (_topBadge.IsDisposed || _toggleLabel.IsDisposed || _masterToggle.IsDisposed)
        {
            return;
        }

        _topBadge.SetMode(RuntimeMode.CoreUnavailable);
        _toggleLabel.Text = "服务离线 · 状态未知";
        _toggleLabel.ForeColor = ThemeManager.Current.Danger;
        _masterToggle.Enabled = false;
        if (!_serviceStateLabel.IsDisposed)
        {
            _serviceStateLabel.Text = "●  本地服务离线";
            _serviceStateLabel.ForeColor = ThemeManager.Current.Danger;
        }
    }

    private void SyncToggle(bool value)
    {
        _syncingToggle = true;
        try
        {
            _masterToggle.SetCheckedSilently(value);
        }
        finally
        {
            _syncingToggle = false;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(RebuildForTheme);
        }
        catch (InvalidOperationException)
        {
            // The window is already being destroyed.
        }
    }

    private void RebuildForTheme()
    {
        if (IsDisposed)
        {
            return;
        }

        BackColor = ThemeManager.Current.BackgroundPage;
        BuildShell();
        NavigateTo(_activeKind, force: true);
        if (_lastStatus is not null)
        {
            ApplyStatus(_lastStatus);
        }

        WindowChrome.Apply(this);
        NormalizeLayout();
    }

    private void NormalizeLayout()
    {
        if (IsDisposed)
        {
            return;
        }

        UiLayout.Normalize(this);
        PerformLayout();
        _activePage?.PerformLayout();
    }

    private void CancelPageRefresh()
    {
        if (_pageRefreshCancellation is null)
        {
            return;
        }

        _pageRefreshCancellation.Cancel();
        _pageRefreshCancellation.Dispose();
        _pageRefreshCancellation = null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void AttachDragHandlers(Control control)
    {
        control.MouseDown += BeginWindowDrag;
        control.DoubleClick += (_, _) => ToggleMaximize();
        foreach (Control child in control.Controls)
        {
            AttachDragHandlers(child);
        }
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (WindowState == FormWindowState.Maximized)
        {
            return;
        }

        _ = ReleaseCapture();
        _ = SendMessage(Handle, 0x00A1, 2, 0);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            CancelPageRefresh();
            ThemeManager.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }

    private enum NavGlyph
    {
        Overview,
        Proxies,
        ResidentialProxy,
        Subscriptions,
        Rules,
        Logs,
        Diagnostics,
        Settings
    }

    private sealed class NavItem : Control
    {
        private bool _hovered;
        private bool _selected;
        private readonly NavGlyph _glyph;

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Invalidate();
            }
        }

        public NavItem(string text, NavGlyph glyph)
        {
            Text = text;
            _glyph = glyph;
            Font = UiFonts.Body;
            Height = UiMetrics.Scale(this, UiMetrics.NavItemHeight);
            MinimumSize = new Size(0, UiMetrics.Scale(this, UiMetrics.NavItemHeight));
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.StandardClick,
                true);
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = text;
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Height = UiMetrics.Scale(this, UiMetrics.NavItemHeight);
            MinimumSize = new Size(0, UiMetrics.Scale(this, UiMetrics.NavItemHeight));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var theme = ThemeManager.Current;
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(theme.SidebarBackground);

            var background = _selected
                ? theme.SidebarSelected
                : _hovered
                    ? theme.SidebarHover
                    : theme.SidebarBackground;
            var foreground = _selected
                ? theme.SidebarText
                : _hovered
                    ? theme.SidebarText
                    : theme.SidebarMuted;
            var iconColor = _selected ? theme.AccentText : foreground;

            // Item background (inset 2px from left to leave room for indicator bar)
            var rect = new Rectangle(2, 0, Width - 3, Height - 1);
            using var path = UiDrawing.Rounded(rect, UiMetrics.RadiusMd);
            using (var brush = new SolidBrush(background))
            {
                graphics.FillPath(brush, path);
            }

            // 3px accent indicator bar on the left when selected
            if (_selected)
            {
                var barWidth = UiMetrics.Scale(this, 3);
                var barHeight = UiMetrics.Scale(this, 18);
                var barY = (Height - barHeight) / 2;
                using var barPath = UiDrawing.Rounded(
                    new Rectangle(0, barY, barWidth, barHeight), barWidth / 2);
                using var barBrush = new SolidBrush(theme.Accent);
                graphics.FillPath(barBrush, barPath);
            }

            if (Focused)
            {
                var focusRect = Rectangle.Inflate(rect, -2, -2);
                using var focusPath = UiDrawing.Rounded(focusRect, UiMetrics.RadiusSm);
                using var focusPen = new Pen(theme.Accent, 1.5f);
                graphics.DrawPath(focusPen, focusPath);
            }

            int Scale(int value) => UiMetrics.Scale(this, value);
            var iconRect = new Rectangle(Scale(9), Scale(6), Scale(28), Scale(28));
            // Icon background: accent-tinted pill when selected
            if (_selected)
            {
                using var iconPath = UiDrawing.Rounded(iconRect, UiMetrics.RadiusMd);
                using var iconFill = new SolidBrush(UiDrawing.WithAlpha(theme.Accent, 42));
                graphics.FillPath(iconFill, iconPath);
            }

            TextRenderer.DrawText(
                graphics,
                GlyphText(_glyph),
                UiFonts.IconLarge,
                iconRect,
                iconColor,
                TextFormatFlags.VerticalCenter
                    | TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPadding);
            var textRect = new Rectangle(
                Scale(47),
                0,
                Width - Scale(57),
                Height);
            TextRenderer.DrawText(
                graphics,
                Text,
                _selected ? UiFonts.BodyStrong : Font,
                textRect,
                foreground,
                TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Left
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.NoPrefix);
        }

        private static string GlyphText(NavGlyph glyph)
        {
            return glyph switch
            {
                NavGlyph.Overview => UiGlyphs.Overview,
                NavGlyph.Proxies => UiGlyphs.Proxies,
                NavGlyph.ResidentialProxy => UiGlyphs.ResidentialProxy,
                NavGlyph.Subscriptions => UiGlyphs.Subscriptions,
                NavGlyph.Rules => UiGlyphs.Rules,
                NavGlyph.Logs => UiGlyphs.Logs,
                NavGlyph.Diagnostics => UiGlyphs.Shield,
                NavGlyph.Settings => UiGlyphs.Settings,
                _ => UiGlyphs.Overview
            };
        }
    }

    private enum WindowAction
    {
        Minimize,
        Maximize,
        Close
    }

    private sealed class WindowButton : Control
    {
        private readonly WindowAction _action;
        private bool _hovered;
        private bool _maximized;

        public WindowButton(WindowAction action)
        {
            _action = action;
            Size = new Size(UiMetrics.Scale(this, 40), UiMetrics.Scale(this, 32));
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = action switch
            {
                WindowAction.Minimize => "最小化",
                WindowAction.Maximize => "最大化",
                _ => "关闭窗口"
            };
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Selectable
                | ControlStyles.StandardClick,
                true);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            Size = new Size(UiMetrics.Scale(this, 40), UiMetrics.Scale(this, 32));
        }

        public void SetMaximized(bool maximized)
        {
            _maximized = maximized;
            AccessibleName = maximized ? "还原窗口" : "最大化";
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var theme = ThemeManager.Current;
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(UiDrawing.Backdrop(this));
            if (_hovered)
            {
                var hoverColor = _action == WindowAction.Close
                    ? theme.Danger
                    : theme.BackgroundSurface2;
                using var hover = new SolidBrush(hoverColor);
                graphics.FillRectangle(hover, ClientRectangle);
            }

            var color = _hovered && _action == WindowAction.Close
                ? Color.White
                : theme.TextSecondary;
            using var pen = new Pen(color, 1.2f);
            var centerX = Width / 2;
            var centerY = Height / 2;
            int Scale(int value) => UiMetrics.Scale(this, value);
            switch (_action)
            {
                case WindowAction.Minimize:
                    graphics.DrawLine(
                        pen,
                        centerX - Scale(5),
                        centerY + Scale(3),
                        centerX + Scale(5),
                        centerY + Scale(3));
                    break;
                case WindowAction.Maximize:
                    if (_maximized)
                    {
                        graphics.DrawRectangle(
                            pen,
                            centerX - Scale(3),
                            centerY - Scale(5),
                            Scale(8),
                            Scale(8));
                        graphics.DrawRectangle(
                            pen,
                            centerX - Scale(5),
                            centerY - Scale(3),
                            Scale(8),
                            Scale(8));
                    }
                    else
                    {
                        graphics.DrawRectangle(
                            pen,
                            centerX - Scale(5),
                            centerY - Scale(5),
                            Scale(10),
                            Scale(10));
                    }

                    break;
                case WindowAction.Close:
                    graphics.DrawLine(
                        pen,
                        centerX - Scale(5),
                        centerY - Scale(5),
                        centerX + Scale(5),
                        centerY + Scale(5));
                    graphics.DrawLine(
                        pen,
                        centerX + Scale(5),
                        centerY - Scale(5),
                        centerX - Scale(5),
                        centerY + Scale(5));
                    break;
            }
        }
    }

    private sealed class BottomBorderPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(ThemeManager.Current.Border);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    private sealed class RightBorderPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(ThemeManager.Current.Border);
            e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint window,
        int message,
        nint wParam,
        nint lParam);
}
