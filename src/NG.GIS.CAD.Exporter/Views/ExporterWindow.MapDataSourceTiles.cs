using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Reordering the data source tiles by dragging one onto another.
///
/// The order of the tiles is the draw order on the map, top of the list drawn on top. Buttons did this
/// before, a place at a time; dragging says where a source should go rather than how many places it
/// should travel, which is the thing actually being decided.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>Where the button went down, so a click can be told from the start of a drag.</summary>
    private Point _mapDataSourceDragOrigin;

    /// <summary>The tile the button went down on, or null when it went down on something else.</summary>
    private MapDataSourceViewModel? _mapDataSourceDragCandidate;

    /// <summary>The colour a tile is outlined in while a drop on it would land.</summary>
    private static readonly Brush MapDataSourceDropBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xB8));
    private static readonly Brush MapDataSourceTileBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

    private const string MapDataSourceDragFormat = "NgGisCadMapDataSourceTile";

    /// <summary>
    /// Notes what was pressed, without taking the press.
    ///
    /// The drag is not begun here. A press is how a checkbox is ticked and a button is pushed as well as
    /// how a drag starts, and starting one on the way down would mean the tick box could never be
    /// reached. It begins on the first movement past the system's drag threshold instead, which is what
    /// tells the two apart.
    /// </summary>
    private void MapDataSourceTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mapDataSourceDragCandidate = null;

        if (sender is not FrameworkElement tile) { return; }
        if (tile.DataContext is not MapDataSourceViewModel source || !source.CanReorder) { return; }

        // A press on a control is that control's, not the tile's. Without this a drag begun on the tick
        // box would carry the tile away instead of ticking it.
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject)) { return; }

        _mapDataSourceDragOrigin = e.GetPosition(null);
        _mapDataSourceDragCandidate = source;
    }

    private void MapDataSourceTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_mapDataSourceDragCandidate == null) { return; }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _mapDataSourceDragCandidate = null;
            return;
        }

        var moved = e.GetPosition(null) - _mapDataSourceDragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not DependencyObject tile) { return; }

        var dragged = _mapDataSourceDragCandidate;
        _mapDataSourceDragCandidate = null;

        var data = new DataObject(MapDataSourceDragFormat, dragged);
        DragDrop.DoDragDrop(tile, data, DragDropEffects.Move);
    }

    private void MapDataSourceTile_DragOver(object sender, DragEventArgs e)
    {
        var target = ResolveMapDataSourceDropTarget(sender, e, out var border);

        e.Effects = target == null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;

        if (border != null) { border.BorderBrush = target == null ? MapDataSourceTileBrush : MapDataSourceDropBrush; }
    }

    private void MapDataSourceTile_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) { border.BorderBrush = MapDataSourceTileBrush; }
    }

    private async void MapDataSourceTile_Drop(object sender, DragEventArgs e)
    {
        var target = ResolveMapDataSourceDropTarget(sender, e, out var border);
        if (border != null) { border.BorderBrush = MapDataSourceTileBrush; }

        e.Handled = true;
        if (target == null) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }
        if (e.Data.GetData(MapDataSourceDragFormat) is not MapDataSourceViewModel moving) { return; }

        await vm.MoveMapDataSourceToAsync(moving, target);
    }

    /// <summary>
    /// The tile a drop would land on, or null when it would land nowhere worth landing: on itself, on
    /// something that is not a tile, or on one of the map's own layers, which have no place in the
    /// profile to record an order in.
    /// </summary>
    private MapDataSourceViewModel? ResolveMapDataSourceDropTarget(object sender, DragEventArgs e, out Border? border)
    {
        border = sender as Border;

        if (sender is not FrameworkElement tile) { return null; }
        if (tile.DataContext is not MapDataSourceViewModel target || !target.CanReorder) { return null; }
        if (!e.Data.GetDataPresent(MapDataSourceDragFormat)) { return null; }
        if (e.Data.GetData(MapDataSourceDragFormat) is not MapDataSourceViewModel moving) { return null; }
        if (ReferenceEquals(moving, target) || !moving.CanReorder) { return null; }

        return target;
    }

    /// <summary>
    /// Whether this is part of something the user clicks in its own right, walking up from what was hit
    /// to the tile. Asked of the whole chain rather than of the hit element alone, because a click on a
    /// button lands on the text inside it rather than on the button.
    /// </summary>
    private static bool IsWithinInteractiveControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or TextBoxBase or ComboBox) { return true; }

            // The tile itself is where the walk stops: anything above it belongs to the list, not to
            // this tile, and the tile is the only element in the template that takes drops.
            if (source is Border border && border.AllowDrop) { return false; }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : null;
        }

        return false;
    }
}
