#!/usr/bin/env python3
"""
Route matching accuracy check (Epic 12, story 12.4), run against the live database
and the live engine. It prints AGGREGATES ONLY: no coordinate, id or time leaves it,
and it writes nothing to disk or to the database.

Why a script and not a committed fixture: the Owner's tracks must never be
committed to the repository, so the 30-drive reference fixture of the epic is
replaced by this check. Instead of hand-drawn reference lines it uses the Owner's
own DENSE drive recordings (Sports Tracker, Google Fit, GPX: at least --min-per-km
points per km) as ground truth:

  1. take N dense drives (default 30);
  2. thin each to a Timeline-like trace (a point every ~440 m and >= 60 s, the
     production median gap; see the epic's problem table);
  3. match the thinned trace with the same request as the service (auto costing,
     map_snap, Timeline gps_accuracy 75 m, search_radius 100 m, breakage 5 km, timestamps);
  4. measure the share of the matched line within 30 m of the dense recording.

Pass criterion (epic 12.4): for >= 90% of the drives the engine matched, >= 90% of
the matched line lies within 30 m of the reference.

How to run (the engine listens on the routes pod's loopback only; a port-forward
reaches it inside the pod's network namespace, and nothing crosses the egress policy):

    kubectl -n contacts port-forward deploy/contacts-routes 18002:8002 &
    kubectl -n contacts port-forward svc/contacts-postgresql 15432:5432 &
    pip install "psycopg[binary]"
    ATLAS_DSN="host=127.0.0.1 port=15432 dbname=contactsdb user=... password=..." \
    VALHALLA_URL=http://127.0.0.1:18002 \
        python scripts/routes/accuracy-check.py --owner <owner id> [--drives 30]

The owner id and the DSN come from the operator; read-only access is enough.
"""
import argparse
import json
import math
import os
import statistics
import sys
import time
import urllib.error
import urllib.request

R = 6_371_000.0


def haversine(a, b):
    p1, p2 = math.radians(a[0]), math.radians(b[0])
    dp, dl = p2 - p1, math.radians(b[1] - a[1])
    h = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * R * math.asin(math.sqrt(h))


def decode(encoded, precision):
    f = 10 ** precision
    idx = lat = lon = 0
    out = []
    while idx < len(encoded):
        for which in (0, 1):
            shift = res = 0
            while True:
                b = ord(encoded[idx]) - 63
                idx += 1
                res |= (b & 0x1F) << shift
                shift += 5
                if b < 0x20:
                    break
            d = ~(res >> 1) if res & 1 else res >> 1
            if which == 0:
                lat += d
            else:
                lon += d
        out.append((lat / f, lon / f))
    return out


def distance_to_line(p, line):
    kx = math.cos(math.radians(p[0])) * 111_320
    ky = 110_540
    best = float("inf")
    for (alat, alon), (blat, blon) in zip(line, line[1:]):
        ax, ay = (alon - p[1]) * kx, (alat - p[0]) * ky
        bx, by = (blon - p[1]) * kx, (blat - p[0]) * ky
        dx, dy = bx - ax, by - ay
        l2 = dx * dx + dy * dy
        t = max(0.0, min(1.0, -(ax * dx + ay * dy) / l2)) if l2 > 0 else 0.0
        best = min(best, math.hypot(ax + t * dx, ay + t * dy))
    return best


def thin(points, every_m=440, every_s=60):
    """A Timeline-like trace: keep a point when both ~every_m and every_s have passed."""
    out = [points[0]]
    for p in points[1:]:
        if haversine(out[-1][1:], p[1:]) >= every_m and (p[0] - out[-1][0]) >= every_s:
            out.append(p)
    if out[-1] is not points[-1]:
        out.append(points[-1])
    return out


