"""
experiment_mapping_v2.py

EXPERIMENT 2 — Mapping Classification Accuracy (rewritten for rigor)

Measures whether ITCDI correctly classifies the TRANSFORMATION TYPE and
generates the correct executable expression — the novel dimension absent
from all schema-matching baselines (Valentine, ICDE 2021).

IMPROVEMENTS over v1:
  1. Rule labels corrected to the ACTUAL engine (R1 / R2a / R2b / R3),
     not the stale R0/R5/R6.
  2. Cases split into SYNTHETIC (constructed) and DATASET-DRAWN (real
     columns from TPC-H and Wikidata) so the result is not "we only pass
     cases we invented."
  3. N repeated runs to capture LLM-fallback non-determinism; reports
     per-metric accuracy as mean +/- std across runs.
  4. Clean, aligned output.

Metrics: source-column accuracy, type accuracy, expression accuracy.
"""
import httpx
import json
import time
import statistics

ITCDI_URL = "http://localhost:5001"
N_RUNS    = 5   # mapping rules are deterministic, but LLM fallback is not


# ─────────────────────────────────────────────────────────────────────
# CASES.  origin = "synthetic" (constructed) or "dataset" (real columns).
# rule    = the ACTUAL rule expected to fire (R1 / R2a / R2b).
# ─────────────────────────────────────────────────────────────────────
MAPPING_CASES = [
    # ── R1 OneToOne — synthetic ──
    {"rule":"R1","origin":"synthetic","name":"Exact name match",
     "source_cols":[{"name":"birthDate","data_type":"date","is_nullable":False}],
     "target_cols":[{"name":"birthDate","data_type":"date","is_nullable":False}],
     "expected":{"source_columns":["birthDate"],"target_column":"birthDate",
                 "mapping_type":"OneToOne","expression":"birthDate"}},

    {"rule":"R1","origin":"synthetic","name":"Label suffix normalisation",
     "source_cols":[{"name":"cityLabel","data_type":"nvarchar(max)","is_nullable":True}],
     "target_cols":[{"name":"city","data_type":"nvarchar(max)","is_nullable":True}],
     "expected":{"source_columns":["cityLabel"],"target_column":"city",
                 "mapping_type":"OneToOne","expression":"cityLabel"}},

    # ── R1 OneToOne via embedding — dataset (Wikidata real pairs) ──
    {"rule":"R1","origin":"dataset","name":"Wikidata partner->spouse",
     "source_cols":[{"name":"partner","data_type":"nvarchar(max)","is_nullable":True}],
     "target_cols":[{"name":"spouse","data_type":"nvarchar(max)","is_nullable":True}],
     "expected":{"source_columns":["partner"],"target_column":"spouse",
                 "mapping_type":"OneToOne","expression":"partner"}},

    {"rule":"R1","origin":"dataset","name":"Wikidata activityStart->kickoff",
     "source_cols":[{"name":"activityStart","data_type":"date","is_nullable":True}],
     "target_cols":[{"name":"kickoff","data_type":"date","is_nullable":True}],
     "expected":{"source_columns":["activityStart"],"target_column":"kickoff",
                 "mapping_type":"OneToOne","expression":"activityStart"}},

    # ── R2a Concatenation — synthetic ──
    {"rule":"R2a","origin":"synthetic","name":"Concatenation fname+lname",
     "source_cols":[{"name":"fname","data_type":"nvarchar(max)","is_nullable":False},
                    {"name":"lname","data_type":"nvarchar(max)","is_nullable":False}],
     "target_cols":[{"name":"full_name","data_type":"nvarchar(max)","is_nullable":False}],
     "expected":{"source_columns":["fname","lname"],"target_column":"full_name",
                 "mapping_type":"Concatenation","expression":'fname + " " + lname'}},

    {"rule":"R2a","origin":"synthetic","name":"Concatenation first_name+last_name",
     "source_cols":[{"name":"first_name","data_type":"nvarchar(max)","is_nullable":False},
                    {"name":"last_name","data_type":"nvarchar(max)","is_nullable":False}],
     "target_cols":[{"name":"full_name","data_type":"nvarchar(max)","is_nullable":False}],
     "expected":{"source_columns":["first_name","last_name"],"target_column":"full_name",
                 "mapping_type":"Concatenation","expression":'first_name + " " + last_name'}},

    # ── R2b Derived — synthetic ──
    {"rule":"R2b","origin":"synthetic","name":"Derived qty*unit_price->total_price",
     "source_cols":[{"name":"qty","data_type":"int","is_nullable":False},
                    {"name":"unit_price","data_type":"decimal(18,2)","is_nullable":False}],
     "target_cols":[{"name":"total_price","data_type":"decimal(18,2)","is_nullable":False}],
     "expected":{"source_columns":["qty","unit_price"],"target_column":"total_price",
                 "mapping_type":"Derived","expression":"qty * unit_price"}},

    # ── R2b Derived — DATASET (real TPC-H LINEITEM columns) ──
    {"rule":"R2b","origin":"dataset","name":"TPC-H quantity*extendedprice->net_price",
     "source_cols":[
        {"name":"quantity","data_type":"int","is_nullable":False},
        {"name":"extendedprice","data_type":"decimal(18,2)","is_nullable":False},
        {"name":"discount","data_type":"decimal(18,4)","is_nullable":False},
        {"name":"tax","data_type":"decimal(18,4)","is_nullable":False},
        {"name":"linenumber","data_type":"int","is_nullable":False},
        {"name":"returnflag","data_type":"nvarchar(max)","is_nullable":False}],
     "target_cols":[
        {"name":"quantity","data_type":"decimal(18,2)","is_nullable":True},
        {"name":"extendedprice","data_type":"decimal(18,2)","is_nullable":True},
        {"name":"discount","data_type":"decimal(18,4)","is_nullable":True},
        {"name":"tax","data_type":"decimal(18,4)","is_nullable":True},
        {"name":"net_price","data_type":"decimal(18,2)","is_nullable":True},
        {"name":"returnflag","data_type":"nvarchar(5)","is_nullable":True}],
     "expected":{"source_columns":["quantity","extendedprice"],"target_column":"net_price",
                 "mapping_type":"Derived","expression":"quantity * extendedprice"}},
]


