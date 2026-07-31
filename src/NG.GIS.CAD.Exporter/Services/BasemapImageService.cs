using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>One basemap the export can bring into the drawing.</summary>
public sealed class BasemapChoice
{
    public BasemapChoice(string name, string serviceUrl)
    {
        Name = name;
        ServiceUrl = serviceUrl;
    }

    public string Name { get; }
    public string ServiceUrl { get; }

    /// <summary>True for the "None" entry, which is a choice rather than an absent one.</summary>
    public bool IsNone => string.IsNullOrWhiteSpace(ServiceUrl);

    public override string ToString() => Name;
}

/// <summary>
/// Fetches a basemap as a single georeferenced image covering the export extent, for the CAD export to
/// place under the features.
///
/// The image comes from the service's own export endpoint rather than from the map on screen. A screen
/// capture would be limited to the window's pixels and would carry the overlays drawn on top of it;
/// asking the service produces a clean image at whatever resolution is wanted, already in the output
/// spatial reference, which is what makes it possible to place it exactly.
/// </summary>
public sealed class BasemapImageService
{
    private readonly HttpClient _httpClient;

    public BasemapImageService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Cached ArcGIS Online services. These answer without a token, so a basemap works before any
    /// portal sign-in, and every one of them supports the export endpoint even though they are tiled.
    /// </summary>
    public static IReadOnlyList<BasemapChoice> DefaultChoices { get; } = new List<BasemapChoice>
    {
        new("None", string.Empty),
        new("Aerial imagery", "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer"),
        new("Streets", "https://services.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer"),
        new("Topographic", "https://services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer"),
        new("Light grey canvas", "https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer")
    };

    /// <summary>
    /// What the export starts with, so the backdrop is there to check the placement against without
    /// anyone having to ask for it.
    ///
    /// Aerial imagery rather than a street or canvas map, because confirming placement means comparing
    /// the exported features against the ground they sit on: kerbs, buildings and driveways are what a
    /// main is checked against, and a drawn map shows its own idea of where those are.
    ///
    /// Found by name rather than taken by index, so reordering the list above cannot quietly change what
    /// every export starts with. Falls back to the first entry that has a service, and only then to None.
    /// </summary>
    public static BasemapChoice DefaultChoice { get; } =
        DefaultChoices.FirstOrDefault(c => c.Name == "Aerial imagery")
        ?? DefaultChoices.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.ServiceUrl))
        ?? DefaultChoices[0];

    /// <summary>
    /// The largest image an ArcGIS service will normally return on one export request. Asking for more
    /// gets a smaller image back without saying so, which would place a stretched picture.
    /// </summary>
    public const int MaxImagePixels = 4096;

    /// <summary>
    /// Downloads the basemap over <paramref name="extent"/> and writes it beside the drawing.
    ///
    /// The extent is requested in <paramref name="outWkid"/> for both the bounding box and the image,
    /// so the pixels line up with the drawing's coordinates and no reprojection is needed to place it.
    /// </summary>
    /// <param name="widthPixels">
    /// Long edge of the image. The short edge follows the extent's aspect ratio, so the image is never
    /// stretched relative to the ground it covers.
    /// </param>
    public async Task<BasemapImageResult> DownloadAsync(
        string serviceUrl, ExportExtent extent, int outWkid, int widthPixels, string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            throw new ArgumentException("No basemap service was chosen.", nameof(serviceUrl));
        }

        var groundWidth = Math.Abs(extent.XMax - extent.XMin);
        var groundHeight = Math.Abs(extent.YMax - extent.YMin);
        if (groundWidth <= 0 || groundHeight <= 0)
        {
            throw new InvalidOperationException("The export extent has no area, so no basemap image can cover it.");
        }

        var (pixelWidth, pixelHeight) = FitPixels(groundWidth, groundHeight, widthPixels);

        var query =
            "bbox=" + Invariant(extent.XMin) + "," + Invariant(extent.YMin) + "," + Invariant(extent.XMax) + "," + Invariant(extent.YMax)
            + "&bboxSR=" + extent.Wkid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&imageSR=" + outWkid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&size=" + pixelWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "," + pixelHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&format=png24&transparent=false&f=image";

        var url = serviceUrl.TrimEnd('/') + "/export?" + query;

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        // An ArcGIS service reports a bad request as JSON with a 200, so the content type is what says
        // whether an image came back. Saving the JSON as a .png would fail later and less clearly.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                "The basemap service returned " + (string.IsNullOrEmpty(contentType) ? "no content type" : contentType)
                + " rather than an image: " + Truncate(body, 300));
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(
            outputDirectory,
            "basemap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        return new BasemapImageResult
        {
            ImagePath = path,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            ServiceUrl = serviceUrl
        };
    }

    /// <summary>
    /// Picks a pixel size matching the extent's shape, with the long edge at the requested size and
    /// both edges inside what a service will return.
    /// </summary>
    private static (int Width, int Height) FitPixels(double groundWidth, double groundHeight, int requestedLongEdge)
    {
        var longEdge = Math.Clamp(requestedLongEdge, 256, MaxImagePixels);

        if (groundWidth >= groundHeight)
        {
            var height = (int)Math.Round(longEdge * groundHeight / groundWidth);
            return (longEdge, Math.Clamp(height, 1, MaxImagePixels));
        }

        var width = (int)Math.Round(longEdge * groundWidth / groundHeight);
        return (Math.Clamp(width, 1, MaxImagePixels), longEdge);
    }

    private static string Invariant(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";
}

/// <summary>The downloaded basemap image and what the writer needs to place it.</summary>
public sealed class BasemapImageResult
{
    public required string ImagePath { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public string ServiceUrl { get; init; } = string.Empty;
}
