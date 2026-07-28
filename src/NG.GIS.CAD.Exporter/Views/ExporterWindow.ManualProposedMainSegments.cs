using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Esri.ArcGISRuntime.Geometry;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private readonly List<Geometry> _manualProposedMainSegments = new List<Geometry>();
    private bool _isDrawingManualProposedMainSegment;









    private void RedrawManualProposedMainSegments(bool includeBuffer)
    {
        var symbol = _editableImportedProposedMainSymbol ?? new Services.ProposedMainRestSymbol();
        DrawProposedMainGeometries(_manualProposedMainSegments, symbol);
    }

    private void WriteManualProposedMainProof(Envelope extent, double paddingFeet, int snapCount)
    {
        var proof = new
        {
            source = "manual proposed pipeline",
            selectedWorkOrder = GetSelectedNgOdsWorkOrderNumberForProposedMain(),
            segmentCount = _manualProposedMainSegments.Count,
            endpointSnapsApplied = snapCount,
            paddingFeet,
            wkid = 3857,
            xmin = Math.Round(extent.XMin, 3),
            ymin = Math.Round(extent.YMin, 3),
            xmax = Math.Round(extent.XMax, 3),
            ymax = Math.Round(extent.YMax, 3),
            capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        WorkOrderGeometryTextBox.Text = JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FlattenManualProposedMainException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException) { messages.Add(current.GetType().Name + ": " + current.Message); }
        return string.Join(" -> ", messages);
    }
}

