using System.Windows;
using WpfSatelliteTracking.Rendering;

namespace WpfSatelliteTracking;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        try { if (e.Args.Contains("--display3")) DisplayPlacement.Prepare(window); }
        catch { window.Close(); Shutdown(1); throw; }
        MainWindow = window;
        window.Show();
    }
}
