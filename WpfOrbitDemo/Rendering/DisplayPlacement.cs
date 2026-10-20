using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfOrbitDemo.Rendering;

internal static class DisplayPlacement
{
    // Optional --display3 start-up placement for one development machine's monitor; edit DeviceName for other setups.
    private const string DeviceName = @"\\.\DISPLAY8";
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private static MonitorInfo Find()
    {
        MonitorInfo? found = null;
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && info.Device == DeviceName) found = info;
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return found ?? throw new InvalidOperationException("지정한 3번 디스플레이가 없습니다. 다른 화면에서는 진단 창을 열지 않습니다.");
    }

    public static string Describe()
    {
        var info = Find();
        return $"Windows 설정 3번 ({info.Device}): {info.Work.Left},{info.Work.Top}..{info.Work.Right},{info.Work.Bottom}";
    }

    public static void Prepare(Window window)
    {
        var target = Find();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        const uint flags = 0x0004 | 0x0010; // Preserve Z order and keyboard focus; never show the window here.
        if (!SetWindowPos(handle, IntPtr.Zero, target.Work.Left + 30, target.Work.Top + 30, 0, 0, flags | 0x0001))
            throw new InvalidOperationException("Cannot position the hidden diagnostic window.");
        double scale = GetDpiForWindow(handle) / 96.0;
        int width = Math.Min((int)Math.Round(1200 * scale), target.Work.Right - target.Work.Left);
        int height = Math.Min((int)Math.Round(800 * scale), target.Work.Bottom - target.Work.Top);
        int left = target.Work.Left + (target.Work.Right - target.Work.Left - width) / 2;
        int top = target.Work.Top + (target.Work.Bottom - target.Work.Top - height) / 2;
        if (!SetWindowPos(handle, IntPtr.Zero, left, top, width, height, flags) || !GetWindowRect(handle, out var rect)
            || rect.Left < target.Work.Left || rect.Top < target.Work.Top || rect.Right > target.Work.Right || rect.Bottom > target.Work.Bottom)
            throw new InvalidOperationException("진단 창이 3번 디스플레이 안에 배치되지 않아 표시를 중단합니다.");
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
}

