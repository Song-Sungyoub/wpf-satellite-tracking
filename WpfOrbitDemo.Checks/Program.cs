using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WpfOrbitDemo;
using WpfOrbitDemo.Models;
using WpfOrbitDemo.ViewModels;
using WpfOrbitDemo.Rendering;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CheckSatelliteOperations();
        if (args.Contains("--check-scene-views")) return CheckSceneViews(args);
        if (args.Contains("--check-satellite-scale"))
        {
            var scaleApp = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var vm = new MainViewModel();
            vm.SetSimulationTime(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
            var fleet = (Model3DGroup)vm.Scene.Children[5];
            void CheckFleet(bool physical)
            {
                for (int i = 0; i < vm.Orbits.Length; i++)
                {
                    var model = (Model3DGroup)fleet.Children[i];
                    double scale = model.Transform.Transform(new Vector3D(1, 0, 0)).Length;
                    Check((model.Transform.Transform(new Point3D()) - SceneGeometry.Point(vm.Orbits[i].Position(0))).Length < 1e-10, "Scaling preserves orbital position");
                    if (physical)
                        Check(Math.Abs(model.Children[0].Bounds.SizeX * scale * CircularOrbit.EarthRadiusKm * 1000 - SceneScale.SatelliteSpanMeters(vm.Orbits[i].Mission)) < 1e-7, "Physical spacecraft span is preserved");
                    else Check(Math.Abs(scale - SceneScale.ObservationSatelliteScale) < 1e-10, "All observation satellites use the observation model size");
                }
            }
            CheckFleet(false);
            vm.FocusSatelliteCommand.Execute(null);
            Check(Math.Abs(vm.CameraDirection.Length - 0.22) < 1e-10, "Closeup distance follows smaller model");
            vm.Zoom(10000);
            Check(Math.Abs(vm.CameraDirection.Length - 0.14) < 1e-10, "Closeup zoom reaches smaller model safely");
            vm.IsTrueScale = true; CheckFleet(true);
            vm.IsTrueScale = false; CheckFleet(false);
            Console.WriteLine("PASS: all observation sizes/positions, physical spans, closeup zoom and mode round trip; no window shown.");
            return 0;
        }
        try
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) => Console.Error.WriteLine("Dispatcher failure: " + e.Exception);
            var bindingErrors = new StringWriter();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            var window = new MainWindow();
            if (args.Contains("--headless"))
            {
                window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000; window.Top = -10000;
            }
            if (!args.Contains("--headless")) DisplayPlacement.Prepare(window);
            window.Show(); Pump(1);
            var vm = (MainViewModel)window.DataContext;
            Model3DGroup Satellite(int index) => (Model3DGroup)((Model3DGroup)vm.Scene.Children[5]).Children[index];
            var constellation = vm.Orbits;
            Check(constellation.Length == SatelliteConstellation.Count && constellation.Select(s => s.Name).Distinct().Count() == SatelliteConstellation.Count, "50 uniquely named satellites");
            Check(constellation.Count(s => s.Mission == SatelliteMission.A) == 30 && constellation.Count(s => s.Mission == SatelliteMission.B) == 20, "30 type A and 20 type B satellites");
            var clusters = constellation.Where(s => s.Mission == SatelliteMission.A).GroupBy(s => s.ClusterId).ToArray();
            Check(clusters.Length == 3 && clusters.All(g => g.Key is >= 1 and <= 3 && g.Count() == 10), "Three type A planes of ten satellites");
            Check(clusters.All(g => g.Select(s => (s.AltitudeKm, s.InclinationDegrees, s.NodeDegrees)).Distinct().Count() == 1 && g.Select(s => s.InitialPhaseDegrees % 360).Distinct().Count() == 10), "Type A plane members share a plane with distinct phases");
            Check(vm.SatellitesView.Groups?.Count == 5, "Three type A and two type B plane headers");
            Pump(0.3);
            Check(vm.CompositionFps > 0 && double.IsFinite(vm.AverageUpdateMilliseconds) && vm.FrameStatus.Contains("FPS"), "Live composition rate and UI update timing");
            var beforeMinimize = vm.SimulationTime;
            window.WindowState = WindowState.Minimized; Pump(0.15);
            Check(vm.SimulationTime == beforeMinimize, "Minimized window suspends animation work");
            window.WindowState = WindowState.Normal; Pump(0.15);
            Check(vm.SimulationTime > beforeMinimize && (vm.SimulationTime - beforeMinimize).TotalSeconds < 60, "Restored animation resumes without minimized-time jump");
            var beforeHide = vm.SimulationTime;
            window.Hide(); Pump(0.1);
            Check(vm.SimulationTime == beforeHide && vm.CompositionFps == 0 && vm.FrameStatus.Contains("일시 중지"), "Hidden window suspends animation and clears stale FPS");
            window.Show(); Pump(0.15);
            Check(vm.SimulationTime > beforeHide, "Showing the window resumes animation");
            vm.Start(); // Repeated startup must remain safe.
            foreach (var orbit in vm.Orbits)
            {
                for (int step = 0; step <= 100; step++)
                {
                    var p = orbit.Position(step * orbit.PeriodSeconds / 100);
                    Check(Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z) - orbit.Radius) < 1e-10, "Constant orbit radius");
                }
                var a = orbit.Position(0); var b = orbit.Position(orbit.PeriodSeconds);
                Check(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z) < 1e-10, "Period closure");
            }
            var equatorial = new CircularOrbit("test", 500, 0, 0);
            Check(Math.Abs(equatorial.PositionAtAngle(Math.PI / 2).Y) < 1e-10, "Equatorial plane");
            var polar = new CircularOrbit("test", 500, 90, 0);
            Check(Math.Abs(polar.PositionAtAngle(Math.PI / 2).Y - polar.Radius) < 1e-10, "Polar orbit reaches north");
            Check(equatorial.PeriodSeconds is > 5600 and < 5800, "500km orbit period");
            try { _ = new CircularOrbit("bad", double.NaN, 0, 0); throw new Exception("NaN accepted"); }
            catch (ArgumentOutOfRangeException) { }
            Check(vm.ElapsedText != "00:00:00", "Timer advances");
            vm.TogglePlaybackCommand.Execute(null); string paused = vm.ElapsedText; Pump(0.2);
            Check(vm.ElapsedText == paused && vm.PlaybackText == "재생", "Pause command");
            var initialCamera = vm.CameraPosition; vm.Rotate(100, 10000); vm.Zoom(100000);
            Check(vm.CameraPosition != initialCamera && Math.Abs(((Vector3D)vm.CameraPosition).Length - 3.4) < 1e-8, "Rotate and zoom clamp");
            vm.ResetCommand.Execute(null);
            Check(vm.ElapsedText == "00:00:00" && vm.CameraPosition == initialCamera, "Reset command");
            Check(vm.Scene.Children.Count == 11 && ((Model3DGroup)vm.Scene.Children[4]).Children.Count == 5 && ((Model3DGroup)vm.Scene.Children[5]).Children.Count == vm.Orbits.Length + 1, "Five plane orbit paths, one group per satellite plus the static batch");
            var earthSystem = (Model3DGroup)vm.Scene.Children[2];
            Check(earthSystem.Children.Count == 8 && vm.GroundStations.Length == 3, "Earth, ground stations, map tiles, clouds and night layer share rotation");
            var earth = (GeometryModel3D)earthSystem.Children[0]; var mesh = (MeshGeometry3D)earth.Geometry;
            var saturn = CelestialGeometry.Create(7);
            var saturnMesh = (MeshGeometry3D)saturn.Geometry;
            var rings = CelestialGeometry.SaturnRings(); var ringMesh = (MeshGeometry3D)rings.Geometry;
            Check(saturnMesh.IsFrozen && saturnMesh.Positions.Count == 129 * 65 &&
                ((BitmapSource)((ImageBrush)((DiffuseMaterial)saturn.Material).Brush).ImageSource).PixelWidth == 2048,
                "Saturn uses a static high-resolution surface and smooth mesh");
            Check(rings.IsFrozen && ringMesh.Positions.Count == 4 * 257 * 2 &&
                ringMesh.TextureCoordinates.Count == ringMesh.Positions.Count && ringMesh.Normals.Count == ringMesh.Positions.Count &&
                ringMesh.Positions.All(p => ((Vector3D)p).Length is var r &&
                    (r <= 1.95 * CelestialGeometry.Radii[7] + 1e-9 || r >= 2.03 * CelestialGeometry.Radii[7] - 1e-9)),
                "Saturn ring bands share radial UVs and leave the main division empty");
            var saturnPole = ringMesh.Normals[0];
            Check(Math.Abs(Vector3D.DotProduct((Vector3D)saturnMesh.Positions[0], saturnPole) /
                CelestialGeometry.Radii[7] - 0.902) < 1e-10 &&
                saturnMesh.Normals.All(n => Math.Abs(n.Length - 1) < 1e-10),
                "Saturn oblate body and rings share a pole with normalized surface normals");
            var texture = (BitmapImage)((ImageBrush)((DiffuseMaterial)earth.Material).Brush).ImageSource;
            Check(texture.PixelWidth == 5400 && texture.PixelHeight == 2700, "Full resolution earth texture");
            foreach (var station in vm.GroundStations)
            {
                var antenna = SceneGeometry.Antenna(station);
                Point3D origin = antenna.Transform.Transform(new Point3D());
                Check(Math.Abs(((Vector3D)origin).Length - 1.002) < 1e-10, "Station on globe surface");
                double latitude = Math.Asin(origin.Y / 1.002) * 180 / Math.PI;
                double longitude = Math.Atan2(-origin.Z, origin.X) * 180 / Math.PI;
                Check(Math.Abs(latitude - station.Latitude) < 1e-8 && Math.Abs(longitude - station.Longitude) < 1e-8, "Station geographic coordinates");
                Vector3D vertical = antenna.Transform.Transform(new Vector3D(0, 1, 0));
                Check(Vector3D.CrossProduct(vertical, (Vector3D)origin).Length < 1e-10, "Antenna points out of surface");
                var moving = new GroundAntennaGeometry(station); var other = new GroundAntennaGeometry(station);
                Check(ReferenceEquals(moving.Model.Children[0], other.Model.Children[0]) &&
                    SatelliteMeshes(moving.Model).All(p => p.Geometry.IsFrozen && p.Material.IsFrozen) &&
                    SatelliteMeshes(moving.Model).Sum(p => ((MeshGeometry3D)p.Geometry).TriangleIndices.Count / 3) < 18000,
                    "Ground antennas share frozen bounded geometry");
                var beforeFeed = moving.FeedPoint;
                moving.Update(new GroundAntennaAim(90, 25, true));
                Check((moving.FeedPoint - beforeFeed).Length > 1e-5 && moving.Model.Transform.Transform(new Point3D()) == origin,
                    "Antenna articulation moves the feed without moving its foundation");
                foreach (double rotation in new[] { 0.0, 57, 240 }) foreach (double bearing in new[] { 0.0, 90, 180, 270 })
                {
                    double lat = station.Latitude * Math.PI / 180, lon = (station.Longitude + rotation) * Math.PI / 180;
                    var up = new Vector3D(Math.Cos(lat) * Math.Cos(lon), Math.Sin(lat), -Math.Cos(lat) * Math.Sin(lon));
                    var east = new Vector3D(-Math.Sin(lon), 0, -Math.Cos(lon));
                    var north = Vector3D.CrossProduct(up, east);
                    double a = bearing * Math.PI / 180;
                    var direction = up * 0.5 + (east * Math.Sin(a) + north * Math.Cos(a)) * Math.Sqrt(0.75);
                    var target = up + direction * 0.11;
                    var aim = GroundAntennaTracking.Calculate(station, (target.X, target.Y, target.Z), rotation);
                    Check(aim.Tracking && Math.Abs(aim.ElevationDegrees - 30) < 1e-9 &&
                        Math.Abs(((aim.AzimuthDegrees - bearing + 540) % 360) - 180) < 1e-9,
                        "Antenna azimuth/elevation respects station coordinates and Earth rotation");
                    moving.Update(aim);
                    var turret = (Model3DGroup)moving.Model.Children[1]; var head = (Model3DGroup)turret.Children[1];
                    var boresight = moving.Model.Transform.Transform(turret.Transform.Transform(head.Transform.Transform(new Vector3D(0, 1, 0))));
                    boresight = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotation)).Transform(boresight); boresight.Normalize();
                    Check(Vector3D.DotProduct(boresight, direction) > 1 - 1e-10, "Ground dish opening follows the calculated sky direction");
                }
            }
            try { _ = new GroundStation("bad", 91, 0); throw new Exception("Invalid latitude accepted"); }
            catch (ArgumentOutOfRangeException) { }
            var sky = (GeometryModel3D)vm.Scene.Children[3];
            Check(sky.BackMaterial != null && sky.Material == null && sky.Geometry.IsFrozen && sky.BackMaterial.IsFrozen, "Reusable sky renders inward around the camera");
            var clouds = (GeometryModel3D)earthSystem.Children[6];
            var cloudBitmap = (BitmapSource)((ImageBrush)((DiffuseMaterial)clouds.Material).Brush).ImageSource;
            var cloudPixels = new byte[cloudBitmap.PixelWidth * cloudBitmap.PixelHeight * 4];
            cloudBitmap.CopyPixels(cloudPixels, cloudBitmap.PixelWidth * 4, 0);
            Check(cloudPixels.Where((_, i) => i % 4 == 3).Any(a => a > 30) && cloudPixels.Where((_, i) => i % 4 == 3).Any(a => a == 0), "Cloud layer has clouds and transparent gaps");
            Check(((Model3DGroup)vm.Scene.Children[6]).IsFrozen, "Atmosphere geometry is static");
            var satelliteAsset = SceneGeometry.Satellite(Colors.Cyan);
            Check(satelliteAsset.IsFrozen && satelliteAsset.Children.Count == 3, "Type A body, solar wings and antenna are separate joint groups");
            Check(satelliteAsset.Bounds.SizeX > 0.3 && satelliteAsset.Bounds.SizeY > 0.08 && satelliteAsset.Bounds.SizeZ > 0.08
                && SatelliteMeshes(satelliteAsset).Sum(part => ((MeshGeometry3D)part.Geometry).TriangleIndices.Count / 3) > 800, "Batched satellite retains panel span, antenna/dish extent and mesh detail");
            var typeBAsset = SceneGeometry.Satellite(Colors.Cyan, mission: SatelliteMission.B);
            var typeAShadow = SceneGeometry.Satellite(Colors.Cyan, true);
            Check(satelliteAsset.Bounds == typeAShadow.Bounds && typeBAsset.Bounds != satelliteAsset.Bounds, "Type A silhouette has matching lit/shadow bounds and remains distinct from type B");
            Check(SatelliteMeshes(satelliteAsset).All(p => p.Geometry.IsFrozen && p.Material.IsFrozen)
                && SatelliteMeshes(satelliteAsset).Sum(p => ((MeshGeometry3D)p.Geometry).TriangleIndices.Count / 3) < 5000, "Type A uses shared frozen meshes and bounded geometry");
            var wingsJoint = new MatrixTransform3D(); var dishJoint = new MatrixTransform3D(TypeASatelliteGeometry.AntennaPose(0, false));
            var movingTypeA = TypeASatelliteGeometry.CreateInstance(satelliteAsset, wingsJoint, dishJoint);
            var movingNight = TypeASatelliteGeometry.CreateInstance(typeAShadow, wingsJoint, dishJoint);
            Check(ReferenceEquals(movingTypeA.Children[1].Transform, movingNight.Children[1].Transform) &&
                ReferenceEquals(movingTypeA.Children[2].Transform, movingNight.Children[2].Transform) &&
                SatelliteMeshes(movingTypeA).Zip(SatelliteMeshes(satelliteAsset)).All(p => ReferenceEquals(p.First.Geometry, p.Second.Geometry)),
                "Type A lit/shadow share per-instance joints and frozen template meshes");
            var spanBefore = movingTypeA.Bounds.SizeX;
            var wingTurn = Matrix3D.Identity; wingTurn.Rotate(new Quaternion(new Vector3D(1, 0, 0), 83)); wingsJoint.Matrix = wingTurn;
            Check(Math.Abs(movingTypeA.Bounds.SizeX - spanBefore) < 1e-10 &&
                ((Model3DGroup)movingTypeA.Children[0]).Bounds == ((Model3DGroup)satelliteAsset.Children[0]).Bounds,
                "Solar wing articulation preserves physical span and fixed body geometry");
            Check(typeBAsset.IsFrozen && typeBAsset.Children.Count <= 8 && typeBAsset.Bounds.SizeX > 0.3 && typeBAsset.Bounds.SizeZ > 0.05
                && typeBAsset.Bounds.Y < -0.07, "Type B has two extended wings and nadir instruments below the bus");
            Check(typeBAsset.Bounds == SceneGeometry.Satellite(Colors.Cyan, true, SatelliteMission.B).Bounds
                && typeBAsset.Children.Cast<GeometryModel3D>().All(p => p.Geometry.IsFrozen && ((MeshGeometry3D)p.Geometry).TextureCoordinates.Count == ((MeshGeometry3D)p.Geometry).Positions.Count), "Type B lit/shadow spans and shared UV meshes agree");
            foreach (var orbit in vm.Orbits)
                foreach (double phase in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
                {
                    double seconds = phase * orbit.PeriodSeconds;
                    var pose = SceneGeometry.SatellitePose(orbit, seconds);
                    var position = SceneGeometry.Point(orbit.Position(seconds));
                    Check((pose.Transform(new Point3D()) - position).Length < 1e-10, "Satellite model stays on orbit");
                    var up = pose.Transform(new Vector3D(0, 1, 0));
                    var forward = pose.Transform(new Vector3D(0, 0, 1));
                    var velocity = SceneGeometry.Point(orbit.Position(seconds + 1)) - SceneGeometry.Point(orbit.Position(seconds - 1)); velocity.Normalize();
                    Check(Math.Abs(pose.Determinant - 1) < 1e-8 && Math.Abs(Vector3D.DotProduct(up, forward)) < 1e-8, "Satellite rigid orthonormal pose");
                    Check(Vector3D.DotProduct(forward, velocity) > 0.999999 && Vector3D.DotProduct(up, (Vector3D)position) > 0, "Satellite faces velocity with outward axis");
                    if (orbit.Mission == SatelliteMission.A)
                    {
                        var dishPose = TypeASatelliteGeometry.AntennaPose(seconds, true); dishPose.Append(pose);
                        var boresight = dishPose.Transform(new Vector3D(0, 1, 0));
                        Check(Vector3D.DotProduct(boresight, up) < -0.8 &&
                            SatelliteOperations.IntersectEarth((position.X, position.Y, position.Z), (boresight.X, boresight.Y, boresight.Z), out _),
                            "Type A dish points at Earth throughout the orbit");
                    }
                }
            Check(mesh.TextureCoordinates.Count == mesh.Positions.Count, "Earth UV mapping");
            for (int t = 0; t < mesh.TriangleIndices.Count; t += 3)
            {
                var a = mesh.Positions[mesh.TriangleIndices[t]];
                var b = mesh.Positions[mesh.TriangleIndices[t + 1]];
                var c = mesh.Positions[mesh.TriangleIndices[t + 2]];
                Check(Vector3D.DotProduct(Vector3D.CrossProduct(b - a, c - a), (Vector3D)a) >= -1e-10, "Outward sphere faces");
            }
            vm.TogglePlaybackCommand.Execute(null); Pump(0.2);
            Check(vm.ElapsedText != "00:00:00", "Resume command");
            vm.TogglePlaybackCommand.Execute(null); vm.ResetCommand.Execute(null); Pump(0.1);
            foreach (var label in vm.SatelliteLabels)
                Check(label.Left >= 0 && label.Top >= 0 && label.Left < 900 && label.Top < 600, "Visible labels stay inside viewport");
            vm.SelectedOrbit = vm.Orbits[6];
            Check(vm.SatelliteLabels.All(label => label.Orbit != vm.Orbits[6]), "Earth occludes selected far-side satellite label");
            vm.SelectedOrbit = vm.Orbits[0];
            Check(vm.SatelliteLabels.Count <= 10, "Overview limits labels while retaining all satellite models");
            var retainedLabels = vm.SatelliteLabels.ToArray();
            int labelCollectionChanges = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler labelsChanged = (_, _) => labelCollectionChanges++;
            vm.SatelliteLabels.CollectionChanged += labelsChanged;
            vm.Rotate(0.1, 0);
            Check(labelCollectionChanges == 0 && retainedLabels.All(label => vm.SatelliteLabels.Contains(label)), "Moving label positions reuses collection items and brushes");
            vm.SatelliteLabels.CollectionChanged -= labelsChanged;
            vm.Rotate(-0.1, 0);
            var pickingViewport = (System.Windows.Controls.Viewport3D)window.FindName("Viewport");
            var inputSurface = (System.Windows.Controls.Border)window.FindName("OrbitInputSurface");
            Check(!pickingViewport.IsHitTestVisible && inputSurface.IsHitTestVisible && inputSurface.Background is not null, "Routine pointer routing uses the 2D input surface");
            Check(ReferenceEquals(inputSurface.InputHitTest(new Point(2, 2)), inputSurface), "2D input surface receives empty-area pointer input");
            var pickLabel = vm.SatelliteLabels.First();
            bool picked = false;
            VisualTreeHelper.HitTest(pickingViewport, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult hit) { picked = vm.SelectModel(hit.ModelHit); return HitTestResultBehavior.Stop; }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(new Point(pickLabel.Left - 20, pickLabel.Top + 24)));
            Check(picked && vm.SelectedOrbit == pickLabel.Orbit, "Viewport ray hit selects visible 3D satellite");
            vm.SelectedOrbit = vm.Orbits[0];
            for (int i = 0; i < vm.Orbits.Length; i++)
            {
                var satellitePosition = Satellite(i).Transform.Transform(new Point3D());
                Check((satellitePosition - SceneGeometry.Point(vm.Orbits[i].Position(0))).Length < 1e-8, "Every satellite has its own orbital pose");
                var hit = ((Model3DGroup)Satellite(i).Children[0]).Children[0];
                Check(vm.SelectModel(hit) && vm.SelectedOrbit == vm.Orbits[i], "Shared meshes retain unique satellite picking: " + i);
            }
            vm.SelectedOrbit = vm.Orbits[0];
            Check(Enumerable.Range(0, 8).Select(i => ((GeometryModel3D)((Model3DGroup)Satellite(i).Children[0]).Children[0]).Geometry).GroupBy(mesh => mesh).Any(g => g.Count() > 1), "Cluster instances share frozen meshes");
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            // Flags such as --headless are not output paths.
            string output = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "preview.png");
            using (var file = File.Create(output)) encoder.Save(file);
            vm.SelectOrbitCommand.Execute(vm.Orbits[2]);
            Check(vm.SelectedOrbit == vm.Orbits[2] && vm.SelectedInfo.Contains("Type A 1군집") && vm.SelectedInfo.Contains("550"), "Label/list command selects satellite and cluster details");
            var hitPart = ((Model3DGroup)Satellite(1).Children[0]).Children[0];
            Check(vm.SelectModel(hitPart) && vm.SelectedOrbit == vm.Orbits[1], "3D mesh selection resolves satellite");
            vm.SelectOrbitCommand.Execute(vm.Orbits[0]);
            string beforeGroundPlayback = vm.PlaybackText;
            vm.FocusGroundStationsCommand.Execute(null); Pump(0.1);
            Check(vm.PlaybackText == beforeGroundPlayback && vm.CameraDirection.Length is > 0.74 and < 0.76, "Ground station focus preserves playback and antenna animation");
            Check(Vector3D.CrossProduct((Vector3D)vm.CameraPosition, vm.CameraDirection).Length < 1e-8, "Regional camera faces surface from above");
            Check(((Vector3D)vm.CameraPosition).Length > 1.4, "Regional camera stays outside Earth");
            var detailImage = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            detailImage.Render(window);
            var detailEncoder = new PngBitmapEncoder(); detailEncoder.Frames.Add(BitmapFrame.Create(detailImage));
            using (var file = File.Create(Path.Combine(Path.GetDirectoryName(output)!, "preview-stations.png"))) detailEncoder.Save(file);
            vm.Rotate(400, -200); vm.Zoom(100000);
            Check(((Vector3D)vm.CameraPosition).Length >= 1.0005 - 1e-10, "Regional rotation cannot enter Earth");
            vm.ResetCommand.Execute(null);
            Check(vm.CameraPosition == initialCamera, "Reset leaves regional camera mode");
            vm.FocusSatelliteCommand.Execute(null); Pump(0.1);
            Check(((Model3DGroup)vm.Scene.Children[4]).Children.Cast<GeometryModel3D>().All(m => m.Material == null), "Closeup hides orbit lines");
            Check(vm.PlaybackText == "재생" && Math.Abs(vm.CameraDirection.Length - 0.55 * SceneScale.ObservationSatelliteScale) < 1e-8, "Satellite closeup command");
            var satelliteImage = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            satelliteImage.Render(window);
            var satelliteEncoder = new PngBitmapEncoder(); satelliteEncoder.Frames.Add(BitmapFrame.Create(satelliteImage));
            using (var file = File.Create(Path.Combine(Path.GetDirectoryName(output)!, "preview-satellite.png"))) satelliteEncoder.Save(file);
            vm.TogglePlaybackCommand.Execute(null); Pump(0.1);
            var followedPosition = Satellite(0).Transform.Transform(new Point3D());
            var cameraTarget = vm.CameraPosition + vm.CameraDirection;
            Check((cameraTarget - followedPosition).Length < 1e-8, "Closeup follows satellite model position");
            vm.TogglePlaybackCommand.Execute(null); vm.ResetCommand.Execute(null);
            Check(vm.CameraPosition == initialCamera, "Reset leaves satellite closeup");
            vm.SelectedOrbit = vm.Orbits[^1]; vm.FocusSatelliteCommand.Execute(null); Pump(0.1);
            var selectedTarget = vm.CameraPosition + vm.CameraDirection;
            Check((selectedTarget - Satellite(vm.Orbits.Length - 1).Transform.Transform(new Point3D())).Length < 1e-8 && vm.SelectedInfo.Contains("Type B"), "Closeup follows the final Type B satellite");
            vm.ResetCommand.Execute(null); vm.SelectedOrbit = vm.Orbits[0];
            window.Width = 900; window.Height = 650; Pump(0.1);
            var viewport = (System.Windows.Controls.Viewport3D)window.FindName("Viewport");
            Check(viewport.ActualWidth > 0 && viewport.ActualHeight > 0, "Minimum window retains 3D viewport");
            Check(vm.CameraDirection.Length > 3.4, "Resize fits overview");
            var viewForward = vm.CameraDirection; viewForward.Normalize();
            var viewRight = Vector3D.CrossProduct(viewForward, new Vector3D(0, 1, 0)); viewRight.Normalize();
            var viewUp = Vector3D.CrossProduct(viewRight, viewForward);
            double focal = viewport.ActualWidth / (2 * Math.Tan(21 * Math.PI / 180));
            foreach (var orbit in vm.Orbits)
                for (int n = 0; n < 64; n++)
                {
                    var ray = SceneGeometry.Point(orbit.PositionAtAngle(n * 2 * Math.PI / 64)) - vm.CameraPosition;
                    double depth = Vector3D.DotProduct(ray, viewForward);
                    Check(Math.Abs(Vector3D.DotProduct(ray, viewRight) * focal / depth) < viewport.ActualWidth / 2 - 10
                        && Math.Abs(Vector3D.DotProduct(ray, viewUp) * focal / depth) < viewport.ActualHeight / 2 - 10, "Minimum-size overview fits all orbit paths");
                }
            Check(bindingErrors.ToString().Length == 0, "No WPF binding errors: " + bindingErrors);
            var equinox = SolarLighting.At(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero));
            Check(Math.Abs(equinox.LatitudeDegrees) < 1 && Math.Abs(equinox.LongitudeDegrees) < 4, "Equinox solar noon reference");
            Check(SolarLighting.At(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero)).LatitudeDegrees is > 23 and < 24, "Summer solstice declination");
            Check(SolarLighting.At(new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero)).LatitudeDegrees is > -24 and < -23, "Winter solstice declination");
            var j2000 = SolarLighting.At(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));
            Check(Math.Abs(j2000.EarthRotationDegrees - 280.46061837) < 1e-8, "J2000 mean sidereal angle");
            var fixedSun = new Vector3D(equinox.EarthFixedDirection.X, equinox.EarthFixedDirection.Y, equinox.EarthFixedDirection.Z);
            var inertialSun = new Vector3D(equinox.InertialDirection.X, equinox.InertialDirection.Y, equinox.InertialDirection.Z);
            var rotatedSun = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), equinox.EarthRotationDegrees)).Transform(fixedSun);
            Check((rotatedSun - inertialSun).Length < 1e-10 && Math.Abs(inertialSun.Length - 1) < 1e-10, "Solar reference frame and unit direction");
            Check(SolarLighting.NightWeight(1) == 0 && SolarLighting.NightWeight(-1) == 1 && SolarLighting.NightWeight(0) == 0, "Night mask excludes daylight");
            Check(SolarLighting.NightWeight(-0.05) is > 0 and < 1, "Twilight fades continuously");
            bool bufferBusy = true;
            using (var blockedLights = new NightLights(bitmap => !bufferBusy && bitmap.TryLock(new Duration(TimeSpan.Zero))))
            using (var referenceLights = new NightLights())
            {
                var t = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
                blockedLights.Update(t, SolarLighting.At(t));
                Check(blockedLights.HasPendingUpload, "A busy render buffer retains the ready night mask");
                var task = blockedLights.UpdateTask;
                blockedLights.RequestUpdate(t.AddHours(3), SolarLighting.At(t.AddHours(3)));
                Check(ReferenceEquals(task, blockedLights.UpdateTask), "Busy uploads do not launch more mask calculations");
                var responsiveness = Stopwatch.StartNew();
                for (int j = 0; j < 100; j++) Check(!blockedLights.TryPresent(), "Busy upload is deferred rather than waited on");
                double probeMilliseconds = responsiveness.Elapsed.TotalMilliseconds;
                Check(probeMilliseconds < 200, "One hundred busy GPU probes cannot stall the UI");
                var latest = t.AddHours(6);
                blockedLights.Update(latest, SolarLighting.At(latest));
                int heartbeats = 0;
                var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                heartbeat.Tick += (_, _) => { heartbeats++; blockedLights.TryPresent(); };
                heartbeat.Start();
                var heartbeatDeadline = Stopwatch.StartNew();
                while (heartbeats < 3 && heartbeatDeadline.Elapsed.TotalSeconds < 1) Pump(0.02);
                heartbeat.Stop();
                Check(heartbeats >= 3 && blockedLights.HasPendingUpload, $"UI stays responsive during simulated buffer contention: ticks={heartbeats}, pending={blockedLights.HasPendingUpload}, elapsed={heartbeatDeadline.Elapsed.TotalMilliseconds:F1}ms");
                Console.WriteLine($"Forced busy buffer: 100 nonblocking probes {probeMilliseconds:F3}ms; {heartbeats} UI heartbeats in {heartbeatDeadline.Elapsed.TotalMilliseconds:F1}ms");
                bufferBusy = false;
                Check(blockedLights.TryPresent() && !blockedLights.HasPendingUpload, "Deferred upload is retried after buffer recovery");
                referenceLights.Update(latest, SolarLighting.At(latest));
                var expected = new byte[NightLights.Width * NightLights.Height * 4];
                var actual = new byte[expected.Length];
                referenceLights.Texture.CopyPixels(expected, NightLights.Width * 4, 0);
                blockedLights.Texture.CopyPixels(actual, NightLights.Width * 4, 0);
                Check(actual.SequenceEqual(expected), "Recovery uploads the latest seek with unchanged pixels");
                bufferBusy = true;
                blockedLights.Update(t, SolarLighting.At(t)); blockedLights.Dispose();
                Check(!blockedLights.HasPendingUpload && !blockedLights.TryPresent(), "Dispose discards deferred upload safely");
            }
            using (var asyncLights = new NightLights())
            {
                var t = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
                asyncLights.Update(t, SolarLighting.At(t));
                asyncLights.RequestUpdate(t.AddHours(12), SolarLighting.At(t.AddHours(12)));
                var pending = asyncLights.UpdateTask;
                asyncLights.Update(t.AddHours(3), SolarLighting.At(t.AddHours(3)));
                var expected = new byte[NightLights.Width * NightLights.Height * 4];
                asyncLights.Texture.CopyPixels(expected, NightLights.Width * 4, 0);
                var deadline = Stopwatch.StartNew();
                while (!pending.IsCompleted && deadline.Elapsed.TotalSeconds < 5) Pump(0.02);
                var actual = new byte[expected.Length]; asyncLights.Texture.CopyPixels(actual, NightLights.Width * 4, 0);
                Check(pending.IsCompletedSuccessfully && actual.SequenceEqual(expected), "Stale asynchronous night mask cannot overwrite explicit time seek");
                var nextTime = t.AddHours(6);
                asyncLights.RequestUpdate(nextTime, SolarLighting.At(nextTime));
                deadline.Restart();
                while (!asyncLights.UpdateTask.IsCompleted && deadline.Elapsed.TotalSeconds < 5) Pump(0.02);
                Check(asyncLights.UpdateTask.IsCompletedSuccessfully, "Asynchronous night mask completes on UI dispatcher");
                asyncLights.Texture.CopyPixels(actual, NightLights.Width * 4, 0);
                asyncLights.Update(t, SolarLighting.At(t));
                asyncLights.Update(nextTime, SolarLighting.At(nextTime));
                asyncLights.Texture.CopyPixels(expected, NightLights.Width * 4, 0);
                Check(actual.SequenceEqual(expected), "Background night mask exactly matches synchronous pixels");
                asyncLights.RequestUpdate(t.AddHours(12), SolarLighting.At(t.AddHours(12)));
                var closing = asyncLights.UpdateTask;
                asyncLights.Dispose(); deadline.Restart();
                while (!closing.IsCompleted && deadline.Elapsed.TotalSeconds < 5) Pump(0.02);
                Check(closing.IsCompletedSuccessfully, "Closing cancels pending night work without dispatcher errors");
            }
            Check(SolarLighting.IsEclipsed((-inertialSun.X * 1.1, -inertialSun.Y * 1.1, -inertialSun.Z * 1.1), equinox), "Antisolar satellite is eclipsed");
            Check(!SolarLighting.IsEclipsed((inertialSun.X * 1.1, inertialSun.Y * 1.1, inertialSun.Z * 1.1), equinox), "Sunward satellite is illuminated");
            var lateral = Vector3D.CrossProduct(inertialSun, new Vector3D(0, 1, 0)); lateral.Normalize();
            var outsideShadow = -inertialSun * 1.2 + lateral * 1.01;
            Check(!SolarLighting.IsEclipsed((outsideShadow.X, outsideShadow.Y, outsideShadow.Z), equinox), "Outside shadow cylinder stays illuminated");
            try { _ = SolarLighting.At(new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)); throw new Exception("Unsupported year accepted"); }
            catch (ArgumentOutOfRangeException) { }
            window.Width = 1200; window.Height = 800; Pump(0.1);
            vm.ResetCommand.Execute(null);
            var manualTime = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.FromHours(9));
            var maskWatch = Stopwatch.StartNew(); vm.SetSimulationTime(manualTime); maskWatch.Stop();
            Console.WriteLine($"Night mask + scene refresh: {maskWatch.Elapsed.TotalMilliseconds:F1} ms (single sample, not FPS benchmark)");
            var maskDeadline = Stopwatch.StartNew();
            while (vm.NightUploadPending && maskDeadline.Elapsed.TotalSeconds < 5) Pump(0.02);
            Check(!vm.NightUploadPending, "Paused playback still presents a deferred time seek");
            Check(vm.SimulationTime == manualTime && vm.PlaybackText == "재생", "Manual time is applied and paused");
            var nightModel = (GeometryModel3D)earthSystem.Children[5];
            var nightMaterial = (MaterialGroup)nightModel.Material;
            var nightTexture = (WriteableBitmap)((ImageBrush)((EmissiveMaterial)nightMaterial.Children[1]).Brush).ImageSource;
            byte[] nightPixels = new byte[nightTexture.PixelWidth * nightTexture.PixelHeight * 4];
            nightTexture.CopyPixels(nightPixels, nightTexture.PixelWidth * 4, 0);
            byte NightAlpha(double lat, double lon)
            {
                lon = (lon + 540) % 360 - 180;
                int x = Math.Clamp((int)((lon + 180) / 360 * NightLights.Width), 0, NightLights.Width - 1);
                int y = Math.Clamp((int)((90 - lat) / 180 * NightLights.Height), 0, NightLights.Height - 1);
                return nightPixels[(y * NightLights.Width + x) * 4 + 3];
            }
            Check(NightAlpha(vm.CurrentSun.LatitudeDegrees, vm.CurrentSun.LongitudeDegrees) == 0, "City lights absent at subsolar point");
            Check(NightAlpha(-vm.CurrentSun.LatitudeDegrees, vm.CurrentSun.LongitudeDegrees + 180) == 255, "City lights enabled on night hemisphere");
            Check(vm.Scene.Children.OfType<DirectionalLight>().Count() == 0 && vm.Scene.Children.OfType<PointLight>().Count() == 1, "Only the displayed solar source lights the scene");
            vm.FocusGroundStationsCommand.Execute(null); vm.Zoom(-950); Pump(0.1);
            var nightRendered = SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-night.png"));
            var viewportOrigin = viewport.TranslatePoint(new Point(), window);
            var cityRegion = new CroppedBitmap(nightRendered, new Int32Rect((int)viewportOrigin.X, (int)viewportOrigin.Y, (int)viewport.ActualWidth, (int)viewport.ActualHeight));
            byte[] cityPixels = new byte[cityRegion.PixelWidth * cityRegion.PixelHeight * 4];
            cityRegion.CopyPixels(cityPixels, cityRegion.PixelWidth * 4, 0);
            int warmLights = 0;
            for (int i = 0; i < cityPixels.Length; i += 4)
                if (cityPixels[i + 2] > 90 && cityPixels[i + 1] > 60 && cityPixels[i] < cityPixels[i + 2] * 0.9) warmLights++;
            Check(warmLights > 500, "Rendered night view contains visible warm city lights");
            vm.SetSimulationTime(manualTime.AddHours(12)); Pump(0.1);
            SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-day.png"));
            var earthRotationNow = ((RotateTransform3D)earthSystem.Transform).Rotation as AxisAngleRotation3D;
            Check(Math.Abs(earthRotationNow!.Angle - vm.CurrentSun.EarthRotationDegrees) < 1e-10, "Earth orientation follows simulation UTC");
            var unchanged = vm.SimulationTime;
            vm.TimeInput = "2026-02-30 12:00:00"; vm.ApplyTimeCommand.Execute(null);
            Check(vm.TimeError.Length > 0 && vm.SimulationTime == unchanged, "Invalid time does not mutate scene time");
            vm.TimeInput = "2026-10-17 09:30:00"; vm.ApplyTimeCommand.Execute(null);
            Check(vm.TimeError == "" && vm.SimulationTime == new DateTimeOffset(2026, 10, 17, 0, 30, 0, TimeSpan.Zero), "KST entry converts to UTC");
            vm.SelectedOrbit = vm.Orbits[0];
            var dates = Enumerable.Range(1, 12).Select(m => new DateTimeOffset(2026, m, 15, 12, 0, 0, TimeSpan.Zero)).ToArray();
            var modelBrightness = new Dictionary<bool, double>();
            foreach (bool shadow in new[] { false, true })
            {
                var date = dates.First(d => SolarLighting.IsEclipsed(vm.SelectedOrbit.Position(0), SolarLighting.At(d)) == shadow);
                vm.SetSimulationTime(date); vm.FocusSatelliteCommand.Execute(null); Pump(0.1);
                Check(vm.SelectedEclipsed == shadow, "Satellite shadow state connected to model and details");
                var rendered = SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, shadow ? "preview-eclipse.png" : "preview-sunlit.png"));
                var center = new CroppedBitmap(rendered, new Int32Rect((int)(viewportOrigin.X + viewport.ActualWidth / 2 - 45), (int)(viewportOrigin.Y + viewport.ActualHeight / 2 - 45), 90, 90));
                byte[] bodyPixels = new byte[90 * 90 * 4]; center.CopyPixels(bodyPixels, 90 * 4, 0);
                modelBrightness[shadow] = bodyPixels.Where((_, i) => i % 4 != 3).Average(b => (double)b);
            }
            Check(modelBrightness[false] > modelBrightness[true] * 3, "Rendered eclipse satellite is darker than illuminated model");
            vm.SetSimulationTime(new DateTimeOffset(2100, 12, 31, 23, 59, 58, TimeSpan.Zero));
            vm.Speed = 600; vm.TogglePlaybackCommand.Execute(null); Pump(0.1);
            Check(vm.SimulationTime.UtcDateTime.Year == 2100 && vm.TimeError.Length > 0 && vm.PlaybackText == "재생", "Supported date boundary pauses safely");
            vm.CurrentTimeCommand.Execute(null); Pump(0.2);
            Check(vm.Speed == 1 && vm.TimeMode.Contains("실시간") && Math.Abs((vm.SimulationTime - DateTimeOffset.UtcNow).TotalSeconds) < 2, "Current time runs at real UTC and 1x");
            vm.FocusSatelliteCommand.Execute(null);
            Check(vm.PlaybackText == "일시정지" && vm.TimeMode.Contains("실시간"), "Satellite focus also preserves real-time clock playback");
            vm.TogglePlaybackCommand.Execute(null); var stoppedTime = vm.SimulationTime; Pump(0.1);
            Check(vm.SimulationTime == stoppedTime, "Paused real-time mode stays paused");
            Check(bindingErrors.ToString().Length == 0, "Time controls have no binding errors");
            var tileStation = new GroundStation("오사카", 34.6937, 135.5023); double tileLat = tileStation.Latitude * Math.PI / 180;
            var tileKey = new MapTile(12, (int)Math.Floor((tileStation.Longitude + 180) / 360 * 4096), (int)Math.Floor((1 - Math.Log(Math.Tan(tileLat) + 1 / Math.Cos(tileLat)) / Math.PI) / 2 * 4096));
            var tilePlan = MapTilePlan.Create(tileStation.Latitude, tileStation.Longitude, 0.003, 835, 485);
            Check(tilePlan.Count <= 36 && tilePlan.Any(t => t == tileKey), "Station Web Mercator tile and bounded LOD plan");
            Check(tileKey.Bounds.West < tileStation.Longitude && tileKey.Bounds.East > tileStation.Longitude && tileKey.Bounds.North > tileStation.Latitude && tileKey.Bounds.South < tileStation.Latitude, "Tile bounds contain station");
            var dateLinePlan = MapTilePlan.Create(0, 179.999, 0.003, 835, 485);
            Check(dateLinePlan.Any(t => t.X == 0) && dateLinePlan.Any(t => t.X == (1 << t.Zoom) - 1), "Tiles wrap across date line");
            try { new MapTile(20, 0, 0).Validate(); throw new Exception("Invalid tile accepted"); }
            catch (ArgumentOutOfRangeException) { }
            byte[] fixturePixels = new byte[256 * 256 * 4];
            for (int i = 0; i < fixturePixels.Length; i += 4) { fixturePixels[i] = 50; fixturePixels[i + 1] = 100; fixturePixels[i + 2] = 70; fixturePixels[i + 3] = 255; }
            var fixture = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, fixturePixels, 256 * 4);
            var jpegEncoder = new JpegBitmapEncoder(); jpegEncoder.Frames.Add(BitmapFrame.Create(fixture));
            byte[] jpeg; using (var fixtureStream = new MemoryStream()) { jpegEncoder.Save(fixtureStream); jpeg = fixtureStream.ToArray(); }
            MemoryStream TilePackage(bool corruptHash = false)
            {
                var memory = new MemoryStream();
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    string name = $"{tileKey.Zoom}-{tileKey.X}-{tileKey.Y}.jpg";
                    using (var entry = archive.CreateEntry(name).Open()) entry.Write(jpeg);
                    using var manifest = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                    manifest.Write(JsonSerializer.Serialize(new { schema = 1, tiles = new[] { new { name, bytes = jpeg.Length, sha256 = corruptHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(jpeg)) } } }));
                }
                memory.Position = 0; return memory;
            }
            using (var manager = new MapTileManager(TilePackage()))
            {
                var reads = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.GetAsync(tileKey, CancellationToken.None))).GetAwaiter().GetResult();
                Check(reads.All(bytes => bytes.SequenceEqual(jpeg)), "Concurrent read-only package requests preserve bytes");
                try { manager.GetAsync(new MapTile(12, tileKey.X - 1, tileKey.Y), CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Missing tile accepted"); }
                catch (FileNotFoundException) { }
                try { manager.GetAsync(tileKey, new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("Tile request ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            using (var manager = new MapTileManager(TilePackage(true)))
            {
                try { manager.GetAsync(tileKey, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Corrupted tile hash accepted"); }
                catch (InvalidDataException) { }
            }
            var tileImage = MapTileGeometry.Decode(jpeg);
            Check(tileImage.IsFrozen && tileImage.PixelWidth == 256, "Tile image is bounded and transferable to UI");
            var patch = MapTileGeometry.Create(tileKey, tileImage, nightMaterial);
            var patchNight = (MaterialGroup)((GeometryModel3D)patch.Children[1]).Material;
            var patchNightBrush = (ImageBrush)((EmissiveMaterial)patchNight.Children[1]).Brush;
            Check(patchNightBrush.ViewportUnits == BrushMappingMode.Absolute && ReferenceEquals(patchNightBrush.ImageSource, nightTexture), "Night UV uses absolute global domain and live shared texture");
            var patchMesh = (MeshGeometry3D)((GeometryModel3D)patch.Children[0]).Geometry;
            Check(patchMesh.IsFrozen && patchMesh.Positions.All(p => Math.Abs(((Vector3D)p).Length - 1.000005) < 1e-10), "Tile patch follows globe surface");
            Check(patch.Children.Count == 2 && patchMesh.TextureCoordinates.First() == new Point(0, 0), "Tile day and night UV layers");
            vm.SetSimulationTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(9)));
            vm.FocusStationCommand.Execute(vm.GroundStations[1]);
            var loadDeadline = Stopwatch.StartNew();
            while (!vm.TilesLoadingTask.IsCompleted && loadDeadline.Elapsed.TotalSeconds < 60) Pump(0.1);
            Check(vm.TilesLoadingTask.IsCompletedSuccessfully, "Live tile load completes without unhandled failure");
            var tileLayer = (Model3DGroup)earthSystem.Children[4];
            Check(tileLayer.Children.Count > 0, "Bundled tiles rendered for Osaka: " + vm.TileStatus);
            var sharedNightMaterial = ((GeometryModel3D)((Model3DGroup)tileLayer.Children[0]).Children[1]).Material;
            Check(tileLayer.Children.Cast<Model3DGroup>().All(group => ReferenceEquals(((GeometryModel3D)group.Children[1]).Material, sharedNightMaterial)), "Regional tiles share one dynamic night brush and material");
            Check(nightModel.Material == null && clouds.Material == null, "Detailed tiles use local night overlay without global shell obstruction");
            vm.Zoom(100000);
            loadDeadline.Restart();
            while (!vm.TilesLoadingTask.IsCompleted && loadDeadline.Elapsed.TotalSeconds < 60) Pump(0.1);
            Check(vm.CameraDirection.Length < 0.004 && vm.CameraNearPlane < 0.001, "Close regional zoom and near plane enabled");
            Check(tileLayer.Children.Count > 0 && vm.TileStatus.StartsWith("L12"), "Maximum zoom actually displays level-12 tiles: " + vm.TileStatus);
            SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-tiles-osaka.png"));
            var beforePan = vm.CameraPosition + vm.CameraDirection; vm.Rotate(300, 100);
            Check((beforePan - (vm.CameraPosition + vm.CameraDirection)).Length > 0.0001, "Detail dragging pans geographic target");
            foreach (var station in vm.GroundStations.Where(s => s.Name != "오사카"))
            {
                vm.FocusStationCommand.Execute(station); loadDeadline.Restart();
                while (!vm.TilesLoadingTask.IsCompleted && loadDeadline.Elapsed.TotalSeconds < 60) Pump(0.1);
                Check(vm.TilesLoadingTask.IsCompletedSuccessfully && tileLayer.Children.Count > 0, station.Name + " live tiles: " + vm.TileStatus);
                SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, station.Name == "대전" ? "preview-tiles-daejeon.png" : "preview-tiles-shanghai.png"));
            }
            vm.FocusStationCommand.Execute(vm.GroundStations[0]); vm.ResetCommand.Execute(null);
            Pump(0.5);
            Check(tileLayer.Children.Count == 0 && nightModel.Material != null && clouds.Material != null, "Overview cancels stale tile request and restores global layers");
            Check(bindingErrors.ToString().Length == 0, "Tile controls have no binding errors");
            var celestialDate = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
            var celestialState = CelestialPositions.At(celestialDate);
            using (var references = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ephemeris-reference.json"))))
                foreach (var reference in references.RootElement.EnumerateArray())
                {
                    int bodyId = reference.GetProperty("body").GetInt32();
                    var actual = (Vector3D)SceneGeometry.Point(bodyId == 199 ? celestialState.MercuryAu : bodyId == 299 ? celestialState.VenusAu : celestialState.MoonEarthRadii);
                    var expected = new Vector3D(reference.GetProperty("x").GetDouble(), reference.GetProperty("z").GetDouble(), -reference.GetProperty("y").GetDouble());
                    if (bodyId == 301) expected *= 149597870.7 / 6378.14;
                    double angle = Vector3D.AngleBetween(actual, expected);
                    Console.WriteLine($"JPL Horizons {bodyId}: direction error {angle:F4} degrees, distance error {Math.Abs(actual.Length / expected.Length - 1):P3}");
                    Check(angle < (bodyId == 301 ? 0.5 : 0.2) && Math.Abs(actual.Length / expected.Length - 1) < (bodyId == 301 ? 0.02 : 0.002), "Celestial direction and distance against independent Horizons fixture: " + bodyId);
                }
            foreach (int year in new[] { 2000, 2050, 2100 })
            {
                var state = CelestialPositions.At(new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero));
                Check(((Vector3D)SceneGeometry.Point(state.SunAu)).Length is > 0.98 and < 1.02, "Sun distance remains near one AU");
                Check(((Vector3D)SceneGeometry.Point(state.MoonEarthRadii)).Length is > 54 and < 66, "Moon distance remains outside Earth");
                Check(((Vector3D)SceneGeometry.Point(state.MercuryAu)).Length is > 0.45 and < 1.6, "Mercury geocentric distance range");
                Check(((Vector3D)SceneGeometry.Point(state.VenusAu)).Length is > 0.2 and < 1.8, "Venus geocentric distance range");
            }
            try { _ = CelestialPositions.At(celestialDate.AddYears(-30)); throw new Exception("Unsupported celestial date accepted"); }
            catch (ArgumentOutOfRangeException) { }
            vm.SetSimulationTime(celestialDate);
            var sunPosition = vm.Scene.Children[7].Transform.Transform(new Point3D());
            Check(vm.Scene.Children[1] is PointLight solarLight && (solarLight.Position - sunPosition).Length < 1e-10, "Solar point light coincides with visible Sun");
            Check(Math.Abs(((Vector3D)sunPosition).Length - 18) < 1e-10, "Sun moved well beyond satellite and lunar orbits");
            Check(Vector3D.AngleBetween((Vector3D)sunPosition, new Vector3D(vm.CurrentSun.InertialDirection.X, vm.CurrentSun.InertialDirection.Y, vm.CurrentSun.InertialDirection.Z)) < 1e-6, "Visible Sun follows lighting direction");
            foreach (int body in new[] { 1, 2, 3 }) CheckSunFacingHemisphere(vm, body);
            var moonPosition = vm.Scene.Children[8].Transform.Transform(new Point3D());
            Check(((Vector3D)moonPosition).Length is > 3.5 and < 4.8, "Moon uses a separate compressed display distance");
            foreach (var body in vm.Scene.Children.Skip(7).Cast<GeometryModel3D>())
                Check(body.Geometry.IsFrozen && body.Material.IsFrozen, "Celestial meshes and surfaces are reused");
            vm.FocusCelestialsCommand.Execute(null); Pump(0.1);
            Check(vm.CelestialLabels.Where(label => label.Index != 1).All(label => label.Visibility == Visibility.Visible && label.Left >= 0 && label.Top >= 0)
                && vm.CelestialLabels[1].Visibility == Visibility.Collapsed, "Solar overview labels planets and excludes Moon");
            SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-celestials.png"));
            window.Width = 900; window.Height = 650; Pump(0.1);
            Check(vm.CelestialLabels.Where(label => label.Index != 1).All(label => label.Visibility == Visibility.Visible), "Solar overview also fits minimum window");
            window.Width = 1200; window.Height = 800; Pump(0.1);
            foreach (var label in vm.CelestialLabels.Where(label => label.Index < 4))
            {
                vm.FocusCelestialCommand.Execute(label); Pump(0.1);
                var target = vm.CameraPosition + vm.CameraDirection;
                var focusedModel = label.Index == 1 ? vm.Scene.Children[8] : ((Model3DGroup)vm.MainScene).Children[label.Index == 0 ? 2 : label.Index + 1];
                var bodyPosition = focusedModel.Transform.Transform(new Point3D());
                Check((target - bodyPosition).Length < 1e-8, "Body focus follows selected celestial object: " + label.Name);
                vm.Zoom(100000);
                double focusedRadius = label.Index switch { 0 => 0.9, 1 => 0.273, 2 => 0.25, _ => 0.43 };
                Check(vm.CameraDirection.Length >= focusedRadius * 2.39, "Body zoom stays outside surface");
                vm.FocusCelestialCommand.Execute(label);
                SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-body-" + label.Index + ".png"));
            }
            vm.FocusCelestialCommand.Execute(vm.CelestialLabels[1]);
            vm.SetSimulationTime(celestialDate.AddDays(7));
            Check((vm.CameraPosition + vm.CameraDirection - moonPosition).Length > 1, "Changing date updates Moon and focused camera");
            vm.FocusGroundStationsCommand.Execute(null); Check(vm.CameraDirection.Length < 1, "Return from celestial focus to ground station view");
            vm.ResetCommand.Execute(null); Check((vm.CameraPosition + vm.CameraDirection) == new Point3D(), "Reset restores Earth center after celestial views");
            Check(bindingErrors.ToString().Length == 0, "Celestial controls and labels have no binding errors");
            vm.ResetCommand.Execute(null);
            var scaleTime = vm.SimulationTime;
            var scaleSelector = (System.Windows.Controls.ComboBox)window.FindName("ScaleModeSelector");
            scaleSelector.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, 1); Pump(0.1);
            Check(vm.ScaleModeIndex == 1 && vm.SimulationTime == scaleTime && vm.Orbits.Length == SatelliteConstellation.Count, "Scale switch preserves simulation and fleet");
            var scaledState = CelestialPositions.At(vm.SimulationTime);
            var scaledSun = vm.Scene.Children[7].Transform.Transform(new Point3D());
            Check(((Vector3D)scaledSun).Length is > 23000 and < 24000, "True-scale Sun distance is approximately one AU in Earth radii");
            var physicalSunDirection = (Vector3D)scaledSun; physicalSunDirection.Normalize();
            Check(Vector3D.AngleBetween(physicalSunDirection, new Vector3D(vm.CurrentSun.InertialDirection.X, vm.CurrentSun.InertialDirection.Y, vm.CurrentSun.InertialDirection.Z)) < 1e-6, "True scale uses one solar direction for display and lighting");
            var scaleFixedSun = vm.CurrentSun.EarthFixedDirection;
            Check(Vector3D.AngleBetween(earthSystem.Transform.Transform(new Vector3D(scaleFixedSun.X, scaleFixedSun.Y, scaleFixedSun.Z)), physicalSunDirection) < 1e-6, "Real solar direction transforms consistently into Earth-fixed night lighting");
            var scaledMoon = vm.Scene.Children[8].Transform.Transform(new Point3D());
            Check(((Vector3D)scaledMoon).Length is > 54 and < 66, "True-scale Moon is about sixty Earth radii away");
            Check((scaledSun - (Point3D)((Vector3D)SceneGeometry.Point(scaledState.SunAu) * SceneScale.Units(SceneScale.AstronomicalUnitKm))).Length < 1e-8, "AU positions use the common Earth-radius unit");
            foreach (int body in new[] { 0, 1, 2, 3 })
            {
                var model = (GeometryModel3D)vm.Scene.Children[7 + body];
                var center = model.Transform.Transform(new Point3D());
                double radius = (model.Transform.Transform(new Point3D(CelestialGeometry.Radii[body], 0, 0)) - center).Length;
                Check(Math.Abs(radius * CircularOrbit.EarthRadiusKm - new[] { 695700.0, 1737.4, 2439.4, 6051.8 }[body]) < 1e-5, "True body radius: " + body);
                vm.FocusCelestialCommand.Execute(vm.CelestialLabels[body]);
                Check(vm.CameraFarPlane > vm.CameraDirection.Length && vm.CameraNearPlane < vm.CameraDirection.Length - radius, "True body camera clipping: " + body);
                if (body > 0) CheckSunFacingHemisphere(vm, body);
                if (body == 2) { Pump(0.1); SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-real-mercury.png")); }
            }
            vm.FocusCelestialsCommand.Execute(null); Pump(0.1);
            Check(vm.CameraFarPlane > vm.CameraDirection.Length && vm.CelestialLabels.Where(label => label.Index != 1).All(label => label.Visibility == Visibility.Visible), "Actual-scale solar overview fits planets and labels");
            SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-real-overview.png"));
            foreach (int i in new[] { 0, vm.Orbits.Length - 1 })
            {
                vm.SelectedOrbit = vm.Orbits[i]; vm.FocusSatelliteCommand.Execute(null); Pump(0.1);
                var satellite = Satellite(i);
                double span = ((Model3DGroup)satellite.Children[0]).Bounds.SizeX;
                double meters = satellite.Transform.Transform(new Vector3D(span, 0, 0)).Length * CircularOrbit.EarthRadiusKm * 1000;
                Check(Math.Abs(meters - (i == 0 ? 12 : 8)) < 1e-7, "True deployed satellite span in meters");
                Check(vm.UseNearSatelliteLayer && vm.NearSatelliteScene is not null && vm.NearSatellitePlane < vm.CameraDirection.Length
                    && vm.CameraDirection.Length < 0.005 && ((Vector3D)vm.RenderCameraPosition).Length < 1e-4, "Meter-scale satellite uses rebased camera and near depth layer");
                var nearViewport = (System.Windows.Controls.Viewport3D)window.FindName("SatelliteViewport");
                var nearImage = new RenderTargetBitmap((int)nearViewport.ActualWidth, (int)nearViewport.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                nearImage.Render(nearViewport);
                var nearPixels = new byte[nearImage.PixelWidth * nearImage.PixelHeight * 4]; nearImage.CopyPixels(nearPixels, nearImage.PixelWidth * 4, 0);
                Check(nearPixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 500, "Actual meter-scale satellite is visibly rendered in its near layer");
            }
            var fleetBefore = Enumerable.Range(0, vm.Orbits.Length).Select(i => Satellite(i).Transform.Transform(new Point3D())).ToArray();
            var bodiesBefore = vm.Scene.Children.Skip(7).Select(body => body.Transform.Transform(new Point3D())).ToArray();
            var timeBeforePlayback = vm.SimulationTime;
            vm.Speed = 600;
            vm.TogglePlaybackCommand.Execute(null);
            vm.FocusSatelliteCommand.Execute(null);
            Check(vm.PlaybackText == "일시정지", "Focusing a satellite preserves active orbital playback");
            Pump(1.1);
            Check((vm.SimulationTime - timeBeforePlayback).TotalSeconds > 300, "600x playback advances simulated orbital time");
            Check(Enumerable.Range(0, vm.Orbits.Length).All(i => (Satellite(i).Transform.Transform(new Point3D()) - fleetBefore[i]).Length > 0.1),
                "All physical satellites move during playback");
            Check(Enumerable.Range(1, 3).All(i => (vm.Scene.Children[7 + i].Transform.Transform(new Point3D()) - bodiesBefore[i]).Length > 0.01),
                "Moon, Mercury and Venus positions advance during playback");
            foreach (var orbit in vm.Orbits)
                Check((SceneGeometry.Point(orbit.Position(orbit.PeriodSeconds)) - SceneGeometry.Point(orbit.Position(0))).Length < 1e-10,
                    "Satellite completes a circular revolution: " + orbit.Name);
            Check((vm.CameraPosition + vm.CameraDirection - Satellite(vm.Orbits.Length - 1).Transform.Transform(new Point3D())).Length < 1e-10,
                "True-scale camera tracks the moving Type B satellite");
            vm.TogglePlaybackCommand.Execute(null);
            scaleTime = vm.SimulationTime;
            SaveWindow(window, Path.Combine(Path.GetDirectoryName(output)!, "preview-real-satellite.png"));
            vm.Zoom(-10000); Pump(0.1);
            Check(!vm.UseNearSatelliteLayer && vm.NearSatelliteScene is null, "Zooming out returns true satellites to the main depth layer");
            vm.FocusSatelliteCommand.Execute(null); Pump(0.1);
            Check(vm.UseNearSatelliteLayer, "Refocusing restores the satellite near layer");
            var retainedOrbit = vm.SelectedOrbit;
            vm.ScaleModeIndex = 0; Pump(0.1);
            Check(!vm.IsTrueScale && scaleSelector.SelectedIndex == 0 && !vm.UseNearSatelliteLayer && vm.NearSatelliteScene is null && vm.SelectedOrbit == retainedOrbit
                && Math.Abs(vm.CameraDirection.Length - 0.55 * SceneScale.ObservationSatelliteScale) < 1e-8 && vm.SimulationTime == scaleTime, "Observation mode restores model size and focus without changing time or selection");
            vm.ResetCommand.Execute(null);
            Check(Math.Abs(((Vector3D)vm.Scene.Children[7].Transform.Transform(new Point3D())).Length - 18) < 1e-8, "Observation mode restores compressed Sun distance");
            Check(bindingErrors.ToString().Length == 0, "Scale controls and dual-depth view have no binding errors");
            Console.WriteLine("PASS: physical radii/distances, Type A 12m/Type B 8m visible rendering, rebased cameras, mode UI round trip, zoom depth transition and shared solar lighting.");
            window.Close(); string stopped = vm.ElapsedText; Pump(0.1);
            Check(vm.ElapsedText == stopped, "Closed window stops timer");
            app.Shutdown();
            Console.WriteLine("PASS: celestial Horizons reference/poses/focus/framing, WMTS/cache/cancellation/live NASA tiles/night UV, solar/clock/eclipse, rendering lifecycle and existing WPF regression. Preview: " + output);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    private static IEnumerable<GeometryModel3D> SatelliteMeshes(Model3D model)
        => model is GeometryModel3D mesh ? [mesh] : ((Model3DGroup)model).Children.SelectMany(SatelliteMeshes);

    private static void CheckSatelliteOperations()
    {
        var searchStation = new GroundStation("test", 0, 0);
        var passOrbit = new CircularOrbit("pass", 550, 0, 0, 180);
        var planner = new GroundAntennaTracking(searchStation);
        var waiting = planner.Update(passOrbit, 0, 0);
        Check(!waiting.Tracking && planner.NextPassSeconds is > 0 and < 86400 &&
            waiting == planner.Update(passOrbit, 20, 20 * SatelliteOperations.EarthRotationDegreesPerSecond),
            "Ground antenna prepoints once and waits without search rotation");
        double passTime = planner.NextPassSeconds!.Value;
        var entry = GroundAntennaTracking.Calculate(searchStation, passOrbit.Position(passTime), passTime * SatelliteOperations.EarthRotationDegreesPerSecond);
        Check(entry.Tracking && Math.Abs(entry.ElevationDegrees - 10) < 0.02 &&
            Math.Abs(entry.AzimuthDegrees - waiting.AzimuthDegrees) < 1e-8,
            "Waiting aim matches the predicted pass entry and shared elevation threshold");
        Check(planner.Update(passOrbit, passTime + 1, (passTime + 1) * SatelliteOperations.EarthRotationDegreesPerSecond).Tracking,
            "Scheduled pass transitions from waiting to live tracking");
        var polarStation = new GroundStation("no pass", 90, 0);
        var noPass = new GroundAntennaTracking(polarStation);
        var parked = noPass.Update(passOrbit, 0, 0);
        Check(!parked.Tracking && noPass.NextPassSeconds == null &&
            parked == noPass.Update(passOrbit, 30, 30 * SatelliteOperations.EarthRotationDegreesPerSecond),
            "No pass within the planning horizon leaves the antenna stationary");
        Check(planner.Update(passOrbit, 0, 0) == waiting, "Rewinding the simulation rebuilds the pass plan");
        // Repeat ground tracks: every satellite crosses the track centre at its scheduled time.
        var fleet = SatelliteConstellation.Create();
        var center = SatelliteConstellation.TrackCenter;
        Check(fleet.All(o => Enumerable.Range(0, 200).Any(k =>
        {
            double t = k * SatelliteConstellation.CycleSeconds(o.Mission) / 200;
            var time = SatelliteConstellation.ReferenceEpoch.AddSeconds(t);
            return GroundAntennaTracking.Calculate(center, o.Position(t), SolarLighting.At(time).EarthRotationDegrees).ElevationDegrees > 60;
        })), "Every satellite's repeat ground track passes over the station centre");
        // Network schedule: stations take different satellites; servo slews, locks and parks at zenith.
        var stationsNear = new[] { new GroundStation("s1", 0, 0), new GroundStation("s2", 0.5, 0.5) };
        var twoOverhead = new[] { new CircularOrbit("o1", 550, 0, 0, 0), new CircularOrbit("o2", 550, 0, 0, 1) };
        var scheduler = new StationScheduler(stationsNear);
        scheduler.Update(twoOverhead, 0, 0, -1);
        Check(scheduler.Targets.All(t => t >= 0) && scheduler.Targets[0] != scheduler.Targets[1], "Stations in view of two satellites take different ones");
        var servo = new AntennaServo();
        var goal = new GroundAntennaAim(120, 30, true);
        var first = servo.Update(0, goal, 0.1);
        Check(!first.Tracking && Math.Abs(first.ElevationDegrees - (90 - AntennaServo.ElevationRate * 0.1)) < 1e-9, "Servo slews at its rate toward a new target");
        for (int k = 0; k < 100 && !servo.Locked; k++) servo.Update(0, goal, 0.1);
        Check(servo.Locked && servo.Update(0, goal with { AzimuthDegrees = 140 }, 0).AzimuthDegrees == 140, "Servo locks and then follows directly");
        for (int k = 0; k < 100 && servo.Elevation < 90; k++) servo.Update(-1, default, 0.1);
        Check(!servo.Locked && servo.Elevation == 90, "Servo returns to zenith after the pass");
        foreach (double elevation in new[] { 9.99, 10.01, 90 })
        {
            double a = elevation * Math.PI / 180;
            var aim = GroundAntennaTracking.Calculate(searchStation, (1 + 0.1 * Math.Sin(a), 0.1 * Math.Cos(a), 0), 0);
            Check(aim.Tracking == (elevation >= SatelliteOperations.MinimumElevationDegrees), "Ground antenna shares the communications elevation threshold");
        }
        for (int step = 0; step <= 360; step++)
        {
            double a = step * Math.PI / 180;
            double angle = SatelliteArticulation.SolarArrayAngle(Math.Cos(a), Math.Sin(a), 17, false) * Math.PI / 180;
            Check(Math.Cos(angle) * Math.Cos(a) + Math.Sin(angle) * Math.Sin(a) > 1 - 1e-12,
                "Solar wing normal follows the Sun projected onto the hinge plane");
            Check(SatelliteArticulation.SolarArrayAngle(Math.Cos(a), Math.Sin(a), 17, true) == 17,
                "Eclipse holds the last solar wing angle");
            var direction = SatelliteArticulation.TypeASensorDirection(step, false);
            Check(direction.Y < -0.8 && Math.Abs(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z - 1) < 1e-12 &&
                SatelliteOperations.IntersectEarth((0, 1.11, 0), direction, out _), "Type A antenna scan remains Earthward");
        }
        Check(SatelliteArticulation.SolarArrayAngle(0, 0, -39, false) == -39 &&
            SatelliteArticulation.TypeASensorDirection(0, true) == SatelliteArticulation.TypeASensorDirection(60, true) &&
            SatelliteArticulation.TypeASensorDirection(0, false) != SatelliteArticulation.TypeASensorDirection(45, false),
            "Degenerate sunlight holds angle; Type A scanning moves and locks during capture");
        var timing = new FrameTiming();
        timing.Add(0, 1, false); timing.Add(0.02, 2, true); timing.Add(0.32, 4, false);
        Check(timing.Intervals == 2 && timing.ContactFrames == 1 && timing.Over50 == 1 && timing.Over100 == 1 && timing.Over244 == 1
            && Math.Abs(timing.MaximumMs - 300) < 1e-8 && timing.P95Ms >= 300, "Frame intervals include long stalls and skip initial timestamp");
        timing.Reset(); Check(timing.Intervals == 0 && timing.MaximumMs == 0, "Measurement resets old samples");
        var overhead = new GroundStation("overhead", 0, 0);
        var opposite = new GroundStation("opposite", 0, 180);
        var state = new SatelliteOperations([overhead, opposite]);
        var typeA = new CircularOrbit("Type A", 550, 0, 0);
        state.Update(typeA, 0, 0, true);
        Check(state.Contacts[0].Connected && Math.Abs(state.Contacts[0].ElevationDegrees - 90) < 1e-8
            && Math.Abs(state.Contacts[0].RangeKm - 550) < 1e-8, "Overhead contact elevation and range");
        Check(!state.Contacts[1].Connected && state.Contacts[1].ElevationDegrees < 0, "Earth blocks opposite ground station");
        Check(state.HasFootprint && state.Footprint.All(p => Math.Abs(p.X * p.X + p.Y * p.Y + p.Z * p.Z - 1) < 1e-10), "Type A rays intersect spherical Earth");
        double typeAOffset = Math.Abs(state.Footprint.Average(p => p.Y));
        state.Update(new CircularOrbit("Type B", 550, 0, 0, mission: SatelliteMission.B), 0, 0, true);
        Check(state.HasFootprint && Math.Abs(state.Footprint.Average(p => p.Y)) < 1e-10 && typeAOffset > 0.01, "Type B nadir and Type A side-looking footprints differ");
        state.Update(typeA, 0, 0, false);
        Check(!state.HasFootprint, "Stopping capture removes footprint");
        var rotated = SatelliteOperations.StationPosition(overhead, 90);
        Check(Math.Abs(rotated.X) < 1e-10 && Math.Abs(rotated.Z + 1) < 1e-10, "Ground station follows Earth rotation convention");
        Check(SatelliteOperations.IntersectEarth((2, 0, 0), (-1, 0, 0), out var hit) && Math.Abs(hit.X - 1) < 1e-10, "Near-side Earth ray intersection");
        Check(!SatelliteOperations.IntersectEarth((2, 0, 0), (1, 0, 0), out _) && !SatelliteOperations.IntersectEarth((2, 0, 0), (0, 1, 0), out _), "Away and missing rays rejected");
        Check(state.NextContactDelay(typeA, 0, 0, 0) == 0, "Current overhead pass needs no time jump");
        Check(new SatelliteOperations([opposite]).NextContactDelay(typeA, 0, 0, 0) is null, "No available pass in zero-length search");
        var pass = new SatelliteOperations([new GroundStation("대전", 36.3504, 127.3845)]);
        var inclined = new CircularOrbit("Type A", 550, 97.6, 0);
        var delay = pass.NextContactDelay(inclined, 0, 0, 86400);
        Check(delay is not null, "Find a sample Korean pass within one day");
        pass.Update(inclined, delay!.Value, delay.Value * 360.98564736629 / 86400, false);
        Check(pass.Contacts[0].Connected, "Search result agrees with live contact calculation");
        state.Update(typeA, 0, 0, true);
        var effects = new SatelliteEffects(2);
        Point3D[] anchors = [SceneGeometry.Point(state.Contacts[0].Position), SceneGeometry.Point(state.Contacts[1].Position)];
        effects.Update(state, anchors, true, 0, 0.9);
        Check(effects.Scene.Children.Count == 2, "Only visible contact and selected capture are displayed");
        var imaging = (Model3DGroup)effects.Scene.Children[1];
        var patch = (MeshGeometry3D)((GeometryModel3D)imaging.Children[0]).Geometry;
        Check(patch.Positions.Count == 81 && patch.Positions.All(p => ((Vector3D)p).Length > 1.008), "Footprint guide stays above cloud depth layer");
        effects.Update(state, anchors, true, 1, 0.9);
        Check(ReferenceEquals(effects.Scene.Children[1], imaging), "Animation reuses capture models");
        state.Update(typeA, 0, 0, false); effects.Update(state, anchors, false, 1, 0.9);
        Check(effects.Scene.Children.Count == 0, "Capture stop and communications toggle remove effects");
    }

    private static int CheckSceneViews(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var vm = new MainViewModel();
        string output = args.Length > 1 ? Path.GetFullPath(args[1]) : AppContext.BaseDirectory;
        Directory.CreateDirectory(output);
        vm.SetViewportSize(1000, 700);
        var date = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        vm.SetSimulationTime(date);
        vm.FocusEarthSystemCommand.Execute(null);
        var local = (Model3DGroup)vm.MainScene;
        Check(!vm.IsSolarSystemView && SceneGeometry.Contains(local, vm.Scene.Children[8])
            && !SceneGeometry.Contains(local, vm.Scene.Children[7]) && !SceneGeometry.Contains(local, vm.Scene.Children[9])
            && !SceneGeometry.Contains(local, vm.Scene.Children[10]), "Earth view contains Moon and excludes Sun/inner planets");
        Check(((Model3DGroup)vm.Scene.Children[5]).Children.Count == vm.Orbits.Length + 1, "Earth fleet has every satellite (plus the static batch)");
        vm.IsTrueScale = true;
        Check(vm.CameraDirection.Length > 150 && vm.CelestialLabels[1].Visibility == Visibility.Visible, "Scale switch keeps distant physical Moon in the local overview");
        vm.Zoom(-1000);
        Check(vm.CameraDirection.Length > 150, "Physical local view permits zooming beyond lunar distance");
        vm.IsTrueScale = false; vm.FocusEarthSystemCommand.Execute(null);
        Render("earth-local.png");
        vm.FocusCelestialsCommand.Execute(null);
        var solar = (Model3DGroup)vm.MainScene;
        Check(vm.IsSolarSystemView && solar.Children.Count == 13 && ((Model3DGroup)solar.Children[11]).Children.Count == 8,
            "Solar scene has Sun/eight planets/eight orbit guides and no Moon or fleet");
        var start = solar.Children[3].Transform.Transform(new Point3D());
        for (int year = 2000; year <= 2100; year += 5)
        {
            vm.SetSimulationTime(new DateTimeOffset(year, 6, 1, 0, 0, 0, TimeSpan.Zero));
            var state = CelestialPositions.At(vm.SimulationTime);
            var sun = SceneGeometry.Point(state.SunAu);
            var expected = new Point3D[9];
            for (int planet = 0; planet < 8; planet++) expected[planet == 0 ? 3 : planet < 3 ? planet : planet + 1] = (Point3D)((Vector3D)SceneGeometry.Point(state.HeliocentricAu[planet]) * 18);
            double[] radii = [0.9, 0.25, 0.43, 0.45, 0.32, 1.2, 1.0, 0.7, 0.68];
            for (int i = 0; i < 9; i++)
            {
                var position = solar.Children[2 + i].Transform.Transform(new Point3D());
                Check((position - expected[i]).Length < 1e-9, "Solar positions share one heliocentric distance scale");
                for (int j = 0; j < i; j++) Check((expected[i] - expected[j]).Length > radii[i] + radii[j], "Displayed solar bodies do not intersect at sampled dates");
            }
            for (int planet = 0; planet < 8; planet++)
            {
                var path = CelestialPositions.OrbitPath(planet, vm.SimulationTime).Select(SceneGeometry.Point).ToArray();
                Check((path[0] - path[^1]).Length < 1e-10 && path.All(p => double.IsFinite(p.X + p.Y + p.Z)), "Closed finite Kepler orbit path");
                var p = (Point3D)((Vector3D)expected[planet == 0 ? 3 : planet < 3 ? planet : planet + 1] / 18);
                double error = double.PositiveInfinity;
                for (int k = 0; k < path.Length - 1; k++)
                {
                    var edge = path[k + 1] - path[k];
                    double fraction = Math.Clamp(Vector3D.DotProduct(p - path[k], edge) / edge.LengthSquared, 0, 1);
                    error = Math.Min(error, (p - (path[k] + edge * fraction)).Length);
                }
                Check(error < 0.0001 * Math.Max(1, ((Vector3D)p).Length), "Current planet lies on its Kepler orbit guide");
            }
        }
        vm.SetSimulationTime(date.AddDays(30));
        Check((solar.Children[3].Transform.Transform(new Point3D()) - start).Length > 1, "Mercury advances with simulation time");
        vm.SetSimulationTime(date); Render("solar-overview.png");
        Check(vm.CelestialLabels.Where(label => label.Index != 1).All(label => label.Visibility == Visibility.Visible), "Full solar overview labels all eight planets and Sun");
        var visibleLabels = vm.CelestialLabels.Where(label => label.Visibility == Visibility.Visible).ToArray();
        for (int i = 0; i < visibleLabels.Length; i++)
            for (int j = 0; j < i; j++) Check(!new Rect(visibleLabels[i].Left, visibleLabels[i].Top, 65, 24).IntersectsWith(new Rect(visibleLabels[j].Left, visibleLabels[j].Top, 65, 24)), "Solar overview labels do not overlap");
        double fullDistance = vm.CameraDirection.Length;
        vm.FocusInnerPlanetsCommand.Execute(null);
        Check(vm.CameraDirection.Length < fullDistance / 10, "Inner planet view magnifies inner system");
        Render("inner-planets.png");
        Check(vm.CelestialLabels[1].Visibility == Visibility.Collapsed && vm.SatelliteLabels.Count == 0, "Solar labels exclude Moon and satellites");
        vm.TogglePlaybackCommand.Execute(null);
        vm.FocusEarthSystemCommand.Execute(null);
        Check(vm.PlaybackText == "일시정지" && vm.SimulationTime == date, "Switching view preserves playback and time");
        vm.FocusCelestialsCommand.Execute(null); vm.IsTrueScale = true;
        var statePhysical = CelestialPositions.At(date);
        Check(Math.Abs(((Vector3D)solar.Children[5].Transform.Transform(new Point3D())).Length - ((Vector3D)SceneGeometry.Point(statePhysical.SunAu)).Length * SceneScale.Units(SceneScale.AstronomicalUnitKm)) < 1e-7, "Solar Earth is at its physical AU distance");
        Check(Math.Abs(solar.Children[5].Transform.Transform(new Vector3D(1, 0, 0)).Length - 1) < 1e-10, "Physical Earth radius is preserved");
        double physicalSunRadius = solar.Children[2].Transform.Transform(new Vector3D(CelestialGeometry.Radii[0], 0, 0)).Length;
        double beforeSunZoom = vm.CameraDirection.Length;
        var beforeSunTime = vm.SimulationTime; string beforeSunPlayback = vm.PlaybackText;
        vm.FocusSunCommand.Execute(null);
        Check(!solar.Children.OfType<Model3DGroup>().Any(group => group.Children.Count == 8), "Hide thick orbit guides during physical closeups");
        Check(Math.Abs(solar.Children[2].Transform.Transform(new Vector3D(CelestialGeometry.Radii[0], 0, 0)).Length - physicalSunRadius) < 1e-8
            && Math.Abs(physicalSunRadius * CircularOrbit.EarthRadiusKm - 695700) < 1e-6, "Sun zoom preserves physical radius");
        Check(vm.CameraDirection.Length < beforeSunZoom / 100 && vm.SimulationTime == beforeSunTime && vm.PlaybackText == beforeSunPlayback, "Sun zoom changes camera only");
        Render("physical-sun-closeup.png");
        for (int body = 5; body <= 9; body++)
        {
            vm.FocusCelestialCommand.Execute(vm.CelestialLabels[body]);
            var model = solar.Children[body + 1];
            double radius = model.Transform.Transform(new Vector3D(CelestialGeometry.Radii[body], 0, 0)).Length;
            Check(Math.Abs(radius - SceneScale.Radius(body)) < 1e-8 && vm.CameraFarPlane > vm.CameraDirection.Length, "Outer body physical radius and camera clipping");
            if (body == 7) Render("physical-saturn-closeup.png");
        }
        using (var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "outer-ephemeris-reference.json"))))
            foreach (var row in reference.RootElement.EnumerateArray())
            {
                var state = CelestialPositions.At(DateTimeOffset.Parse(row.GetProperty("utc").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                var actual = (Vector3D)SceneGeometry.Point(state.HeliocentricAu[row.GetProperty("planetIndex").GetInt32()]);
                var xyz = row.GetProperty("au").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var expected = new Vector3D(xyz[0], xyz[2], -xyz[1]);
                double angle = Vector3D.AngleBetween(actual, expected), distanceError = Math.Abs(actual.Length / expected.Length - 1);
                Console.WriteLine($"Outer planet {row.GetProperty("planetIndex").GetInt32()}: Horizons angle {angle:F4} deg / distance {distanceError:P3}");
                Check(angle < 0.6 && distanceError < 0.01, "Independent Horizons outer planet reference");
            }
        vm.FocusEarthSystemCommand.Execute(null);
        Check(((Vector3D)vm.Scene.Children[8].Transform.Transform(new Point3D())).Length is > 54 and < 66, "Local physical Moon distance is preserved");
        vm.IsTrueScale = false;
        vm.FocusSatelliteCommand.Execute(null);
        Check(!vm.IsSolarSystemView && Math.Abs(vm.CameraDirection.Length - 0.22) < 1e-8, "Satellite closeup returns to local view at reduced size");
        Console.WriteLine("PASS: Sun zoom preserves radius; all eight planets/Kepler guides/Horizons references; all satellites and playback preserved. No window shown.");
        return 0;

        void Render(string name)
        {
            var grid = new System.Windows.Controls.Grid { Background = new SolidColorBrush(Color.FromRgb(8, 14, 28)) };
            var sky = new System.Windows.Controls.Viewport3D { Camera = new PerspectiveCamera(new Point3D(), vm.CameraDirection, new Vector3D(0, 1, 0), 42) { NearPlaneDistance = 1, FarPlaneDistance = 1000 } };
            sky.Children.Add(new ModelVisual3D { Content = vm.SkyScene }); grid.Children.Add(sky);
            var viewport = new System.Windows.Controls.Viewport3D
            {
                Camera = new PerspectiveCamera(vm.RenderCameraPosition, vm.CameraDirection, new Vector3D(0, 1, 0), 42)
                    { NearPlaneDistance = vm.CameraNearPlane, FarPlaneDistance = vm.CameraFarPlane }
            };
            viewport.Children.Add(new ModelVisual3D { Content = vm.MainScene });
            grid.Children.Add(viewport);
            var labels = new System.Windows.Controls.Canvas(); grid.Children.Add(labels);
            foreach (var label in vm.CelestialLabels.Where(label => label.Visibility == Visibility.Visible))
            {
                var text = new System.Windows.Controls.TextBlock { Text = label.Name, Foreground = label.Color, Background = new SolidColorBrush(Color.FromRgb(8, 14, 28)), FontSize = 14 };
                System.Windows.Controls.Canvas.SetLeft(text, label.Left); System.Windows.Controls.Canvas.SetTop(text, label.Top); labels.Children.Add(text);
            }
            grid.Measure(new Size(1000, 700)); grid.Arrange(new Rect(0, 0, 1000, 700)); grid.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1000, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(grid);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
            viewport.Children.Clear();
        }
    }

    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    private static void CheckSunFacingHemisphere(MainViewModel vm, int body)
    {
        var planet = (GeometryModel3D)vm.Scene.Children[7 + body];
        var center = planet.Transform.Transform(new Point3D());
        var sun = vm.Scene.Children[7].Transform.Transform(new Point3D());
        var sunward = sun - center; sunward.Normalize();
        var offset = Vector3D.CrossProduct(sunward, new Vector3D(0, 1, 0)); offset.Normalize();
        var up = Vector3D.CrossProduct(offset, sunward);
        double radius = vm.IsTrueScale ? SceneScale.Radius(body) : CelestialGeometry.Radii[body];
        var scene = new Model3DGroup();
        scene.Children.Add(vm.Scene.Children[0].CloneCurrentValue());
        scene.Children.Add(vm.Scene.Children[1].CloneCurrentValue());
        scene.Children.Add(new GeometryModel3D(planet.Geometry, new DiffuseMaterial(Brushes.White)) { Transform = planet.Transform });
        var viewport = new System.Windows.Controls.Viewport3D
        {
            Width = 128, Height = 128,
            Camera = new OrthographicCamera(center + offset * radius * 5, -offset, up, radius * 2.8)
        };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        viewport.Measure(new Size(128, 128)); viewport.Arrange(new Rect(0, 0, 128, 128)); viewport.UpdateLayout();
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport);
        var pixels = new byte[128 * 128 * 4]; bitmap.CopyPixels(pixels, 128 * 4, 0);
        double left = 0, right = 0;
        for (int y = 48; y < 80; y++)
            for (int x = 76; x < 96; x++)
            {
                right += pixels[(y * 128 + x) * 4 + 1];
                left += pixels[(y * 128 + 127 - x) * 4 + 1];
            }
        Console.WriteLine($"Body {body} solar-facing/opposite brightness: {right / 640:F1}/{left / 640:F1}");
        Check(right > left * 2 && right > 640 * 50, "Rendered hemisphere faces visible Sun: " + body);
    }
    private static RenderTargetBitmap SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file); return image;
    }
    private static void Pump(double seconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
