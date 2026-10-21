using V = (double X, double Y, double Z);
namespace WpfSatelliteTracking.Models;

// Immutable forecast: no wall-clock, camera or later selection changes move this point.
public sealed record GroundPrediction(SatelliteOrbit Orbit, DateTimeOffset CreatedAt, DateTimeOffset Arrival, V SurfacePoint, double Latitude, double Longitude)
{
    public static GroundPrediction Create(SatelliteOrbit orbit, DateTimeOffset createdAt, DateTimeOffset arrival)
    {
        if (arrival <= createdAt) throw new ArgumentException("예측 시각은 현재보다 이후여야 합니다.");
        var geo = EarthCoordinates.Geodetic(orbit.EarthFixedPosition((arrival - SatelliteCatalog.ReferenceEpoch).TotalSeconds));
        return new(orbit, createdAt, arrival, EarthCoordinates.Surface(geo.Latitude, geo.Longitude), geo.Latitude, geo.Longitude);
    }
    // Compare against the position actually sent to the renderer, not a second
    // SGP4 query for Arrival. renderedMarker is the frozen surface anchor after
    // the scene's current Earth transform, so an extra/missing rotation is detected.
    public static double SceneErrorMeters(V renderedSatellite, DateTimeOffset time, V renderedMarker)
    {
        double gmst = EarthCoordinates.GmstDegrees(time);
        var geo = EarthCoordinates.Geodetic(EarthCoordinates.ToEarthFixed(renderedSatellite, gmst));
        var actual = EarthCoordinates.ToInertial(EarthCoordinates.Surface(geo.Latitude, geo.Longitude), gmst);
        double x=actual.X-renderedMarker.X, y=actual.Y-renderedMarker.Y, z=actual.Z-renderedMarker.Z;
        return Math.Sqrt(x*x+y*y+z*z)*EarthCoordinates.EquatorialRadiusKm*1000;
    }
}
