using System.Globalization;
using Esri.ArcGISRuntime.Geometry;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>One sheet of the strip map index: a rectangle covering a stretch of the proposed main.</summary>
public sealed class StripMapSheet
{
    /// <summary>
    /// Sheet number. Settable because sheets are numbered as they are laid out and then renumbered once
    /// the redundant ones have been dropped, so the set that survives reads 1, 2, 3 rather than carrying
    /// the gaps left by what was removed.
    /// </summary>
    public int Number { get; set; }

    /// <summary>Sheet centre, in the route's own spatial reference.</summary>
    public double CenterX { get; init; }
    public double CenterY { get; init; }

    /// <summary>
    /// Bearing the sheet is turned to, so its long side follows the route.
    ///
    /// Always within a quarter turn of due east, so that up the page is never southward. A sheet may
    /// therefore be turned half a circle from the direction its stretch of route is travelled in, which
    /// costs nothing: the rectangle is unchanged by that turn, and it is the only way a route heading
    /// west can be drawn without north pointing down the paper.
    /// </summary>
    public double RotationDegrees { get; init; }

    /// <summary>Where along the route this sheet starts and ends, in ground feet from the start.</summary>
    public double StartStationFeet { get; init; }
    public double EndStationFeet { get; init; }

    /// <summary>The sheet outline, for drawing on the map and for the index itself.</summary>
    public required Polygon Outline { get; init; }
}

/// <summary>
/// Lays a strip map index out along the proposed main: a run of equally sized sheets, each turned to
/// follow the route, sized so that the ground they cover is what a given viewport shows at a given
/// scale.
///
/// The run is centred on the route, so the slack left over by a whole number of sheets is split
/// evenly between the two ends and the first and last sheet show the same length of pipe.
///
/// The route is expected in Web Mercator, which is what page 2 works in.
/// </summary>
public static class StripMapIndexService
{
    private const double EarthRadiusMetres = 6378137.0;
    private const double MetresPerFoot = 0.3048;

    /// <summary>
    /// Ground size, in metres, of a paper dimension printed at the given scale. A ten inch viewport
    /// at 1:240 covers 60.96 m, which is the 200 ft that scale is usually described by.
    /// </summary>
    public static double PaperToGroundMetres(double paperLength, bool paperIsMillimetres, double scaleDenominator)
    {
        var paperMetres = paperLength * (paperIsMillimetres ? 0.001 : 0.0254);
        return paperMetres * scaleDenominator;
    }

    public static double MetresToFeet(double metres) => metres / MetresPerFoot;

