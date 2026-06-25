"""
experiment_scale.py  —  EXPERIMENT 7: Efficiency at Scale (C4)

Two measurements, kept separate on purpose:

  PART A — Scaling:  submit independent warm batches of increasing size
           (1k..100k) and record SERVER-SIDE pipeline time (matching +
           transformation + conflict + insertion), per-phase breakdown,
           and rows/sec. The client round-trip is also recorded for
           honesty (it includes JSON serialization, which is harness
           overhead, not pipeline work).

  PART B — Amortization: submit the same schema repeatedly and compare the
           first batch (full Python matching) against later batches
           (R3 registry-reuse, Python skipped). Shows matching cost is paid
           once, then amortizes to near-zero — the architecture's core
           efficiency claim.

HONEST SCOPE: the target writer upserts row-by-row inside one shared
transaction (one commit per batch, but one MERGE + one provenance write per
row). Insertion therefore scales linearly with rows and is the dominant cost
at scale. Throughput here reflects the current writer, not an upper bound; a
set-based SqlBulkCopy writer is identified as future work.

Requires: C# API on http://localhost:5227, a TPC-H line-item CSV with >=100k rows.
"""
import argparse
import math
import time
import json
import statistics
import pandas as pd
import httpx

API_URL = "http://localhost:5227"


def safe(v):
    if v is None:
        return None
    if isinstance(v, float) and math.isnan(v):
        return None
    return v


def to_rows(df):
    return [{c: safe(row[c]) for c in df.columns} for _, row in df.iterrows()]


def submit(source_id, rows, target):
    t0 = time.monotonic()
    try:
        r = httpx.post(f"{API_URL}/api/integration/submit", json={
            "sourceId": source_id,
            "sourceTable": f"src_{source_id}",
            "targetTable": target,
            "sourceRows": rows,
            "resolutionPolicy": "LastWriteWins",
            "runMode": "Incremental",
        }, timeout=1800.0)
        ms = int((time.monotonic() - t0) * 1000)
        res = r.json() if r.status_code == 200 else {"status": "Failed", "error": r.text[:300]}
        res["client_latency_ms"] = ms
        return res
    except Exception as e:
        return {"status": "Failed", "error": str(e), "client_latency_ms": 0}


def parse(res):
    m = res.get("metrics") or {}
    return {
        "status":       res.get("status", "?"),
        "client_ms":    res.get("client_latency_ms", 0),
        "inserted":     res.get("rowsInserted", 0),
        "updated":      res.get("rowsUpdated", 0),
        "skipped":      res.get("rowsSkipped", 0),
        "match_ms":     m.get("matchingTimeMs", 0),
        "transform_ms": m.get("transformationTimeMs", 0),
        "conflict_ms":  m.get("conflictDetectionTimeMs", 0),
        "insert_ms":    m.get("insertionTimeMs", 0),
        "error":        res.get("error"),
    }


def server_total(p):
    """The complete per-batch pipeline: the four phases that scale with data."""
    return p["match_ms"] + p["transform_ms"] + p["conflict_ms"] + p["insert_ms"]


def health():
    try:
        h = httpx.get(f"{API_URL}/api/integration/health", timeout=5)
        print(f"C# API: {h.json().get('status', '?')}")
        return True
    except Exception:
        print("ERROR: C# API not running on 5227")
        return False


