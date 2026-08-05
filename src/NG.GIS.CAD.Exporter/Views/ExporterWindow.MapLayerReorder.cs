using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Esri.ArcGISRuntime.Mapping;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Reordering the map's layers by dragging one onto another in the map layers tree, the same way the
/// data source tiles are reordered.
///
/// The drop does not touch the map. It finds the data source tile that stands for each layer and moves
/// that, which is what already reorders the map. Two panels showing the same order had to agree, and
/// the only way to be sure of that is for one of them to be the one that decides. Reordering here and
/// on the map separately would have been two answers to the same question, with the next reconcile
/// picking whichever it read last.
///
/// Top level layers only. A sublayer belongs to the service that carries it and has no position of its
/// own in the draw order to change.
/// </summary>
public partial class ExporterWindow
{
    private const string MapLayerNodeDragFormat = "NgGisCadMapLayerNode";

    private static readonly Brush MapLayerDropBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xB8));

    private Point _mapLayerDragOrigin;
    private MapLayerToggleViewModel? _mapLayerDragCandidate;

    private void MapLayerNode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mapLayerDragCandidate = null;

        if (sender is not FrameworkElement row) { return; }
        if (row.DataContext is not MapLayerToggleViewModel node || !node.CanReorder) { return; }
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject)) { return; }

        _mapLayerDragOrigin = e.GetPosition(null);
        _mapLayerDragCandidate = node;
    }

    private void MapLayerNode_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_mapLayerDragCandidate == null) { return; }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _mapLayerDragCandidate = null;
            return;
        }

        var moved = e.GetPosition(null) - _mapLayerDragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not DependencyObject row) { return; }

        var dragged = _mapLayerDragCandidate;
        _mapLayerDragCandidate = null;

        DragDrop.DoDragDrop(row, new DataObject(MapLayerNodeDragFormat, dragged), DragDropEffects.Move);
    }

    private void MapLayerNode_DragOver(object sender, DragEventArgs e)
    {
        var target = ResolveMapLayerDropTarget(sender, e, out var border);

        e.Effects = target == null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;

        if (border != null) { border.BorderBrush = target == null ? Brushes.Transparent : MapLayerDropBrush; }
    }

    private void MapLayerNode_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) { border.BorderBrush = Brushes.Transparent; }
    }

    private async void MapLayerNode_Drop(object sender, DragEventArgs e)
    {
        var target = ResolveMapLayerDropTarget(sender, e, out var border);
        if (border != null) { border.BorderBrush = Brushes.Transparent; }

        e.Handled = true;
        if (target == null) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }
        if (e.Data.GetData(MapLayerNodeDragFormat) is not MapLayerToggleViewModel moving) { return; }

        var movingTile = TileForMapLayer(moving.LayerRef as Layer);
        var targetTile = TileForMapLayer(target.LayerRef as Layer);
        if (movingTile == null || targetTile == null || ReferenceEquals(movingTile, targetTile)) { return; }

        await vm.MoveMapDataSourceToAsync(movingTile, targetTile);
    }

    private MapLayerToggleViewModel? ResolveMapLayerDropTarget(object sender, DragEventArgs e, out Border? border)
    {
        border = sender as Border;

        if (sender is not FrameworkElement row) { return null; }
        if (row.DataContext is not MapLayerToggleViewModel target || !target.CanReorder) { return null; }
        if (!e.Data.GetDataPresent(MapLayerNodeDragFormat)) { return null; }
        if (e.Data.GetData(MapLayerNodeDragFormat) is not MapLayerToggleViewModel moving) { return null; }
        if (ReferenceEquals(moving, target) || !moving.CanReorder) { return null; }

        return target;
    }

    /// <summary>
    /// The data sources tile that stands for one map layer. Every layer on the map has one, whether it
    /// came from the web map or from a profile source, which is what lets a drop in the tree be carried
    /// out as a move in the panel.
    /// </summary>
    private MapDataSourceViewModel? TileForMapLayer(Layer? layer)
    {
        if (layer == null) { return null; }
        if (DataContext is not ExporterViewModel vm) { return null; }

        return vm.MapDataSources.FirstOrDefault(source => ReferenceEquals(ResolveLayerForDataSource(source), layer));
    }
}