    /// <summary>
    /// Builds the sheets covering <paramref name="route"/>.
    ///
    /// Sizes are ground measurements. Web Mercator is not ground truth — its units stretch by
    /// 1/cos(latitude), which around Boston is a third again — so laying sheets out in raw map units
    /// would make every one of them cover about a quarter less ground than the scale claims. The
    /// route's own latitude is used to convert, which is exact enough over a strip map's extent.
    /// </summary>
    /// <param name="alongRouteGroundMetres">Sheet size in the direction the route runs.</param>
    /// <param name="acrossRouteGroundMetres">Sheet size across the route.</param>
    /// <param name="overlapFraction">
    /// How much of each sheet repeats on the next, as a fraction of the along-route size. Some
    /// overlap is what keeps a feature landing on a sheet edge from being cut in half on both sheets.
    /// </param>
    public static IReadOnlyList<StripMapSheet> Build(
        Geometry? route,
        double alongRouteGroundMetres,
        double acrossRouteGroundMetres,
        double overlapFraction)
    {
        if (route == null || route.IsEmpty) { return Array.Empty<StripMapSheet>(); }
        if (alongRouteGroundMetres <= 0 || acrossRouteGroundMetres <= 0) { return Array.Empty<StripMapSheet>(); }

        var paths = ParsePaths(route, out var spatialReferenceJson);
        if (paths.Count == 0) { return Array.Empty<StripMapSheet>(); }

        // Numbering starts on the longest run, and runs left to right across the paper along each one.
        // The longest run is the main line of the job, so sheet 1 is where the work mostly is rather
        // than wherever the service happened to return a stub first.
        paths = OrderPathsForNumbering(paths);

        var stretch = MapUnitsPerGroundMetre(paths);
        var alongMapUnits = alongRouteGroundMetres * stretch;
        var acrossMapUnits = acrossRouteGroundMetres * stretch;

        // A whole sheet of advance would butt the sheets edge to edge; overlapping means advancing
        // by less. Clamped so a silly overlap cannot stall the walk.
        var clampedOverlap = Math.Clamp(overlapFraction, 0.0, 0.9);
        var advance = alongMapUnits * (1.0 - clampedOverlap);

        var sheets = new List<StripMapSheet>();
        var groundFeetPerMapUnit = MetresToFeet(1.0 / stretch);
        var routeStartFeet = 0.0;

        foreach (var path in paths)
        {
            var stations = CumulativeStations(path);
            var length = stations[^1];
            if (length <= 0) { continue; }

            var count = length <= alongMapUnits
                ? 1
                : (int)Math.Ceiling((length - alongMapUnits) / advance) + 1;

            // A whole number of sheets almost never lands exactly on the length of the route, so
            // there is always some slack. Splitting it evenly across both ends is what makes the
            // first and last sheet show the same length of pipe and hang over by the same amount.
            //
            // Pinning the last sheet flush with the end of the route instead, which is what this did
            // before, pushes all the slack into the final gap: on a 250 unit route with 100 unit
            // sheets the last two sheets overlapped by 45 while the first two overlapped by 5.
            var span = alongMapUnits + (count - 1) * advance;
            var offset = (length - span) / 2.0;

            for (var i = 0; i < count; i++)
            {
                var start = offset + i * advance;
                var end = start + alongMapUnits;

                // Reported against the route, since what a sheet is worth in an index is the pipe it
                // shows, not the paper that hangs off the end of it.
                var pipeStart = Math.Clamp(start, 0.0, length);
                var pipeEnd = Math.Clamp(end, 0.0, length);

                var sheet = BuildSheet(
                    path, stations, start, end, length, alongMapUnits, acrossMapUnits,
                    sheets.Count + 1, spatialReferenceJson,
                    routeStartFeet + pipeStart * groundFeetPerMapUnit,
                    routeStartFeet + pipeEnd * groundFeetPerMapUnit);

                if (sheet != null) { sheets.Add(sheet); }
            }

            routeStartFeet += length * groundFeetPerMapUnit;
        }

        // A stub running off the main line is often already inside the sheets covering the line it comes
        // off, so it earns a drawing that shows nothing the set does not already have. Those are dropped,
        // and what is left renumbered, because the point of the index is the fewest sheets that still
        // cover the whole job.
        return RemoveRedundantSheets(sheets, route);
    }

    /// <summary>
    /// Puts the paths in the order they should be numbered, and turns each one so numbering runs the way
    /// a drawing set is read.
    ///
    /// Longest first, because the longest run is the main line of the job. Sheet 1 should be where the
    /// work mostly is rather than wherever the service happened to return a stub first, and a reader
    /// looking for the start of a job looks for the main line.
    ///
    /// Then each path is turned to run west to east, so its sheets count left to right across the paper.
    ///
    /// Left to right on paper is eastward on the ground, and that follows from north being kept up. A
    /// sheet's bearing is held within a quarter turn of due east so that up the page has north in it,
    /// which leaves the right of the page pointing east. A path already running eastward therefore
    /// numbers left to right; one running west counts backwards across every sheet in the set, which is
    /// the thing this prevents.
    ///
    /// A path running true north or south has nothing east of anything, and for those the right of the
    /// page points north instead, so numbering runs south to north to keep counting rightwards. The
    /// tolerance keeps a nearly vertical route from swapping ends on rounding noise.
    ///
    /// This is what decides which end is sheet 1, and it is decided by the paper rather than by the
    /// compass: a west-to-east rule and a north-to-south rule disagree on most real routes, and only one
    /// of them can hold while the numbers still read left to right.
    /// </summary>
    private static List<List<RoutePoint>> OrderPathsForNumbering(List<List<RoutePoint>> paths)
    {
        var ordered = new List<(List<RoutePoint> Path, double Length)>();

        foreach (var path in paths)
        {
            if (path.Count < 2) { continue; }

            var oriented = ShouldReverseForNumbering(path) ? Reversed(path) : path;
            ordered.Add((oriented, PathLength(oriented)));
        }

        // Longest first, and the length is the tie break for nothing else, so two paths of the same
        // length keep the order they arrived in rather than swapping about between runs.
        return ordered
            .OrderByDescending(p => p.Length)
            .Select(p => p.Path)
            .ToList();
    }

