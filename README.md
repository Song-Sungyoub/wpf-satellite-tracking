# wpf-satellite-tracking

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF%20Viewport3D-0078D4)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)
![Offline](https://img.shields.io/badge/runtime-offline-success)

**Satellite orbits and ground-station antenna tracking, simulated with nothing but WPF's built-in `Viewport3D`** —
no Unity, no HelixToolkit, no third-party NuGet packages.

Fifty satellites fly repeat ground tracks over three ground stations; each station picks its own satellite, slews its
antenna onto it, holds the link through the pass and parks at zenith afterwards. The Solar System view is included as a
rendering stress test for `Viewport3D`. The app never touches the network at runtime; all imagery is bundled.

> **Disclaimer** — Orbits, constellation layout, ground-station locations and sensor fields of view are
> illustrative values made up for this demo. They do not describe any real mission, spacecraft or facility.

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Usage](#usage)
- [Project structure](#project-structure)
- [How it works](#how-it-works)
- [Regenerating the regional map tiles](#regenerating-the-regional-map-tiles)
- [Limitations](#limitations)
- [Credits](#credits)

---

## Features

**Earth**
- Day/night Blue Marble & Black Marble imagery with city lights, clouds, atmosphere and a star field
- Real-time Sun direction and terminator; bundled high-resolution tiles around each ground station

**Satellites (50)**
- **Type A** — 30 satellites (3 groups × 10, ~561 km, side-looking sensor)
- **Type B** — 20 satellites (2 groups × 10, ~725 km, nadir-looking sensor)
- **Repeat ground tracks**: type A repeats every day after 15 revolutions, type B every 2 days after 29.
  Every satellite's track crosses the centre of the three stations (≈ 34.1°N, 128.1°E). Orbits are referenced to a
  fixed epoch (2026-01-01 00:00 UTC), so the geometry holds on any simulated date.
- Articulated solar wings and sensor panel for the selected satellite, and for every visible satellite when you zoom in
  (8 or fewer on screen); otherwise satellites are drawn as a single static batch for performance

**Ground stations (3)** — Daejeon, Osaka, Shanghai (city-centre coordinates)
- Each station locks onto a **different** satellite above 10° elevation (keeps it until it sets; the selected satellite
  first, otherwise the highest)
- Antennas slew at a visible rate (azimuth 40°/s, elevation 25°/s), draw the link and up/down pulses once on target,
  and return to zenith after the pass
- **Coverage mode** shows each station's 10° visibility cone and ground footprint

**Solar System**
- Sun, Moon and eight planets from approximate ephemerides, with orbit guides
- Procedural surfaces: cratered Moon with relief shading, banded Jupiter with vortices (high-res map swapped in for
  close-ups), Saturn's rings, and more

**Viewer**
- True-scale and observation-scale modes, focus on satellites / stations / bodies, time travel and up to 600× playback
- Display & load toggles (orbits, ground track, labels, clouds, atmosphere, night lights, stars, Moon). Because every
  satellite has its own orbital plane, only the selected satellite's group of rings is drawn by default; "all orbit
  groups" shows all 50
- Full-screen 3D view (**F11** / **Esc**) and a built-in frame-timing meter

## Requirements

| | Version |
|---|---|
| OS | Windows 10 or 11 (x64) |
| SDK (to build) | [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) |
| GPU | Any DirectX 9-class GPU (WPF render tier 2 recommended) |

## Getting started

### Build and run from source

```powershell
git clone https://github.com/Song-Sungyoub/wpf-satellite-tracking.git
cd wpf-satellite-tracking
dotnet build .\WpfSatelliteTracking.sln -c Release
dotnet run --project .\WpfSatelliteTracking\WpfSatelliteTracking.csproj -c Release
```

Or open `WpfSatelliteTracking.sln` in Visual Studio 2022 (17.12+) and run the `WpfSatelliteTracking` project.

### Self-contained build (runs without .NET installed)

```powershell
dotnet publish .\WpfSatelliteTracking\WpfSatelliteTracking.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\Published
```

Copy the whole `Published` folder to the target PC and run `WpfSatelliteTracking.exe`.
`Directory.Build.targets` excludes the optional `createdump.exe` from the output.

## Usage

| Action | Control |
|---|---|
| Rotate / pan (regional view) | Left-drag in the 3D view |
| Zoom | Mouse wheel |
| Select a satellite | Click it, its label, or pick it from the list |
| Play / pause, reset | Bottom bar |
| Ground stations / satellite close-up | **지상 안테나 보기** / **위성 모델 보기** |
| Regional high-resolution map | Station buttons (대전 / 오사카 / 상하이) |
| Full screen | **F11** (exit with **Esc** or **F11**) |
| Set date & time (KST) | Simulation panel → 날짜·시간 설정 |

> The UI text is in Korean.

## Project structure

```
wpf-satellite-tracking/
├─ WpfSatelliteTracking.sln
├─ Directory.Build.targets         # trims createdump.exe from self-contained output
└─ WpfSatelliteTracking/           # the application
   ├─ ViewModels/MainViewModel.cs  # UI state, commands, scene assembly, per-frame update (single VM, MVVM)
   ├─ Models/                      # orbits, constellation, Sun & planets, visibility, station scheduling, map tiles
   ├─ Rendering/                   # meshes, materials, joints, batching, coverage, planet surfaces
   ├─ Assets/                      # Earth day/night maps, regional tile ZIP (embedded in the DLL)
   └─ build_assets/                # offline tile preparation script (never run by the app or the build)
```

## How it works

WPF's `Viewport3D` has no custom 3D shaders or GPU instancing, so performance comes from keeping the number of
draw calls and per-frame scene changes small:

- **Batching** — every spacecraft/antenna surface is packed into one texture atlas and one mesh
  (`SceneGeometry.MergeWithAtlas`); static satellites share one mesh per material (`SatelliteBatch`).
- **Culling & LOD** — satellites behind the camera, off screen or behind the Earth are skipped; distant ones use a
  low-detail proxy; antennas on the far side of the Earth are not drawn.
- **Baked brushes** — vector drawing brushes are rasterised once into bitmaps.
- **Background work** — night-light masks, Moon relief shading and the high-resolution Jupiter map are computed off
  the UI thread and uploaded without blocking on the render thread.

## Regenerating the regional map tiles

If you change the ground stations, edit `STATIONS` in `build_assets/prepare_offline_tiles.py`, move the existing
`Assets/offline-map-tiles.zip` away and run the script (Python 3 + Pillow). Only this step downloads from NASA GIBS.

```powershell
python .\WpfSatelliteTracking\build_assets\prepare_offline_tiles.py              # build the ZIP if missing, then verify
python .\WpfSatelliteTracking\build_assets\prepare_offline_tiles.py --verify-only  # verify only, no network
```

Rebuild the app afterwards — the ZIP is embedded as a resource.

## Limitations

- Circular two-body orbits with constructed elements (no TLE/SGP4, no J2); approximate ephemerides (UTC used for TDB)
- Generic spacecraft and antenna models (capsule-and-trunk, box bus, parabolic dish) — not CAD or real dimensions;
  enlarged in observation scale for visibility
- Greedy per-frame station assignment; no pass planning, link budget, terrain masking or drive dynamics
- No shadow maps (satellite eclipse uses a cylindrical umbra only)
- FPS is measured from WPF `CompositionTarget.Rendering` callbacks

## Credits

- Earth imagery: NASA Visible Earth — Blue Marble, Black Marble
- Regional imagery: NASA GIBS, Landsat WELD Corrected Reflectance True Color (2000 composite)
- Planetary elements: JPL, *Keplerian Elements for Approximate Positions of the Major Planets*
- Moon and planet surfaces are procedural textures generated in code