# ─────────────────────────────────────────────────────────────────────
# PART A — Scaling
# ─────────────────────────────────────────────────────────────────────
def run_scaling(csv_path, source_id, target, sizes, repeats):
    print("=" * 86)
    print("PART A — SCALING (warm: schema already registered; server-side pipeline time)")
    print("=" * 86)
    df = pd.read_csv(csv_path)
    total = len(df)
    print(f"Loaded {total:,} rows from {csv_path}")

    # Warm-up so the schema is registered and matching is R3-reused below.
    print("Warming up (registering schema)...")
    _ = parse(submit(source_id, to_rows(df.iloc[:100]), target))

    rows_out = []
    for n in sizes:
        if n > total:
            print(f"  skip {n:,} (file only has {total:,} rows)")
            continue
        server_list, client_list, phases = [], [], []
        for _ in range(repeats):
            batch = to_rows(df.iloc[:n])
            p = parse(submit(source_id, batch, target))
            if p["status"] != "Success":
                print(f"  rows={n:,}  FAILED: {p.get('error')}")
                break
            server_list.append(server_total(p))
            client_list.append(p["client_ms"])
            phases.append(p)
        if not phases:
            continue
        server_ms = statistics.mean(server_list)
        client_ms = statistics.mean(client_list)
        rps = n / (server_ms / 1000) if server_ms else 0
        mm = statistics.mean(x["match_ms"]     for x in phases)
        tm = statistics.mean(x["transform_ms"] for x in phases)
        cm = statistics.mean(x["conflict_ms"]  for x in phases)
        im = statistics.mean(x["insert_ms"]    for x in phases)
        rows_out.append({"rows": n,
                         "server_ms": round(server_ms), "client_ms": round(client_ms),
                         "rows_per_sec": round(rps),
                         "match_ms": round(mm), "transform_ms": round(tm),
                         "conflict_ms": round(cm), "insert_ms": round(im)})
        print(f"  rows={n:>7,}  server={server_ms:>8.0f}ms  client={client_ms:>8.0f}ms  "
              f"{rps:>7.0f} rows/s | match={mm:.0f} transform={tm:.0f} "
              f"conflict={cm:.0f} insert={im:.0f}")
    return rows_out


# ─────────────────────────────────────────────────────────────────────
# PART B — Amortization
# ─────────────────────────────────────────────────────────────────────
def run_amortization(csv_path, source_id, target, batch_size, n_batches):
    print("\n" + "=" * 86)
    print("PART B — MATCHING AMORTIZATION (same schema, repeated batches)")
    print("=" * 86)
    df = pd.read_csv(csv_path)
    rows = to_rows(df.iloc[:batch_size])

    match_times = []
    for i in range(n_batches):
        p = parse(submit(source_id, rows, target))
        match_times.append(p["match_ms"])
        tag = "FULL MATCH" if i == 0 else "reuse"
        print(f"  batch {i+1:>2}  match={p['match_ms']:>6}ms  ({tag})")

    first = match_times[0]
    reuse = match_times[1:] if len(match_times) > 1 else []
    print("-" * 86)
    print(f"First-batch matching (full Python): {first} ms")
    if reuse:
        avg = statistics.mean(reuse)
        speedup = first / max(avg, 0.001)
        print(f"Avg reuse-batch matching (R3 skip): {avg:.1f} ms  (~{speedup:.0f}x faster)")
    return {"first_match_ms": first, "reuse_match_ms": reuse}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default="tpch_data/lineitem_100k.csv")
    ap.add_argument("--source-id", default="scale_test")
    ap.add_argument("--target", default="unified_lineitem")
    ap.add_argument("--sizes", default="1000,5000,10000,25000,50000,100000")
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--amort-batch", type=int, default=10000)
    ap.add_argument("--amort-count", type=int, default=6)
    args = ap.parse_args()

    if not health():
        return

    sizes = [int(s) for s in args.sizes.split(",") if s.strip()]
    print(f"\nReset {args.source_id} + clear target first, then point C# at the target.")
    print(f"  ITCDI_Registry: DELETE rows WHERE source_id='{args.source_id}'")
    print(f"    from mapping_registry, source_schema_snapshot, drift_log,")
    print(f"    integration_run, integration_provenance, conflict_log")
    print(f"  TargetDB: clear / truncate {args.target}")
    input("Ready? Press Enter...")

    scaling = run_scaling(args.csv, args.source_id, args.target, sizes, args.repeats)
    amort = run_amortization(args.csv, "amort_test", args.target,
                             args.amort_batch, args.amort_count)

    with open("results_scale.json", "w") as f:
        json.dump({"scaling": scaling, "amortization": amort}, f, indent=2)
    print("\nSaved results_scale.json")


if __name__ == "__main__":
    main()