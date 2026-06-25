"""
conflict_runner.py  —  cross-source conflict + resolution runner

Conflicts in ITCDI fire ONLY when a row with the same primary key already
exists in the target AND was written by a DIFFERENT source_id. Same-source
re-submission is a legitimate update (0 conflicts). So this script submits:

  BATCH 1 : source_id = SOURCE_A  (owns the rows)
  BATCH 2 : source_id = SOURCE_B  (same line_ids, some values changed)

into the SAME target table, so SOURCE_B collides with SOURCE_A's rows.

The resolution policy for BATCH 2 is selectable:
  LastWriteWins        — B accepted, overwrites A
  ProvenancePreserved  — disputed columns nulled, row kept
  Rejected             — triggered via a NOT NULL violation (--null-violation)

Usage examples:
  # LastWriteWins: B overwrites A on the shared keys
  python conflict_runner.py --policy LastWriteWins

  # ProvenancePreserved: disputed columns nulled
  python conflict_runner.py --policy ProvenancePreserved

  # ConstraintViolation -> Rejected: B sends a null in a NOT NULL column
  python conflict_runner.py --policy LastWriteWins --null-violation

IMPORTANT — run order / reset:
  Reset BOTH sources' registry+provenance for unified_lineitem before a
  clean run, then let A bootstrap/own the rows, then fire B. See the printed
  reset hint. Do NOT reset between A and B.
"""
import argparse
import time
import math
import httpx
import pandas as pd

API_URL = "http://localhost:5227"

# ── CONFIG ───────────────────────────────────────────────────────────
SOURCE_A = "warehouse_a"          # writes first, owns the rows
SOURCE_B = "warehouse_b"          # collides on the same keys
TARGET   = "unified_lineitem"
PK_COL   = "line_id"

BASE_FILE = "tpch_data/lineitem_base.csv"
SHARED_ROWS = (0, 100)            # SAME slice for both batches -> shared keys

# Which columns B changes, and to what, so ValueConflict fires.
# (Numeric-normalisation guard in C# means the value must REALLY differ,
#  not just 0.02 vs 0.0200 — so we use clearly distinct values.)
MUTATIONS = {"tax": 0.99, "sale": 0.88}

# For the Rejected case: B nulls a NOT NULL column on a few rows.
NULL_VIOLATION_COL = "returnflag"
NULL_VIOLATION_N   = 5


def safe(v):
    if v is None:
        return None
    if isinstance(v, float) and math.isnan(v):
        return None
    return v


def to_rows(df):
    return [{c: safe(r[c]) for c in df.columns} for _, r in df.iterrows()]


def load_batch(path, rows):
    df = pd.read_csv(path)
    s, e = rows
    df = df.iloc[s:e].copy()
    if PK_COL == "line_id" and "line_id" not in df.columns \
       and {"orderkey", "linenumber"}.issubset(df.columns):
        df["line_id"] = df["orderkey"].astype(str) + "_" + df["linenumber"].astype(str)
    return df


def mutate(df, null_violation=False):
    """Return a copy of df with values changed so B disagrees with A."""
    d = df.copy()
    for col, val in MUTATIONS.items():
        if col in d.columns:
            d[col] = val
    if null_violation and NULL_VIOLATION_COL in d.columns:
        idx = d.index[:NULL_VIOLATION_N]
        d.loc[idx, NULL_VIOLATION_COL] = None
    return d


def submit(source_id, rows, policy):
    t0 = time.monotonic()
    try:
        r = httpx.post(f"{API_URL}/api/integration/submit", json={
            "sourceId": source_id,
            "sourceTable": f"src_{source_id}",
            "targetTable": TARGET,
            "sourceRows": rows,
            "resolutionPolicy": policy,
            "runMode": "Incremental",
        }, timeout=600.0)
        ms = int((time.monotonic() - t0) * 1000)
        res = r.json() if r.status_code == 200 else {"status": "Failed", "error": r.text[:300]}
        res["latency_ms"] = ms
        return res
    except Exception as e:
        return {"status": "Failed", "error": str(e), "latency_ms": 0}


