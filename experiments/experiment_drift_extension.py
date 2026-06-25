"""
experiment_drift_extension.py  —  EXPERIMENT 3 EXTENSION

Exercises the drift types Experiment 3 did not cover but the system already
implements and classifies:

  RENAME : a column is renamed, same type -> ColumnRename (MEDIUM),
           orchestrator re-points the stored mapping (R3 reuse).
  WIDEN  : amount int -> bigint -> TypeWidening (LOW).
  NARROW : amount bigint -> int -> TypeNarrowing (HIGH).

Each scenario is fully system-driven:
  batch 1 = BOOTSTRAP (system creates the target table from declared columns),
  batch 2 = the schema change, drifts against the stored baseline.

Each scenario uses its OWN source id AND its OWN target table, so every
scenario starts from a clean bootstrap and drifts independently.

Explicit sourceColumns are sent so declared SQL types are controlled — this is
required for the type-change tests (row inference cannot express int/bigint).

PRE-RUN RESET: clear registry rows for the three sources and DROP the three
target tables so the system bootstraps them itself. Do NOT create any table
manually.
"""
import argparse
import time
import httpx

API_URL = "http://localhost:5227"


def col(name, dtype, nullable=True):
    return {"name": name, "dataType": dtype, "isNullable": nullable}


def submit(source_id, target, columns, rows, label):
    t0 = time.monotonic()
    payload = {
        "sourceId": source_id,
        "sourceTable": f"src_{source_id}",
        "targetTable": target,
        "sourceColumns": columns,    # explicit schema — controls declared types
        "sourceRows": rows,
        "resolutionPolicy": "LastWriteWins",
        "runMode": "Incremental",
    }
    try:
        r = httpx.post(f"{API_URL}/api/integration/submit", json=payload, timeout=120.0)
        ms = int((time.monotonic() - t0) * 1000)
        res = r.json() if r.status_code == 200 else {"status": "Failed", "error": r.text[:300]}
        res["latency_ms"] = ms
    except Exception as e:
        res = {"status": "Failed", "error": str(e), "latency_ms": 0}
    show(label, res)
    return res


def show(label, res):
    ok = res.get("status") == "Success"
    print(f"\n  {'OK' if ok else 'XX'} {label}")
    print(f"     status={res.get('status')}  latency={res.get('latency_ms')}ms")
    print(f"     rows ins={res.get('rowsInserted', 0)} upd={res.get('rowsUpdated', 0)} "
          f"skip={res.get('rowsSkipped', 0)}")
    print(f"     drift={res.get('driftEventsDetected', 0)}  conflicts={res.get('conflictsDetected', 0)}")
    if res.get("error"):
        print(f"     error={res['error']}")


def rows_baseline():
    # Content is token; only declared schema + names drive drift. PK stable.
    return [
        {"line_id": "1", "amount": 10, "note": "alpha"},
        {"line_id": "2", "amount": 20, "note": "beta"},
        {"line_id": "3", "amount": 30, "note": "gamma"},
    ]


def rows_renamed():
    # 'note' renamed to 'remark', same values.
    return [
        {"line_id": "1", "amount": 10, "remark": "alpha"},
        {"line_id": "2", "amount": 20, "remark": "beta"},
        {"line_id": "3", "amount": 30, "remark": "gamma"},
    ]


def health():
    try:
        h = httpx.get(f"{API_URL}/api/integration/health", timeout=5)
        print(f"C# API: {h.json().get('status', '?')}")
        return True
    except Exception:
        print("ERROR: C# API not running on 5227")
        return False


# ─────────────────────────────────────────────────────────────────────
def run_rename():
    sid, target = "drift_rename", "drift_rename_tbl"
    print("=" * 78)
    print("SCENARIO 1 — RENAME  (note -> remark, same type)")
    print("=" * 78)
    input(f"Reset source '{sid}' + ensure '{target}' dropped. Press Enter for BOOTSTRAP...")
    base_cols = [col("line_id", "varchar(255)", False),
                 col("amount", "int"),
                 col("note", "varchar(100)")]
    submit(sid, target, base_cols, rows_baseline(), "BOOTSTRAP (note)")

    input("\nPress Enter for RENAME batch (note -> remark)...")
    ren_cols = [col("line_id", "varchar(255)", False),
                col("amount", "int"),
                col("remark", "varchar(100)")]
    submit(sid, target, ren_cols, rows_renamed(), "RENAME (remark)")
    print("\nExpect console: \"Column RENAMED 'note' -> 'remark' — MEDIUM impact\"")
    print("           and: \"Re-pointed mapping 'note' -> 'remark' (rename)\"")


def run_widen():
    sid, target = "drift_widen", "drift_widen_tbl"
    print("\n" + "=" * 78)
    print("SCENARIO 2 — TYPE WIDENING  (amount int -> bigint)")
    print("=" * 78)
    input(f"Reset source '{sid}' + ensure '{target}' dropped. Press Enter for BOOTSTRAP...")
    base_cols = [col("line_id", "varchar(255)", False),
                 col("amount", "int"),
                 col("note", "varchar(100)")]
    submit(sid, target, base_cols, rows_baseline(), "BOOTSTRAP (amount int)")

    input("\nPress Enter for WIDEN batch (amount bigint)...")
    wide_cols = [col("line_id", "varchar(255)", False),
                 col("amount", "bigint"),
                 col("note", "varchar(100)")]
    submit(sid, target, wide_cols, rows_baseline(), "WIDEN (amount bigint)")
    print("\nExpect console: \"Column 'amount' type changed int -> bigint — Low\" (TypeWidening)")


def run_narrow():
    sid, target = "drift_narrow", "drift_narrow_tbl"
    print("\n" + "=" * 78)
    print("SCENARIO 3 — TYPE NARROWING  (amount bigint -> int)")
    print("=" * 78)
    input(f"Reset source '{sid}' + ensure '{target}' dropped. Press Enter for BOOTSTRAP...")
    base_cols = [col("line_id", "varchar(255)", False),
                 col("amount", "bigint"),
                 col("note", "varchar(100)")]
    submit(sid, target, base_cols, rows_baseline(), "BOOTSTRAP (amount bigint)")

    input("\nPress Enter for NARROW batch (amount int)...")
    narrow_cols = [col("line_id", "varchar(255)", False),
                   col("amount", "int"),
                   col("note", "varchar(100)")]
    submit(sid, target, narrow_cols, rows_baseline(), "NARROW (amount int)")
    print("\nExpect console: \"Column 'amount' type changed bigint -> int — High\" (TypeNarrowing)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scenario", choices=["rename", "widen", "narrow", "all"], default="all")
    args = ap.parse_args()

    if not health():
        return

    print("\nEach scenario = its own source id AND its own target table, so every")
    print("scenario starts from a clean system bootstrap. Reset that source's registry")
    print("rows and DROP its target table before each scenario. Create nothing manually.\n")

    if args.scenario in ("rename", "all"):
        run_rename()
    if args.scenario in ("widen", "all"):
        run_widen()
    if args.scenario in ("narrow", "all"):
        run_narrow()

    print("\n" + "=" * 78)
    print("Confirm in the registry:")
    print("  USE ITCDI_Registry;")
    print("  SELECT source_id, drift_type, impact_level, affected_columns, detected_at")
    print("  FROM drift_log")
    print("  WHERE source_id IN ('drift_rename','drift_widen','drift_narrow')")
    print("  ORDER BY detected_at;")
    print("=" * 78)


if __name__ == "__main__":
    main()