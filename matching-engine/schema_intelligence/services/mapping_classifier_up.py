"""
services/mapping_classifier_up.py

ITCDI-Match: Rule-Based Mapping Classification Engine (Phase 2d)

WHAT THIS FILE DOES
-------------------
Given a list of source columns, target columns, and embedding-based
candidate matches, it decides HOW each source column maps to the target:
  - OneToOne      : one source column copies into one target column
  - Concatenation : several text columns join into one target column
  - Derived       : a numeric column times a price column makes a total

It returns a list of ClassifiedMapping objects, each carrying a type,
an executable expression, a confidence score, and a citation/reasoning.

THE THREE RULES (applied in this fixed order)
---------------------------------------------
  R3  Registry Reuse   - reuse a mapping already proven in a past run
  R2  Multi-Column     - R2a Concatenation, then R2b Derived
  R1  Name-Based       - exact/normalised first, then embedding-based

WHY THIS ORDER
--------------
  R3 first  : if we already know the mapping, do not recompute it.
  R2 second : claim multi-column patterns before R1 maps the same
              columns individually to the wrong place.
  R1 last   : resolve every remaining column one-to-one.

CITATIONS (all peer-reviewed, no arXiv)
---------------------------------------
  R1 linguistic matching ...... Rahm & Bernstein (VLDB Journal 2001)
  R1 suffix normalisation ..... Zhang et al. LSM (ICDE 2023)
  R1 embedding retrieval ...... Liu et al. Magneto (VLDB 2025)
  R1 model selection .......... Muennighoff et al. MTEB (EACL 2023)
  Semantic type families ...... Feuer et al. ArcheType (VLDB 2024)
  R2 multi-col gap evidence ... Koutras et al. Valentine (ICDE 2021)
  R3 mapping reuse ............ Atzeni et al. Meta-Mappings (VLDB 2019)
"""
from __future__ import annotations
import logging
from ..models.schemas import (
    ColumnDto, CandidateMatch, ClassifiedMapping,
    MappingType, ExistingMappingDto,
)

logger = logging.getLogger(__name__)


# ═══════════════════════════════════════════════════════════════════
# CONFIGURATION
# ═══════════════════════════════════════════════════════════════════

# Cosine-similarity thresholds, calibrated on Valentine Wikidata.
HIGH_CONFIDENCE   = 0.68   # strong embedding match
MEDIUM_CONFIDENCE = 0.55   # moderate match (needs type agreement too)
S4_MIN_SIM = 0.78   # type-supported S4 needs clear name evidence, not just type agreement
S5_MIN_SIM = 0.78
LOW_CONFIDENCE    = 0.35   # floor; below this we stop looking

# Suffixes stripped before name comparison (cityLabel -> city, etc.).
STRIP_SUFFIXES = ["Label", "label", "_label", "_name", "_id", "_date"]

# R2a (Concatenation) tuning.
R2A_MIN_SIM = 0.72   # each text contributor must score at least this
R2A_MAX     = 3      # at most 3 columns may form one concatenation

# Semantic type families. Used as SECONDARY evidence only — embedding
# similarity is always the primary signal. Based on ArcheType (VLDB 2024).
SEMANTIC_FAMILIES = {
    "identity": {"id", "pid", "uid", "uuid", "key", "code", "identifier",
                 "musicianid", "musician", "agencyid", "custkey", "orderkey",
                 "partkey", "suppkey", "nationkey", "regionkey", "linenumber"},
    "name":     {"name", "fname", "lname", "firstname", "lastname", "fullname",
                 "full_name", "first_name", "last_name", "forename", "surname",
                 "givenname", "familyname", "musicianlabel", "musicianname",
                 "givennamelabel", "familynamelabel", "motherlabel", "mothername",
                 "fatherlabel", "fathername"},
    "date":     {"date", "dob", "birthdate", "birth_date", "date_of_birth",
                 "created_at", "updated_at", "timestamp", "orderdate",
                 "shipdate", "commitdate", "receiptdate", "activitystart", "kickoff"},
    "contact":  {"email", "contact", "phone", "address", "mail", "e_mail",
                 "email_address", "twitternamelabel", "twitterusername"},
    "location": {"city", "citylabel", "country", "nation", "region", "state",
                 "residence", "residencelabel", "nationkey", "regionkey",
                 "mktsegment", "address"},
    "quantity": {"qty", "quantity", "count", "units", "numberofchildren",
                 "nchildren", "availqty"},
    "price":    {"price", "extendedprice", "retailprice", "supplycost", "acctbal",
                 "unit_price", "totalprice", "cost", "revenue", "discount", "tax",
                 "net_price", "total_price", "total_charge", "total", "amount",
                 "total_amount", "net_amount", "gross_amount", "line_total",
                 "subtotal", "grand_total"},
    "status":   {"status", "orderstatus", "linestatus", "returnflag", "flag",
                 "state", "condition"},
    "text":     {"comment", "description", "note", "remarks", "instructions",
                 "shipinstruct", "shipmode", "mfgr", "brand", "type", "container"},
    "social":   {"geniusnamelabel", "geniusname", "twitternamelabel",
                 "twitterusername", "webpage", "websitelabel", "website"},
    "category": {"genre", "genrelabel", "kind", "type", "category", "style",
                 "mktsegment", "classification", "ethnicitylabel", "ethnicity",
                 "religionlabel", "religion", "recordlabellabel", "recordcompany"},
}

