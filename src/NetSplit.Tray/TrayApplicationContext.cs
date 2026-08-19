using System.ComponentModel;
using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NamedPipeRpcClient _client = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly MainForm _mainForm;
    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _nodeItem;
    private readonly System.Windows.Forms.Timer _timer = new();

    private string _nodeMenuSignature = string.Empty;
    private bool _refreshing;

    public TrayApplicationContext(bool startMinimized)
    {
        _mainForm = new MainForm(_client);

        var menu = new ContextMenuStrip();
        _headerItem = new ToolStripMenuItem("net-split - 正在连接服务")
        {
            Enabled = false
        };
        _toggleItem = new ToolStripMenuItem(
            "开启分流",
            null,
            async (_, _) => await ToggleAsync().ConfigureAwait(true));
        _nodeItem = new ToolStripMenuItem("代理节点");
        menu.Items.Add(_headerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_nodeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开控制台", null, (_, _) => ShowMainForm());
        menu.Items.Add(
            "修复运行状态",
            null,
            async (_, _) => await RunAsync(RpcCommands.Repair).ConfigureAwait(true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出界面", null, (_, _) => ExitThread());
        menu.Opening += async (_, _) => await RefreshAsync().ConfigureAwait(true);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "net-split",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainForm();

        _timer.Interval = 5000;
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _timer.Start();
        Application.Idle += OnFirstIdle;

        if (!startMinimized)
        {
            ShowMainForm();
        }
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        _ = RefreshAsync();
    }

    private void ShowMainForm()
    {
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
    }

    private async Task ToggleAsync()
    {
        try
        {
            var status = await _client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            await RunAsync(status?.Enabled == true
                    ? RpcCommands.Disable
                    : RpcCommands.Enable)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowBalloon(exception.Message);
        }
    }

    private async Task RunAsync(string command)
    {
        try
        {
            var timeout = command is RpcCommands.Enable or RpcCommands.Repair
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromSeconds(30);
            await _client.SendAsync(command, timeout: timeout).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowBalloon(exception.Message);
        }
    }

    private async Task SelectProxyAsync(string name)
    {
        try
        {
            await _client.SendAsync(
                RpcCommands.SelectProxy,
                new SelectProxyRequest { Name = name },
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowBalloon(exception.Message);
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var status = await _client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            if (status is null)
            {
                return;
            }

            var modeText = ModeVisuals.Text(status);
            _notifyIcon.Text = $"net-split - {modeText}";
            _headerItem.Text = $"net-split - {modeText}";
            _toggleItem.Text = status.Enabled ? "关闭分流" : "开启分流";
            _toggleItem.Enabled = true;
            _nodeItem.Enabled = true;
            RebuildNodeMenuIfChanged(
                status.AvailableProxies ?? [],
                status.CurrentProxy,
                status.ProxyRouteHealthKnown,
                status.HealthyProxies ?? []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notifyIcon.Text = "net-split - 服务离线";
            _headerItem.Text = "net-split - 服务离线";
            _toggleItem.Text = "重新连接后开启";
            _toggleItem.Enabled = false;
            _nodeItem.Enabled = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RebuildNodeMenuIfChanged(
        IReadOnlyList<string> proxies,
        string current,
        bool healthKnown,
        IReadOnlyList<string> healthyProxies)
    {
        var signature =
            $"{current}|{healthKnown}|{string.Join("|", proxies)}|{string.Join("|", healthyProxies)}";
        if (signature.Equals(_nodeMenuSignature, StringComparison.Ordinal)
            || _nodeItem.DropDown.Visible)
        {
            return;
        }

        _nodeItem.DropDownItems.Clear();
        var auto = new ToolStripMenuItem(
            "自动选择",
            null,
            async (_, _) => await SelectProxyAsync(
                MihomoConfigGenerator.AutoProxyGroupName).ConfigureAwait(true))
        {
            Checked = current.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal)
        };
        _nodeItem.DropDownItems.Add(auto);

        var nodes = proxies.Where(name =>
            !name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal)).ToArray();
        if (nodes.Length > 0)
        {
            _nodeItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var name in nodes)
            {
                var item = new ToolStripMenuItem(
                    name,
                    null,
                    async (_, _) => await SelectProxyAsync(name).ConfigureAwait(true))
                {
                    Checked = name.Equals(current, StringComparison.Ordinal),
                    Enabled = !healthKnown
                        || healthyProxies.Contains(name, StringComparer.Ordinal)
                };
                _nodeItem.DropDownItems.Add(item);
            }
        }

        _nodeMenuSignature = signature;
    }

    private void ShowBalloon(string message)
    {
        _notifyIcon.ShowBalloonTip(
            5000,
            "net-split",
            message,
            ToolTipIcon.Error);
    }

    protected override void ExitThreadCore()
    {
        Application.Idle -= OnFirstIdle;
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _mainForm.Dispose();
        base.ExitThreadCore();
    }
}