    private static bool ShouldReverseForNumbering(List<RoutePoint> path)
    {
        var start = path[0];
        var end = path[^1];

        // A whisker in Web Mercator metres. Anything smaller than this is a route running vertically
        // enough that its longitude difference says nothing about which end is the western one.
        const double EastTolerance = 1.0;

        // Turn a westward path around, so numbering runs with the right of the page rather than against
        // it.
        if (Math.Abs(end.X - start.X) > EastTolerance) { return end.X < start.X; }

        // True north or south. The right of the page is north for these, so the southern end is sheet 1.
        return end.Y < start.Y;
    }

    private static List<RoutePoint> Reversed(List<RoutePoint> path)
    {
        var copy = new List<RoutePoint>(path);
        copy.Reverse();
        return copy;
    }

    private static double PathLength(List<RoutePoint> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            total += Math.Sqrt(
                Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
        }
        return total;
    }

    /// <summary>
    /// Drops sheets that show no length of route the sheets before them do not already show, and
    /// renumbers what is left.
    ///
    /// A short stub coming off a main line is the case this exists for. The sheets covering the main line
    /// usually reach across the stub as they pass it, so the stub's own sheet is a whole drawing showing
    /// pipe that is already on another one. Sheets are kept in numbering order, which is why the longest
    /// path is numbered first: the main line claims its coverage before anything hanging off it can.
    ///
    /// The test is length of route rather than area of paper. Two sheets can overlap heavily and both
    /// still be needed, because what matters is the pipe each one shows.
    ///
    /// Anything that cannot be measured is kept. Dropping a sheet because a geometry operation failed
    /// would leave a hole in the set, and a redundant drawing costs a sheet of paper where a missing one
    /// costs a length of main nobody looked at.
    /// </summary>
    private static IReadOnlyList<StripMapSheet> RemoveRedundantSheets(List<StripMapSheet> sheets, Geometry route)
    {
        if (sheets.Count < 2) { return Renumber(sheets); }

        var kept = new List<StripMapSheet>();
        Geometry? covered = null;

        foreach (var sheet in sheets)
        {
            try
            {
                var onSheet = GeometryEngine.Intersection(route, sheet.Outline);
                if (onSheet == null || onSheet.IsEmpty)
                {
                    // Covers no route at all. That is not a drawing of anything.
                    continue;
                }

                if (covered != null)
                {
                    var addition = GeometryEngine.Difference(onSheet, covered);
                    var added = addition == null || addition.IsEmpty ? 0.0 : GeometryEngine.Length(addition);
                    var total = GeometryEngine.Length(onSheet);

                    // A twentieth of what the sheet shows. A sheet earning less than that is repeating
                    // its neighbours rather than carrying anything, and the threshold is a fraction of
                    // the sheet rather than a fixed distance so it means the same at any scale.
                    if (total > 0 && added / total < 0.05) { continue; }
                }

                kept.Add(sheet);
                covered = covered == null
                    ? sheet.Outline
                    : GeometryEngine.Union(covered, sheet.Outline);
            }
            catch
            {
                // Kept rather than judged, for the reason in the summary.
                kept.Add(sheet);
            }
        }

        return Renumber(kept.Count > 0 ? kept : sheets);
    }

    private static IReadOnlyList<StripMapSheet> Renumber(List<StripMapSheet> sheets)
    {
        for (var i = 0; i < sheets.Count; i++) { sheets[i].Number = i + 1; }
        return sheets;
    }