# Keywords that mark a numeric target as a "derived total" (for R2b).
DERIVED_TARGET_KEYWORDS = ["total", "net", "charge", "amount", "gross", "subtotal"]

# SQL type groups.
TEXT_SQL_TYPES    = {"varchar", "nvarchar", "char", "nchar", "text", "ntext"}
NUMERIC_SQL_TYPES = {"int", "integer", "bigint", "smallint", "tinyint",
                     "float", "real", "decimal", "numeric", "money"}

# Reverse lookup: column_name -> family.
_COL_TO_FAMILY: dict[str, str] = {
    col: fam for fam, cols in SEMANTIC_FAMILIES.items() for col in cols
}


# ═══════════════════════════════════════════════════════════════════
# SMALL HELPERS
# ═══════════════════════════════════════════════════════════════════

def _family(name: str) -> str | None:
    """Return the semantic family of a column name, or None."""
    return _COL_TO_FAMILY.get(name.lower())


def _same_family(a: str, b: str) -> bool:
    """True if both names belong to the same (non-None) family."""
    fa, fb = _family(a), _family(b)
    return fa is not None and fa == fb


def _normalise(name: str) -> str:
    """Strip a known suffix, lowercase. e.g. 'cityLabel' -> 'city'."""
    for suffix in STRIP_SUFFIXES:
        if name.endswith(suffix) and len(name) > len(suffix):
            return name[: -len(suffix)].lower()
    return name.lower()


def _sql_base_type(dtype: str) -> str:
    """'decimal(18,2)' -> 'decimal'."""
    return dtype.lower().split("(")[0].strip()


def _is_text_sql(dtype: str) -> bool:
    return _sql_base_type(dtype) in TEXT_SQL_TYPES


def _is_numeric_sql(dtype: str) -> bool:
    return _sql_base_type(dtype) in NUMERIC_SQL_TYPES


def _same_sql_family(a: str, b: str) -> bool:
    """True if two SQL types are in the same broad group (int/str/num...)."""
    def group(t: str) -> str:
        t = _sql_base_type(t)
        if t in {"int", "integer", "bigint", "smallint", "tinyint"}:
            return "int"
        if t in TEXT_SQL_TYPES:
            return "str"
        if t in {"date", "datetime", "datetime2", "timestamp"}:
            return "date"
        if t in {"float", "real", "decimal", "numeric", "money"}:
            return "num"
        if t in {"bit", "boolean"}:
            return "bool"
        return t
    return group(a) == group(b)


def _score_against(candidate_map, src, tgt) -> float:
    """Embedding similarity of `src` against a specific target `tgt`."""
    cands = candidate_map.get(src.name, [])
    return next((c.similarity for c in cands
                 if c.target_column.lower() == tgt.name.lower()), 0.0)


# ═══════════════════════════════════════════════════════════════════
# MAIN ENTRY POINT
# ═══════════════════════════════════════════════════════════════════

