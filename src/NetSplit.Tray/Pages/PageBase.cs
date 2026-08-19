using NetSplit.Core;

namespace NetSplit.Tray;

public abstract class PageBase : UserControl
{
    private bool _actionRunning;

    protected NamedPipeRpcClient Client { get; }
    protected InlineBanner Banner { get; }
    protected Panel Content { get; }

    protected PageBase(
        NamedPipeRpcClient client,
        string title,
        string description)
    {
        Client = client;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = ThemeManager.Current.BackgroundPage;
        Dock = DockStyle.Fill;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = ThemeManager.Current.BackgroundPage,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(title, description), 0, 0);

        Banner = new InlineBanner
        {
            Dock = DockStyle.Top,
            Margin = new Padding(
                UiMetrics.Space3xl,
                0,
                UiMetrics.Space3xl,
                UiMetrics.SpaceSm)
        };
        root.Controls.Add(Banner, 0, 1);

        Content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ThemeManager.Current.BackgroundPage,
            Padding = new Padding(
                UiMetrics.Space3xl,
                UiMetrics.SpaceSm,
                UiMetrics.Space3xl,
                UiMetrics.Space3xl),
            AutoScroll = true,
            Margin = Padding.Empty
        };
        root.Controls.Add(Content, 0, 2);
        Controls.Add(root);
    }

    public abstract Task RefreshAsync(CancellationToken cancellationToken = default);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiLayout.Normalize(this);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UiLayout.Normalize(this);
    }

    protected void ShowError(string message)
    {
        Banner.Show(message, ThemeManager.Current.Danger);
    }

    protected void ShowWarning(string message)
    {
        Banner.Show(message, ThemeManager.Current.Warning);
    }

    protected void ShowInfo(string message)
    {
        Banner.Show(message, ThemeManager.Current.Info);
    }

    protected void ClearBanner()
    {
        Banner.Clear();
    }

    protected static ThemedButton CreateButton(
        string text,
        ButtonKind kind,
        EventHandler onClick,
        int minimumWidth = 0,
        string glyph = "")
    {
        var button = new ThemedButton
        {
            Text = text,
            Kind = kind,
            Glyph = glyph,
            Margin = Padding.Empty
        };
        button.SizeToContent(minimumWidth);
        button.Click += onClick;
        return button;
    }

    protected async Task RunActionAsync(Func<Task> action)
    {
        if (_actionRunning)
        {
            return;
        }

        _actionRunning = true;
        try
        {
            Content.Enabled = false;
            UseWaitCursor = true;
            ClearBanner();
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            Content.Enabled = true;
            UseWaitCursor = false;
            _actionRunning = false;
        }
    }

    private static Panel BuildHeader(string title, string description)
    {
        var theme = ThemeManager.Current;
        var header = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = theme.BackgroundPage,
            Padding = new Padding(
                UiMetrics.Space3xl,
                UiMetrics.Space2xl,
                UiMetrics.Space3xl,
                UiMetrics.SpaceSm)
        };

        var copy = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = theme.BackgroundPage,
            Margin = Padding.Empty
        };
        copy.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = UiFonts.Title,
            ForeColor = theme.TextPrimary,
            Margin = Padding.Empty
        });
        copy.Controls.Add(new Label
        {
            Text = description,
            AutoSize = true,
            Font = UiFonts.Body,
            ForeColor = theme.TextSecondary,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(0, UiMetrics.SpaceSm, 0, 0)
        });
        header.Controls.Add(copy);
        return header;
    }
}
