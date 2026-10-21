using System.IO;

namespace WpfSatelliteTracking.Models;

public static class SatelliteCatalog
{
    public static readonly DateTimeOffset ReferenceEpoch = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Lazy<SatelliteOrbit[]> entries = new(Load);
    public static int Count => entries.Value.Length;
    public static SatelliteOrbit[] Create() => entries.Value.ToArray();
    public static DateTimeOffset EarliestEpoch => entries.Value.Min(o => o.TleEpoch);
    public static DateTimeOffset LatestEpoch => entries.Value.Max(o => o.TleEpoch);
    // This is a conservative application extrapolation limit, not an accuracy guarantee.
    public static DateTimeOffset MinimumTime => LatestEpoch.AddDays(-7);
    public static DateTimeOffset MaximumTime => EarliestEpoch.AddDays(7);
    public static DateTimeOffset InitialTime => DateTimeOffset.UtcNow >= MinimumTime && DateTimeOffset.UtcNow <= MaximumTime ? DateTimeOffset.UtcNow : LatestEpoch;
    private static SatelliteOrbit[] Load()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--tle");
        string path = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "satellites.tle");
        if (File.Exists(path)) return Parse(File.ReadAllText(path));
        using var stream = typeof(SatelliteCatalog).Assembly.GetManifestResourceStream("WpfSatelliteTracking.TleCatalog")!;
        using var reader = new StreamReader(stream); return Parse(reader.ReadToEnd());
    }
    public static SatelliteOrbit[] Parse(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToArray();
        var result = new List<SatelliteOrbit>();
        for (int i = 0; i < lines.Length;)
        {
            string name = lines[i].StartsWith("1 ") ? "NORAD " + lines[i].Substring(2, 5) : lines[i++].Trim();
            if (name.StartsWith("0 ")) name = name[2..];
            if (i + 1 >= lines.Length) throw new InvalidDataException("TLE 두 행이 필요합니다.");
            result.Add(new SatelliteOrbit(name, lines[i++].TrimEnd(), lines[i++].TrimEnd(), result.Count % 2 == 0 ? SatelliteMission.A : SatelliteMission.B));
        }
        if (result.Count is < 1 or > 200 || result.Select(o => o.NoradId).Distinct().Count() != result.Count)
            throw new InvalidDataException("TLE는 중복 없이 1~200개를 지정하세요.");
        if (result.Max(o => o.TleEpoch) - result.Min(o => o.TleEpoch) > TimeSpan.FromDays(7))
            throw new InvalidDataException("TLE 기준시각 차이가 7일을 초과합니다. 같은 시기의 자료를 사용하세요.");
        return result.ToArray();
    }
}