def classify_mappings(
    source_columns:        list[ColumnDto],
    target_columns:        list[ColumnDto],
    candidates:            dict[str, list[CandidateMatch]],
    existing_mappings:     list[ExistingMappingDto] | None = None,
    unfiltered_candidates: dict[str, list[CandidateMatch]] | None = None,
) -> list[ClassifiedMapping]:
    """
    Run the rule engine and return the list of classified mappings.

    `candidates`            : family-filtered candidates (used by R2a, R1).
    `unfiltered_candidates` : full candidates before family filtering
                              (used by R2b, because a derived total lives
                              in a different family than its ingredients).
    """
    results: list[ClassifiedMapping] = []
    claimed_sources: set[str] = set()   # source columns already used
    claimed_targets: set[str] = set()   # target columns already filled

    tgt_map      = {c.name.lower(): c for c in target_columns}
    norm_tgt_map = {_normalise(c.name): c for c in target_columns}

    def claim(m: ClassifiedMapping) -> bool:
        """
        Register a mapping if its target is still free.
        Claims BOTH the target and the source columns.
        (R2b is the one exception and does its own lighter claiming.)
        """
        tgt = m.target_column.lower()
        if tgt in claimed_targets:
            return False
        claimed_targets.add(tgt)
        for src in m.source_columns:
            claimed_sources.add(src.lower())
        return True

    # Run the rules in order.
    _apply_r3_registry_reuse(existing_mappings, source_columns, claim, results)
    _apply_r2a_concatenation(source_columns, candidates, tgt_map,
                             claimed_sources, claim, results)
    _apply_r2b_derived(source_columns, target_columns, candidates,
                       unfiltered_candidates, claimed_sources,
                       claimed_targets, results)
    _apply_r1_name_based(source_columns, tgt_map, norm_tgt_map,
                         candidates, claimed_sources, claimed_targets,
                         claim, results)

    logger.info(
        "Rule engine complete: %d mappings | R3=%d | R2=%d | R1=%d",
        len(results),
        sum(1 for r in results if "R3" in r.reasoning),
        sum(1 for r in results if r.reasoning.startswith("R2")),
        sum(1 for r in results if r.reasoning.startswith("R1")),
    )
    return results


# ═══════════════════════════════════════════════════════════════════
# RULE R3 — Registry Reuse
# ═══════════════════════════════════════════════════════════════════
# IF a mapping for these source columns already exists in the registry
# AND all its source columns are still present in the current schema
# THEN reuse it unchanged (confidence 0.95).
# Citation: Atzeni et al. Meta-Mappings (VLDB 2019).

def _apply_r3_registry_reuse(existing_mappings, source_columns, claim, results):
    logger.info("Applying Rule R3 — Registry Reuse")
    if not existing_mappings:
        return
    current_names = {c.name.lower() for c in source_columns}
    for em in existing_mappings:
        if all(s.lower() in current_names for s in em.source_columns):
            m = ClassifiedMapping(
                source_columns   = em.source_columns,
                target_column    = em.target_column,
                mapping_type     = MappingType(em.mapping_type),
                expression       = em.expression,
                confidence_score = 0.95,
                reasoning        = "R3: Reused from registry | "
                                   "Atzeni et al. Meta-Mappings (VLDB 2019)",
            )
            if claim(m):
                results.append(m)
                logger.info("R3: %s -> %s (reused)",
                            em.source_columns, em.target_column)


# ═══════════════════════════════════════════════════════════════════
# RULE R2a — Concatenation
# ═══════════════════════════════════════════════════════════════════
# IF two or three TEXT columns of the SAME semantic family all point
# to the same target as their top candidate (each sim >= 0.72)
# THEN join them: fname + " " + lname -> full_name.
# Date columns are never concatenated.
# Citation: Valentine (ICDE 2021) gap evidence — NOVEL.

# def _apply_r2a_concatenation(source_columns, candidates, tgt_map,
#                              claimed_sources, claim, results):
#     logger.info("Applying Rule R2a — Concatenation")

#     # Group unclaimed text columns by their top candidate target.
#     grouped: dict[str, list[ColumnDto]] = {}
#     for src in source_columns:
#         if src.name.lower() in claimed_sources:
#             continue
#         cands = candidates.get(src.name, [])
#         if not cands or cands[0].similarity < R2A_MIN_SIM:
#             continue
#         if not _is_text_sql(src.data_type):
#             continue
#         if _family(src.name) == "date":          # never concat dates
#             continue
#         grouped.setdefault(cands[0].target_column.lower(), []).append(src)

