"""
generate_tpch_scale.py

Pure-Python TPC-H LINEITEM generator for scale testing (no external deps).
Produces a CSV with the same 16 LINEITEM columns ITCDI already uses, but at
an arbitrary row count so you can demonstrate pipeline throughput at scale.

Usage:
    python generate_tpch_scale.py --rows 1000000 --out tpch_data/lineitem_1m.csv
    python generate_tpch_scale.py --rows 100000  --out tpch_data/lineitem_100k.csv

The composite key line_id = orderkey + "_" + linenumber is generated so the
integration PK logic works unchanged. Values follow TPC-H column semantics
closely enough for an integration/throughput benchmark (this is NOT the
official dbgen; state that in the thesis — it is a synthetic generator that
reproduces the LINEITEM schema and value ranges for scale testing).
"""
import argparse
import csv
import random
import datetime

RETURNFLAGS = ["A", "N", "R"]
LINESTATUS  = ["O", "F"]
SHIPMODES   = ["TRUCK", "MAIL", "REG AIR", "AIR", "FOB", "RAIL", "SHIP"]
INSTRUCTS   = ["DELIVER IN PERSON", "COLLECT COD", "NONE", "TAKE BACK RETURN"]

def random_date(start_year=1992, end_year=1998):
    start = datetime.date(start_year, 1, 1)
    end   = datetime.date(end_year, 12, 31)
    delta = (end - start).days
    return start + datetime.timedelta(days=random.randint(0, delta))

def generate(rows: int, out_path: str, seed: int = 42):
    random.seed(seed)  # reproducible — important for N-run experiments
    header = ["orderkey", "partkey", "suppkey", "linenumber", "quantity",
              "extendedprice", "discount", "tax", "returnflag", "linestatus",
              "shipdate", "commitdate", "receiptdate", "shipinstruct",
              "shipmode", "comment", "line_id"]

    # Orders get 1..7 line items, like real TPC-H, so line_id is composite-unique.
    written = 0
    orderkey = 0
    with open(out_path, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(header)
        while written < rows:
            orderkey += 1
            n_lines = random.randint(1, 7)
            for linenumber in range(1, n_lines + 1):
                if written >= rows:
                    break
                quantity      = random.randint(1, 50)
                extendedprice = round(random.uniform(900.0, 105000.0), 2)
                discount      = round(random.choice([0.0,0.01,0.02,0.04,0.05,0.06,0.08,0.10]), 4)
                tax           = round(random.choice([0.0,0.02,0.04,0.06,0.08]), 4)
                ship    = random_date()
                commit  = random_date()
                receipt = random_date()
                row = [
                    orderkey,
                    random.randint(1, 200000),          # partkey
                    random.randint(1, 10000),           # suppkey
                    linenumber,
                    f"{quantity}.00",
                    f"{extendedprice}",
                    f"{discount}",
                    f"{tax}",
                    random.choice(RETURNFLAGS),
                    random.choice(LINESTATUS),
                    ship.isoformat(),
                    commit.isoformat(),
                    receipt.isoformat(),
                    random.choice(INSTRUCTS),
                    random.choice(SHIPMODES),
                    f"line comment {written+1}",
                    f"{orderkey}_{linenumber}",          # composite PK
                ]
                w.writerow(row)
                written += 1
                if written % 100000 == 0:
                    print(f"  ... {written:,} rows")
    print(f"Done: {written:,} rows -> {out_path}")

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=1_000_000)
    ap.add_argument("--out",  type=str, default="tpch_data/lineitem_1m.csv")
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()
    generate(args.rows, args.out, args.seed)
