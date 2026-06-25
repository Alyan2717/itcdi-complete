"""
experiment_matching_v2.py  (WIRED — runnable)

EXPERIMENT 1 — Schema Matching Accuracy, with rigor layer.

Combines v1 (baselines + real ITCDI /match call + ground truth) with:
  1. N repeated runs of ITCDI -> mean +/- std F1 (captures LLM-fallback variance)
  2. explicit TRAIN / TEST dataset split (thresholds were tuned on TRAIN)
  3. clean output table
  4. honest empty slot for a MODERN baseline (Magneto) — fill after you run it

Requires:
  - Python matcher on http://localhost:5001
  - the `valentine` package (same as v1) for baselines
"""
import pandas as pd
import httpx
import time
import json
import threading
import statistics
from valentine import valentine_match
from valentine.algorithms import (
    JaccardDistanceMatcher, Cupid, DistributionBased,
    SimilarityFlooding, ComaPy,
)

ITCDI_URL = "http://localhost:5001"
BASE      = "valentine_data/zendo/Valentine-datasets"
N_RUNS    = 5   # ITCDI repeated runs (LLM fallback is non-deterministic)

# Thresholds were developed against these — held-out vs tuned.
TRAIN_NAMES = {"Wikidata_Unionable", "Magellan_Amazon"}