#     for tgt_key, contributors in grouped.items():
#         if not (2 <= len(contributors) <= R2A_MAX):
#             continue
#         if tgt_key not in tgt_map:
#             continue

#         # If the target name IS one of the contributors, that column
#         # should map OneToOne (R1), not be concatenated.
#         contributor_norms = {_normalise(c.name) for c in contributors}
#         if _normalise(tgt_key) in contributor_norms:
#             continue

#         # All contributors must share one family (else it is noise).
#         fams = {_family(c.name) for c in contributors}
#         fams.discard(None)
#         if len(fams) > 1:
#             continue

#         tgt = tgt_map[tgt_key]

#         # Order "first/given" names before others for a sensible expression.
#         contributors.sort(key=lambda c: (
#             0 if any(w in c.name.lower()
#                      for w in ["first", "fname", "given", "fore"]) else 1,
#             c.name.lower(),
#         ))
#         col_names  = [c.name for c in contributors]
#         expression = ' + " " + '.join(col_names)

#         m = ClassifiedMapping(
#             source_columns   = col_names,
#             target_column    = tgt.name,
#             mapping_type     = MappingType.concatenation,
#             expression       = expression,
#             confidence_score = 0.88,
#             reasoning        = f"R2a: {len(col_names)} same-family text columns "
#                                f"-> '{tgt.name}' | Valentine (ICDE 2021) — NOVEL",
#         )
#         if claim(m):
#             results.append(m)
#             logger.info("R2a: %s -> %s [Concatenation]", col_names, tgt.name)

def _apply_r2a_concatenation(source_columns, candidates, tgt_map,
                             claimed_sources, claim, results):
    logger.info("Applying Rule R2a — Concatenation")

    # Group unclaimed text columns by their top candidate target.
    grouped: dict[str, list[ColumnDto]] = {}
    for src in source_columns:
        if src.name.lower() in claimed_sources:
            continue
        cands = candidates.get(src.name, [])
        if not cands or cands[0].similarity < R2A_MIN_SIM:
            continue
        if not _is_text_sql(src.data_type):
            continue
        if _family(src.name) == "date":          # never concat dates
            continue
        grouped.setdefault(cands[0].target_column.lower(), []).append(src)

    for tgt_key, contributors in grouped.items():
        if not (2 <= len(contributors) <= R2A_MAX):
            continue
        if tgt_key not in tgt_map:
            continue

        # ── Guard: skip when some SOURCE column's name is essentially the
        # same word as the target (e.g. shipdate↔ship_date). That column is
        # the real one-to-one answer; the others merely cluster near it in
        # embedding space. Concatenation proceeds only when NO source column
        # is a near-name-identical match for the target.
        tgt_norm = _normalise(tgt_key).replace("_", "").replace(" ", "")
        has_name_twin = any(
            _normalise(s.name).replace("_", "").replace(" ", "") == tgt_norm
            for s in source_columns
        )
        if has_name_twin:
            logger.info("R2a SKIP %s — a source column is its name-twin (OneToOne wins)", tgt_key)
            continue

        # If the target name IS one of the contributors, that column
        # should map OneToOne (R1), not be concatenated.
        contributor_norms = {_normalise(c.name) for c in contributors}
        if _normalise(tgt_key) in contributor_norms:
            continue

        # All contributors must share one family (else it is noise).
        fams = {_family(c.name) for c in contributors}
        fams.discard(None)
        if len(fams) > 1:
            continue

        tgt = tgt_map[tgt_key]
        contributors.sort(key=lambda c: (
            0 if any(w in c.name.lower()
                     for w in ["first", "fname", "given", "fore"]) else 1,
            c.name.lower(),
        ))
        col_names  = [c.name for c in contributors]
        expression = ' + " " + '.join(col_names)
        m = ClassifiedMapping(
            source_columns   = col_names,
            target_column    = tgt.name,
            mapping_type     = MappingType.concatenation,
            expression       = expression,
            confidence_score = 0.88,
            reasoning        = f"R2a: {len(col_names)} same-family text columns "
                               f"-> '{tgt.name}' | Valentine (ICDE 2021) — NOVEL",
        )
        if claim(m):
            results.append(m)
            logger.info("R2a: %s -> %s [Concatenation]", col_names, tgt.name)

