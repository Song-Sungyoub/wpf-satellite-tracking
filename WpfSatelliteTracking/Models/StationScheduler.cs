namespace WpfSatelliteTracking.Models;

// Ground-network scheduling: every station talks to a different satellite inside its 10° mask. A station keeps the
// satellite it is tracking until that satellite sets; free stations then take, in order, the selected satellite and
// otherwise the highest unclaimed one. Stations without a satellite park their antenna at zenith.
// shortcut: greedy assignment per frame; no pass planning, priorities, handover windows or link budgets.
public sealed class StationScheduler(GroundStation[] stations)
{
    private readonly List<(int Station, int Satellite, double Elevation)> candidates = [];
    private GroundAntennaAim?[,] aims = new GroundAntennaAim?[stations.Length, 0];
    private bool[] taken = [];
    private readonly bool[] assigned = new bool[stations.Length];
    private (double X, double Y, double Z)[] positions = [];
    public int[] Targets { get; } = Enumerable.Repeat(-1, stations.Length).ToArray();
    public int[] InView { get; } = new int[stations.Length];
    public GroundAntennaAim[] Desired { get; } = new GroundAntennaAim[stations.Length];

    public void Update(SatelliteOrbit[] orbits, double seconds, double earthRotationDegrees, int selected)
    {
        candidates.Clear();
        Array.Clear(InView);
        if (positions.Length != orbits.Length)
        {
            positions = new (double X, double Y, double Z)[orbits.Length];
            aims = new GroundAntennaAim?[stations.Length, orbits.Length];
            taken = new bool[orbits.Length];
        }
        Array.Clear(aims);
        Array.Clear(taken);
        Array.Clear(assigned);
        for (int i = 0; i < orbits.Length; i++) positions[i] = orbits[i].Position(seconds);
        for (int s = 0; s < stations.Length; s++)
            for (int i = 0; i < orbits.Length; i++)
            {
                var aim = GroundAntennaTracking.Calculate(stations[s], positions[i], earthRotationDegrees);
                if (!aim.Tracking) continue;
                aims[s, i] = aim; InView[s]++;
                candidates.Add((s, i, aim.ElevationDegrees));
            }
        // 1. Keep current locks while the satellite stays above the mask.
        for (int s = 0; s < stations.Length; s++)
            if (Targets[s] >= 0 && Targets[s] < orbits.Length && aims[s, Targets[s]] is not null && !taken[Targets[s]])
            { assigned[s] = true; taken[Targets[s]] = true; }
            else Targets[s] = -1;
        // 2. Free stations: selected satellite first, then the highest unclaimed satellite.
        foreach (var (s, i, _) in candidates.OrderByDescending(c => c.Satellite == selected).ThenByDescending(c => c.Elevation))
        {
            if (assigned[s] || taken[i]) continue;
            Targets[s] = i; assigned[s] = true; taken[i] = true;
        }
        for (int s = 0; s < stations.Length; s++) Desired[s] = Targets[s] >= 0 ? aims[s, Targets[s]]!.Value : default;
    }
}

// Azimuth/elevation drive: slews at a fixed on-screen rate when the target changes or the antenna parks at zenith,
// then follows the satellite directly once it is on target (locked). Rates are in wall-clock seconds so the motion
// stays visible at any playback speed.
public sealed class AntennaServo
{
    public const double AzimuthRate = 40, ElevationRate = 25, LockToleranceDegrees = 0.5;
    private int target = -1;
    public double Azimuth { get; private set; }
    public double Elevation { get; private set; } = 90;
    public bool Locked { get; private set; }
    public int Target => target;

    public GroundAntennaAim Update(int satellite, GroundAntennaAim desired, double wallSeconds)
    {
        if (satellite != target) { target = satellite; Locked = false; }
        double goalAzimuth = satellite >= 0 ? desired.AzimuthDegrees : Azimuth, goalElevation = satellite >= 0 ? desired.ElevationDegrees : 90;
        if (Locked) { Azimuth = goalAzimuth; Elevation = goalElevation; }
        else
        {
            double dAz = ((goalAzimuth - Azimuth) % 360 + 540) % 360 - 180;
            double dEl = goalElevation - Elevation;
            double stepAz = AzimuthRate * wallSeconds, stepEl = ElevationRate * wallSeconds;
            Azimuth = ((Azimuth + Math.Clamp(dAz, -stepAz, stepAz)) % 360 + 360) % 360;
            Elevation += Math.Clamp(dEl, -stepEl, stepEl);
            if (satellite >= 0 && Math.Abs(dAz) <= Math.Max(stepAz, LockToleranceDegrees) && Math.Abs(dEl) <= Math.Max(stepEl, LockToleranceDegrees))
            {
                Locked = true; Azimuth = goalAzimuth; Elevation = goalElevation;
            }
        }
        return new GroundAntennaAim(Azimuth, Elevation, Locked);
    }
}
