"""Prepare reviewed local map data; never called by the WPF application.

Checks JPEG decoding/dimensions and SHA-256, not antivirus safety.
Existing application cache is read only. Only NASA GIBS is contacted.
"""
import concurrent.futures
import hashlib
import io
import json
import math
import os
from pathlib import Path
import sys
import urllib.request
import urllib.parse
import zipfile
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / "Assets" / "offline-map-tiles.zip"
CACHE = Path(os.environ["LOCALAPPDATA"]) / "WpfSatelliteTracking/Tiles/landsat-weld-2000"
STATIONS = [("대전", 36.3504, 127.3845), ("오사카", 34.6937, 135.5023), ("상하이", 31.2304, 121.4737)]
LAYER = "Landsat_WELD_CorrectedReflectance_TrueColor_Global_Annual"
LIMIT = 1024 * 1024

def validate(data):
    if not 2 < len(data) <= LIMIT or not data.startswith(b"\xff\xd8") or not data.endswith(b"\xff\xd9"):
        raise ValueError("Invalid bounded JPEG")
    with Image.open(io.BytesIO(data)) as image:
        if image.format != "JPEG" or image.size != (256, 256):
            raise ValueError("Expected a 256x256 JPEG tile")
        image.load()

def plan(lat, lon, distance, width, height):
    cosine = math.cos(math.radians(lat))
    span = 2 * distance * 6371 * 1000 * math.tan(math.radians(21)) * 1.5
    zoom = min(12, max(5, math.ceil(math.log2(156543.033928 * cosine / (span / width)))))
    while zoom > 5 and span * max(1, height / width) / (40075016.686 * cosine / 2**zoom) > 4:
        zoom -= 1
    n = 2**zoom
    tx = ((lon + 180) % 360) / 360 * n
    ty = (1 - math.log(math.tan(math.radians(lat)) + 1 / cosine) / math.pi) / 2 * n
    meters = 40075016.686 * cosine / n
    rx, ry = span / meters / 2 + .25, span * height / width / meters / 2 + .25
    return {(zoom, x % n, y) for y in range(max(0, math.floor(ty-ry)), min(n-1, math.floor(ty+ry))+1)
            for x in range(math.floor(tx-rx), math.floor(tx+rx)+1)}

def required_tiles():
    tiles = set()
    for _, lat, lon in STATIONS:
        for distance in (.09, .05, .02, .012, .008, .004, .003):
            for width in (640, 1280, 2560):
                tiles.update(plan(lat, lon, distance, width, width * .7))
        for z in range(5, 13):
            n = 2**z
            x = math.floor((lon + 180) / 360 * n)
            y = math.floor((1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * n)
            tiles.update((z, (x+dx) % n, y+dy) for dx in (-1, 0, 1) for dy in (-1, 0, 1) if 0 <= y+dy < n)
    return sorted(tiles)

def acquire(tile):
    z, x, y = tile
    name = f"{z}-{x}-{y}.jpg"
    url = f"https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/{LAYER}/default/2000-12-01/GoogleMapsCompatible_Level12/{z}/{y}/{x}.jpeg"
    cached = CACHE / name
    if cached.is_file() and cached.stat().st_size <= LIMIT:
        data = cached.read_bytes()
        try:
            validate(data)
            return name, data, url, "existing-cache"
        except (ValueError, OSError):
            pass
    request = urllib.request.Request(url, headers={"User-Agent": "WpfSatelliteTracking-offline-data-preparation/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        if urllib.parse.urlparse(response.url).hostname != "gibs.earthdata.nasa.gov":
            raise ValueError("Unexpected download host")
        if response.headers.get_content_type() != "image/jpeg":
            raise ValueError("Unexpected content type")
        data = response.read(LIMIT + 1)
    validate(data)
    return name, data, url, "downloaded"

def verify_package():
    with zipfile.ZipFile(PACKAGE) as archive:
        if archive.testzip() is not None:
            raise ValueError("Invalid ZIP CRC")
        manifest = json.loads(archive.read("manifest.json"))
        for item in manifest["tiles"]:
            data = archive.read(item["name"])
            validate(data)
            if hashlib.sha256(data).hexdigest() != item["sha256"] or len(data) != item["bytes"]:
                raise ValueError("Tile hash/length mismatch")
        required = {f"{z}-{x}-{y}.jpg" for z, x, y in required_tiles()}
        if not required.issubset(archive.namelist()):
            raise ValueError("Station coverage is incomplete")
    print(f"PASS: {len(required)} tile JPEG decodes, dimensions, hashes, ZIP CRC and station coverage; no network.", flush=True)

if "--verify-only" in sys.argv or PACKAGE.exists():
    verify_package()
else:
    tiles = required_tiles()
    print(f"Preparing {len(tiles)} tiles for three station regions, levels 5..12", flush=True)
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(acquire, tile) for tile in tiles]
        for future in concurrent.futures.as_completed(futures):
            results.append(future.result())
            if len(results) % 20 == 0:
                print(f"Validated {len(results)}/{len(tiles)}", flush=True)
    manifest = {"schema": 1, "layer": LAYER, "imageryDate": "2000-12-01", "stations": STATIONS,
                "validation": "JPEG full decode 256x256, SHA-256, ZIP CRC; not an antivirus scan", "tiles": []}
    with zipfile.ZipFile(PACKAGE, "x", compression=zipfile.ZIP_STORED) as archive:
        for name, data, url, origin in sorted(results):
            archive.writestr(name, data)
            manifest["tiles"].append({"name": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "source": url, "origin": origin})
        archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    print(f"Generated {PACKAGE.name}: {PACKAGE.stat().st_size} bytes; reused {sum(r[3] == 'existing-cache' for r in results)} cached tiles", flush=True)
    verify_package()
