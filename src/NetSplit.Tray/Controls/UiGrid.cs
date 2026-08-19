namespace NetSplit.Tray;

public static class UiGrid
{
    public static DataGridView Create()
    {
        var theme = ThemeManager.Current;
        var grid = new UiDataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = theme.BackgroundSurface,
            BorderStyle = BorderStyle.None,
            GridColor = theme.Border,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = 38,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            ShowCellToolTips = true,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = theme.BackgroundSurface2,
                ForeColor = theme.TextSecondary,
                SelectionBackColor = theme.BackgroundSurface2,
                SelectionForeColor = theme.TextSecondary,
                Font = UiFonts.CaptionStrong,
                Padding = new Padding(12, 0, 12, 0),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = theme.BackgroundSurface,
                ForeColor = theme.TextPrimary,
                SelectionBackColor = theme.AccentSoft,
                SelectionForeColor = theme.TextPrimary,
                Font = UiFonts.Body,
                Padding = new Padding(12, 0, 12, 0),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        };
        ApplyDpiMetrics(grid);
        grid.CellToolTipTextNeeded += OnCellToolTipTextNeeded;
        grid.AccessibleRole = AccessibleRole.Table;
        return grid;
    }

    private static void ApplyDpiMetrics(DataGridView grid)
    {
        var horizontalPadding = UiMetrics.Scale(grid, 12);
        grid.ColumnHeadersHeight = UiMetrics.Scale(grid, 38);
        grid.RowTemplate.Height = UiMetrics.Scale(grid, 42);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(
            horizontalPadding,
            0,
            horizontalPadding,
            0);
        grid.DefaultCellStyle.Padding = new Padding(
            horizontalPadding,
            0,
            horizontalPadding,
            0);
    }

    private static void OnCellToolTipTextNeeded(
        object? sender,
        DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (sender is not DataGridView grid
            || e.RowIndex < 0
            || e.ColumnIndex < 0)
        {
            return;
        }

        var value = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        if (!string.IsNullOrWhiteSpace(value) && value.Length > 18)
        {
            e.ToolTipText = value;
        }
    }

    private sealed class UiDataGridView : DataGridView
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiMetrics(this);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiMetrics(this);
        }
    }

    public static DataGridViewTextBoxColumn TextColumn(
        string name,
        string header,
        int? width = null,
        bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            DataPropertyName = name,
            HeaderText = header,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            MinimumWidth = 50
        };
        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        }
        else if (width is { } value)
        {
            column.Width = value;
        }

        return column;
    }

    public static void BindRowsPreservingSelection<T>(
        DataGridView grid,
        IReadOnlyList<T> rows,
        Func<T, string> keySelector,
        string? preferredKey = null)
    {
        var selectedKey = preferredKey;
        if (string.IsNullOrWhiteSpace(selectedKey)
            && grid.CurrentRow?.DataBoundItem is T selected)
        {
            selectedKey = keySelector(selected);
        }

        var firstDisplayedRow = grid.FirstDisplayedScrollingRowIndex;
        grid.DataSource = rows;
        grid.ClearSelection();

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.DataBoundItem is T item
                    && keySelector(item).Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected = true;
                    grid.CurrentCell = row.Cells.Cast<DataGridViewCell>()
                        .FirstOrDefault(cell => cell.Visible);
                    break;
                }
            }
        }

        if (firstDisplayedRow >= 0 && firstDisplayedRow < grid.RowCount)
        {
            grid.FirstDisplayedScrollingRowIndex = firstDisplayedRow;
        }
    }
}
