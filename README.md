# WPF Satellite Tracking

.NET 9 / Windows WPF Viewport3D demo of satellite orbits and ground-station antenna tracking. Real public TLEs are propagated with **SGP.NET 1.6.0**, fully offline at runtime. No Unity, HelixToolkit or custom shaders.

## Tracking data

`WpfSatelliteTracking/Assets/satellites.tle` contains 50 LEO weather satellites from [CelesTrak](https://celestrak.org/NORAD/elements/gp.php?GROUP=weather&FORMAT=tle), retrieved 2026-10-20 (metadata in `Assets/tle-source.json`). It is a snapshot, not a live feed or a guarantee that a spacecraft is active.

- **TLE 불러오기** validates another 2-line/3-line TLE file and opens it in a new app window.
- Alternatively place `satellites.tle` next to the executable, or run `WpfSatelliteTracking.exe --tle path/to/catalog.tle`.
- Checksums, catalog IDs, duplicates, count (1–200) and epoch spread are validated.
- Refresh the bundled snapshot with `python WpfSatelliteTracking/build_assets/update_tles.py`.

Simulation time is limited to the common ±7-day window of the loaded TLEs. This is an extrapolation guard, **not an accuracy guarantee**; use recent elements for pass predictions.

## Coordinate conventions

- SGP4 takes UTC and returns TEME kilometers (WGS84 constants in SGP.NET).
- TEME maps to the right-handed WPF scene as `(X, Z, -Y) / 6378.137`; north is +Y.
- Vallado GMST rotates TEME to pseudo Earth-fixed (PEF). UTC approximates UT1; polar motion and Earth-orientation data are not applied, so this is not a full ITRF transform.
- Stations use WGS84 geodetic coordinates; look angles use the geodetic local normal. Station coordinates are city centres for illustration, not facility locations.
- Sun/planet positions, coverage cones and spacecraft meshes are illustrative. The two satellite meshes are generic display models, **not** the real spacecraft geometry.

## Using the app

Side panel:

- **시간**: simulation time, real-time button, playback speed, and a KST date/time input.
- **위성**: satellite list and selected satellite summary (NORAD ID, altitude, inclination, TLE epoch age, sunlit/eclipsed).
- **지상국 추적**: per-station status for Daejeon, Osaka and Shanghai; link/pulse and coverage toggles; next-pass prediction for the selected satellite; jump to the next contact.
- **표시**: selected-satellite orbit, ground tracks with 10-minute KST time labels, all-satellite ground tracks, labels, Earth-fixed camera, and Earth environment layers.
- **예측 검증** (collapsed): pin a forecast ground point at a future time and replay to that exact moment.
- **태양계 · 성능 테스트** (collapsed): scale modes, Solar System views, articulation toggle and frame timing. The Solar System exists only as a rendering stress test.

Viewport:

- **지상 안테나 보기**: regional overview; **안테나 모델 보기**: dish close-up; city buttons: detailed offline map.
- **위성 모델 보기**: follows the selected spacecraft.
- Mouse drag rotates (pans in a regional close-up); wheel zooms. F11 toggles full-screen 3D; Esc exits.

Ground network: each station tracks a different satellite above a 10° mask, keeps its lock while visible, slews at a fixed rate and parks at zenith when idle. Links are drawn only once the antenna is on target. This is greedy scheduling, not a mission planner.

By default the selected satellite shows its future one-revolution orbit in inertial space at its altitude, regenerated from SGP4 as it advances. The optional green ground track projects the same future path onto the Earth; each sample uses its own future GMST.

## Build

```powershell
dotnet build WpfSatelliteTracking.sln -c Release
dotnet run --project WpfSatelliteTracking -c Release
```

On machines with Smart App Control in enforcement mode, unsigned local builds can be blocked per binary; trusted Authenticode signing is the supported fix.

## Licenses and imagery

SGP.NET: MIT, see `Licenses/SGP.NET.txt` and https://github.com/parzivail/SGP.NET. Earth imagery: NASA Blue Marble / Black Marble / GIBS (Landsat/WELD); regional imagery is historical, not live.
