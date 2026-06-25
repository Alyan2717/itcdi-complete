"""
routers/matching.py

POST /match  — called by C# SchemaMatchingClient
GET  /health — called by health checks

Pipeline:
  Phase 2a: Scoping     — prune unlinkable source columns
             (Traeger et al., EDBT 2026)
  Phase 2c: Retrieval   — SLM cosine similarity top-k candidates
             (Liu et al. Magneto, VLDB 2025)
  Phase 2c+: Filtering  — restrict candidates to same semantic group
             (Feuer et al. ArcheType, VLDB 2024)
  Phase 2d: Classify    — rule-based mapping type classification R0-R7
             (YOUR NOVEL CONTRIBUTION)
"""
import time
import logging
from fastapi import APIRouter
from ..models.schemas import MatchRequest, MatchResponse, MappingDto
from ..services.scoping import scope_columns
from ..services.candidate_retriever import retrieve_candidates
from ..services.mapping_classifier_up import (classify_mappings, filter_candidates_by_semantic_group)
# from ..services.mapping_classifier import (classify_mappings, filter_candidates_by_semantic_group)
from ..config import settings

logger = logging.getLogger(__name__)
router = APIRouter()


async def _apply_llm_fallback_async(
    unresolved:      list,
    target_columns:  list,
    claimed_targets: set,
    tgt_map:         dict,
) -> list:
    from ..services.llm_interface import call_llm
    from ..models.schemas import ClassifiedMapping, MappingType
    import json

    available = [c.name for c in target_columns
                 if c.name.lower() not in claimed_targets]
    if not available:
        return []

    prompt = f"""You are a schema matching expert.
    Match each source column to the best target column.

    Source columns (unmatched): {[c.name for c in unresolved]}
    Available target columns: {available}

    Rules:
    - Only match if there is a genuine semantic relationship
    - Return null for target_column if no good match exists
    - confidence must be between 0.0 and 1.0

    Return ONLY a JSON array like:
    [{{"source_column": "genreLabel", "target_column": "kind", "confidence": 0.85}}]

    JSON only, no other text:"""

    raw = await call_llm(prompt, max_tokens=500)
    results = []

    try:
        clean = raw.strip()
        if "```" in clean:
            clean = clean.split("```")[1]
            if clean.startswith("json"):
                clean = clean[4:]
        clean = clean.strip()

        items = json.loads(clean)
        for item in items:
            src_name = item.get("source_column","")
            tgt_name = item.get("target_column")
            # conf     = float(item.get("confidence", 0.0))
            conf = item.get("confidence", 0.0)
            if conf is None:
                conf = 0.0
            conf = float(conf)

            if not tgt_name or conf < 0.35:
                continue
            if tgt_name.lower() not in tgt_map:
                continue
            if tgt_name.lower() in claimed_targets:
                continue

            tgt = tgt_map[tgt_name.lower()]
            results.append(ClassifiedMapping(
                source_columns   = [src_name],
                target_column    = tgt.name,
                mapping_type     = MappingType.one_to_one,
                expression       = src_name,
                confidence_score = round(min(1.0, conf), 4),
                reasoning        = "LLM fallback (Llama 3.1 via Ollama) | "
                                   "ArcheType (VLDB 2024), Touvron et al. 2023"
            ))
            claimed_targets.add(tgt_name.lower())

    except Exception as e:
        logger.warning("LLM parse failed: %s | raw: %s", e, raw[:100])

    return results


