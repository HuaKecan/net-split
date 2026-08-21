using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class DiagnosticsPage : PageBase
{
    private static readonly TimeSpan P0ObservationTimeout = TimeSpan.FromSeconds(45);

    private readonly ModeBadge _modeBadge = new();
    private readonly Label _readinessValue = new();
    private readonly Label _generatedValue = new();
    private readonly Label _serviceValue = new();
    private readonly Label _routingValue = new();
    private readonly Label _adaptersValue = new();
    private readonly Label _configurationValue = new();
    private readonly Label _startupValue = new();
    private readonly Label _lastErrorValue = new();
    private readonly DataGridView _files = UiGrid.Create();
    private readonly Label _emptyFiles = new();
    private readonly StartupRegistrationProbe _startupProbe = new();

    private DiagnosticsSnapshot? _snapshot;

    public DiagnosticsPage(NamedPipeRpcClient client)
        : base(
            client,
            "诊断",
            "检查服务、TUN/DNS、物理网卡和运行文件；复制或导出的内容不包含订阅地址与节点凭据。")
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
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildSummaryHeader(), 0, 0);
        root.Controls.Add(BuildHealthCard(), 0, 1);
        root.Controls.Add(BuildFilesCard(), 0, 2);
        Content.Controls.Add(root);
    }

    private Card BuildSummaryHeader()
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
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _modeBadge.Margin = new Padding(0, 2, UiMetrics.SpaceLg, 0);
        table.Controls.Add(_modeBadge, 0, 0);

        _readinessValue.Text = "正在读取服务状态…";
        _readinessValue.Font = UiFonts.BodyStrong;
        _readinessValue.ForeColor = theme.TextPrimary;
        _readinessValue.AutoSize = true;
        _readinessValue.Anchor = AnchorStyles.Left;
        _readinessValue.Margin = new Padding(0, 5, UiMetrics.SpaceLg, 0);
        table.Controls.Add(_readinessValue, 1, 0);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        var copy = CreateButton(
            "复制诊断",
            ButtonKind.Secondary,
            (_, _) => _ = CopyDiagnosticsAsync(),
            0,
            UiGlyphs.Copy);
        copy.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        actions.Controls.Add(copy);
        actions.Controls.Add(CreateButton(
            "导出 JSON",
            ButtonKind.Secondary,
            (_, _) => _ = ExportDiagnosticsAsync(),
            0,
            UiGlyphs.Export));
        actions.Controls.Add(CreateButton(
            "修复启动",
            ButtonKind.Secondary,
            (_, _) => _ = RepairStartupAsync(),
            0,
            UiGlyphs.Repair));
        table.Controls.Add(actions, 2, 0);

        card.Controls.Add(table);
        return card;
    }

    private Card BuildHealthCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 7,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.Controls.Add(UiStyle.SectionTitle("运行检查"), 0, 0);
        _generatedValue.Text = "尚未生成";
        _generatedValue.Font = UiFonts.Caption;
        _generatedValue.ForeColor = theme.TextMuted;
        _generatedValue.AutoSize = true;
        _generatedValue.Anchor = AnchorStyles.Right;
        _generatedValue.Margin = new Padding(0, 2, 0, 0);
        heading.Controls.Add(_generatedValue, 1, 0);
        var observe = CreateButton(
            "采集 P0 证据",
            ButtonKind.Secondary,
            (_, _) => _ = CaptureP0ObservationAsync(),
            0,
            UiGlyphs.Network);
        observe.Margin = new Padding(UiMetrics.SpaceSm, UiMetrics.SpaceSm, 0, 0);
        observe.AccessibleName = "采集只读 P0 证据";
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        actions.Controls.Add(observe);
        heading.Controls.Add(actions, 1, 1);
        table.Controls.Add(heading, 0, 0);

        table.Controls.Add(
            HealthLine(
                "服务与核心",
                "服务初始化、Mihomo 进程和配置控制器",
                _serviceValue),
            0,
            1);
        table.Controls.Add(
            HealthLine(
                "透明接管",
                "TUN、Fake-IP DNS 和 IPv4 严格路由",
                _routingValue),
            0,
            2);
        table.Controls.Add(
            HealthLine(
                "物理出口",
                "网卡1直连、网卡2代理及代理健康状态",
                _adaptersValue),
            0,
            3);
        table.Controls.Add(
            HealthLine(
                "运行配置",
                "Mihomo、GeoData、订阅和自定义规则",
                _configurationValue),
            0,
            4);
        table.Controls.Add(
            HealthLine(
                "启动注册",
                "Windows 服务和当前用户的登录托盘任务",
                _startupValue),
            0,
            5);

        _lastErrorValue.Text = "最近错误：无";
        _lastErrorValue.Font = UiFonts.Caption;
        _lastErrorValue.ForeColor = theme.TextMuted;
        _lastErrorValue.Dock = DockStyle.Top;
        _lastErrorValue.AutoSize = true;
        _lastErrorValue.AutoEllipsis = true;
        _lastErrorValue.Margin = Padding.Empty;
        table.Controls.Add(_lastErrorValue, 0, 6);

        card.Controls.Add(table);
        return card;
    }

    private Card BuildFilesCard()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(UiMetrics.SpaceMd),
            Margin = Padding.Empty
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
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(UiStyle.SectionTitle("关键文件"), 0, 0);
        heading.Controls.Add(
            UiStyle.MutedText("必需文件缺失会影响启动；运行痕迹为空属于正常状态"),
            0,
            1);
        table.Controls.Add(heading, 0, 0);

        _files.AccessibleName = "诊断关键文件列表";
        _files.Columns.Add(UiGrid.TextColumn("Name", "文件", fill: true));
        _files.Columns.Add(UiGrid.TextColumn("State", "状态", 76));
        _files.Columns.Add(UiGrid.TextColumn("Size", "大小", 96));
        _files.Columns.Add(UiGrid.TextColumn("Updated", "更新时间", 150));
        _files.Columns.Add(UiGrid.TextColumn("Hash", "SHA-256", 150));
        _files.CellFormatting += OnFileCellFormatting;

        _emptyFiles.Text = "暂无诊断文件信息。";
        _emptyFiles.Font = UiFonts.Body;
        _emptyFiles.ForeColor = theme.TextMuted;
        _emptyFiles.TextAlign = ContentAlignment.MiddleCenter;
        _emptyFiles.Dock = DockStyle.Fill;
        _emptyFiles.Visible = false;
        _emptyFiles.BackColor = theme.BackgroundSurface;

        var fileHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        fileHost.Controls.Add(_files);
        fileHost.Controls.Add(_emptyFiles);
        table.Controls.Add(fileHost, 0, 1);
        card.Controls.Add(table);
        return card;
    }

    private static TableLayoutPanel HealthLine(
        string label,
        string detail,
        Label value)
    {
        var theme = ThemeManager.Current;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiMetrics.SpaceXs, 0, UiMetrics.SpaceXs)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var copy = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = theme.BackgroundSurface,
            Margin = Padding.Empty
        };
        copy.Controls.Add(new Label
        {
            Text = label,
            Font = UiFonts.CaptionStrong,
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Margin = Padding.Empty
        });
        copy.Controls.Add(new Label
        {
            Text = detail,
            Font = UiFonts.Caption,
            ForeColor = theme.TextMuted,
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 2, 0, 0)
        });
        row.Controls.Add(copy, 0, 0);

        value.Text = "读取中";
        value.Font = UiFonts.CaptionStrong;
        value.ForeColor = theme.TextSecondary;
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.AutoEllipsis = true;
        value.Margin = Padding.Empty;
        row.Controls.Add(value, 1, 0);
        return row;
    }

    private void OnFileCellFormatting(
        object? sender,
        DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0
            || _files.Rows[e.RowIndex].DataBoundItem is not DiagnosticFileRow row)
        {
            return;
        }

        if (_files.Columns[e.ColumnIndex].Name == "State")
        {
            var cellStyle = e.CellStyle;
            if (cellStyle is null)
            {
                return;
            }

            cellStyle.ForeColor = row.Warning
                ? ThemeManager.Current.Warning
                : row.Exists
                    ? ThemeManager.Current.Success
                    : row.Healthy
                        ? ThemeManager.Current.TextMuted
                        : ThemeManager.Current.Danger;
            cellStyle.Font = UiFonts.BodyStrong;
        }
    }

    private async Task<DiagnosticsSnapshot> EnsureSnapshotAsync()
    {
        _snapshot = await Client.SendAsync<DiagnosticsSnapshot>(
            RpcCommands.GetDiagnostics,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        return _snapshot
            ?? throw new InvalidOperationException("服务未返回诊断数据。");
    }

    private async Task CopyDiagnosticsAsync()
    {
        await RunActionAsync(async () =>
        {
            var snapshot = await EnsureSnapshotAsync().ConfigureAwait(true);
            var json = Serialize(snapshot);
            try
            {
                Clipboard.SetText(json);
            }
            catch (Exception exception) when (
                exception is ExternalException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"无法写入剪贴板：{exception.Message}",
                    exception);
            }

            ShowInfo("脱敏诊断报告已复制到剪贴板。");
        }).ConfigureAwait(true);
    }

    private async Task ExportDiagnosticsAsync()
    {
        await RunActionAsync(async () =>
        {
            var snapshot = await EnsureSnapshotAsync().ConfigureAwait(true);
            using var dialog = new SaveFileDialog
            {
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                FileName = $"net-split-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            await File.WriteAllTextAsync(
                dialog.FileName,
                Serialize(snapshot),
                new UTF8Encoding(false)).ConfigureAwait(true);
            ShowInfo($"诊断报告已导出到 {dialog.FileName}");
        }).ConfigureAwait(true);
    }

    private async Task RepairStartupAsync()
    {
        await RunActionAsync(async () =>
        {
            var scriptPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "net-split",
                "repair-startup.ps1");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException(
                    $"找不到启动修复脚本：{scriptPath}");
            }

            using var process = Process.Start(
                CreateElevatedPowerShellStartInfo(scriptPath))
                ?? throw new InvalidOperationException("无法启动启动修复工具。");
            await process.WaitForExitAsync().ConfigureAwait(true);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"启动修复工具返回错误码 {process.ExitCode}。");
            }

            _startupProbe.Invalidate();
            ShowInfo("启动注册已修复，当前服务和 TUN 状态未被改变。");
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task CaptureP0ObservationAsync()
    {
        await RunActionAsync(async () =>
        {
            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "net-split");
            var scriptPath = Path.Combine(installRoot, "p0-observe.ps1");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException(
                    $"找不到 P0 观察脚本：{scriptPath}。请重新安装 net-split。");
            }

            var outputDirectory = Path.Combine(
                installRoot,
                "artifacts",
                "p0",
                $"gui-{Guid.NewGuid():N}");
            ShowInfo("正在采集 8 秒只读网络证据，请保持国内与境外连接有流量。");
            using var process = Process.Start(
                CreateElevatedPowerShellStartInfo(
                    scriptPath,
                    "-SampleSeconds",
                    "8",
                    "-OutputDirectory",
                    outputDirectory))
                ?? throw new InvalidOperationException("无法启动 P0 观察工具。");

            await WaitForExitAsync(process, P0ObservationTimeout).ConfigureAwait(true);
            var reportPath = FindLatestP0ObservationReport(outputDirectory);
            if (reportPath is null)
            {
                throw new InvalidOperationException(
                    $"P0 观察工具返回错误码 {process.ExitCode}，但未在输出目录生成报告。");
            }

            var reportJson = await File.ReadAllTextAsync(
                reportPath,
                new UTF8Encoding(false)).ConfigureAwait(true);
            var summary = ParseP0ObservationReport(reportJson);
            if (process.ExitCode is not 0 and not 2 and not 3)
            {
                throw new InvalidOperationException(
                    $"P0 观察工具执行失败（错误码 {process.ExitCode}）。"
                    + $"诊断报告已保留为 {Path.GetFileName(reportPath)}。");
            }

            var bindingText = summary.BindingEvidenceObserved
                ? $"已观察到（网卡1 {summary.DirectAdapterTcpCount} / "
                  + $"网卡2 {summary.ProxyAdapterTcpCount}）"
                : "未观察到";
            var message =
                $"P0 只读证据已生成：{Path.GetFileName(reportPath)} · "
                + $"采集就绪 {(summary.CaptureReady ? "是" : "否")} · "
                + $"网卡绑定证据 {bindingText} · "
                + $"Mihomo 校验 {(summary.MihomoHashVerified ? "通过" : "未通过")}";
            if (process.ExitCode == 0
                && summary.CaptureReady
                && summary.BindingEvidenceObserved
                && summary.MihomoHashVerified)
            {
                ShowInfo(message);
            }
            else
            {
                ShowWarning(message);
            }
        }).ConfigureAwait(true);
    }

    internal static ProcessStartInfo CreateElevatedPowerShellStartInfo(
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static string? FindLatestP0ObservationReport(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(
                outputDirectory,
                "p0-observe-*.json",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    internal static P0ObservationSummary ParseP0ObservationReport(string json)
    {
        try
        {
            using var report = JsonDocument.Parse(json);
            var root = report.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException("P0 报告根节点必须是 JSON 对象。");
            }

            var captureReady = ReadRequiredBoolean(root, "CaptureReady");
            var bindingObserved = ReadRequiredBoolean(
                root,
                "BindingEvidenceObserved");
            var hashVerified = root.TryGetProperty("Mihomo", out var mihomo)
                && mihomo.ValueKind is JsonValueKind.Object
                && mihomo.TryGetProperty("HashMatchesExpected", out var hash)
                && hash.ValueKind is JsonValueKind.True;
            var directTcpCount = ReadOptionalInt32(
                root,
                "ConnectionSummary",
                "DirectAdapterTcp");
            var proxyTcpCount = ReadOptionalInt32(
                root,
                "ConnectionSummary",
                "ProxyAdapterTcp");

            return new P0ObservationSummary(
                captureReady,
                bindingObserved,
                hashVerified,
                directTcpCount,
                proxyTcpCount);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("P0 报告不是有效的 JSON。", exception);
        }
    }

    private static bool ReadRequiredBoolean(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"P0 报告缺少布尔字段 {propertyName}。");
        }

        return value.GetBoolean();
    }

    private static int ReadOptionalInt32(
        JsonElement root,
        string objectName,
        string propertyName)
    {
        return root.TryGetProperty(objectName, out var container)
            && container.ValueKind is JsonValueKind.Object
            && container.TryGetProperty(propertyName, out var value)
            && value.TryGetInt32(out var result)
                ? Math.Max(0, result)
                : 0;
    }

    private static async Task WaitForExitAsync(
        Process process,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            throw new TimeoutException(
                $"P0 观察工具在 {timeout.TotalSeconds:0} 秒内未完成，已停止等待。");
        }
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            // Best effort: an elevated process may deny termination to the tray.
        }
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            _snapshot = await Client.SendAsync<DiagnosticsSnapshot>(
                RpcCommands.GetDiagnostics,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken).ConfigureAwait(true);
            if (_snapshot is null)
            {
                throw new InvalidOperationException("服务未返回诊断数据。");
            }

            var startup = await _startupProbe.ReadAsync(cancellationToken)
                .ConfigureAwait(true);
            ApplySnapshot(_snapshot, startup);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法读取诊断信息：{exception.Message}");
        }
    }

    private void ApplySnapshot(
        DiagnosticsSnapshot snapshot,
        StartupProbeResult startup)
    {
        var theme = ThemeManager.Current;
        var runtime = snapshot.Runtime ?? new RuntimeStatus();
        _modeBadge.SetMode(runtime.Mode);
        _readinessValue.Text = ReadinessText(snapshot);
        _readinessValue.ForeColor = ReadinessColor(snapshot, theme);
        _generatedValue.Text = $"生成于 {snapshot.GeneratedAt.ToLocalTime():HH:mm:ss}";

        _serviceValue.Text = snapshot.StartupDisableActive
            ? "服务就绪 · 安装保护中 · 核心锁定关闭"
            : $"{(snapshot.ServiceReady ? "就绪" : "未就绪")} · "
              + $"{(runtime.MihomoRunning ? "Mihomo 运行中" : "Mihomo 未运行")}";
        _serviceValue.ForeColor = snapshot.StartupDisableActive
            ? theme.Warning
            : !snapshot.ServiceReady
                ? theme.Danger
                : runtime.Enabled
                    ? runtime.MihomoRunning ? theme.Success : theme.Danger
                    : theme.TextMuted;

        var dnsText = !runtime.DnsStatusKnown
            ? "DNS 未确认"
            : runtime.DnsEnabled
                ? "DNS 已接管"
                : "DNS 未启用";
        _routingValue.Text =
            $"{(runtime.TunEnabled ? "TUN 已接管" : "TUN 未接管")} · {dnsText}";
        _routingValue.ForeColor = runtime.TunEnabled && runtime.DnsStatusKnown
            && runtime.DnsEnabled
            ? theme.Success
            : runtime.TunEnabled
                ? theme.Warning
                : theme.TextMuted;

        var proxyState = runtime.ProxyRouteHealthKnown
            ? runtime.ProxyRouteAvailable ? "代理健康" : "国外流量阻断"
            : "代理健康待确认";
        _adaptersValue.Text =
            $"网卡1 {(runtime.DirectAdapterAvailable ? "在线" : "不可用")} · "
            + $"网卡2 {(runtime.ProxyAdapterAvailable ? "在线" : "不可用")} · "
            + proxyState;
        _adaptersValue.ForeColor = runtime.DirectAdapterAvailable
            && runtime.ProxyAdapterAvailable
            && (!runtime.ProxyRouteHealthKnown || runtime.ProxyRouteAvailable)
            ? theme.Success
            : runtime.DirectAdapterAvailable
                ? theme.Warning
                : theme.Danger;

        var settings = snapshot.Settings ?? new DiagnosticsSettingsSummary();
        _configurationValue.Text =
            $"Mihomo {(settings.MihomoAvailable ? "可用" : "缺失")} · "
            + $"GeoData {(settings.GeoDataAvailable ? "可用" : "缺失")} · "
            + $"{settings.EnabledSubscriptionCount}/{settings.SubscriptionCount} 个订阅 · "
            + $"{settings.RuleCount} 条规则";
        _configurationValue.ForeColor = settings.MihomoAvailable
            && settings.GeoDataAvailable
            ? theme.Success
            : theme.Warning;

        if (!startup.Available)
        {
            _startupValue.Text = "检查不可用";
            _startupValue.ForeColor = theme.Warning;
        }
        else if (!startup.RegistrationHealthy)
        {
            _startupValue.Text =
                $"需要修复 · {startup.Issues.Count} 项不一致";
            _startupValue.ForeColor = theme.Danger;
        }
        else if (!startup.Runtime.Reachable)
        {
            _startupValue.Text = "注册正常 · 服务状态不可读";
            _startupValue.ForeColor = theme.Warning;
        }
        else
        {
            var serviceText = startup.Service.State.Equals(
                "Running",
                StringComparison.OrdinalIgnoreCase)
                ? "服务运行中"
                : $"服务{startup.Service.State}";
            var taskText = startup.TrayTask.Enabled
                ? startup.TrayProcess.Running ? "托盘运行中" : "托盘未运行"
                : "登录任务已禁用";
            var monitorText = startup.TrayTask.LauncherExists
                ? startup.TrayTask.DiagnosticLogExists
                    ? "启动监护有记录"
                    : "启动监护待首次验证"
                : "启动监护缺失";
            _startupValue.Text = $"{serviceText} · {taskText} · {monitorText}";
            _startupValue.ForeColor = startup.Service.State.Equals(
                    "Running",
                    StringComparison.OrdinalIgnoreCase)
                && startup.TrayTask.Enabled
                && startup.TrayProcess.Running
                && startup.TrayTask.LauncherExists
                    ? theme.Success
                    : theme.Warning;
        }

        _lastErrorValue.Text = string.IsNullOrWhiteSpace(runtime.LastError)
            ? "最近错误：无"
            : $"最近错误：{runtime.LastError}";
        _lastErrorValue.ForeColor = string.IsNullOrWhiteSpace(runtime.LastError)
            ? theme.TextMuted
            : theme.Danger;

        var rows = (snapshot.Files ?? [])
            .Select(CreateFileRow)
            .ToArray();
        UiGrid.BindRowsPreservingSelection(
            _files,
            rows,
            row => row.Name);
        _files.Visible = rows.Length > 0;
        _emptyFiles.Visible = rows.Length == 0;
        if (_emptyFiles.Visible)
        {
            _emptyFiles.BringToFront();
        }
    }

    private static string ReadinessText(DiagnosticsSnapshot snapshot)
    {
        if (snapshot.ServiceReady && snapshot.StartupDisableActive)
        {
            return "安装保护中 · 分流锁定关闭";
        }

        if (snapshot.ServiceReady)
        {
            return "服务已就绪";
        }

        return snapshot.Readiness switch
        {
            CoordinatorReadiness.RecoveryRequired => "需要恢复",
            CoordinatorReadiness.Starting => "服务初始化中",
            _ => "服务未就绪"
        };
    }

    private static Color ReadinessColor(DiagnosticsSnapshot snapshot, UiTheme theme)
    {
        if (snapshot.StartupDisableActive)
        {
            return theme.Warning;
        }

        if (snapshot.ServiceReady)
        {
            return theme.Success;
        }

        return snapshot.Readiness == CoordinatorReadiness.Starting
            ? theme.Warning
            : theme.Danger;
    }

    private static string Serialize(DiagnosticsSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, JsonDefaults.Create());
    }

    private static string FormatSize(long length)
    {
        return length switch
        {
            < 1024 => $"{length} B",
            < 1024 * 1024 => $"{length / 1024d:0.0} KiB",
            < 1024L * 1024 * 1024 => $"{length / 1024d / 1024d:0.0} MiB",
            _ => $"{length / 1024d / 1024d / 1024d:0.0} GiB"
        };
    }

    private static string ShortHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash)
            ? "—"
            : hash.Length <= 12
                ? hash
                : $"{hash[..12]}…";
    }

    private static DiagnosticFileRow CreateFileRow(DiagnosticsFileSnapshot file)
    {
        var startupDisableMarker = file.Name.Equals(
            "startup.force-disabled",
            StringComparison.Ordinal);
        var required = file.Name is "mihomo.exe"
            or "geoip.dat"
            or "geosite.dat"
            or "runtime-config.yaml";
        var state = startupDisableMarker
            ? file.Exists ? "保护中" : "未启用"
            : file.Exists
                ? "存在"
                : required
                    ? "缺失"
                    : "未生成";
        return new DiagnosticFileRow(
            file.Name,
            state,
            FormatSize(file.Length),
            file.LastWriteTimeUtc?.ToLocalTime().ToString(
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture)
                ?? "—",
            ShortHash(file.Sha256),
            file.Exists,
            file.Exists || !required,
            startupDisableMarker && file.Exists);
    }

    private sealed record DiagnosticFileRow(
        string Name,
        string State,
        string Size,
        string Updated,
        string Hash,
        bool Exists,
        bool Healthy,
        bool Warning);
}

internal sealed record P0ObservationSummary(
    bool CaptureReady,
    bool BindingEvidenceObserved,
    bool MihomoHashVerified,
    int DirectAdapterTcpCount,
    int ProxyAdapterTcpCount);