def evaluate_case_once(case):
    payload = {"source_id":case["name"],"source_columns":case["source_cols"],
               "target_columns":case["target_cols"],"existing_mappings":None}
    t0 = time.monotonic()
    r  = httpx.post(f"{ITCDI_URL}/match", json=payload, timeout=60.0)
    ms = int((time.monotonic()-t0)*1000)
    mappings = r.json().get("mappings", []) if r.status_code==200 else []
    exp = case["expected"]
    matched = next((m for m in mappings
                    if m["target_column"].lower()==exp["target_column"].lower()), None)
    src_ok = type_ok = expr_ok = False
    if matched:
        src_ok  = sorted(s.lower() for s in matched["source_columns"]) == \
                  sorted(s.lower() for s in exp["source_columns"])
        type_ok = matched["mapping_type"] == exp["mapping_type"]
        expr_ok = matched["expression"].strip() == exp["expression"].strip()
    return src_ok, type_ok, expr_ok, ms, matched


def main():
    print("="*72)
    print(f"EXPERIMENT 2 — Mapping Classification Accuracy (N={N_RUNS} runs)")
    print("Novel dimension: no Valentine baseline produces types or expressions.")
    print("="*72)

    # accuracy per run
    run_src, run_type, run_expr = [], [], []
    last_detail = []

    for run in range(N_RUNS):
        s = t = e = 0
        detail = []
        for case in MAPPING_CASES:
            src_ok, type_ok, expr_ok, ms, matched = evaluate_case_once(case)
            s += src_ok; t += type_ok; e += expr_ok
            detail.append((case, src_ok, type_ok, expr_ok, ms, matched))
        n = len(MAPPING_CASES)
        run_src.append(s/n); run_type.append(t/n); run_expr.append(e/n)
        last_detail = detail

    # ── Per-case detail (from last run) ──
    print("\nPer-case (last run):")
    for case, src_ok, type_ok, expr_ok, ms, matched in last_detail:
        ok = "OK " if (src_ok and type_ok and expr_ok) else "XX "
        print(f"  [{ok}] {case['rule']:<4} {case['origin']:<9} {case['name']}")
        if matched:
            print(f"        -> {matched['source_columns']} -> {matched['target_column']} "
                  f"[{matched['mapping_type']}] {matched['expression']}  ({ms}ms)")
        else:
            print(f"        -> NO MAPPING FOUND")

    def ms_(x): return statistics.mean(x)
    def sd_(x): return statistics.stdev(x) if len(x) > 1 else 0.0

    # ── Summary ──
    print(f"\n{'='*72}")
    print("MAPPING ACCURACY (mean +/- std over runs)")
    print(f"{'='*72}")
    print(f"  Source-column accuracy : {ms_(run_src):.1%} +/- {sd_(run_src):.1%}")
    print(f"  Type-classification    : {ms_(run_type):.1%} +/- {sd_(run_type):.1%}")
    print(f"  Expression generation  : {ms_(run_expr):.1%} +/- {sd_(run_expr):.1%}")

    # split synthetic vs dataset on the last run
    syn = [d for d in last_detail if d[0]["origin"]=="synthetic"]
    dat = [d for d in last_detail if d[0]["origin"]=="dataset"]
    def acc(group, idx): return sum(1 for d in group if d[idx])/len(group) if group else 0
    print(f"\n  By origin (type accuracy, last run):")
    print(f"    synthetic : {acc(syn,2):.1%}  ({len(syn)} cases)")
    print(f"    dataset   : {acc(dat,2):.1%}  ({len(dat)} cases)")
    print(f"\n  By rule (type accuracy, last run):")
    for rid in ["R1","R2a","R2b"]:
        g = [d for d in last_detail if d[0]["rule"]==rid]
        if g: print(f"    {rid:<4}: {acc(g,2):.1%}  ({len(g)} cases)")

    with open("results_mapping_v2.json","w") as f:
        json.dump({"src":run_src,"type":run_type,"expr":run_expr}, f, indent=2)
    print("\nSaved to results_mapping_v2.json")


if __name__ == "__main__":
    main()