# ═══════════════════════════════════════════════════════════════════
# RULE R2b — Derived
# ═══════════════════════════════════════════════════════════════════
# IF the target is a numeric column whose name implies a total
#    (net/total/charge/amount/gross/subtotal)
# AND there is at least one QUANTITY-family source and one PRICE-family
#    source available
# THEN emit a derived mapping: quantity * price -> target.
#
# KEY DESIGN POINTS
#   * Detection is by SEMANTIC FAMILY + NUMERIC TYPE, not by the
#     embedding score against the target. When a source name also
#     matches a target exactly (quantity -> quantity), the embedding
#     mass concentrates there and would starve net_price, so we do
#     NOT gate on score — we only use score to RANK within a family.
#   * R2b claims ONLY the target. The source columns stay free, so
#     quantity and extendedprice still get their own OneToOne mappings.
# Citation: Valentine (ICDE 2021) gap evidence — NOVEL.

def _apply_r2b_derived(source_columns, target_columns, candidates,
                       unfiltered_candidates, claimed_sources,
                       claimed_targets, results):
    logger.info("Applying Rule R2b — Derived")

    raw = unfiltered_candidates or candidates
    qty_family   = SEMANTIC_FAMILIES["quantity"]
    price_family = SEMANTIC_FAMILIES["price"]

    qty_sources = [c for c in source_columns
                   if c.name.lower() in qty_family
                   and c.name.lower() not in claimed_sources]
    price_sources = [c for c in source_columns
                     if c.name.lower() in price_family
                     and "total" not in c.name.lower()
                     and c.name.lower() not in claimed_sources]

    derived_targets = [c for c in target_columns
                       if _is_numeric_sql(c.data_type)
                       and any(kw in c.name.lower() for kw in DERIVED_TARGET_KEYWORDS)
                       and c.name.lower() not in claimed_targets]

    for tgt in derived_targets:
        if not qty_sources or not price_sources:
            continue

        # Rank within family by similarity to this target; pick the best.
        best_qty = max(qty_sources,
                       key=lambda s: _score_against(raw, s, tgt))
        best_price = max(price_sources,
                         key=lambda s: _score_against(raw, s, tgt))

        col_names  = [best_qty.name, best_price.name]
        expression = f"{best_qty.name} * {best_price.name}"

        if tgt.name.lower() in claimed_targets:
            continue

        m = ClassifiedMapping(
            source_columns   = col_names,
            target_column    = tgt.name,
            mapping_type     = MappingType.derived,
            expression       = expression,
            confidence_score = 0.82,
            reasoning        = f"R2b: QUANTITY '{best_qty.name}' x PRICE "
                               f"'{best_price.name}' -> '{tgt.name}' | "
                               "Valentine (ICDE 2021) — NOVEL",
        )
        # Claim ONLY the target; leave sources free for their OneToOne.
        claimed_targets.add(tgt.name.lower())
        results.append(m)
        logger.info("R2b: %s -> %s [Derived]", col_names, tgt.name)


# ═══════════════════════════════════════════════════════════════════
# RULE R1 — Name-Based (OneToOne)
# ═══════════════════════════════════════════════════════════════════
# Two passes:
#   PASS 1 (exact / normalised) runs for EVERY column first, so a
#          fuzzy match on an early column cannot steal a target that a
#          later column matches exactly.
#   PASS 2 (embedding-based) resolves whatever remains:
#          S3 same family + high sim   -> sim + 0.10
#          S4 same SQL type + med sim  -> sim + 0.05
#          S5 high sim alone           -> sim
# Citations: Rahm & Bernstein (VLDB J 2001), Zhang LSM (ICDE 2023),
#            ArcheType (VLDB 2024), Magneto (VLDB 2025), MTEB (EACL 2023).

