"""
services/candidate_retriever.py

Phase 2c — SLM Candidate Retrieval
Based on: Liu et al., Magneto, VLDB 2025 (retrieval phase)

For each linkable source column, finds the top-k most
similar target columns using embedding cosine similarity.

This is the recall-focused phase — we want to make sure
the correct match is in the candidate list even if it is
not ranked first. The classifier (Phase 2d) then picks
the best from these candidates.

top_k is a hyperparameter. Evaluated at {1, 3, 5} in
Chapter 8. Higher k = higher recall, more work for classifier.
"""
import logging
import numpy as np
from sklearn.metrics.pairwise import cosine_similarity
from ..models.schemas import ColumnDto, CandidateMatch
from ..services.scoping import get_model
from ..config import settings

logger = logging.getLogger(__name__)


def retrieve_candidates(
    source_columns: list[ColumnDto],
    target_columns: list[ColumnDto],
    top_k: int | None = None
) -> dict[str, list[CandidateMatch]]:
    """
    For each source column returns top-k target candidates.

    Returns:
        { source_column_name → [CandidateMatch, ...] }
        Sorted by similarity descending.
        Length is min(top_k, len(target_columns)).
    """
    if not source_columns or not target_columns:
        return {}

    k = top_k if top_k is not None else settings.retrieval_top_k
    model = get_model()

    src_texts = [
        f"{c.name} {c.data_type}" for c in source_columns
    ]
    tgt_texts = [
        f"{c.name} {c.data_type}" for c in target_columns
    ]

    src_embs = model.encode(src_texts, normalize_embeddings=True)
    tgt_embs = model.encode(tgt_texts, normalize_embeddings=True)

    sim = cosine_similarity(src_embs, tgt_embs)

    results: dict[str, list[CandidateMatch]] = {}

    for i, src_col in enumerate(source_columns):
        # Build (target_name, score) pairs and sort descending
        scores = [
            (target_columns[j].name, float(sim[i][j]))
            for j in range(len(target_columns))
        ]
        scores.sort(key=lambda x: x[1], reverse=True)

        results[src_col.name] = [
            CandidateMatch(
                source_column = src_col.name,
                target_column = tgt_name,
                similarity    = round(score, 4)
            )
            for tgt_name, score in scores[:k]
        ]

        logger.debug(
            "Candidates for '%s': %s",
            src_col.name,
            [(c.target_column, c.similarity)
             for c in results[src_col.name]]
        )

    return results