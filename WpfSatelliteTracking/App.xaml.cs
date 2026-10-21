using System.Windows;

namespace WpfSatelliteTracking;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show("TLE/장면 초기화 실패: " + ex.GetBaseException().Message, "WPF Satellite Tracking"); Shutdown(1); }
    }
}
