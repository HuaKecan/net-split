using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetSplit.Tray;

public enum UiThemeMode
{
    FollowSystem,
    Dark,
    Light
}

public enum UiThemeKind
{
    Dark,
    Light
}

public sealed class UiTheme
{
    public Color BackgroundPage { get; private init; }
    public Color BackgroundChrome { get; private init; }
    public Color BackgroundSurface { get; private init; }
    public Color BackgroundSurface2 { get; private init; }
    public Color BackgroundElevated { get; private init; }
    public Color Border { get; private init; }
    public Color BorderStrong { get; private init; }
    public Color TextPrimary { get; private init; }
    public Color TextSecondary { get; private init; }
    public Color TextMuted { get; private init; }
    public Color Accent { get; private init; }
    public Color AccentText { get; private init; }
    public Color AccentSoft { get; private init; }
    public Color OnAccent { get; private init; }
    public Color Success { get; private init; }
    public Color Warning { get; private init; }
    public Color Danger { get; private init; }
    public Color SidebarBackground { get; private init; }
    public Color SidebarHover { get; private init; }
    public Color SidebarSelected { get; private init; }
    public Color SidebarText { get; private init; }
    public Color SidebarMuted { get; private init; }
    public Color SuccessSoft { get; private init; }
    public Color WarningSoft { get; private init; }
    public Color DangerSoft { get; private init; }
    public Color ChartDirect { get; private init; }
    public Color ChartProxy { get; private init; }

    public Color Info => Accent;

    public static UiTheme Dark { get; } = new()
    {
        BackgroundPage = Rgb(0x10, 0x12, 0x16),
        BackgroundChrome = Rgb(0x15, 0x18, 0x1D),
        BackgroundSurface = Rgb(0x19, 0x1D, 0x23),
        BackgroundSurface2 = Rgb(0x21, 0x26, 0x2E),
        BackgroundElevated = Rgb(0x25, 0x2A, 0x33),
        Border = Rgb(0x2D, 0x34, 0x3E),
        BorderStrong = Rgb(0x46, 0x50, 0x5D),
        TextPrimary = Rgb(0xF2, 0xF5, 0xF8),
        TextSecondary = Rgb(0xB2, 0xBC, 0xC8),
        TextMuted = Rgb(0x7F, 0x8B, 0x99),
        Accent = Rgb(0x5B, 0x8D, 0xEF),
        AccentText = Rgb(0x8E, 0xB4, 0xFF),
        AccentSoft = Rgb(0x22, 0x31, 0x4C),
        OnAccent = Color.White,
        Success = Rgb(0x3A, 0xD3, 0x9D),
        Warning = Rgb(0xF4, 0xAC, 0x45),
        Danger = Rgb(0xF3, 0x72, 0x72),
        SidebarBackground = Rgb(0x0B, 0x0D, 0x11),
        SidebarHover = Rgb(0x16, 0x1A, 0x20),
        SidebarSelected = Rgb(0x20, 0x27, 0x32),
        SidebarText = Rgb(0xF4, 0xF7, 0xFA),
        SidebarMuted = Rgb(0x7F, 0x8A, 0x98),
        SuccessSoft = Color.FromArgb(30, 0x3A, 0xD3, 0x9D),
        WarningSoft = Color.FromArgb(30, 0xF4, 0xAC, 0x45),
        DangerSoft = Color.FromArgb(30, 0xF3, 0x72, 0x72),
        ChartDirect = Rgb(0x3A, 0xD3, 0x9D),
        ChartProxy = Rgb(0x5B, 0x8D, 0xEF)
    };

