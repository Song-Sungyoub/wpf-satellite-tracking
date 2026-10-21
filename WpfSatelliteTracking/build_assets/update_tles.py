"""Refresh the offline real-TLE snapshot from CelesTrak (run explicitly)."""
import datetime
import json
from pathlib import Path
import urllib.request

URL = "https://celestrak.org/NORAD/elements/gp.php?GROUP=weather&FORMAT=tle"

def main():
    lines = [line.strip() for line in urllib.request.urlopen(URL, timeout=30).read().decode("ascii").splitlines() if line.strip()]
    if len(lines) % 3:
        raise ValueError("Expected named three-line TLE records")
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
        if float(second[52:63]) > 10:
            records.append([name, first, second])
    records = records[:50]
    if not records:
        raise ValueError("No LEO weather satellites returned")
    output = Path(__file__).resolve().parents[1] / "Assets"
    data = "\n".join(line for record in records for line in record) + "\n"
    temporary = output / "satellites.tle.tmp"
    temporary.write_text(data, encoding="ascii")
    temporary.replace(output / "satellites.tle")
    (output / "tle-source.json").write_text(json.dumps({"source": URL, "retrievedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "selection": "weather, mean motion > 10 rev/day, first 50", "count": len(records)}, indent=2), encoding="utf-8")
    print(f"Updated {len(records)} TLEs. Rebuild the app to update the embedded snapshot.")

if __name__ == "__main__":
    main()
