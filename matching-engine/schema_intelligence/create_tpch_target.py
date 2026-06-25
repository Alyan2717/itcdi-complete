# run once: python create_tpch_target.py
import pandas as pd, os
os.makedirs("tpch_data", exist_ok=True)

target = pd.DataFrame({
    "line_id":       ["L1"],
    "order_id":      ["O1"],
    "part_id":       ["P1"],
    "supplier_id":   ["S1"],
    "quantity":      [5.0],
    "unit_price":    [100.0],
    "discount_rate": [0.05],
    "tax_rate":      [0.04],
    "net_price":     [95.0],
    "return_flag":   ["N"],
    "ship_date":     ["1994-01-01"],
    "ship_mode":     ["RAIL"],
    "comments":      ["test comment"],
})
target.to_csv("tpch_data/target_unified_lineitem.csv", index=False)
print("Created tpch_data/target_unified_lineitem.csv")
print("Columns:", list(target.columns))