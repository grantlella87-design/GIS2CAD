using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Renders the legend swatches for the page 2 map layer tree.
///
/// This runs after the tree is built and never blocks it. Rendering a swatch means asking the runtime
/// to draw a symbol, and for a map image layer the service's legend has to be fetched first, so on a
/// portal item with a few hundred sublayers this is far too slow to hold the page up for. The tree
/// appears with names and checkboxes, and the swatches fill in behind it.
/// </summary>
public partial class ExporterWindow
{
    private const int LegendSwatchPixels = 20;
    private const double LegendSwatchDpi = 96;

    /// <summary>
    /// Cancels the legend pass in flight when the map is rebuilt, so swatches from the old tree are
    /// not still arriving against nodes that have been replaced.
    /// </summary>
    private CancellationTokenSource? _legendLoadCancellation;

    private void StartMapLayerLegendLoad(ExporterViewModel vm)
    {
        _legendLoadCancellation?.Cancel();
        _legendLoadCancellation?.Dispose();
        _legendLoadCancellation = new CancellationTokenSource();

        // Deliberately not awaited. The caller is building the map, and the swatches are decoration
        // that arrives when it arrives.
        _ = LoadMapLayerLegendsAsync(vm, _legendLoadCancellation.Token);
    }

    private async Task LoadMapLayerLegendsAsync(ExporterViewModel vm, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var node in ExporterViewModel.FlattenMapLayers(vm.MapLayers))
            {
                if (cancellationToken.IsCancellationRequested) { return; }
                if (!_mapLayerContentByPath.TryGetValue(node.Path, out var content)) { continue; }

                var items = await BuildLegendItemsAsync(content, cancellationToken);
                if (cancellationToken.IsCancellationRequested) { return; }
                if (items.Count > 0) { node.SetLegendItems(items); }
            }
        }
        catch (OperationCanceledException)
        {
            // The map was rebuilt underneath this pass, which is not a failure.
        }
        catch (Exception ex)
        {
            // A legend is not worth interrupting the page for, so this is reported and left there.
            vm.Status = "Some map legend symbols could not be drawn: " + ex.Message;
        }
    }

    private static async Task<List<MapLegendItemViewModel>> BuildLegendItemsAsync(
        ILayerContent content, CancellationToken cancellationToken)
    {
        var items = new List<MapLegendItemViewModel>();

        IReadOnlyList<LegendInfo> legendInfos;
        try
        {
            legendInfos = await content.GetLegendInfosAsync();
        }
        catch
        {
            // A layer that cannot report a legend still belongs in the tree with its checkbox.
            return items;
        }

        foreach (var legendInfo in legendInfos)
        {
            if (cancellationToken.IsCancellationRequested) { return items; }
            if (legendInfo.Symbol == null) { continue; }

            try
            {
                var swatch = await legendInfo.Symbol.CreateSwatchAsync(
                    LegendSwatchPixels, LegendSwatchPixels, LegendSwatchDpi, System.Drawing.Color.Transparent);
                var imageSource = await swatch.ToImageSourceAsync();
                items.Add(new MapLegendItemViewModel(legendInfo.Name ?? string.Empty, imageSource));
            }
            catch
            {
                // One symbol the runtime will not draw should not cost the layer the rest of its
                // legend, so the label is kept without a swatch.
                items.Add(new MapLegendItemViewModel(legendInfo.Name ?? string.Empty, null));
            }
        }

        return items;
    }
}
