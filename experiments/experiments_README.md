# Experiments

This folder contains the scripts and data fixtures behind the evaluation chapter
of the dissertation.

## Data fixtures (`tpch_data/`)

These are constructed on top of the TPC-H line-item schema. Each small fixture
corresponds to a specific mapping or drift case; the expected outcome for each is
defined in the ground-truth files.

| File | Role |
|---|---|
| `lineitem.csv` | Source line-item rows used across the matching cases |
| `lineitem_base.csv` | Baseline batch (the schema registered before drift) |
| `lineitem_add.csv` | Drift: a column added (`surcharge`) |
| `lineitem_add_tax.csv` | Drift fixture used in the conflict / tax cases |
| `lineitem_remove.csv` | Drift: a column removed (`tax`) |
| `target_unified_lineitem.csv` | The fixed target schema definition |

The large `lineitem_100k.csv` file used for the scale experiment is **not**
committed, as it is large and fully regenerable. Recreate it with the included
generator:

```bash
python generate_tpch_scale.py --rows 100000 --out tpch_data/lineitem_100k.csv
```

`generate_tpch_scale.py` is a pure-Python synthetic generator that reproduces the
16-column LINEITEM schema and TPC-H value ranges for throughput testing. It is not
the official TPC-H `dbgen`. The random seed is fixed (default 42), so the same
command reproduces the same rows, which is what makes the scale results in
Experiment 7 repeatable. Pass `--rows 1000000` for the one-million-row run.

## Ground truth

The ground-truth files (JSON) define, for each constructed case, the mappings that
should be produced and the drift events that should be detected. For the Valentine
experiments the ground truth comes from the benchmark itself; for the TPC-H
testbed it is defined here, as described in the dissertation.

## Experiment scripts

| Experiment | Description | Script |
|---|---|---|
| 1 | Matching quality vs. five baselines | `experiment_matching_v2` |
| 2 | Mapping classification and expression accuracy | `experiment_mapping_v2` |
| 3 | Schema drift detection and target protection | `drift_two_batch, experiment_drift_extension` |
| 4 | Mapping reuse and incremental savings | `drift_two_batch` |
| 5 | Cross-source conflict detection and resolution | `conflict_runner` |
| 6 | Design justification by ablation | `experiment_ablation` |
| 7 | Efficiency at scale | `experiment_scale` |

Replace the script placeholders above with the actual filenames. Each deterministic
experiment was run five times; the standard deviation was zero on every rule-engine
run.