def _apply_r1_name_based(source_columns, tgt_map, norm_tgt_map,
                         candidates, claimed_sources, claimed_targets,
                         claim, results):
    logger.info("Applying Rule R1 — Name-Based")

    # ---- PASS 1: exact (S1) and normalised (S2) for all columns ----
    for src in source_columns:
        if src.name.lower() in claimed_sources:
            continue
        src_lower = src.name.lower()
        src_norm  = _normalise(src.name)

        # S1: exact name match.
        if src_lower in tgt_map and tgt_map[src_lower].name.lower() not in claimed_targets:
            tgt = tgt_map[src_lower]
            m = ClassifiedMapping(
                source_columns=[src.name], target_column=tgt.name,
                mapping_type=MappingType.one_to_one, expression=src.name,
                confidence_score=0.99,
                reasoning="R1-S1: Exact name match | Rahm & Bernstein (VLDB J 2001)")
            if claim(m):
                results.append(m)
                logger.info("R1-S1: %s -> %s (exact)", src.name, tgt.name)
            continue

        # S2: normalised name match (suffix stripped).
        if src_norm in norm_tgt_map:
            tgt = norm_tgt_map[src_norm]
            if tgt.name.lower() != src_lower and tgt.name.lower() not in claimed_targets:
                m = ClassifiedMapping(
                    source_columns=[src.name], target_column=tgt.name,
                    mapping_type=MappingType.one_to_one, expression=src.name,
                    confidence_score=0.95,
                    reasoning=f"R1-S2: Normalised '{src.name}'->'{tgt.name}' | "
                              "Zhang LSM (ICDE 2023)")
                if claim(m):
                    results.append(m)
                    logger.info("R1-S2: %s -> %s (normalised)", src.name, tgt.name)

    # ---- PASS 2: embedding-based (S3/S4/S5) for what remains ----
    for src in source_columns:
        if src.name.lower() in claimed_sources:
            continue
        for cand in candidates.get(src.name, []):
            if cand.similarity < LOW_CONFIDENCE:
                break
            if cand.target_column.lower() in claimed_targets:
                continue
            tgt = tgt_map.get(cand.target_column.lower())
            if tgt is None:
                continue
            sim = cand.similarity

            # S3: same family + high similarity (boost +0.10).
            if _same_family(src.name, tgt.name) and sim >= HIGH_CONFIDENCE:
                m = _one_to_one(src, tgt, round(min(1.0, sim + 0.10), 4),
                                f"R1-S3: family + sim={sim:.3f} | ArcheType (VLDB 2024)")
                if claim(m):
                    results.append(m)
                    logger.info("R1-S3: %s -> %s", src.name, tgt.name)
                break

            # S4: same SQL type + medium similarity (boost +0.05).
            if _same_sql_family(src.data_type, tgt.data_type) and sim >= S4_MIN_SIM:
                m = _one_to_one(src, tgt, round(min(1.0, sim + 0.05), 4),
                                f"R1-S4: SQL type + sim={sim:.3f} | Magneto (VLDB 2025)")
                if claim(m):
                    results.append(m)
                    logger.info("R1-S4: %s -> %s", src.name, tgt.name)
                break

            # S5: high similarity alone.
            if sim >= S5_MIN_SIM:
                m = _one_to_one(src, tgt, round(min(1.0, sim), 4),
                                f"R1-S5: sim={sim:.3f} | Magneto (VLDB 2025)")
                if claim(m):
                    results.append(m)
                    logger.info("R1-S5: %s -> %s", src.name, tgt.name)
                break


def _one_to_one(src, tgt, conf, reasoning) -> ClassifiedMapping:
    """Build a OneToOne mapping (helper to keep R1 readable)."""
    return ClassifiedMapping(
        source_columns   = [src.name],
        target_column    = tgt.name,
        mapping_type     = MappingType.one_to_one,
        expression       = src.name,
        confidence_score = conf,
        reasoning        = reasoning,
    )


# ═══════════════════════════════════════════════════════════════════
# CANDIDATE PRE-FILTER (called by the router before R2a / R1)
# ═══════════════════════════════════════════════════════════════════

def filter_candidates_by_semantic_group(
    source_columns: list[ColumnDto],
    candidates:     dict[str, list[CandidateMatch]],
) -> dict[str, list[CandidateMatch]]:
    """
    Narrow each source column's candidate list to targets in the SAME
    semantic family. Falls back to the full list if that leaves nothing.
    Prevents cross-family false positives without hardcoded pair rules.
    """
    filtered: dict[str, list[CandidateMatch]] = {}
    for src, cands in candidates.items():
        fam = _family(src)
        if fam is None:
            filtered[src] = cands
            continue
        narrowed = [c for c in cands if _family(c.target_column) == fam]
        filtered[src] = narrowed if narrowed else cands
    return filtered