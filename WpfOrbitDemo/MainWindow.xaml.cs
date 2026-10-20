using System.Windows;
using System.Windows.Input;
using WpfOrbitDemo.ViewModels;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WpfOrbitDemo;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private Point? lastPointer;
    private Point pointerDown;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.SetRenderingActive(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => viewModel.SetRenderingActive(IsVisible && WindowState != WindowState.Minimized);
        IsVisibleChanged += (_, _) => viewModel.SetRenderingActive(IsVisible && WindowState != WindowState.Minimized);
        Closed += (_, _) => viewModel.Dispose();
    }
    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        pointerDown = e.GetPosition(Viewport); lastPointer = pointerDown; OrbitInputSurface.CaptureMouse();
    }
    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(Viewport);
        if ((point - pointerDown).Length < 4)
            VisualTreeHelper.HitTest(viewModel.UseNearSatelliteLayer ? SatelliteViewport : Viewport, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult hit) { viewModel.SelectModel(hit.ModelHit, hit.VertexIndex1); return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(point));
        lastPointer = null; OrbitInputSurface.ReleaseMouseCapture();
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => viewModel.SetViewportSize(e.NewSize.Width, e.NewSize.Height);
    private void OnLostCapture(object sender, MouseEventArgs e) => lastPointer = null;
    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (lastPointer is not Point previous) return;
        Point current = e.GetPosition(Viewport);
        viewModel.Rotate(current.X - previous.X, current.Y - previous.Y); lastPointer = current;
    }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e) => viewModel.Zoom(e.Delta);

    // Full screen: only the 3D view fills the monitor (borderless, maximized). F11 toggles, Esc leaves.
    // The previous window style/state/margins are restored exactly on exit.
    private (WindowStyle Style, WindowState State, ResizeMode Resize, Thickness Margin, Thickness Frame, CornerRadius Corner, Thickness Border)? windowed;
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && windowed is not null)) { ToggleFullScreen(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
    private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void ToggleFullScreen()
    {
        bool enter = windowed is null;
        if (enter)
        {
            windowed = (WindowStyle, WindowState, ResizeMode, RootLayout.Margin, ViewportFrame.Margin, ViewportFrame.CornerRadius, ViewportFrame.BorderThickness);
            WindowState = WindowState.Normal; // re-maximize after removing the frame so the window also covers the taskbar
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            RootLayout.Margin = ViewportFrame.Margin = ViewportFrame.BorderThickness = new Thickness(0); ViewportFrame.CornerRadius = new CornerRadius(0);
            WindowState = WindowState.Maximized;
        }
        else if (windowed is { } saved)
        {
            WindowState = WindowState.Normal;
            WindowStyle = saved.Style; ResizeMode = saved.Resize;
            RootLayout.Margin = saved.Margin; ViewportFrame.Margin = saved.Frame; ViewportFrame.CornerRadius = saved.Corner; ViewportFrame.BorderThickness = saved.Border;
            WindowState = saved.State;
            windowed = null;
        }
        var chrome = enter ? Visibility.Collapsed : Visibility.Visible;
        HeaderBar.Visibility = SidePanel.Visibility = BottomBar.Visibility = chrome;
        SidePanelColumn.Width = enter ? new GridLength(0) : new GridLength(300);
        ExitFullScreenButton.Visibility = enter ? Visibility.Visible : Visibility.Collapsed;
        OrbitInputSurface.Focus();
    }
}
