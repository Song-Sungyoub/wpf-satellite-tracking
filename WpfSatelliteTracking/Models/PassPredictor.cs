namespace WpfSatelliteTracking.Models;

public sealed record PredictedPass(DateTimeOffset Acquisition, DateTimeOffset Peak, DateTimeOffset Loss, double MaxElevation, bool AlreadyVisible, bool ClippedEnd);

public static class PassPredictor
{
    public static PredictedPass? Next(SatelliteOrbit orbit, GroundStation station, DateTimeOffset start, DateTimeOffset limit, double mask = SatelliteOperations.MinimumElevationDegrees)
    {
        double span = Math.Max(0, Math.Min(86400, (limit - start).TotalSeconds));
        double seconds = (start - SatelliteCatalog.ReferenceEpoch).TotalSeconds;
        double Elevation(double t) => EarthCoordinates.LookAngles(station, orbit.Position(seconds + t), EarthCoordinates.GmstDegrees(start.AddSeconds(t))).Elevation;
        const double step = 10;
        double Refine(double low, double high, bool rising)
        {
            while (high - low > 0.1) { double mid = (low + high) / 2; if ((Elevation(mid) >= mask) == rising) high = mid; else low = mid; }
            return high;
        }
        bool already = Elevation(0) >= mask;
        double acquisition = already ? 0 : double.NaN;
        for (double t = Math.Min(step, span); double.IsNaN(acquisition) && t > 0; t = Math.Min(t + step, span))
        {
            if (Elevation(t) >= mask) acquisition = Refine(Math.Max(0, t - step), t, true);
            if (t >= span) break;
        }
        if (double.IsNaN(acquisition)) return null;
        double loss = span, best = acquisition, bestElevation = Elevation(acquisition);
        bool clipped = true;
        for (double t = Math.Min(acquisition + step, span); t > acquisition; t = Math.Min(t + step, span))
        {
            double el = Elevation(t);
            if (el < mask) { loss = Refine(Math.Max(acquisition, t - step), t, false); clipped = false; break; }
            if (el > bestElevation) { best = t; bestElevation = el; }
            if (t >= span) break;
        }
        double a = Math.Max(acquisition, best - step), b = Math.Min(loss, best + step);
        for (int i = 0; i < 24; i++) { double left = (2 * a + b) / 3, right = (a + 2 * b) / 3; if (Elevation(left) < Elevation(right)) a = left; else b = right; }
        double peak = (a + b) / 2;
        return new(start.AddSeconds(acquisition), start.AddSeconds(peak), start.AddSeconds(loss), Elevation(peak), already, clipped);
    }
}
