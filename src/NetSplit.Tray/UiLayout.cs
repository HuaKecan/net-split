using System.Runtime.CompilerServices;

namespace NetSplit.Tray;

internal static class UiLayout
{
    private sealed class LayoutBaseline
    {
        public required float[] RowHeights { get; init; }
        public required float[] ColumnWidths { get; init; }
    }

    private static readonly ConditionalWeakTable<TableLayoutPanel, LayoutBaseline> Baselines = new();

    public static void Normalize(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var table in Enumerate(root).OfType<TableLayoutPanel>())
        {
            NormalizeTable(table);
        }
    }

    private static void NormalizeTable(TableLayoutPanel table)
    {
        var baseline = Baselines.GetValue(
            table,
            static item => new LayoutBaseline
            {
                RowHeights = CaptureRowHeights(item),
                ColumnWidths = CaptureColumnWidths(item)
            });
        var scale = Math.Max(1d, table.DeviceDpi / 96d);

        for (var index = 0; index < table.RowStyles.Count && index < baseline.RowHeights.Length; index++)
        {
            if (table.RowStyles[index].SizeType != SizeType.Absolute
                || baseline.RowHeights[index] <= 0)
            {
                continue;
            }

            var height = Math.Max(1, (int)Math.Round(baseline.RowHeights[index] * scale));
            if (Math.Abs(table.RowStyles[index].Height - height) > 0.1f)
            {
                table.RowStyles[index].Height = height;
            }
        }

        for (var index = 0;
             index < table.ColumnStyles.Count && index < baseline.ColumnWidths.Length;
             index++)
        {
            if (table.ColumnStyles[index].SizeType != SizeType.Absolute
                || baseline.ColumnWidths[index] <= 0)
            {
                continue;
            }

            var width = Math.Max(1, (int)Math.Round(baseline.ColumnWidths[index] * scale));
            if (Math.Abs(table.ColumnStyles[index].Width - width) > 0.1f)
            {
                table.ColumnStyles[index].Width = width;
            }
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

    private static float[] CaptureRowHeights(TableLayoutPanel table)
    {
        var heights = new float[table.RowStyles.Count];
        for (var index = 0; index < table.RowStyles.Count; index++)
        {
            var style = table.RowStyles[index];
            heights[index] = style.SizeType == SizeType.Absolute ? style.Height : 0;
        }

        return heights;
    }

    private static float[] CaptureColumnWidths(TableLayoutPanel table)
    {
        var widths = new float[table.ColumnStyles.Count];
        for (var index = 0; index < table.ColumnStyles.Count; index++)
        {
            var style = table.ColumnStyles[index];
            widths[index] = style.SizeType == SizeType.Absolute ? style.Width : 0;
        }

        return widths;
    }
}
