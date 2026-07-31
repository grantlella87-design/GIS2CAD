using Esri.ArcGISRuntime.Geometry;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Puts a readable name to a WKID.
///
/// A bare "WKID 2249" tells almost nobody anything. The number is what the services and the profile
/// speak in, so it stays, but it is shown alongside the name of the system it stands for.
/// </summary>
public static class SpatialReferenceNames
{
    /// <summary>
    /// Names for the systems this work actually uses. Written out rather than derived, because these
    /// read better than the WKT ones do: WKT calls 2249 "NAD_1983_StatePlane_Massachusetts_Mainland_FIPS_2001_Feet",
    /// which is accurate and unhelpful on a form.
    /// </summary>
    private static readonly Dictionary<int, string> Curated = new()
    {
        [2249] = "NAD83 State Plane, Massachusetts Mainland (US survey feet)",
        [2250] = "NAD83 State Plane, Massachusetts Island (US survey feet)",
        [26986] = "NAD83 State Plane, Massachusetts Mainland (metres)",
        [26987] = "NAD83 State Plane, Massachusetts Island (metres)",
        [102686] = "NAD83 State Plane, Massachusetts Mainland (US survey feet)",
        [3857] = "Web Mercator (metres) — what web maps use",
        [102100] = "Web Mercator (metres) — what web maps use",
        [4326] = "Latitude and longitude, WGS84 (degrees)",
        [4269] = "Latitude and longitude, NAD83 (degrees)",
        [2882] = "NAD83 State Plane, New York Long Island (US survey feet)",
        [2263] = "NAD83 State Plane, New York Long Island (US survey feet)",
        [3437] = "NAD83 State Plane, New Hampshire (US survey feet)",
        [3438] = "NAD83 State Plane, Rhode Island (US survey feet)"
    };

    /// <summary>
    /// A label like "NAD83 State Plane, Massachusetts Mainland (US survey feet) — WKID 2249". The number
    /// is kept because it is what the profile and the services are configured with.
    /// </summary>
    public static string Describe(int wkid)
    {
        var name = ResolveName(wkid);
        return string.IsNullOrEmpty(name) ? "WKID " + wkid : name + " — WKID " + wkid;
    }

    /// <summary>Just the name, with no WKID, for places that already show the number.</summary>
    public static string ResolveName(int wkid)
    {
        if (Curated.TryGetValue(wkid, out var curated)) { return curated; }

        // Anything else is asked of the runtime and read out of its well known text, so a WKID this
        // does not have a hand written name for still says something rather than nothing.
        try
        {
            var wkText = SpatialReference.Create(wkid)?.WkText;
            var parsed = ParseWkTextName(wkText);
            if (!string.IsNullOrEmpty(parsed)) { return parsed; }
        }
        catch
        {
            // An unknown WKID throws. The number on its own is then the honest answer.
        }

        return string.Empty;
    }

    /// <summary>
    /// Pulls the system name out of well known text, which opens with it in quotes:
    /// PROJCS["NAD_1983_StatePlane_...",GEOGCS[...]]. Underscores are spaced out, since WKT uses them
    /// where a name would have spaces.
    /// </summary>
    private static string ParseWkTextName(string? wkText)
    {
        if (string.IsNullOrWhiteSpace(wkText)) { return string.Empty; }

        var open = wkText.IndexOf('"');
        if (open < 0) { return string.Empty; }

        var close = wkText.IndexOf('"', open + 1);
        if (close <= open + 1) { return string.Empty; }

        return wkText[(open + 1)..close].Replace('_', ' ').Trim();
    }
}
