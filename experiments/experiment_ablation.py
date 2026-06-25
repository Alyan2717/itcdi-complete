"""
experiment_ablation.py  —  EXPERIMENT 6: Design Justification (Ablation)

Proves each pipeline stage earns its place by toggling stages off and
measuring the F1 / coverage cost. Four configs on the same dataset(s):

  full     : scoping + filtering + rules + LLM
  no_llm   : disable_llm=True
  no_filter: disable_filtering=True   (requires the server-side flag)
  no_both  : both disabled

Scoping is reported separately from the Exp 1 finding (pruned nothing),
so it is not toggled here.

Requires: Python matcher on http://localhost:5001
"""
import pandas as pd
import httpx
import statistics
import json

ITCDI_URL = "http://localhost:5001"
BASE = "valentine_data/zendo/Valentine-datasets"
N_RUNS = 5   # repeat to capture LLM-fallback variance in the 'full' config

# Wikidata is the dataset that actually exercises filtering + LLM fallback.
DATASETS = [
    {"name": "Wikidata_Unionable",
     "source_file": f"{BASE}/Wikidata/Musicians/Musicians_unionable/musicians_unionable_source.csv",
     "target_file": f"{BASE}/Wikidata/Musicians/Musicians_unionable/musicians_unionable_target.csv",
     "ground_truth": [
        ("musician","musicianID"),("musicianLabel","musicianName"),
        ("genderLabel","genderType"),("birthDate","birthDate"),
        ("cityLabel","city"),("familyNameLabel","familyName"),
        ("givenNameLabel","forename"),("fatherLabel","fatherName"),
        ("motherLabel","motherName"),("partner","spouse"),
        ("numberOfChildren","NChildren"),("genreLabel","kind"),
        ("websiteLabel","webpage"),("residenceLabel","residence"),
        ("ethnicityLabel","ethnicity"),("religionLabel","religionLabel"),
        ("activityStart","kickoff"),("twitterNameLabel","twitterUsername"),
        ("geniusNameLabel","geniusNameLabel"),("recordLabelLabel","recordCompany")]},
    {"name": "Wikidata_ViewUnion",
     "source_file": f"{BASE}/Wikidata/Musicians/Musicians_viewunion/musicians_viewunion_source.csv",
     "target_file": f"{BASE}/Wikidata/Musicians/Musicians_viewunion/musicians_viewunion_target.csv",
     "ground_truth": [
        ("musician","musicianID"),("birthDate","birthDate"),
        ("familyNameLabel","familyName"),("givenNameLabel","forename"),
        ("numberOfChildren","NChildren"),("websiteLabel","webpage")]},
    
    {"name": "TPC-H_LINEITEM",
     "source_file": "tpch_data/lineitem.csv",
     "target_file": "tpch_data/target_unified_lineitem.csv",
     "ground_truth": [
        ("orderkey","order_id"),("partkey","part_id"),("suppkey","supplier_id"),
        ("quantity","quantity"),("extendedprice","unit_price"),
        ("discount","discount_rate"),("tax","tax_rate"),
        ("returnflag","return_flag"),("shipdate","ship_date"),
        ("shipmode","ship_mode"),("comment","comments"),
        ("quantity","net_price"),("extendedprice","net_price")]},
]

CONFIGS = {
    "full":      {"disable_llm": False, "disable_filtering": False},
    "no_llm":    {"disable_llm": True,  "disable_filtering": False},
    "no_filter": {"disable_llm": False, "disable_filtering": True},
    "no_both":   {"disable_llm": True,  "disable_filtering": True},
}


def df_to_cols(df):
    out = []
    for c in df.columns:
        dt = str(df[c].dtype)
        t = "int" if "int" in dt else "decimal(18,2)" if "float" in dt else "nvarchar(max)"
        out.append({"name": c, "data_type": t, "is_nullable": bool(df[c].isnull().any())})
    return out


def metrics(produced, gt):
    tp = len(produced & gt)
    p = tp / len(produced) if produced else 0.0
    r = tp / len(gt) if gt else 0.0
    f1 = 2*p*r/(p+r) if (p+r) else 0.0
    return {"P": round(p,3), "R": round(r,3), "F1": round(f1,3),
            "correct": tp, "produced": len(produced), "gt": len(gt)}


def run_match(ds, cfg):
    df_s = pd.read_csv(ds["source_file"])
    df_t = pd.read_csv(ds["target_file"])
    r = httpx.post(f"{ITCDI_URL}/match", json={
        "source_id": ds["name"],
        "source_columns": df_to_cols(df_s.head(50)),
        "target_columns": df_to_cols(df_t.head(50)),
        "existing_mappings": None,
        "disable_llm": cfg["disable_llm"],
        "disable_filtering": cfg["disable_filtering"],
    }, timeout=300.0)
    if r.status_code != 200:
        return set(), []
    mappings = r.json().get("mappings", [])
    produced = {(src.lower(), m["target_column"].lower())
                for m in mappings for src in m["source_columns"]}
    return produced, mappings


def main():
    print("="*78)
    print("EXPERIMENT 6 — Design Justification (Ablation)")
    print("="*78)

    all_rows = []
    for ds in DATASETS:
        gt = {(s.lower(), t.lower()) for s, t in ds["ground_truth"]}
        print(f"\n### {ds['name']}  (gt={len(gt)})")
        for cfg_name, cfg in CONFIGS.items():
            f1s, last_mappings, last_produced = [], [], set()
            for _ in range(N_RUNS):
                produced, mappings = run_match(ds, cfg)
                f1s.append(metrics(produced, gt)["F1"])
                last_mappings, last_produced = mappings, produced
            m = metrics(last_produced, gt)
            mean_f1 = statistics.mean(f1s)
            std_f1  = statistics.stdev(f1s) if len(f1s) > 1 else 0.0
            # false positives = produced pairs not in ground truth
            fps = last_produced - gt
            print(f"  {cfg_name:<10} F1={mean_f1:.3f}±{std_f1:.3f}  "
                  f"P={m['P']:.3f} R={m['R']:.3f}  "
                  f"produced={m['produced']} correct={m['correct']} "
                  f"FPs={len(fps)}")
            all_rows.append({
                "dataset": ds["name"], "config": cfg_name,
                "f1_mean": round(mean_f1,3), "f1_std": round(std_f1,3),
                "P": m["P"], "R": m["R"], "produced": m["produced"],
                "correct": m["correct"], "false_positives": sorted(fps),
            })

    with open("results_ablation.json", "w") as f:
        json.dump(all_rows, f, indent=2)
    print("\nSaved results_ablation.json")


if __name__ == "__main__":
    main()