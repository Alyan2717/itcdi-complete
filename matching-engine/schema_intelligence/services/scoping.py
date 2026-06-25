"""
services/scoping.py

Phase 2a — Linkability Scoping
Based on: Traeger et al., Collaborative Scoping, EDBT 2026

Before running the full matcher, prune source columns that
have no plausible correspondence in the target schema.

Why this matters:
A source table may have 50 columns but only 10 relate to
your target schema. Running matching on all 50 wastes
computation and produces false positives.

Threshold θ is a hyperparameter evaluated in Chapter 8
at values {0.15, 0.20, 0.25, 0.30, 0.35}.
"""
import logging
import numpy as np
from sentence_transformers import SentenceTransformer
from sklearn.metrics.pairwise import cosine_similarity
from ..models.schemas import ColumnDto, ScopingResult
from ..config import settings

logger = logging.getLogger(__name__)

# Single model instance shared across all requests
# Loaded once at startup in main.py lifespan
_model: SentenceTransformer | None = None


def get_model() -> SentenceTransformer:
    global _model
    if _model is None:
        logger.info("Loading sentence-transformer model...")
        # _model = SentenceTransformer("all-MiniLM-L6-v2")
        _model = SentenceTransformer("BAAI/bge-large-en-v1.5")
        logger.info("Model loaded successfully.")
    return _model


# def scope_columns(
#     source_columns: list[ColumnDto],
#     target_columns: list[ColumnDto],
#     threshold: float | None = None
# ) -> ScopingResult:
#     """
#     Returns linkable and unlinkable source columns.

#     A source column is linkable if its maximum cosine similarity
#     to ANY target column exceeds threshold θ.

#     If no target columns exist, all source columns are unlinkable.
#     """
#     if not target_columns:
#         logger.warning("No target columns provided — all source cols unlinkable.")
#         return ScopingResult(
#             linkable_source_columns=[],
#             unlinkable_source_columns=[c.name for c in source_columns]
#         )

#     θ = threshold if threshold is not None else settings.scoping_threshold
#     model = get_model()

#     # Include data type in text for richer embedding
#     # "fname varchar" embeds differently from "fname date"
#     src_texts = [
#         f"{c.name} {c.data_type}" for c in source_columns
#     ]
#     tgt_texts = [
#         f"{c.name} {c.data_type}" for c in target_columns
#     ]

#     src_embs = model.encode(src_texts, normalize_embeddings=True)
#     tgt_embs = model.encode(tgt_texts, normalize_embeddings=True)

#     # sim[i][j] = similarity between source col i and target col j
#     sim = cosine_similarity(src_embs, tgt_embs)

#     linkable   = []
#     unlinkable = []

#     for i, col in enumerate(source_columns):
#         max_sim = float(np.max(sim[i]))

#         if max_sim >= θ:
#             linkable.append(col)
#             logger.debug(
#                 "LINKABLE   %s (max_sim=%.3f)", col.name, max_sim)
#         else:
#             unlinkable.append(col.name)
#             logger.debug(
#                 "UNLINKABLE %s (max_sim=%.3f < θ=%.2f)",
#                 col.name, max_sim, θ)

#     logger.info(
#         "Scoping complete: %d/%d linkable (θ=%.2f)",
#         len(linkable), len(source_columns), θ)

#     return ScopingResult(
#         linkable_source_columns=linkable,
#         unlinkable_source_columns=unlinkable
#     )

def scope_columns(
    source_columns: list[ColumnDto],
    target_columns: list[ColumnDto],
    threshold: float | None = None
) -> ScopingResult:

    if not target_columns:
        return ScopingResult(
            linkable_source_columns=[],
            unlinkable_source_columns=[c.name for c in source_columns]
        )

    θ = threshold if threshold is not None else settings.scoping_threshold
    model = get_model()

    src_texts = [f"{c.name} {c.data_type}" for c in source_columns]
    tgt_texts = [f"{c.name} {c.data_type}" for c in target_columns]

    src_embs = model.encode(src_texts, normalize_embeddings=True)
    tgt_embs = model.encode(tgt_texts, normalize_embeddings=True)

    sim = cosine_similarity(src_embs, tgt_embs)

    # ── Adaptive threshold ─────────────────────────────────────────────
    # When average maximum similarity is low (< 0.50), schemas are likely
    # from different domains — Joinable rather than Unionable scenario.
    # Raise threshold to reduce false positives.
    # Grounded in: Traeger et al. (EDBT 2026) who evaluate θ ∈ {0.15..0.35}
    # and recommend adaptive selection based on schema characteristics.
    avg_max_sim = float(np.mean(np.max(sim, axis=1)))
    if avg_max_sim < 0.50:
        θ = max(θ, 0.45)
        logger.info(
            "Low schema overlap (avg_max_sim=%.3f) — "
            "adaptive threshold raised to %.2f",
            avg_max_sim, θ)

    linkable   = []
    unlinkable = []

    for i, col in enumerate(source_columns):
        max_sim = float(np.max(sim[i]))
        if max_sim >= θ:
            linkable.append(col)
            logger.debug("LINKABLE   %s (max_sim=%.3f)", col.name, max_sim)
        else:
            unlinkable.append(col.name)
            logger.debug("UNLINKABLE %s (max_sim=%.3f < θ=%.2f)",
                         col.name, max_sim, θ)

    logger.info("Scoping: %d/%d linkable (θ=%.2f, avg_max=%.3f)",
                len(linkable), len(source_columns), θ, avg_max_sim)

    return ScopingResult(
        linkable_source_columns=linkable,
        unlinkable_source_columns=unlinkable
    )