@router.post("/match", response_model=MatchResponse)
async def match(req: MatchRequest) -> MatchResponse:
    t0 = time.monotonic()

    logger.info(
        "Match request | source_id=%s | src=%d cols | tgt=%d cols",
        req.source_id,
        len(req.source_columns),
        len(req.target_columns)
    )

    # ── Phase 2a: Scoping ─────────────────────────────────────────────
    # Prune source columns with no viable target match.
    # Adaptive threshold: raises to 0.45 for cross-domain schemas.
    # Traeger et al. (EDBT 2026): +80% precision improvement.
    scoping = scope_columns(req.source_columns, req.target_columns)

    if not scoping.linkable_source_columns:
        logger.warning(
            "No linkable columns after scoping for %s", req.source_id)
        return MatchResponse(
            mappings=[],
            unlinkable_source_columns=[
                c.name for c in req.source_columns],
            processing_time_ms=0
        )

    logger.info(
        "Scoping: %d/%d linkable | unlinkable=%s",
        len(scoping.linkable_source_columns),
        len(req.source_columns),
        scoping.unlinkable_source_columns
    )

    # ── Phase 2c: Candidate Retrieval ─────────────────────────────────
    # Encode column names as dense vectors using BAAI/bge-large-en-v1.5.
    # Return top-k most similar target columns per source column.
    # Liu et al. Magneto (VLDB 2025): SLM retrieval phase.
    # Muennighoff et al. MTEB (EACL 2023): model selection.
    candidates = retrieve_candidates(
        scoping.linkable_source_columns,
        req.target_columns
    )

    # ── Phase 2c+: Semantic Group Filtering ──────────────────────────
    # Restrict each source column's candidate list to targets in the
    # same semantic type group. Prevents cross-group false positives
    # (e.g. motherLabel scoring above threshold against geniusNameLabel).
    # Falls back to full candidate list if no group match found.
    # Feuer et al. ArcheType (VLDB 2024): semantic type taxonomy.
    # candidates = filter_candidates_by_semantic_group(
    #     scoping.linkable_source_columns,
    #     candidates
    # )

    # # Sort each column's candidates by similarity descending
    # candidates = {
    #     src: sorted(cands, key=lambda c: c.similarity, reverse=True)
    #     for src, cands in candidates.items()
    # }

    # # ── Phase 2d: Mapping Classification (Rule Engine) ───────────────
    # # Apply rules R0-R7 in priority order.
    # # Each rule has formal condition, action, confidence, citation.
    # # Rule order: R6 → R5 → R0 → R1 → R7 → R2 → R3/R4 → LLM
    # classified = classify_mappings(
    #     scoping.linkable_source_columns,
    #     req.target_columns,
    #     candidates,
    #     req.existing_mappings
    # )

    # Store unfiltered for R2b Derived detection
    unfiltered_candidates = dict(candidates)  # copy before filtering

    # ── Phase 2c+: Semantic Group Filtering
    # candidates = filter_candidates_by_semantic_group(
    #     scoping.linkable_source_columns,
    #     candidates
    # )
    # candidates = {
    #     src: sorted(cands, key=lambda c: c.similarity, reverse=True)
    #     for src, cands in candidates.items()
    # }
    # ── Phase 2c+: Semantic Group Filtering (ablatable) ──────────────
    disable_filtering = bool(getattr(req, "disable_filtering", False))
    if not disable_filtering:
        candidates = filter_candidates_by_semantic_group(
            scoping.linkable_source_columns,
            candidates
        )
    else:
        logger.info("Filtering SKIPPED (disable_filtering=True)")

    # Sort each column's candidates by similarity descending (both paths)
    candidates = {
        src: sorted(cands, key=lambda c: c.similarity, reverse=True)
        for src, cands in candidates.items()
    }

    # ── Phase 2d: Classification
    classified = classify_mappings(
        scoping.linkable_source_columns,
        req.target_columns,
        candidates,
        req.existing_mappings,
        unfiltered_candidates=unfiltered_candidates,  # pass unfiltered
    )

    # ── LLM Fallback — unresolved columns ────────────────────────────
    # Columns that passed scoping but no rule R0-R7 resolved them.
    # classify_mappings is synchronous so LLM call happens here.
    claimed_targets = {m.target_column.lower() for m in classified}
    claimed_sources = {
        src.lower() for m in classified
        for src in m.source_columns
    }
    unresolved = [
        c for c in scoping.linkable_source_columns
        if c.name.lower() not in claimed_sources
    ]

    # Respect an optional disable_llm flag from the request (defaults to False).
    # Lets experiments run the deterministic rule engine alone (no LLM fallback).
    disable_llm = bool(getattr(req, "disable_llm", False))

    if unresolved and not disable_llm:
        logger.info("LLM fallback for %d columns: %s",
                    len(unresolved), [c.name for c in unresolved])
        try:
            llm_results = await _apply_llm_fallback_async(
                unresolved,
                req.target_columns,
                claimed_targets,
                {c.name.lower(): c for c in req.target_columns}
            )
            classified.extend(llm_results)
            if llm_results:
                logger.info("LLM resolved %d additional mappings",
                            len(llm_results))
        except Exception as e:
            logger.warning("LLM fallback failed: %s", e)
    elif unresolved and disable_llm:
        logger.info("LLM fallback SKIPPED (disable_llm=True) | %d unresolved: %s",
                    len(unresolved), [c.name for c in unresolved])

    mappings = [
        MappingDto(
            source_columns   = m.source_columns,
            target_column    = m.target_column,
            mapping_type     = m.mapping_type,
            expression       = m.expression,
            confidence_score = m.confidence_score,
            reasoning        = m.reasoning,
        )
        for m in classified
    ]

    elapsed_ms = int((time.monotonic() - t0) * 1000)

    logger.info(
        "Match complete | %d mappings | %dms | unlinkable=%s",
        len(mappings), elapsed_ms,
        scoping.unlinkable_source_columns
    )

    return MatchResponse(
        mappings=mappings,
        unlinkable_source_columns=scoping.unlinkable_source_columns,
        processing_time_ms=elapsed_ms
    )


@router.get("/health")
async def health():
    return {
        "status":       "ok",
        "llm_provider": settings.llm_provider,
        "model":        settings.ollama_model,
        "scoping_threshold": settings.scoping_threshold,
    }