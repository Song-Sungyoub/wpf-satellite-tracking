# WPF Satellite Tracking

.NET 9 / Windows WPF Viewport3D demo of satellite orbits and ground-station antenna tracking. Real public TLEs are propagated with **SGP.NET 1.6.0**, fully offline at runtime. No Unity, HelixToolkit or custom shaders.

## Tracking data

`WpfSatelliteTracking/Assets/satellites.tle` contains 65 satellites from [CelesTrak](https://celestrak.org/) retrieved 2026-10-21 (metadata in `Assets/tle-source.json`): 40 LEO and 12 geostationary weather satellites (`GROUP=weather`), plus 6 GPS, 4 Galileo and 3 QZSS navigation satellites (`GROUP=gnss`) for medium and geosynchronous orbits. It is a snapshot, not a live feed or a guarantee that a spacecraft is active.

Satellites are grouped by mean altitude/period into LEO (< 2,000 km), MEO and GEO (geosynchronous period, including inclined QZSS orbits).

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
- **위성**: checkable satellite list with **All** and LEO/MEO/GEO group toggles (unchecked satellites are hidden and not tracked; the selected one is always shown), and the selected satellite summary (orbit class, NORAD ID, altitude, inclination, TLE epoch age, sunlit/eclipsed).
- **지상국 추적**: per-station status for Daejeon, Osaka and Shanghai (S or S·X contact); link/pulse and coverage toggles (one color per station); next-pass prediction with S- and X-band windows for the selected satellite; jump to the next contact.
- **표시**: orbit and ground track (10-minute KST time labels) for the selected satellite or, with **모든 위성에 적용**, for all satellites; labels, Earth-fixed camera, and Earth environment layers.
- **예측 검증** (collapsed): pin a forecast ground point at a future time and replay to that exact moment.
- **태양계 · 성능 테스트** (collapsed): scale modes, Solar System views, articulation toggle and frame timing. The Solar System exists only as a rendering stress test.

Viewport:

- **지상 안테나 보기**: regional overview; **안테나 모델 보기**: dish close-up; city buttons: detailed offline map.
- **위성 모델 보기**: follows the selected spacecraft.
- Mouse drag rotates (pans in a regional close-up); wheel zooms. F11 toggles full-screen 3D; Esc exits.

Ground network: each station tracks a different satellite, keeps its lock while visible, slews at a fixed rate and parks at zenith when idle. Checked LEO satellites are tracked automatically; MEO/GEO satellites stay in view for hours, so they are tracked only when selected. Links are drawn only once the antenna is on target. This is greedy scheduling, not a mission planner.

Radio bands are an **illustrative rule applied alike to every satellite** (real band assignments are not in TLEs): S-band TT&C from 5° elevation (link line with white/blue pulses) and X-band data downlink from 10° (gold pulses). No frequencies, data rates or link budgets are modelled. Coverage cones use the 10° mask up to the highest LEO shell.

By default the selected satellite shows its future one-revolution path at orbit altitude in **Earth-fixed** coordinates, regenerated from SGP4 as it advances: it rotates with the Earth and lies directly above the ground track (a geostationary satellite stays near one point). Turning off **지구 기준** draws the inertial orbit instead, a ring fixed in space while the Earth rotates beneath it, so it drifts away from the ground track. The optional green ground track projects the same future path onto the Earth; each sample uses its own future GMST. Paths are built on a background thread.

## Build

```powershell
dotnet build WpfSatelliteTracking.sln -c Release
dotnet run --project WpfSatelliteTracking -c Release
```

On machines with Smart App Control in enforcement mode, unsigned local builds can be blocked per binary; trusted Authenticode signing is the supported fix.

## Licenses and imagery

SGP.NET: MIT, see `Licenses/SGP.NET.txt` and https://github.com/parzivail/SGP.NET. Earth imagery: NASA Blue Marble / Black Marble / GIBS (Landsat/WELD); regional imagery is historical, not live.