    private static StripMapSheet? BuildSheet(
        List<RoutePoint> path, List<double> stations, double start, double end, double routeLength,
        double alongMapUnits, double acrossMapUnits, int number, string spatialReferenceJson,
        double startStationFeet, double endStationFeet)
    {
        // The first and last sheet reach past the ends of the route, which is the whole point of the
        // even overhang, so the stretch of route actually on the sheet is the clamped range.
        var pipeStart = Math.Clamp(start, 0.0, routeLength);
        var pipeEnd = Math.Clamp(end, 0.0, routeLength);

        var chunk = PointsBetween(path, stations, pipeStart, pipeEnd);
        if (chunk.Count < 2) { return null; }

        // The sheet is turned to the straight line between where the chunk starts and ends. A route
        // that bends inside one sheet still bulges away from that line, which is what the across
        // dimension has to absorb; that is inherent to a strip map rather than something to correct.
        var angle = Math.Atan2(chunk[^1].Y - chunk[0].Y, chunk[^1].X - chunk[0].X);
        if (double.IsNaN(angle) || (chunk[^1].X == chunk[0].X && chunk[^1].Y == chunk[0].Y))
        {
            // A sheet whose stretch of route collapses to a point has no direction of its own, so it
            // takes the direction of the route as a whole.
            angle = Math.Atan2(path[^1].Y - path[0].Y, path[^1].X - path[0].X);
        }
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        // Across the route, centred on the chunk's own extent, so a bend leaves the route inside the
        // sheet instead of pushed to one edge.
        double minV = double.MaxValue, maxV = double.MinValue;
        foreach (var point in chunk)
        {
            var v = -point.X * sin + point.Y * cos;
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        // Along the route, centred on the sheet's own station range rather than on the chunk. That
        // is what carries the overhang: recentring on the chunk would pull the end sheets back onto
        // the route and undo the even spacing worked out above.
        var uAtStart = chunk[0].X * cos + chunk[0].Y * sin;
        var uAtEnd = chunk[^1].X * cos + chunk[^1].Y * sin;
        var centreU = ((uAtStart - (pipeStart - start)) + (uAtEnd + (end - pipeEnd))) / 2.0;

        var centreV = (minV + maxV) / 2.0;
        var centreX = centreU * cos - centreV * sin;
        var centreY = centreU * sin + centreV * cos;

        var halfAlong = alongMapUnits / 2.0;
        var halfAcross = acrossMapUnits / 2.0;
        var corners = new[]
        {
            (-halfAlong, -halfAcross),
            (halfAlong, -halfAcross),
            (halfAlong, halfAcross),
            (-halfAlong, halfAcross)
        };

        var builder = new StringBuilder();
        builder.Append("{\"rings\":[[");
        for (var i = 0; i <= corners.Length; i++)
        {
            var (dx, dy) = corners[i % corners.Length];
            if (i > 0) { builder.Append(','); }
            var x = centreX + dx * cos - dy * sin;
            var y = centreY + dx * sin + dy * cos;
            builder.Append('[').Append(x.ToString("R", CultureInfo.InvariantCulture))
                   .Append(',').Append(y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
        }
        builder.Append("]],\"spatialReference\":").Append(spatialReferenceJson).Append('}');

        if (Geometry.FromJson(builder.ToString()) is not Polygon outline) { return null; }

        return new StripMapSheet
        {
            Number = number,
            CenterX = centreX,
            CenterY = centreY,
            RotationDegrees = NorthUpRotationDegrees(angle),
            StartStationFeet = startStationFeet,
            EndStationFeet = endStationFeet,
            Outline = outline
        };
    }

    /// <summary>
    /// The bearing to turn the sheet to, given the direction its stretch of route runs.
    ///
    /// Turning a sheet to follow the route is what makes a strip map, but followed literally it turns
    /// half the sheets upside down. A route heading west wants a bearing near 180 degrees, and a sheet
    /// at 180 has south up the page: the north arrow points down, and the sheet number is drawn on its
    /// head. Half of Massachusetts runs east to west, so this was not a corner case.
    ///
    /// The fix is a half turn, and it is free. A rectangle turned half a circle about its own centre is
    /// the same rectangle, so the sheet covers exactly the ground it did before and the layout above is
    /// untouched -- only the bearing reported for it changes, which is what the paper is turned by.
    ///
    /// What it costs is the reading direction: a westward route now runs right to left across the page,
    /// because the two cannot both be had. Ground going left to right is what puts north up, and north
    /// up is the one a drawing is read by.
    /// </summary>
    private static double NorthUpRotationDegrees(double angle)
    {
        // Up the page is a quarter turn anticlockwise from the sheet's long axis, so how much of it is
        // northward is the cosine. Negative is a sheet with south up.
        var northUpComponent = Math.Cos(angle);

        // Inside the tolerance the route runs true north or true south, and north lies flat across the
        // page rather than up or down it. Neither turn is southerly, so the tie goes to the one that
        // runs the route's own direction to the right, which stops two such sheets facing opposite ways
        // for no reason a reader could see.
        const double AlongThePage = 1e-9;
        var upsideDown = northUpComponent < -AlongThePage
                         || (Math.Abs(northUpComponent) <= AlongThePage && Math.Sin(angle) < 0);

        if (upsideDown) { angle += Math.PI; }

        return NormalizeDegrees(angle * 180.0 / Math.PI);
    }

    /// <summary>Brings a bearing into (-180, 180], so a half turn does not report 359 degrees.</summary>
    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees > 180.0) { return degrees - 360.0; }
        if (degrees <= -180.0) { return degrees + 360.0; }
        return degrees;
    }

