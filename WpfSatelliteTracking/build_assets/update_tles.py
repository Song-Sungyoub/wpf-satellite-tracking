"""Refresh the offline real-TLE snapshot from CelesTrak (run explicitly).

Selection: LEO and geostationary weather satellites, plus a few GPS, Galileo and QZSS
navigation satellites for medium/inclined geosynchronous orbits. All records must share
a 7-day epoch window (the app's extrapolation guard).
"""
import datetime
import json
from pathlib import Path
import urllib.request

BASE = "https://celestrak.org/NORAD/elements/gp.php?GROUP={}&FORMAT=tle"
WINDOW_DAYS = 6.5


def fetch(group):
    url = BASE.format(group)
    lines = [line.strip() for line in urllib.request.urlopen(url, timeout=30).read().decode("ascii").splitlines() if line.strip()]
    if len(lines) % 3:
        raise ValueError(f"{group}: expected named three-line TLE records")
    records = []
    for i in range(0, len(lines), 3):
        name, first, second = lines[i:i + 3]
        for number, line in [("1", first), ("2", second)]:
            if len(line) != 69 or line[0] != number:
                raise ValueError("Unexpected TLE format")
            checksum = sum(int(c) if c.isdigit() else 1 if c == "-" else 0 for c in line[:68]) % 10
            if checksum != int(line[68]):
                raise ValueError("TLE checksum mismatch")
        if first[2:7] != second[2:7]:
            raise ValueError("Mismatched satellite IDs")
        records.append([name, first, second])
    return url, records


def mean_motion(record):
    return float(record[2][52:63])


def eccentricity(record):
    return float("0." + record[2][26:33])


def epoch(record):
    year = int(record[1][18:20]); day = float(record[1][20:32])
    return datetime.datetime(2000 + year if year < 57 else 1900 + year, 1, 1, tzinfo=datetime.timezone.utc) + datetime.timedelta(days=day - 1)


def main():
    sources = {}
    url, weather = fetch("weather"); sources["weather"] = url
    url, gnss = fetch("gnss"); sources["gnss"] = url
    leo = [r for r in weather if mean_motion(r) > 10][:40]
    geo = [r for r in weather if 0.9 < mean_motion(r) < 1.1 and eccentricity(r) < 0.01][:12]
    meo = [r for r in gnss if r[0].startswith("GPS")][:6] + [r for r in gnss if "GALILEO" in r[0]][:4]
    igso = [r for r in gnss if r[0].startswith("QZS")][:3]
    records, seen = [], set()
    for record in leo + geo + meo + igso:
        if record[1][2:7] not in seen:
            seen.add(record[1][2:7]); records.append(record)
    newest = max(epoch(r) for r in records)
    records = [r for r in records if (newest - epoch(r)).total_seconds() <= WINDOW_DAYS * 86400]
    if not records or len(records) > 200:
        raise ValueError("Unexpected record count")
    output = Path(__file__).resolve().parents[1] / "Assets"
    data = "\n".join(line for record in records for line in record) + "\n"
    temporary = output / "satellites.tle.tmp"
    temporary.write_text(data, encoding="ascii")
    temporary.replace(output / "satellites.tle")
    counts = {"leo": sum(r in records for r in leo), "geo": sum(r in records for r in geo), "meo": sum(r in records for r in meo), "igso": sum(r in records for r in igso)}
    (output / "tle-source.json").write_text(json.dumps({
        "sources": sources,
        "retrievedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "selection": "weather LEO (mean motion > 10 rev/day, first 40), weather GEO (first 12), GPS 6, Galileo 4, QZSS 3; epochs within 6.5 days",
        "counts": counts,
        "count": len(records)}, indent=2), encoding="utf-8")
    print(f"Updated {len(records)} TLEs {counts}. Rebuild the app to update the embedded snapshot.")


if __name__ == "__main__":
    main()