    public static UiTheme Light { get; } = new()
    {
        BackgroundPage = Rgb(0xF2, 0xF5, 0xF8),
        BackgroundChrome = Color.White,
        BackgroundSurface = Color.White,
        BackgroundSurface2 = Rgb(0xF8, 0xFA, 0xFC),
        BackgroundElevated = Rgb(0xFB, 0xFC, 0xFD),
        Border = Rgb(0xD4, 0xDE, 0xE8),
        BorderStrong = Rgb(0xA9, 0xB9, 0xC9),
        TextPrimary = Rgb(0x17, 0x24, 0x34),
        TextSecondary = Rgb(0x52, 0x64, 0x78),
        TextMuted = Rgb(0x76, 0x89, 0x9E),
        Accent = Rgb(0x0B, 0x6B, 0xD3),
        AccentText = Rgb(0x07, 0x53, 0xA7),
        AccentSoft = Rgb(0xDF, 0xEE, 0xFA),
        OnAccent = Color.White,
        Success = Rgb(0x12, 0x7C, 0x62),
        Warning = Rgb(0xA8, 0x66, 0x18),
        Danger = Rgb(0xC4, 0x4A, 0x4A),
        SidebarBackground = Rgb(0xF7, 0xF9, 0xFB),
        SidebarHover = Rgb(0xEA, 0xF0, 0xF6),
        SidebarSelected = Rgb(0xDF, 0xEE, 0xFA),
        SidebarText = Rgb(0x15, 0x4B, 0x80),
        SidebarMuted = Rgb(0x78, 0x8A, 0x9F),
        SuccessSoft = Color.FromArgb(25, 0x12, 0x7C, 0x62),
        WarningSoft = Color.FromArgb(25, 0xA8, 0x66, 0x18),
        DangerSoft = Color.FromArgb(25, 0xC4, 0x4A, 0x4A),
        ChartDirect = Rgb(0x12, 0x7C, 0x62),
        ChartProxy = Rgb(0x0B, 0x6B, 0xD3)
    };

    private static Color Rgb(int red, int green, int blue)
    {
        return Color.FromArgb(red, green, blue);
    }
}

public static class UiMetrics
{
    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 12;
    public const int SpaceLg = 16;
    public const int SpaceXl = 20;
    public const int Space2xl = 24;
    public const int Space3xl = 32;

    public const int RadiusSm = 4;
    public const int RadiusMd = 6;
    public const int RadiusLg = 8;
    public const int RadiusXl = 10;

    public const int ControlHeight = 40;
    public const int NavItemHeight = 44;

    public const float FontBadge = 8.5f;
    public const float FontCaption = 9.25f;
    public const float FontBody = 10.5f;
    public const float FontSection = 11.5f;
    public const float FontTitle = 18f;
    public const float FontMetric = 17f;
    public const float FontMetricCompact = 15f;
    public const float FontMetricSmall = 13.5f;
    public const float FontMetricTiny = 12f;
    public const float FontMono = 9f;

    public static int Scale(Control control, int value)
    {
        ArgumentNullException.ThrowIfNull(control);
        return Math.Max(1, (int)Math.Round(value * control.DeviceDpi / 96d));
    }
}

public static class UiFonts
{
    public static FontFamily UiFamily { get; } = ResolveFamily(
        FontFamily.GenericSansSerif,
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "DengXian",
        "Segoe UI Variable",
        "Segoe UI");

    public static FontFamily MonoFamily { get; } = ResolveFamily(
        FontFamily.GenericMonospace,
        "Cascadia Mono",
        "Consolas");
    public static FontFamily IconFamily { get; } = ResolveFamily(
        FontFamily.GenericSansSerif,
        "Segoe Fluent Icons",
        "Segoe MDL2 Assets");

    public static Font Body { get; } = new(UiFamily, UiMetrics.FontBody);
    public static Font BodyStrong { get; } = new(
        UiFamily,
        UiMetrics.FontBody,
        FontStyle.Bold);
    public static Font Caption { get; } = new(UiFamily, UiMetrics.FontCaption);
    public static Font CaptionStrong { get; } = new(
        UiFamily,
        UiMetrics.FontCaption,
        FontStyle.Bold);
    public static Font Badge { get; } = new(UiFamily, UiMetrics.FontBadge);
    public static Font Section { get; } = new(
        UiFamily,
        UiMetrics.FontSection,
        FontStyle.Bold);
    public static Font Title { get; } = new(
        UiFamily,
        UiMetrics.FontTitle,
        FontStyle.Bold);
    public static Font Metric { get; } = new(
        MonoFamily,
        UiMetrics.FontMetric,
        FontStyle.Bold);
    public static Font MetricCompact { get; } = new(
        MonoFamily,
        UiMetrics.FontMetricCompact,
        FontStyle.Bold);
    public static Font MetricSmall { get; } = new(
        MonoFamily,
        UiMetrics.FontMetricSmall,
        FontStyle.Bold);
    public static Font MetricTiny { get; } = new(
        MonoFamily,
        UiMetrics.FontMetricTiny,
        FontStyle.Bold);
    public static Font Mono { get; } = new(MonoFamily, UiMetrics.FontMono);
    public static Font Icon { get; } = new(IconFamily, 10.5f);
    public static Font IconLarge { get; } = new(IconFamily, 13f);

