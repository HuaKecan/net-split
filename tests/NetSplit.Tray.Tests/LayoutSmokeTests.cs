using System.Runtime.ExceptionServices;
using NetSplit.Core;
using NetSplit.Tray;

namespace NetSplit.Tray.Tests;

public sealed class LayoutSmokeTests
{
    private static readonly string[] ChineseUiFamilies =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "DengXian"
    ];

    [Fact]
    public void UiFontPrefersAChineseCapableFamilyForMixedText()
    {
        Assert.Contains(
            UiFonts.UiFamily.Name,
            ChineseUiFamilies,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SilentToggleSynchronizationDoesNotRaiseUserChangeEvent()
    {
        using var toggle = new ToggleSwitch();
        var changes = 0;
        toggle.CheckedChanged += (_, _) => changes++;

        toggle.SetCheckedSilently(true);

        Assert.True(toggle.Checked);
        Assert.Equal(0, changes);

        toggle.Checked = false;

        Assert.False(toggle.Checked);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DisabledResidentialModeDoesNotClaimAnActiveResidentialExit()
    {
        var status = new RuntimeStatus
        {
            Enabled = false,
            MihomoRunning = false
        };

        Assert.Equal(
            "分流已关闭",
            ProxiesPage.ResolveCurrentRouteText(status, ProxyExitMode.Residential));
    }

    [Fact]
    public void UnreadyCoreDoesNotClaimAnActiveResidentialExit()
    {
        var status = new RuntimeStatus
        {
            Mode = RuntimeMode.CoreUnavailable,
            Enabled = true,
            MihomoRunning = true,
            TunEnabled = false,
            DnsEnabled = false,
            DnsStatusKnown = true
        };

        Assert.Equal(
            "代理核心未就绪",
            ProxiesPage.ResolveCurrentRouteText(status, ProxyExitMode.Residential));
    }

    [Fact]
    public void MissingProxyAdapterDoesNotClaimAnActiveResidentialExit()
    {
        var status = new RuntimeStatus
        {
            Mode = RuntimeMode.ProxyUnavailable,
            Enabled = true,
            MihomoRunning = true,
            TunEnabled = true,
            DnsEnabled = true,
            DnsStatusKnown = true,
            ProxyAdapterAvailable = false,
            ProxyRouteAvailable = false,
            ProxyRouteHealthKnown = false,
            ProxyRouteFailure = ProxyRouteFailureReason.ProxyAdapterUnavailable
        };

        Assert.Equal(
            "网卡2不可用 · 国外流量已阻断",
            ProxiesPage.ResolveCurrentRouteText(status, ProxyExitMode.Residential));
    }

    [Fact]
    public void AllPagesCreateAndLayOutWithoutClippingFixedHeightText()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                RunLayoutChecks();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(15)),
            "WinForms layout smoke test timed out.");
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunLayoutChecks()
    {
        var client = new NamedPipeRpcClient($"net-split-layout-{Guid.NewGuid():N}");
        using (var mainForm = new MainForm(client)
        {
            Size = new Size(1080, 700),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        })
        {
            _ = mainForm.Handle;
            mainForm.PerformLayout();
            AssertTextHeights(mainForm);
            AssertSidebarFooterFits(mainForm);
        }

        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(845, 644),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        var pages = new PageBase[]
        {
            new OverviewPage(client, static () => { }),
            new ProxiesPage(client),
            new ResidentialProxyPage(client),
            new SubscriptionsPage(client),
            new RulesPage(client),
            new LogsPage(client),
            new DiagnosticsPage(client),
            new SettingsPage(client)
        };

        _ = host.Handle;
        foreach (var page in pages)
        {
            host.Controls.Clear();
            page.Dock = DockStyle.Fill;
            host.Controls.Add(page);
            page.CreateControl();
            host.PerformLayout();
            page.PerformLayout();
            AssertTextHeights(page);
            if (page is ProxiesPage proxiesPage)
            {
                AssertProxyToolbarFits(proxiesPage);
            }

            if (page is OverviewPage overviewPage)
            {
                AssertOverviewFlowHasRoom(overviewPage);
                AssertOverviewMetricsAndHealthFit(overviewPage);
            }

            if (page is DiagnosticsPage diagnosticsPage)
            {
                AssertDiagnosticsActionsFit(diagnosticsPage);
            }

            if (page is SettingsPage settingsPage)
            {
                AssertSettingsNotificationPreferenceFits(settingsPage);
            }

            AssertRoundedManagementSurface(page);
        }

        host.Controls.Clear();
        foreach (var page in pages)
        {
            page.Dispose();
        }

        RunScaledManagementChecks(client);
    }

    private static void AssertTextHeights(Control root)
    {
        foreach (var control in Enumerate(root))
        {
            Assert.True(control.Width >= 0, $"{control.GetType().Name} has a negative width.");
            Assert.True(control.Height >= 0, $"{control.GetType().Name} has a negative height.");

            if (control is Label { AutoSize: false } label
                && !string.IsNullOrWhiteSpace(label.Text)
                && label.Height > 0)
            {
                Assert.True(
                    label.Height + 1 >= label.Font.Height,
                    $"Label '{label.Text}' in {ControlPath(label)} has height "
                    + $"{label.Height}, but its font needs {label.Font.Height}.");
            }

            if (control is ThemedButton button
                && !string.IsNullOrWhiteSpace(button.Text))
            {
                Assert.True(
                    button.Height >= button.Font.Height + 8,
                    $"Button '{button.Text}' is too short for its font.");
            }
        }
    }

    private static void AssertProxyToolbarFits(ProxiesPage page)
    {
        var toolbar = Enumerate(page)
            .OfType<TableLayoutPanel>()
            .Single(panel => panel.AccessibleName == "代理节点操作栏");
        toolbar.PerformLayout();

        var sections = toolbar.Controls.Cast<Control>().ToArray();
        Assert.Equal(4, sections.Length);
        foreach (var section in sections)
        {
            Assert.True(
                section.Left >= 0
                && section.Top >= 0
                && section.Right <= toolbar.ClientSize.Width + 1
                && section.Bottom <= toolbar.ClientSize.Height + 1,
                $"{section.GetType().Name} overflows the proxy toolbar: "
                + $"{section.Bounds} within {toolbar.ClientRectangle}.");
        }

        for (var left = 0; left < sections.Length; left++)
        {
            for (var right = left + 1; right < sections.Length; right++)
            {
                Assert.False(
                    sections[left].Bounds.IntersectsWith(sections[right].Bounds),
                    $"Proxy toolbar sections overlap: "
                    + $"{sections[left].Bounds} and {sections[right].Bounds}.");
            }
        }

        var buttons = Enumerate(toolbar).OfType<ThemedButton>().ToArray();
        Assert.Equal(5, buttons.Length);
        foreach (var button in buttons)
        {
            var textWidth = TextRenderer.MeasureText(
                button.Text,
                button.Font,
                new Size(int.MaxValue, button.Height),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            var scale = button.DeviceDpi / 96f;
            var iconWidth = button.Icon is not null || !string.IsNullOrWhiteSpace(button.Glyph)
                ? (int)Math.Ceiling(22 * scale)
                : 0;
            var paddingWidth = (int)Math.Ceiling(24 * scale);
            Assert.True(
                button.ClientSize.Width >= textWidth + iconWidth + paddingWidth,
                $"Button '{button.Text}' clips its content at "
                + $"{button.ClientSize.Width}px; needs at least "
                + $"{textWidth + iconWidth + paddingWidth}px "
                + $"(DPI {button.DeviceDpi}, minimum {button.MinimumSize.Width}px, "
                + $"font {button.Font.Name} {button.Font.SizeInPoints:0.##}pt).");
        }
    }

    private static void AssertOverviewFlowHasRoom(OverviewPage page)
    {
        var flow = Enumerate(page).OfType<FlowDiagram>().Single();
        var card = flow.Parent;
        while (card is not null && card is not Card)
        {
            card = card.Parent;
        }

        Assert.NotNull(card);
        Assert.True(
            card!.Height >= flow.MinimumSize.Height + card.Padding.Vertical + 28,
            $"Flow card is too short for the diagram: card {card.Height}px, "
            + $"diagram minimum {flow.MinimumSize.Height}px, padding {card.Padding.Vertical}px.");
    }

    private static void AssertOverviewMetricsAndHealthFit(OverviewPage page)
    {
        var statusDetail = Enumerate(page)
            .OfType<Label>()
            .Single(label => label.Text == "正在读取运行状态…");
        var nextAction = Enumerate(page)
            .OfType<Label>()
            .Single(label => label.Text == "下一步：正在读取服务建议…");
        statusDetail.Text = "透明分流已接管 IPv4 流量  ·  住宅 SOCKS5";
        nextAction.Text = "下一步：无需操作；国内直连与国外代理正在按规则分流。";

        var metrics = Enumerate(page).OfType<MetricCard>().ToArray();
        Assert.Equal(4, metrics.Length);
        var values = new[]
        {
            "1023.9 MiB/s",
            "999.9 MiB/s",
            "9999 ms",
            "999/999"
        };
        for (var index = 0; index < metrics.Length; index++)
        {
            metrics[index].SetValue(values[index], "完整说明文字");
        }

        page.PerformLayout();
        AssertInsideAncestorCard(statusDetail);
        AssertInsideAncestorCard(nextAction);
        foreach (var metric in metrics)
        {
            metric.PerformLayout();
            var value = Enumerate(metric)
                .OfType<MetricValueDisplay>()
                .Single(display => display.AccessibleName?.EndsWith(
                    "数值",
                    StringComparison.Ordinal) == true);
            Assert.DoesNotContain(' ', value.Text);
            Assert.True(
                value.ContentFits,
                $"Metric '{value.AccessibleName}' clips '{value.Text}': "
                + $"{value.ClientSize.Width}px available, "
                + $"font {value.DisplayFont.Name} "
                + $"{value.DisplayFont.SizeInPoints:0.##}pt.");
        }

        foreach (var text in new[] { "TUN 与 DNS", "网卡1 · 主宽带", "网卡2 · F50" })
        {
            var label = Enumerate(page)
                .OfType<Label>()
                .Single(item => item.Text == text);
            var measured = TextRenderer.MeasureText(
                label.Text,
                label.Font,
                new Size(int.MaxValue, Math.Max(1, label.Height)),
                TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.NoPadding).Width;
            Assert.True(
                label.ClientSize.Width >= measured,
                $"Health label '{text}' is compressed to {label.ClientSize.Width}px; "
                + $"needs {measured}px.");
        }
    }

    private static void AssertSidebarFooterFits(MainForm mainForm)
    {
        var serviceState = Enumerate(mainForm)
            .OfType<Label>()
            .Single(label => label.Text == "●  本地服务连接中");
        AssertInsideParent(serviceState);
        AssertInsideParent(serviceState.Parent!);
        var version = Enumerate(mainForm)
            .OfType<Label>()
            .Single(label => label.Text == "Windows 11 x64  ·  v1.0.0");
        AssertInsideParent(version);

        var navigation = Enumerate(mainForm)
            .OfType<FlowLayoutPanel>()
            .Single(panel => panel.AutoScroll && panel.Controls.Count == 10);
        Assert.False(navigation.HorizontalScroll.Visible);
        foreach (Control item in navigation.Controls)
        {
            Assert.True(
                item.Right <= navigation.ClientSize.Width,
                $"Sidebar item '{item.Text}' overflows horizontally: "
                + $"{item.Bounds} within {navigation.ClientRectangle}.");
        }
    }

    private static void AssertInsideAncestorCard(Control control)
    {
        var card = control.Parent;
        while (card is not null && card is not Card)
        {
            card = card.Parent;
        }

        Assert.NotNull(card);
        var bounds = card!.RectangleToClient(
            control.Parent!.RectangleToScreen(control.Bounds));
        Assert.True(
            bounds.Top >= card.Padding.Top
            && bounds.Bottom <= card.ClientSize.Height - card.Padding.Bottom,
            $"Control '{control.Text}' is clipped in its card: "
            + $"{bounds} within {card.ClientRectangle}, padding {card.Padding}.");
    }

    private static void AssertInsideParent(Control control)
    {
        Assert.NotNull(control.Parent);
        Assert.True(
            control.Left >= 0
            && control.Top >= 0
            && control.Right <= control.Parent!.ClientSize.Width
            && control.Bottom <= control.Parent.ClientSize.Height,
            $"Control '{control.Text}' is clipped by its parent: "
            + $"{control.Bounds} within {control.Parent.ClientRectangle}.");
    }

    private static void AssertDiagnosticsActionsFit(DiagnosticsPage page)
    {
        var observe = Enumerate(page)
            .OfType<ThemedButton>()
            .Single(button => button.Text == "采集 P0 证据");
        var textWidth = TextRenderer.MeasureText(
            observe.Text,
            observe.Font,
            new Size(int.MaxValue, observe.Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        var scale = observe.DeviceDpi / 96f;
        var iconAndGapWidth = (int)Math.Ceiling(22 * scale);
        var paddingAndSlackWidth = (int)Math.Ceiling(28 * scale);
        Assert.True(
            observe.ClientSize.Width
                >= textWidth + iconAndGapWidth + paddingAndSlackWidth,
            $"The P0 observation button clips its content at "
            + $"{observe.ClientSize.Width}px; needs at least "
            + $"{textWidth + iconAndGapWidth + paddingAndSlackWidth}px.");
        Assert.NotNull(observe.Parent);
        Assert.True(
            observe.Left >= 0 && observe.Right <= observe.Parent!.ClientSize.Width + 1,
            $"The P0 observation button overflows its parent: {observe.Bounds}.");

        var actionBar = observe.Parent;
        Assert.NotNull(actionBar);
        Assert.True(
            actionBar!.Left >= 0
                && actionBar.Right <= actionBar.Parent!.ClientSize.Width + 1,
            $"The diagnostic action bar overflows its parent: {actionBar.Bounds}.");
    }

    private static void AssertSettingsNotificationPreferenceFits(SettingsPage page)
    {
        var toggle = Enumerate(page)
            .OfType<ToggleSwitch>()
            .Single(control => control.AccessibleName == "静默通知");
        AssertInsideParent(toggle);
        Assert.NotNull(toggle.Parent);
        AssertInsideParent(toggle.Parent!);
    }

    private static void AssertRoundedManagementSurface(PageBase page)
    {
        foreach (var card in Enumerate(page).OfType<Card>())
        {
            Assert.Equal(UiMetrics.RadiusXl, card.CornerRadius);
        }

        var expectedInputCount = page switch
        {
            ResidentialProxyPage => 4,
            SubscriptionsPage => 2,
            RulesPage => 1,
            LogsPage => 1,
            DiagnosticsPage or SettingsPage => 0,
            _ => -1
        };
        if (expectedInputCount < 0)
        {
            return;
        }

        var inputs = Enumerate(page).OfType<RoundedTextBox>().ToArray();
        Assert.Equal(expectedInputCount, inputs.Length);
        foreach (var input in inputs)
        {
            input.PerformLayout();
            Assert.True(
                input.CornerRadius >= UiMetrics.RadiusMd,
                $"Rounded input uses a sharp {input.CornerRadius}px radius.");
            Assert.True(
                input.Height >= UiMetrics.Scale(input, UiMetrics.ControlHeight) - 1,
                $"Rounded input is too short at {input.Height}px.");

            var editor = Enumerate(input).OfType<TextBox>().Single();
            AssertInsideParent(editor);
            Assert.False(
                string.IsNullOrWhiteSpace(editor.AccessibleName),
                "Rounded input editor is missing an accessible name.");
        }

        if (page is ResidentialProxyPage residentialProxyPage)
        {
            AssertResidentialConnectionLayout(residentialProxyPage);
        }
    }

    private static void AssertResidentialConnectionLayout(
        ResidentialProxyPage page)
    {
        var title = Enumerate(page)
            .OfType<Label>()
            .Single(label => label.Text == "SOCKS5 连接");
        var card = title.Parent;
        while (card is not null && card is not Card)
        {
            card = card.Parent;
        }

        Assert.NotNull(card);
        Assert.Equal(UiMetrics.RadiusXl, ((Card)card!).CornerRadius);
        foreach (var input in Enumerate(card).OfType<RoundedTextBox>())
        {
            AssertInsideEachParentUntil(input, card);
        }

        foreach (var button in Enumerate(card).OfType<ThemedButton>())
        {
            AssertInsideEachParentUntil(button, card);
        }
    }

    private static void AssertInsideEachParentUntil(
        Control control,
        Control ancestor)
    {
        for (var current = control; current != ancestor; current = current.Parent!)
        {
            Assert.NotNull(current.Parent);
            AssertInsideParent(current);
        }
    }

    private static void RunScaledManagementChecks(NamedPipeRpcClient client)
    {
        using var host = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size(1056, 805),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        var pages = new PageBase[]
        {
            new ProxiesPage(client),
            new ResidentialProxyPage(client),
            new SubscriptionsPage(client),
            new RulesPage(client),
            new LogsPage(client),
            new DiagnosticsPage(client),
            new SettingsPage(client)
        };

        _ = host.Handle;
        foreach (var page in pages)
        {
            host.Controls.Clear();
            page.Dock = DockStyle.None;
            page.Size = new Size(845, 644);
            host.Controls.Add(page);
            page.CreateControl();
            page.PerformLayout();
            page.Scale(new SizeF(1.25f, 1.25f));
            page.Size = host.ClientSize;
            host.PerformLayout();
            page.PerformLayout();
            AssertTextHeights(page);
            AssertRoundedManagementSurface(page);
            if (page is ProxiesPage proxiesPage)
            {
                AssertProxyToolbarFits(proxiesPage);
            }

            if (page is SettingsPage settingsPage)
            {
                AssertSettingsNotificationPreferenceFits(settingsPage);
            }
        }

        host.Controls.Clear();
        foreach (var page in pages)
        {
            page.Dispose();
        }
    }

    private static IEnumerable<Control> Enumerate(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }

    private static string ControlPath(Control control)
    {
        var parts = new List<string>();
        for (var current = control; current is not null; current = current.Parent)
        {
            parts.Add(current.GetType().Name);
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }
}
