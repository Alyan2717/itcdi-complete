# ITCDI — Incremental Target-Constrained Data Integration

ITCDI is a deterministic, explainable middleware for incremental data integration.
It matches each source column to a fixed target (including columns that are named
differently), produces executable transformations rather than only reporting that
columns correspond, detects and classifies schema drift, reuses mappings across
batches so the same work is not repeated, and detects and resolves conflicts when
more than one source writes to the same target. A deterministic rule cascade does
the common cases; a local language-model fallback handles only the columns the
rules cannot resolve.

This repository accompanies the dissertation *Incremental Target-Constrained Data
Integration: A Deterministic, Explainable Middleware for Schema Matching,
Executable Mapping, and Drift-Resilient Integration*.

## Architecture

ITCDI is two services that work together over HTTP:

- **`orchestrator/`** — a C# / .NET 8 service that owns the flow: it checks for
  drift, decides whether to bootstrap, reuse, or re-match, transforms the rows,
  detects and resolves conflicts, writes to the target, and records provenance.
- **`matching-engine/`** — a Python / FastAPI service that holds the embedding
  model, the four-stage matching pipeline (scoping, candidate retrieval, semantic
  filtering, rule-based classification), and the language-model fallback.

The orchestrator is in charge; the matching engine only answers when asked, and is
called only when a schema actually changes. State lives in two SQL Server
databases: a registry (mappings, schema snapshots, fingerprints, drift events,
provenance, run history) and the target database (the consolidated table).

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com)
- [Python 3.12](https://www.python.org)
- [Microsoft SQL Server Express](https://www.microsoft.com/sql-server)
- [Ollama](https://ollama.com) with the Llama 3.1 model (`ollama pull llama3.1`)
- The `bge-large-en-v1.5` embedding model (downloaded automatically by
  sentence-transformers on first run)

## Setup

### 1. Matching engine (Python)

The matcher is a package (`schema_intelligence`), so it is launched from the
`matching-engine` folder, not from inside the package.

```bash
cd matching-engine/schema_intelligence
python -m venv .venv
.venv\Scripts\activate            # macOS/Linux: source .venv/bin/activate
pip install -r requirements.txt

# run from the matching-engine folder (one level up), as a module:
cd ..
python -m uvicorn schema_intelligence.main:app --port 5001
```

Copy `schema_intelligence/.env.example` to `schema_intelligence/.env` and adjust
if needed (LLM provider, thresholds, port, Ollama URL). The first start loads the
`bge-large-en-v1.5` embedding model, which takes a moment and downloads it on the
first run. A health-check endpoint reports the model and threshold in use.

### 2. Orchestrator (C# / .NET 8)

Copy the example config and fill in your own SQL Server connection strings:

```bash
cd orchestrator
cp appsettings.example.json appsettings.json
# edit appsettings.json: set the registry and target DB connection strings,
# and the matching-engine URL (e.g. http://localhost:8000)
dotnet restore
dotnet run
```

The orchestrator creates the registry and target databases on first run.

## Running an integration

An integration is one source batch submitted to the orchestrator. A batch carries
a source identifier, the target table, the rows to integrate, and the conflict
resolution policy. The orchestrator runs the full pipeline (drift detection,
mapping resolution, transformation, conflict detection, write) and records the
metrics for that run. See `experiments/` for runnable examples.

## Reproducing the experiments

The `experiments/` folder contains the scripts behind each experiment in the
dissertation's evaluation chapter.

| Experiment | Description | Script |
|---|---|---|
| 1 | Matching quality vs. five baselines (Valentine + TPC-H) | `experiment_matching_v2` |
| 2 | Mapping classification and expression accuracy | `experiment_mapping_v2` |
| 3 | Schema drift detection and target protection | `drift_two_batch, experiment_drift_extension` |
| 4 | Mapping reuse and incremental savings | `drift_two_batch` |
| 5 | Cross-source conflict detection and resolution | `conflict_runner` |
| 6 | Design justification by ablation | `experiment_ablation` |
| 7 | Efficiency at scale | `experiment_scale` |

The deterministic experiments were each run five times; the standard deviation was
zero on every rule-engine run.

## Datasets

The evaluation uses the [Valentine](https://github.com/delftdata/valentine)
benchmark and a testbed built on the [TPC-H](https://www.tpc.org/tpch/) schema.
These are not redistributed here; download them from their original sources and
place them under `data/` (git-ignored).

## License

See [LICENSE](LICENSE).