    private static FontFamily ResolveFamily(
        FontFamily genericFallback,
        params string[] candidates)
    {
        var installed = FontFamily.Families;
        foreach (var candidate in candidates)
        {
            var match = installed.FirstOrDefault(
                family => family.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return genericFallback;
    }
}

public static class UiStyle
{
    public static void Apply(TextBox textBox)
    {
        var theme = ThemeManager.Current;
        textBox.AutoSize = false;
        textBox.Height = UiMetrics.Scale(textBox, UiMetrics.ControlHeight);
        textBox.MinimumSize = new Size(0, UiMetrics.Scale(textBox, UiMetrics.ControlHeight));
        textBox.Font = UiFonts.Body;
        textBox.BackColor = theme.BackgroundSurface;
        textBox.ForeColor = theme.TextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void Apply(ComboBox comboBox)
    {
        var theme = ThemeManager.Current;
        comboBox.Font = UiFonts.Body;
        comboBox.BackColor = theme.BackgroundSurface;
        comboBox.ForeColor = theme.TextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.IntegralHeight = false;
        comboBox.ItemHeight = UiMetrics.Scale(comboBox, 24);
        comboBox.DropDownHeight = UiMetrics.Scale(comboBox, 240);
        comboBox.MinimumSize = new Size(0, UiMetrics.Scale(comboBox, UiMetrics.ControlHeight));
    }

    public static void Apply(RichTextBox richTextBox)
    {
        var theme = ThemeManager.Current;
        richTextBox.BackColor = theme.BackgroundSurface;
        richTextBox.ForeColor = theme.TextSecondary;
        richTextBox.BorderStyle = BorderStyle.None;
    }

    public static Label FieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFonts.CaptionStrong,
            ForeColor = ThemeManager.Current.TextSecondary,
            Margin = new Padding(0, 0, 0, UiMetrics.SpaceXs)
        };
    }

    public static Label SectionTitle(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFonts.Section,
            ForeColor = ThemeManager.Current.TextPrimary,
            Margin = Padding.Empty
        };
    }

    public static Label MutedText(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = UiFonts.Caption,
            ForeColor = ThemeManager.Current.TextMuted,
            Margin = Padding.Empty
        };
    }
}

public static class ThemeManager
{
    private const string RegistryPath = @"Software\net-split";
    private const string ThemeValueName = "ThemeMode";
    private static UiThemeMode _mode = LoadMode();

    public static event EventHandler? Changed;

    public static UiThemeMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            SaveMode(value);
            Reapply();
        }
    }

    public static UiThemeKind ResolvedKind { get; private set; } = DetectSystemKind();

    public static UiTheme Current =>
        ResolvedKind == UiThemeKind.Dark ? UiTheme.Dark : UiTheme.Light;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Reapply();
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode == UiThemeMode.FollowSystem)
        {
            Reapply();
        }
    }

    private static void Reapply()
    {
        var kind = _mode switch
        {
            UiThemeMode.Dark => UiThemeKind.Dark,
            UiThemeMode.Light => UiThemeKind.Light,
            _ => DetectSystemKind()
        };

        if (kind == ResolvedKind)
        {
            return;
        }

        ResolvedKind = kind;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static UiThemeMode LoadMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return Enum.TryParse<UiThemeMode>(
                key?.GetValue(ThemeValueName) as string,
                ignoreCase: true,
                out var mode)
                ? mode
                : UiThemeMode.FollowSystem;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException)
        {
            return UiThemeMode.FollowSystem;
        }
    }

    private static void SaveMode(UiThemeMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(ThemeValueName, mode.ToString(), RegistryValueKind.String);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException)
        {
            // Theme persistence is best effort and must never block the UI.
        }
    }

    private static UiThemeKind DetectSystemKind()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? UiThemeKind.Dark
                : UiThemeKind.Light;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException)
        {
            return UiThemeKind.Light;
        }
    }
}

internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorner = 2;

    public static void Apply(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        var enabled = ThemeManager.ResolvedKind == UiThemeKind.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(
            form.Handle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
        var cornerPreference = DwmRoundCorner;
        _ = DwmSetWindowAttribute(
            form.Handle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
