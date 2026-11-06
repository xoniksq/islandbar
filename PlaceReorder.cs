using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IslandBar;

sealed class PlaceReorder(Panel rows, Action<string[]> saveOrder, Action<string> reportError)
{
    const string DragFormat = "IslandBar.PlaceOrder";
    Border? _dragging;
    public ScrollViewer? Scroll { get; set; }

    public bool Move(Border row, int index)
    {
        int from = rows.Children.IndexOf(row);
        if (from < 0 || from == index || index < 0 || index >= rows.Children.Count) return false;
        var paths = rows.Children.Cast<Border>().Select(item => (string)item.Tag).ToList();
        string path = paths[from];
        paths.RemoveAt(from);
        paths.Insert(index, path);
        try { saveOrder([..paths]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            reportError("Не удалось сохранить порядок.");
            Program.Log("places order: " + ex.Message);
            return false;
        }
        rows.Children.RemoveAt(from);
        rows.Children.Insert(index, row);
        row.BringIntoView();
        return true;
    }

    public void Bind(Border row, FrameworkElement grip)
    {
        row.AllowDrop = true;
        Point? down = null;
        grip.MouseLeftButtonDown += (_, e) => {
            if (grip.CaptureMouse()) down = e.GetPosition(rows);
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) => { grip.ReleaseMouseCapture(); e.Handled = true; };
        grip.LostMouseCapture += (_, _) => down = null;
        grip.MouseMove += (_, e) => {
            if (down is not { } start || _dragging is not null || !grip.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var distance = e.GetPosition(rows) - start;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            e.Handled = true;
            down = null;
            _dragging = row;
            grip.ReleaseMouseCapture();
            try { DragDrop.DoDragDrop(row, new DataObject(DragFormat, (string)row.Tag), DragDropEffects.Move); }
            finally {
                _dragging = null;
                foreach (Border item in rows.Children) item.BorderBrush = null;
            }
        };
        bool CanMove(DragEventArgs e) => _dragging is { } source && source != row &&
            e.Data.GetData(DragFormat) is string path && Equals(source.Tag, path);
        row.DragOver += (_, e) => {
            if (_dragging is null) return;
            bool accepted = CanMove(e);
            row.BorderBrush = accepted ? PanelControls.Silver : null;
            e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (Scroll is { } scroll) {
                double y = e.GetPosition(scroll).Y;
                if (y < 26) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 20);
                else if (y > scroll.ActualHeight - 26) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 20);
            }
        };
        row.DragLeave += (_, _) => row.BorderBrush = null;
        row.Drop += (_, e) => {
            if (_dragging is null) return;
            row.BorderBrush = null;
            bool moved = CanMove(e) && Move(_dragging!, rows.Children.IndexOf(row));
            e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
    }
}