DATASETS = [
    {"name":"Wikidata_Unionable",
     "source_file":f"{BASE}/Wikidata/Musicians/Musicians_unionable/musicians_unionable_source.csv",
     "target_file":f"{BASE}/Wikidata/Musicians/Musicians_unionable/musicians_unionable_target.csv",
     "ground_truth":[
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
    {"name":"Wikidata_ViewUnion",
     "source_file":f"{BASE}/Wikidata/Musicians/Musicians_viewunion/musicians_viewunion_source.csv",
     "target_file":f"{BASE}/Wikidata/Musicians/Musicians_viewunion/musicians_viewunion_target.csv",
     "ground_truth":[
        ("musician","musicianID"),("birthDate","birthDate"),
        ("familyNameLabel","familyName"),("givenNameLabel","forename"),
        ("numberOfChildren","NChildren"),("websiteLabel","webpage")]},
    {"name":"TPC-DI_ec",
     "source_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ec_ev/prospect_both_50_1_ec_ev_source.csv",
     "target_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ec_ev/prospect_both_50_1_ec_ev_target.csv",
     "ground_truth":[("AgencyID","AgencyID")]},
    {"name":"TPC-DI_ac1",
     "source_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ac1_ev/prospect_both_50_1_ac1_ev_source.csv",
     "target_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ac1_ev/prospect_both_50_1_ac1_ev_target.csv",
     "ground_truth":[("AgencyID","prospect_AgencyID")]},
    {"name":"TPC-DI_ac2",
     "source_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ac2_ev/prospect_both_50_1_ac2_ev_source.csv",
     "target_file":f"{BASE}/TPC-DI/Joinable/prospect_both_50_1_ac2_ev/prospect_both_50_1_ac2_ev_target.csv",
     "ground_truth":[("AgencyID","AgID")]},
    {"name":"Magellan_Amazon",
     "source_file":f"{BASE}/Magellan/amazon_google_exp/amazon_google_exp_source.csv",
     "target_file":f"{BASE}/Magellan/amazon_google_exp/amazon_google_exp_target.csv",
     "ground_truth":[("id","id"),("title","title"),
                     ("manufacturer","manufacturer"),("price","price")]},
    {"name":"TPC-H_LINEITEM",
     "source_file":"tpch_data/lineitem.csv",
     "target_file":"tpch_data/target_unified_lineitem.csv",
     "ground_truth":[
        ("orderkey","order_id"),("partkey","part_id"),("suppkey","supplier_id"),
        ("quantity","quantity"),("extendedprice","unit_price"),
        ("discount","discount_rate"),("tax","tax_rate"),
        ("returnflag","return_flag"),("shipdate","ship_date"),
        ("shipmode","ship_mode"),("comment","comments"),
        ("quantity","net_price"),("extendedprice","net_price")]},
]


def metrics(produced, gt):
    correct = len(produced & gt)
    p = correct/len(produced) if produced else 0.0
    r = correct/len(gt) if gt else 0.0
    f1 = 2*p*r/(p+r) if (p+r) else 0.0
    return {"P":round(p,3),"R":round(r,3),"F1":round(f1,3),"correct":correct,"gt":len(gt)}


def run_with_timeout(fn, timeout=60):
    result=[None]; err=[None]
    def t():
        try: result[0]=fn()
        except Exception as e: err[0]=e
    th=threading.Thread(target=t); th.start(); th.join(timeout)
    if th.is_alive(): return None,"TIMEOUT"
    if err[0]: return None,str(err[0])
    return result[0],None


def df_to_cols(df):
    return [{"name":c,
             "data_type":"int" if "int" in str(df[c].dtype)
                         else "decimal(18,2)" if "float" in str(df[c].dtype)
                         else "nvarchar(max)",
             "is_nullable":bool(df[c].isnull().any())}
            for c in df.columns]


def run_itcdi_matching(ds):
    df_s = pd.read_csv(ds["source_file"])
    df_t = pd.read_csv(ds["target_file"])
    r = httpx.post(f"{ITCDI_URL}/match", json={
        "source_id": ds["name"],
        "source_columns": df_to_cols(df_s.head(50)),
        "target_columns": df_to_cols(df_t.head(50)),
        "existing_mappings": None,
    }, timeout=300.0)
    if r.status_code != 200:
        return set()
    mappings = r.json().get("mappings", [])
    return {(src.lower(), m["target_column"].lower())
            for m in mappings for src in m["source_columns"]}


def ground_truth(ds):
    return {(s.lower(), t.lower()) for s, t in ds["ground_truth"]}


def run_baselines(ds, gt):
    df_s = pd.read_csv(ds["source_file"])
    df_t = pd.read_csv(ds["target_file"])
    s100 = df_s.sample(min(100,len(df_s)), random_state=42)
    t100 = df_t.sample(min(100,len(df_t)), random_state=42)
    out = {}
    for name, algo in [("Jaccard",JaccardDistanceMatcher()),("Cupid",Cupid()),
                       ("Distribution",DistributionBased()),
                       ("SimFlooding",SimilarityFlooding()),("COMA",ComaPy())]:
        matches, err = run_with_timeout(lambda a=algo: valentine_match(s100,t100,a), 60)
        if matches is None:
            out[name] = 0.0
        else:
            produced = {(k[0][1].lower(), k[1][1].lower()) for k in matches.one_to_one()}
            out[name] = metrics(produced, gt)["F1"]
    return out


def main():
    print("="*86)
    print(f"EXPERIMENT 1 — Schema Matching F1 (ITCDI N={N_RUNS} runs, mean+/-std)")
    print("TRAIN = thresholds tuned here; TEST = held out.")
    print("="*86)

    rows = []
    for ds in DATASETS:
        split = "train" if ds["name"] in TRAIN_NAMES else "test"
        gt = ground_truth(ds)
        base = run_baselines(ds, gt)
        f1s = []
        for _ in range(N_RUNS):
            produced = run_itcdi_matching(ds)
            f1s.append(metrics(produced, gt)["F1"])
        itcdi_mean = statistics.mean(f1s)
        itcdi_std  = statistics.stdev(f1s) if len(f1s)>1 else 0.0
        rows.append({"name":ds["name"],"split":split,"base":base,
                     "itcdi_mean":itcdi_mean,"itcdi_std":itcdi_std})
        print(f"  done: {ds['name']:<20} ITCDI F1={itcdi_mean:.3f}+/-{itcdi_std:.3f}")

    print(f"\n{'='*86}")
    print(f"{'Dataset':<20}{'Split':<6}{'Jacc':>6}{'Cupid':>6}{'Dist':>6}"
          f"{'SimF':>6}{'COMA':>6}{'ITCDI(mean+/-std)':>20}{'Magneto*':>10}")
    print("-"*86)
    for r in rows:
        b = r["base"]
        itcdi = f"{r['itcdi_mean']:.3f}+/-{r['itcdi_std']:.3f}"
        print(f"{r['name']:<20}{r['split']:<6}"
              f"{b.get('Jaccard',0):>6.3f}{b.get('Cupid',0):>6.3f}"
              f"{b.get('Distribution',0):>6.3f}{b.get('SimFlooding',0):>6.3f}"
              f"{b.get('COMA',0):>6.3f}{itcdi:>20}{'(TODO)':>10}")
    print("-"*86)
    test = [r for r in rows if r["split"]=="test"]
    if test:
        print(f"TEST macro-F1 (ITCDI): {statistics.mean(r['itcdi_mean'] for r in test):.3f}")
    print("\n* Magneto (VLDB 2025): PLACEHOLDER. Run the real baseline; do not fabricate.")
    with open("results_matching_v2.json","w") as f:
        json.dump(rows, f, indent=2)
    print("Saved results_matching_v2.json")


if __name__ == "__main__":
    main()