    /// <summary>
    /// How many Web Mercator units one ground metre takes up at this route's latitude. Web Mercator
    /// stretches away from the equator by 1/cos(latitude), so this is always at least one.
    /// </summary>
    private static double MapUnitsPerGroundMetre(List<List<RoutePoint>> paths)
    {
        double sum = 0;
        var count = 0;
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                sum += point.Y;
                count++;
            }
        }
        if (count == 0) { return 1.0; }

        var latitude = 2.0 * Math.Atan(Math.Exp(sum / count / EarthRadiusMetres)) - Math.PI / 2.0;
        var cosine = Math.Cos(latitude);

        // Guards the poles, where the conversion runs away and no strip map is being drawn anyway.
        return cosine < 0.01 ? 1.0 : 1.0 / cosine;
    }

    private static List<double> CumulativeStations(List<RoutePoint> path)
    {
        var stations = new List<double>(path.Count) { 0.0 };
        for (var i = 1; i < path.Count; i++)
        {
            var dx = path[i].X - path[i - 1].X;
            var dy = path[i].Y - path[i - 1].Y;
            stations.Add(stations[i - 1] + Math.Sqrt(dx * dx + dy * dy));
        }
        return stations;
    }

    /// <summary>The stretch of the path between two stations, with both ends interpolated onto it.</summary>
    private static List<RoutePoint> PointsBetween(List<RoutePoint> path, List<double> stations, double start, double end)
    {
        var points = new List<RoutePoint> { PointAt(path, stations, start) };
        for (var i = 0; i < path.Count; i++)
        {
            if (stations[i] > start && stations[i] < end) { points.Add(path[i]); }
        }
        points.Add(PointAt(path, stations, end));
        return points;
    }

    private static RoutePoint PointAt(List<RoutePoint> path, List<double> stations, double station)
    {
        if (station <= 0) { return path[0]; }
        if (station >= stations[^1]) { return path[^1]; }

        for (var i = 1; i < stations.Count; i++)
        {
            if (stations[i] < station) { continue; }
            var segmentLength = stations[i] - stations[i - 1];
            if (segmentLength <= 0) { return path[i]; }
            var t = (station - stations[i - 1]) / segmentLength;
            return new RoutePoint(
                path[i - 1].X + t * (path[i].X - path[i - 1].X),
                path[i - 1].Y + t * (path[i].Y - path[i - 1].Y));
        }
        return path[^1];
    }

    /// <summary>
    /// Reads the route's paths out of its JSON. The geometry is taken apart rather than walked
    /// through the runtime's own segment types so that this stays plain arithmetic, which is what
    /// makes the layout above straightforward to reason about.
    /// </summary>
    private static List<List<RoutePoint>> ParsePaths(Geometry route, out string spatialReferenceJson)
    {
        spatialReferenceJson = "{\"wkid\":3857}";
        var paths = new List<List<RoutePoint>>();

        using var document = JsonDocument.Parse(route.ToJson());
        if (document.RootElement.TryGetProperty("spatialReference", out var spatialReference))
        {
            spatialReferenceJson = spatialReference.GetRawText();
        }
        if (!document.RootElement.TryGetProperty("paths", out var pathArray) || pathArray.ValueKind != JsonValueKind.Array)
        {
            return paths;
        }

        foreach (var pathElement in pathArray.EnumerateArray())
        {
            if (pathElement.ValueKind != JsonValueKind.Array) { continue; }
            var points = new List<RoutePoint>();
            foreach (var pointElement in pathElement.EnumerateArray())
            {
                if (pointElement.ValueKind != JsonValueKind.Array) { continue; }
                var values = pointElement.EnumerateArray().ToArray();
                if (values.Length < 2) { continue; }
                if (values[0].TryGetDouble(out var x) && values[1].TryGetDouble(out var y))
                {
                    points.Add(new RoutePoint(x, y));
                }
            }
            if (points.Count >= 2) { paths.Add(points); }
        }
        return paths;
    }

    private readonly struct RoutePoint
    {
        public RoutePoint(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
    }
}
