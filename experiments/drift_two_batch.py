"""
drift_two_batch.py  —  minimal single-drift runner

Submits a BASELINE file (batch 1), pauses, then a CHANGED file (batch 2)
under the SAME source_id with NO reset in between, so DriftDetectionService
compares the changed schema against the registered baseline.

Usage (edit the CONFIG block, or pass on the command line):
  python drift_two_batch.py
  python drift_two_batch.py --base lineitem_base.csv --changed lineitem_add.csv

Watch the C# console for the drift line, e.g.:
  Column ADDED 'surcharge' — LOW impact
and the orchestrator's reuse-vs-rematch decision.

NOTE: reset the registry for the source_id ONCE before running (so batch 1
is a true first-time baseline). The SQL is printed below for convenience.
"""
import argparse
import time
import math
import httpx
import pandas as pd

API_URL = "http://localhost:5227"

# ── CONFIG (defaults; override via CLI) ──────────────────────────────
SOURCE_ID = "tpch_lineitem"
TARGET    = "unified_lineitem"
PK_COL    = "line_id"          # composite PK built from orderkey_linenumber
BASE_FILE    = "tpch_data/lineitem_base.csv"   # baseline schema (no change)
CHANGED_FILE = "tpch_data/lineitem_add.csv"    # one schema change applied
BASE_ROWS    = (0, 100)        # batch 1 row slice
CHANGED_ROWS = (100, 200)      # batch 2 row slice (different rows, changed schema)


def safe(v):
    if v is None: return None
    if isinstance(v, float) and math.isnan(v): return None
    return v

def to_rows(df):
    return [{c: safe(r[c]) for c in df.columns} for _, r in df.iterrows()]

def load_batch(path, rows):
    df = pd.read_csv(path)
    s, e = rows
    df = df.iloc[s:e].copy()
    # build composite PK if the pipeline expects line_id and it's absent
    if PK_COL == "line_id" and "line_id" not in df.columns \
       and {"orderkey", "linenumber"}.issubset(df.columns):
        df["line_id"] = df["orderkey"].astype(str) + "_" + df["linenumber"].astype(str)
    return df

def submit(rows):
    t0 = time.monotonic()
    try:
        r = httpx.post(f"{API_URL}/api/integration/submit", json={
            "sourceId": SOURCE_ID, "sourceTable": f"src_{SOURCE_ID}",
            "targetTable": TARGET, "sourceRows": rows,
            "resolutionPolicy": "LastWriteWins", "runMode": "Incremental",
        }, timeout=600.0)
        ms = int((time.monotonic() - t0) * 1000)
        res = r.json() if r.status_code == 200 else {"status": "Failed", "error": r.text[:200]}
        res["latency_ms"] = ms
        return res
    except Exception as e:
        return {"status": "Failed", "error": str(e), "latency_ms": 0}

def show(label, res):
    m = res.get("metrics") or {}
    print(f"\n  {'OK' if res.get('status')=='Success' else 'XX'} {label}")
    print(f"     status={res.get('status')}  latency={res.get('latency_ms')}ms  "
          f"match_ms={m.get('matchingTimeMs',0)}")
    print(f"     rows ins={res.get('rowsInserted',0)} upd={res.get('rowsUpdated',0)} "
          f"skip={res.get('rowsSkipped',0)}")
    print(f"     MSR={m.get('mappingStabilityRate',0):.1%}  "
          f"HRR={m.get('historicalRewriteRatio',0):.3f}  "
          f"drift={res.get('driftEventsDetected',0)}  "
          f"conflicts={res.get('conflictsDetected',0)}")
    if res.get("error"):
        print(f"     error={res['error']}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=BASE_FILE)
    ap.add_argument("--changed", default=CHANGED_FILE)
    args = ap.parse_args()

    try:
        h = httpx.get(f"{API_URL}/api/integration/health", timeout=5)
        print(f"C# API: {h.json().get('status','?')}")
    except Exception:
        print("ERROR: C# API not running on 5227"); return

    print(f"\nsource_id={SOURCE_ID}  target={TARGET}")
    print(f"baseline = {args.base}  rows {BASE_ROWS}")
    print(f"changed  = {args.changed}  rows {CHANGED_ROWS}")
    print("\nReset the registry for this source_id ONCE before batch 1:")
    print(f"  DELETE FROM ... WHERE source_id='{SOURCE_ID}';  (mapping_registry, "
          "source_schema_snapshot, drift_log, integration_run, etc.)")
    input("\nRegistry reset done? Press Enter to submit BATCH 1 (baseline)...")

    b1 = load_batch(args.base, BASE_ROWS)
    print(f"  batch1 cols ({len(b1.columns)}): {list(b1.columns)}")
    show("BATCH 1 — baseline schema", submit(to_rows(b1)))

    input("\nPress Enter to submit BATCH 2 (changed schema, NO reset)...")
    b2 = load_batch(args.changed, CHANGED_ROWS)
    print(f"  batch2 cols ({len(b2.columns)}): {list(b2.columns)}")
    # quick header diff so you can SEE the single change before it runs
    only1 = set(b1.columns) - set(b2.columns)
    only2 = set(b2.columns) - set(b1.columns)
    print(f"  schema diff: removed={sorted(only1)}  added={sorted(only2)}")
    show("BATCH 2 — changed schema", submit(to_rows(b2)))

    print("\nNow read the C# console for the drift line + reuse/re-match decision.")


if __name__ == "__main__":
    main()