def show(label, res):
    m = res.get("metrics") or {}
    print(f"\n  {'OK' if res.get('status') == 'Success' else 'XX'} {label}")
    print(f"     status={res.get('status')}  latency={res.get('latency_ms')}ms")
    print(f"     rows ins={res.get('rowsInserted', 0)} upd={res.get('rowsUpdated', 0)} "
          f"skip={res.get('rowsSkipped', 0)}")
    print(f"     conflicts={res.get('conflictsDetected', 0)}  "
          f"drift={res.get('driftEventsDetected', 0)}")
    if res.get("error"):
        print(f"     error={res['error']}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--policy", default="LastWriteWins",
                    choices=["LastWriteWins", "ProvenancePreserved", "Rejected"])
    ap.add_argument("--null-violation", action="store_true",
                    help="B nulls a NOT NULL column to trigger ConstraintViolation -> Rejected")
    ap.add_argument("--base", default=BASE_FILE)
    args = ap.parse_args()

    try:
        h = httpx.get(f"{API_URL}/api/integration/health", timeout=5)
        print(f"C# API: {h.json().get('status', '?')}")
    except Exception:
        print("ERROR: C# API not running on 5227")
        return

    print(f"\nSOURCE_A (owner) = {SOURCE_A}")
    print(f"SOURCE_B (collider) = {SOURCE_B}")
    print(f"target = {TARGET}   shared rows = {SHARED_ROWS}   policy = {args.policy}")
    print(f"B mutations = {MUTATIONS}"
          + (f"  + NULL on {NULL_VIOLATION_COL}[:{NULL_VIOLATION_N}]" if args.null_violation else ""))
    print("\nReset BOTH sources for a clean run (registry + provenance + target rows):")
    print(f"  -- ITCDI_Registry: DELETE rows WHERE source_id IN ('{SOURCE_A}','{SOURCE_B}')")
    print(f"  --   from mapping_registry, source_schema_snapshot, drift_log,")
    print(f"  --   integration_run, integration_provenance, conflict_log")
    print(f"  -- TargetDB_TPCH: DELETE FROM {TARGET}  (clear the shared rows)")
    input("\nReset done? Press Enter to submit BATCH 1 as SOURCE_A (owner)...")

    a = load_batch(args.base, SHARED_ROWS)
    print(f"  batch A cols ({len(a.columns)}): {list(a.columns)}")
    # A always lands cleanly; LastWriteWins is fine for the owner write.
    show(f"BATCH 1 — {SOURCE_A} (owner, inserts)", submit(SOURCE_A, to_rows(a), "LastWriteWins"))

    input(f"\nPress Enter to submit BATCH 2 as SOURCE_B (collider, policy={args.policy})...")
    b = mutate(a, null_violation=args.null_violation)
    print(f"  batch B cols ({len(b.columns)}): {list(b.columns)}")
    print(f"  B shares {len(b)} keys with A; values changed on: {list(MUTATIONS)}"
          + (f" + nulled {NULL_VIOLATION_COL}" if args.null_violation else ""))
    show(f"BATCH 2 — {SOURCE_B} (collides with {SOURCE_A})", submit(SOURCE_B, to_rows(b), args.policy))

    print("\nNow read the C# console for:")
    print("  'PK collision: ... owned by warehouse_a, incoming from warehouse_b'")
    print("  'Value conflict: ... incoming=... existing=...'")
    print(f"  'Conflict detection: N clean, M conflicts, R rejected'")
    print(f"\nThen verify the target, e.g.:")
    print(f"  USE TargetDB_TPCH; SELECT line_id, tax, sale, returnflag")
    print(f"  FROM {TARGET} WHERE line_id IN (SELECT TOP 5 line_id FROM {TARGET});")


if __name__ == "__main__":
    main()