def match(url, trace, timeout):
    body = {
        "shape": [{"lat": round(lat, 6), "lon": round(lon, 6), "time": int(t)} for t, lat, lon in trace],
        "costing": "auto",
        "shape_match": "map_snap",
        "use_timestamps": True,
        "trace_options": {"search_radius": 100, "gps_accuracy": 75, "breakage_distance": 5000},
        "filters": {"attributes": ["shape", "matched.type"], "action": "include"},
    }
    req = urllib.request.Request(url.rstrip("/") + "/trace_attributes", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--owner", required=True)
    ap.add_argument("--drives", type=int, default=30)
    ap.add_argument("--min-per-km", type=float, default=20.0, help="dense enough to serve as the reference")
    ap.add_argument("--timeout", type=float, default=15.0)
    ap.add_argument("--pause", type=float, default=1.0, help="seconds between engine calls (AD-36)")
    args = ap.parse_args()

    import psycopg  # imported here so --help works without it

    dsn = os.environ["ATLAS_DSN"]
    url = os.environ.get("VALHALLA_URL", "http://127.0.0.1:18002")
    with psycopg.connect(dsn) as conn:
        conn.read_only = True
        ids = [r[0] for r in conn.execute(
            """SELECT a."Id" FROM atlas.activities a
                WHERE a."OwnerId" = %s AND a."Type" = 'drive' AND a."Source" <> 'google-timeline'
                  AND a."PointCount" >= 50 AND a."DistanceM" > 2000
                  AND a."PointCount" / (a."DistanceM" / 1000.0) >= %s
                ORDER BY a."StartTime" DESC LIMIT %s""",
            (args.owner, args.min_per_km, args.drives * 2)).fetchall()]
        results, failures, timings = [], 0, []
        for activity_id in ids:
            if len(results) + failures >= args.drives:
                break
            rows = conn.execute(
                """SELECT extract(epoch FROM "Time"), "Lat", "Lon" FROM atlas.track_points
                    WHERE "ActivityId" = %s ORDER BY "Time" """, (activity_id,)).fetchall()
            dense = [(float(t), float(lat), float(lon)) for t, lat, lon in rows]
            trace = thin(dense)
            if len(trace) < 3 or len(trace) > 2000:
                continue
            started = time.monotonic()
            try:
                answer = match(url, trace, args.timeout)
            except (urllib.error.URLError, TimeoutError):
                failures += 1
                continue
            finally:
                timings.append(time.monotonic() - started)
                time.sleep(args.pause)
            labels = [m.get("type") for m in answer.get("matched_points", [])]
            matched_share = sum(1 for t in labels if t in ("matched", "interpolated")) / max(1, len(labels))
            line = decode(answer.get("shape", ""), 6)
            reference = [(lat, lon) for _, lat, lon in dense]
            # Sample the matched line every ~20 m so long straight edges count by length
            samples = []
            for a, b in zip(line, line[1:]):
                n = max(1, int(haversine(a, b) // 20))
                samples += [(a[0] + (b[0] - a[0]) * k / n, a[1] + (b[1] - a[1]) * k / n) for k in range(n)]
            near = sum(1 for p in samples if distance_to_line(p, reference) <= 30) / max(1, len(samples))
            results.append({"matched": matched_share >= 0.7, "near": near, "points": len(trace)})

    matched = [r for r in results if r["matched"]]
    good = [r for r in matched if r["near"] >= 0.9]
    report = {
        "drives_checked": len(results),
        "engine_failures": failures,
        "matched": len(matched),
        "matched_with_90pct_within_30m": len(good),
        "pass_rate": round(len(good) / len(matched), 3) if matched else None,
        "pass": bool(matched) and len(good) / len(matched) >= 0.9,
        "near_share_median": round(statistics.median(r["near"] for r in matched), 3) if matched else None,
        "trace_points_median": statistics.median(r["points"] for r in results) if results else None,
        "seconds_per_call_median": round(statistics.median(timings), 3) if timings else None,
        "seconds_per_call_p95": round(sorted(timings)[int(0.95 * (len(timings) - 1))], 3) if timings else None,
    }
    print(json.dumps(report, indent=2))
    return 0 if report["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
