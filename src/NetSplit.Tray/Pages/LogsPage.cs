using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Tray;

public sealed class LogsPage : PageBase
{
    private readonly RichTextBox _viewer = new();
    private readonly ComboBox _level = new();
    private readonly RoundedTextBox _search = new();
    private readonly ToggleSwitch _autoScroll = new();
    private readonly Label _countLabel = new();

    private IReadOnlyList<string> _lines = [];
    private string _lastRenderedText = string.Empty;

    public LogsPage(NamedPipeRpcClient client)
        : base(
            client,
            "运行日志",
            "查看服务与 Mihomo 的脱敏日志。订阅地址、节点凭据和完整运行配置不会出现在这里。")
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildViewer(), 0, 1);
        Content.Controls.Add(root);
    }

    private TableLayoutPanel BuildToolbar()
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

        var filters = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Left,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        UiStyle.Apply(_level);
        _level.DropDownStyle = ComboBoxStyle.DropDownList;
        _level.Items.AddRange(["全部级别", "INFO", "WARN", "ERROR", "MIHOMO"]);
        _level.SelectedIndex = 0;
        _level.Width = 112;
        _level.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _level.SelectedIndexChanged += (_, _) => Render(force: true);
        filters.Controls.Add(_level);

        _search.Width = 220;
        _search.PlaceholderText = "搜索日志";
        _search.InputAccessibleName = "搜索日志";
        _search.CornerRadius = 7;
        _search.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        _search.TextChanged += (_, _) => Render(force: true);
        filters.Controls.Add(_search);

        var copy = CreateButton(
            "复制",
            ButtonKind.Secondary,
            (_, _) => Copy(),
            0,
            UiGlyphs.Copy);
        copy.Margin = new Padding(0, 0, UiMetrics.SpaceSm, 0);
        filters.Controls.Add(copy);
        filters.Controls.Add(CreateButton(
            "导出",
            ButtonKind.Secondary,
            (_, _) => Export(),
            0,
            UiGlyphs.Export));
        filters.Controls.Add(CreateButton(
            "\u5bfc\u51fa\u8bca\u65ad",
            ButtonKind.Secondary,
            (_, _) => _ = ExportDiagnosticsAsync(),
            0,
            UiGlyphs.Shield));
        toolbar.Controls.Add(filters, 0, 0);

        var state = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        _countLabel.Text = "0 行";
        _countLabel.Font = UiFonts.Caption;
        _countLabel.ForeColor = theme.TextMuted;
        _countLabel.AutoSize = true;
        _countLabel.Margin = new Padding(0, 7, UiMetrics.SpaceLg, 0);
        state.Controls.Add(_countLabel);
        state.Controls.Add(new Label
        {
            Text = "自动滚动",
            Font = UiFonts.Caption,
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0, 7, UiMetrics.SpaceSm, 0)
        });
        _autoScroll.Checked = true;
        _autoScroll.Margin = new Padding(0, 4, 0, 0);
        _autoScroll.AccessibleName = "自动滚动日志";
        _autoScroll.CheckedChanged += (_, _) => Render(force: true);
        state.Controls.Add(_autoScroll);
        toolbar.Controls.Add(state, 1, 0);
        return toolbar;
    }

    private Card BuildViewer()
    {
        var theme = ThemeManager.Current;
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(UiMetrics.SpaceMd),
            Margin = Padding.Empty
        };
        _viewer.Dock = DockStyle.Fill;
        _viewer.ReadOnly = true;
        _viewer.WordWrap = false;
        _viewer.Font = UiFonts.Mono;
        _viewer.DetectUrls = false;
        _viewer.ScrollBars = RichTextBoxScrollBars.Both;
        _viewer.AccessibleName = "运行日志内容";
        UiStyle.Apply(_viewer);
        card.Controls.Add(_viewer);
        return card;
    }

    private void Copy()
    {
        if (_viewer.TextLength == 0)
        {
            ShowWarning("当前没有可复制的日志。");
            return;
        }

        try
        {
            var text = _viewer.SelectionLength > 0
                ? _viewer.SelectedText
                : _viewer.Text;
            Clipboard.SetText(text);
            ShowInfo("日志已复制到剪贴板。");
        }
        catch (ExternalException exception)
        {
            ShowError($"无法写入剪贴板：{exception.Message}");
        }
    }

    private void Export()
    {
        if (_viewer.TextLength == 0)
        {
            ShowWarning("当前没有可导出的日志。");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"net-split-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _viewer.Text);
            ShowInfo($"日志已导出到 {dialog.FileName}");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            ShowError($"无法导出日志：{exception.Message}");
        }
    }

    private async Task ExportDiagnosticsAsync()
    {
        await RunActionAsync(async () =>
        {
            var snapshot = await Client.SendAsync<DiagnosticsSnapshot>(
                RpcCommands.GetDiagnostics,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (snapshot is null)
            {
                throw new InvalidOperationException("The service returned no diagnostics.");
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "Diagnostics JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"net-split-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Create());
            await File.WriteAllTextAsync(
                dialog.FileName,
                json,
                new UTF8Encoding(false)).ConfigureAwait(true);
            ShowInfo($"Diagnostics exported to {dialog.FileName}");
        }).ConfigureAwait(true);
    }

    private void Render(bool force = false)
    {
        var theme = ThemeManager.Current;
        var level = _level.SelectedIndex switch
        {
            1 => "INFO",
            2 => "WARN",
            3 => "ERROR",
            4 => "MIHOMO",
            _ => null
        };
        var keyword = _search.Text.Trim();
        var filtered = _lines.Where(line =>
        {
            if (level is not null
                && !line.Contains($"[{level}]", StringComparison.OrdinalIgnoreCase)
                && !line.Contains($"[{level}-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return keyword.Length == 0
                || line.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var renderedText = string.Join(Environment.NewLine, filtered);
        if (filtered.Length > 0)
        {
            renderedText += Environment.NewLine;
        }

        _countLabel.Text = $"{filtered.Length} 行";
        if (!force && renderedText.Equals(_lastRenderedText, StringComparison.Ordinal))
        {
            return;
        }

        var firstVisibleCharacter = _viewer.GetCharIndexFromPosition(new Point(1, 1));
        var selectionStart = _viewer.SelectionStart;
        var selectionLength = _viewer.SelectionLength;

        _viewer.SuspendLayout();
        _viewer.Clear();
        if (filtered.Length == 0)
        {
            _viewer.SelectionColor = theme.TextMuted;
            _viewer.AppendText("暂无匹配日志。");
        }
        else
        {
            foreach (var line in filtered)
            {
                _viewer.SelectionColor = ColorForLevel(line, theme);
                _viewer.AppendText(line + Environment.NewLine);
            }
        }

        if (_autoScroll.Checked)
        {
            _viewer.SelectionStart = _viewer.TextLength;
            _viewer.SelectionLength = 0;
            _viewer.ScrollToCaret();
        }
        else
        {
            _viewer.SelectionStart = Math.Min(firstVisibleCharacter, _viewer.TextLength);
            _viewer.SelectionLength = 0;
            _viewer.ScrollToCaret();
            _viewer.SelectionStart = Math.Min(selectionStart, _viewer.TextLength);
            _viewer.SelectionLength = Math.Min(
                selectionLength,
                _viewer.TextLength - _viewer.SelectionStart);
        }

        _viewer.ResumeLayout();
        _lastRenderedText = renderedText;
    }

    private static Color ColorForLevel(string line, UiTheme theme)
    {
        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[MIHOMO-ERR]", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Danger;
        }

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
        {
            return theme.Warning;
        }

        if (line.Contains("[MIHOMO]", StringComparison.OrdinalIgnoreCase))
        {
            return theme.TextMuted;
        }

        return theme.TextSecondary;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ClearBanner();
            _lines = await Client.SendAsync<IReadOnlyList<string>>(
                RpcCommands.GetLogs,
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellationToken).ConfigureAwait(true) ?? [];
            Render();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"无法连接服务：{exception.Message}");
        }
    }
}
