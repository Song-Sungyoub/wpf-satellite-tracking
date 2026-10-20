namespace WpfOrbitDemo.Models;

// Fixed histogram: no per-frame storage allocation. P95 is rounded up to 1 ms.
public sealed class FrameTiming
{
    private readonly int[] histogram = new int[1002];
    private double previous = double.NaN;
    public int Intervals { get; private set; }
    public double Seconds { get; private set; }
    public double MaximumMs { get; private set; }
    public double WorkMs { get; private set; }
    public int Over50 { get; private set; }
    public int Over100 { get; private set; }
    public int Over244 { get; private set; }
    public int ContactFrames { get; private set; }
    public void Reset()
    {
        Array.Clear(histogram); previous = double.NaN;
        Intervals = Over50 = Over100 = Over244 = ContactFrames = 0;
        Seconds = MaximumMs = WorkMs = 0;
    }
    public void Add(double now, double workMs, bool contact)
    {
        if (!double.IsFinite(now) || !double.IsFinite(workMs) || workMs < 0 || now < previous) throw new ArgumentOutOfRangeException(nameof(now));
        if (double.IsNaN(previous)) { previous = now; return; }
        double ms = (now - previous) * 1000; previous = now;
        Seconds += ms / 1000; Intervals++; WorkMs += workMs;
        MaximumMs = Math.Max(MaximumMs, ms);
        histogram[Math.Min(1001, (int)Math.Ceiling(Math.Min(ms, 1001)))]++;
        if (ms >= 50) Over50++; if (ms >= 100) Over100++; if (ms >= 244) Over244++;
        if (contact) ContactFrames++;
    }
    public int P95Ms
    {
        get { int count = 0, target = (int)Math.Ceiling(Intervals * 0.95); for (int i = 0; i < histogram.Length; i++) { count += histogram[i]; if (count >= target) return i; } return 0; }
    }
    public string Summary => $"{Seconds:F1}s · {Intervals:N0}간격 · 평균 {(Seconds > 0 ? Intervals / Seconds : 0):F1} FPS\nP95 {(P95Ms > 1000 ? ">1000" : $"≤{P95Ms}")}ms · 최대 {MaximumMs:F1}ms\n≥50/100/244ms: {Over50}/{Over100}/{Over244}\n갱신 {(Intervals > 0 ? WorkMs / Intervals : 0):F2}ms · 교신 {ContactFrames:N0} frames";
}

