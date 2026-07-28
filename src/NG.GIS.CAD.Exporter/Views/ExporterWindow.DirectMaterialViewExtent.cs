using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private DispatcherTimer? _directMaterialViewRetryTimer;
    private int _directMaterialViewRetryCount;
    private const string DirectMaterialViewMapServerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";
    private const string DirectMaterialViewPortalItemId = "c214d72caefb40699b129bc47b1b22a7";

    private void DirectScheduleMaterialViewRetries(string reason)
    {
        _directMaterialViewRetryCount = 0;
        if (_directMaterialViewRetryTimer == null)
        {
            _directMaterialViewRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            _directMaterialViewRetryTimer.Tick += async (_, __) =>
            {
                _directMaterialViewRetryCount++;
                await DirectEnsureMaterialViewOnExtentMapAsync("retry " + _directMaterialViewRetryCount);
                if (_directMaterialViewRetryCount >= 20) _directMaterialViewRetryTimer.Stop();
            };
            AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => DirectScheduleMaterialViewRetries("button action")), true);
        }
        _directMaterialViewRetryTimer.Stop();
        _directMaterialViewRetryTimer.Start();
    }

    private async Task DirectEnsureMaterialViewOnExtentMapAsync(string reason)
    {
        DirectScheduleMaterialViewRetries(reason);
        try
        {
            var mapViews = DirectFindAllMapViews().Distinct().ToList();
            if (mapViews.Count == 0)
            {
                DirectAppendStatus("Material_View_MA not added: no ArcGIS MapView found in window (" + reason + ").");
                return;
            }
            var token = await DirectGetArcGisPortalTokenAsync();
            var securedUrl = DirectAppendToken(DirectMaterialViewMapServerUrl, token);
            foreach (var mapView in mapViews)
            {
                if (mapView.Map == null) mapView.Map = new Map(BasemapStyle.ArcGISTopographic);
                var existing = mapView.Map.OperationalLayers.FirstOrDefault(l => string.Equals(l.Name, "Material_View_MA", StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsVisible = true;
                    continue;
                }
                var layer = new ArcGISMapImageLayer(new Uri(securedUrl)) { Name = "Material_View_MA", IsVisible = true };
                mapView.Map.OperationalLayers.Insert(0, layer);
                await layer.LoadAsync();
                if (layer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    foreach (var sublayer in layer.Sublayers) sublayer.IsVisible = true;
                    DirectAppendStatus("Material_View_MA loaded with ArcGIS token (" + reason + "). Sublayers: " + layer.Sublayers.Count + "; operational layers: " + mapView.Map.OperationalLayers.Count + "; portal item: " + DirectMaterialViewPortalItemId + ".");
                }
                else if (layer.LoadError != null)
                {
                    DirectAppendStatus("Material_View_MA load error after token append: " + layer.LoadError.GetType().Name + ": " + layer.LoadError.Message);
                }
            }
        }
        catch (Exception ex)
        {
            DirectAppendStatus("Material_View_MA token/load failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task<string?> DirectGetArcGisPortalTokenAsync()
    {
        await Task.Yield();
        foreach (var token in DirectCollectTokensFromObject(this, new HashSet<object>(ReferenceEqualityComparer.Instance), 0))
        {
            if (DirectLooksLikeArcGisToken(token)) return token;
        }
        foreach (var token in DirectCollectTokensFromLocalCache())
        {
            if (DirectLooksLikeArcGisToken(token)) return token;
        }
        DirectAppendStatus("Material_View_MA token lookup failed: no existing ArcGIS token was found in window objects or local cache.");
        return null;
    }

    private IEnumerable<string> DirectCollectTokensFromObject(object? obj, HashSet<object> visited, int depth)
    {
        if (obj == null || depth > 3) yield break;
        if (obj is string s)
        {
            if (DirectLooksLikeArcGisToken(s)) yield return s;
            yield break;
        }
        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum) yield break;
        if (!visited.Add(obj)) yield break;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var prop in type.GetProperties(flags))
        {
            object? value = null;
            try { value = prop.GetValue(obj); } catch { }
            if (value is string text && prop.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) && DirectLooksLikeArcGisToken(text)) yield return text;
            if (depth < 2 && value != null && !(prop.PropertyType.FullName ?? string.Empty).StartsWith("System.Windows", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var token in DirectCollectTokensFromObject(value, visited, depth + 1)) yield return token;
            }
        }
        foreach (var field in type.GetFields(flags))
        {
            object? value = null;
            try { value = field.GetValue(obj); } catch { }
            if (value is string text && field.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) && DirectLooksLikeArcGisToken(text)) yield return text;
            if (depth < 2 && value != null && !(field.FieldType.FullName ?? string.Empty).StartsWith("System.Windows", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var token in DirectCollectTokensFromObject(value, visited, depth + 1)) yield return token;
            }
        }
        foreach (var method in type.GetMethods(flags).Where(m => m.GetParameters().Length == 0 && m.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)))
        {
            object? result = null;
            try { result = method.Invoke(obj, null); } catch { }
            if (result is string text && DirectLooksLikeArcGisToken(text)) yield return text;
        }
    }

    private static IEnumerable<string> DirectCollectTokensFromLocalCache()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NationalGrid", "GisCadExporter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NationalGrid"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NationalGrid")
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)); }
            catch { continue; }
            foreach (var file in files.OrderByDescending(f => Path.GetFileName(f).Contains("arcgis", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("portal", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("token", StringComparison.OrdinalIgnoreCase)))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                foreach (var token in DirectExtractTokenStrings(text)) yield return token;
            }
        }
    }

    private static IEnumerable<string> DirectExtractTokenStrings(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (DirectLooksLikeArcGisToken(text.Trim())) yield return text.Trim();
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text); } catch { }
        if (doc != null)
        {
            foreach (var token in DirectExtractTokenStringsFromJson(doc.RootElement)) yield return token;
            doc.Dispose();
            yield break;
        }
        foreach (var piece in text.Split(new[] { ' ', '\r', '\n', '\t', '"', '\'', ':', ',', '{', '}', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (DirectLooksLikeArcGisToken(piece)) yield return piece;
        }
    }

    private static IEnumerable<string> DirectExtractTokenStringsFromJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
                {
                    var value = prop.Value.GetString();
                    if (DirectLooksLikeArcGisToken(value)) yield return value!;
                }
                foreach (var nested in DirectExtractTokenStringsFromJson(prop.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in DirectExtractTokenStringsFromJson(item)) yield return nested;
            }
        }
    }

    private static bool DirectLooksLikeArcGisToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Length < 20) return false;
        if (token.Contains(" ")) return false;
        if (token.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string DirectAppendToken(string url, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return url;
        var separator = url.Contains("?") ? "&" : "?";
        return url + separator + "token=" + Uri.EscapeDataString(token);
    }

    private IEnumerable<MapView> DirectFindAllMapViews()
    {
        foreach (var mv in DirectFindVisualChildren<MapView>(this)) yield return mv;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in GetType().GetFields(flags))
        {
            object? value = null;
            try { value = field.GetValue(this); } catch { }
            if (value is MapView mv) yield return mv;
        }
        foreach (var prop in GetType().GetProperties(flags))
        {
            object? value = null;
            try { value = prop.GetValue(this); } catch { }
            if (value is MapView mv) yield return mv;
        }
    }

    private static IEnumerable<T> DirectFindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in DirectFindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void DirectAppendStatus(string message)
    {
        try
        {
            var boxes = DirectFindVisualChildren<TextBox>(this).ToList();
            var blocks = DirectFindVisualChildren<TextBlock>(this).ToList();
            var targetBox = boxes.FirstOrDefault(b => b.Name == "WorkOrderGeometryTextBox") ?? boxes.FirstOrDefault();
            if (targetBox != null && !targetBox.Text.Contains(message))
            {
                targetBox.Text = string.IsNullOrWhiteSpace(targetBox.Text) ? message : targetBox.Text + Environment.NewLine + message;
            }
            foreach (var block in blocks.Where(b => (b.Text ?? string.Empty).Contains("Material_View_MA") || (b.Text ?? string.Empty).Contains("Native ArcGIS MapView loaded") || (b.Text ?? string.Empty).Contains("Extent page")))
            {
                block.Text = message;
            }
        }
        catch { }
    }
}