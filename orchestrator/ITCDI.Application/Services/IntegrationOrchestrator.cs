using ITCDI.Core.Enums;
using ITCDI.Core.Interfaces;
using ITCDI.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Application.Services
{
    public class IntegrationOrchestrator
    {
        private readonly IDriftDetectionService _drift;
        private readonly IMappingRegistry _registry;
        private readonly ISchemaMatchingClient _matcher;
        private readonly ITransformationEngine _transformer;
        private readonly IConflictDetectionService _conflict;
        private readonly ITargetDbWriter _writer;
        private readonly IIntegrationRunRepository _runs;
        private readonly ITargetSchemaReader _targetSchemaReader;
        private readonly ILogger<IntegrationOrchestrator> _logger;

        public IntegrationOrchestrator(IDriftDetectionService drift, IMappingRegistry registry, ISchemaMatchingClient matcher,
                                       ITransformationEngine transformer, IConflictDetectionService conflict, ITargetDbWriter writer,
                                       IIntegrationRunRepository runs, ITargetSchemaReader targetSchemaReader,
                                       ILogger<IntegrationOrchestrator> logger)
        {
            _drift = drift;
            _registry = registry;
            _matcher = matcher;
            _transformer = transformer;
            _conflict = conflict;
            _writer = writer;
            _runs = runs;
            _targetSchemaReader = targetSchemaReader;
            _logger = logger;
        }

        //public async Task<IntegrationResult> RunAsync(IntegrationRequest request, CancellationToken ct = default)
        //{
        //    // ── Read target schema automatically from Database B ──────────────
        //    // This replaces the need to pass target columns in the request body.
        //    // The target schema T is fixed — we always read it fresh from the DB
        //    // so any changes to T are immediately reflected without redeployment.
        //    //if (request.TargetColumns.Count == 0)
        //    //{
        //    //    _logger.LogInformation("Reading target schema for {Table} from Database B", request.TargetTable);

        //    //    request.TargetColumns = await _targetSchemaReader.ReadTargetSchemaAsync(request.TargetTable);
        //    //}

        //    // ── Resolve PK first (needed for both bootstrap and normal paths) ──
        //    if (string.IsNullOrEmpty(request.PrimaryKeyColumn))
        //        request.PrimaryKeyColumn = _targetSchemaReader.GetPrimaryKeyColumn(request.TargetTable);

        //    // ── Infer source columns from row data if not provided ────────────
        //    if (request.SourceColumns.Count == 0 && request.SourceRows.Count > 0)
        //    {
        //        request.SourceColumns = request.SourceRows[0].Keys
        //            .Select(k => new SchemaColumn(
        //                Name: k,
        //                DataType: InferDataType(request.SourceRows, k),
        //                IsNullable: request.SourceRows.Any(r => r.TryGetValue(k, out var v) && v is null)))
        //            .ToList();
        //        _logger.LogInformation("Inferred {Count} source columns from row data", request.SourceColumns.Count);
        //    }

        //    // ── BOOTSTRAP DETECTION: is this the first iteration for this source? ──
        //    // First iteration  ⟺  no stored fingerprint AND no mappings yet.
        //    var storedFp = await _drift.GetStoredFingerprintAsync(request.SourceId);
        //    var hasMappingsBootstrap = await _registry.HasMappingsAsync(request.SourceId);
        //    bool isFirstIteration = storedFp is null && !hasMappingsBootstrap;

        //    if (isFirstIteration)
        //    {
        //        // The first source schema BECOMES the target, verbatim.
        //        _logger.LogInformation(
        //            "[BOOTSTRAP] First iteration for {Source}: source schema becomes the fixed target.",
        //            request.SourceId);

        //        await _targetSchemaReader.EnsureTargetTableAsync(
        //            request.TargetTable, request.SourceColumns, request.PrimaryKeyColumn);

        //        // Target columns = source columns (verbatim copy).
        //        request.TargetColumns = request.SourceColumns
        //            .Select(c => new SchemaColumn(c.Name, c.DataType, c.IsNullable))
        //            .ToList();
        //    }
        //    else
        //    {
        //        // Normal path: read the (already-existing) fixed target schema.
        //        if (request.TargetColumns.Count == 0)
        //            request.TargetColumns = await _targetSchemaReader.ReadTargetSchemaAsync(request.TargetTable);
        //    }

        //    // ── Resolve primary key column from config if not provided ────────
        //    if (string.IsNullOrEmpty(request.PrimaryKeyColumn))
        //    {
        //        request.PrimaryKeyColumn = _targetSchemaReader.GetPrimaryKeyColumn(request.TargetTable);
        //    }

        //    // ── Infer source columns from row data if not provided ────────────
        //    // In the API push model, the caller sends rows as dictionaries.
        //    // Column names are the dictionary keys — no need to declare them separately.
        //    if (request.SourceColumns.Count == 0 && request.SourceRows.Count > 0)
        //    {
        //        request.SourceColumns = request.SourceRows[0].Keys
        //            .Select(k => new SchemaColumn(
        //                Name: k,
        //                DataType: InferDataType(request.SourceRows, k),
        //                IsNullable: request.SourceRows.Any(r => r.TryGetValue(k, out var v) && v is null)))
        //            .ToList();

        //        _logger.LogInformation("Inferred {Count} source columns from row data", request.SourceColumns.Count);
        //    }

        //    // ── Create run record ─────────────────────────────────────────────
        //    var batchNumber = await _runs.GetNextBatchNumberAsync(request.SourceId);

        //    var run = new IntegrationRun
        //    {
        //        SourceId = request.SourceId,
        //        SourceTable = request.SourceTable,
        //        TriggeredBy = request.TriggeredBy,
        //        BatchNumber = batchNumber,
        //        RunMode = request.RunMode,
        //        RowsSubmitted = request.SourceRows.Count
        //    };

        //    await _runs.CreateRunAsync(run);

        //    _logger.LogInformation("=== Run {RunId} started | Source={Source} | Batch={Batch} ===", run.RunId, run.SourceId, run.BatchNumber);

        //    var metrics = new IntegrationMetrics
        //    {
        //        RunId = run.RunId,
        //        SourceId = run.SourceId,
        //        BatchNumber = run.BatchNumber
        //    };

        //    try
        //    {
        //        // ── Phase 1: Drift Detection ──────────────────────────────────
        //        _logger.LogInformation("[{RunId}] Phase 1 — Drift detection", run.RunId);

        //        var newFingerprint = _drift.ComputeFingerprint(request.SourceId, request.SourceColumns);

        //        var driftResult = await _drift.AnalyseDriftAsync(request.SourceId, newFingerprint);

        //        // ── Phase 2: Schema Matching or Reuse ────────────────────────
        //        _logger.LogInformation("[{RunId}] Phase 2 — Schema matching", run.RunId);

        //        var sw = Stopwatch.StartNew();
        //        List<MappingRule> mappings;

        //        var hasMappings = await _registry.HasMappingsAsync(request.SourceId);

        //        //bool reMatched = !(driftResult.CanReuseExistingMappings && hasMappings);

        //        //if (driftResult.CanReuseExistingMappings && hasMappings)
        //        //{
        //        //    // Reuse path — no Python call needed
        //        //    _logger.LogInformation("[{RunId}] Reusing existing mappings — Python skipped", run.RunId);

        //        //    mappings = await _registry.GetActiveMappingsAsync(request.SourceId);

        //        //    foreach (var m in mappings)
        //        //        await _registry.UpdateLastValidatedAsync(m.MappingId);

        //        //    metrics.TotalMappings = mappings.Count;
        //        //    metrics.UnchangedMappings = mappings.Count;
        //        //}
        //        // A ColumnAdd is LOW-impact (existing mappings stay valid) BUT the new
        //        // column still needs mapping — pure reuse would silently drop it. So if
        //        // the only drift is additive, we still reuse via R3 by passing existing
        //        // mappings to the matcher, which maps ONLY the new column(s).
        //        bool hasColumnAdd = driftResult.Events.Any(e => e.DriftType == DriftType.ColumnAdd);
        //        bool pureReuse = driftResult.CanReuseExistingMappings && hasMappings && !hasColumnAdd;

        //        bool reMatched = !(driftResult.CanReuseExistingMappings && hasMappings);

        //        if (pureReuse)
        //        {
        //            // Reuse path — no Python call needed (no new columns to map)
        //            _logger.LogInformation("[{RunId}] Reusing existing mappings — Python skipped", run.RunId);

        //            mappings = await _registry.GetActiveMappingsAsync(request.SourceId);

        //            foreach (var m in mappings)
        //                await _registry.UpdateLastValidatedAsync(m.MappingId);

        //            metrics.TotalMappings = mappings.Count;
        //            metrics.UnchangedMappings = mappings.Count;
        //        }
        //        else
        //        {
        //            // Re-match path — call Python
        //            _logger.LogInformation("[{RunId}] Calling Python schema matcher", run.RunId);

        //            // ── NEW: apply any detected renames to the registry FIRST ──
        //            // A rename was detected in Phase 1. Re-point the stored mappings'
        //            // source column names from old -> new BEFORE reading them, so the
        //            // Python R3 rule can reuse them (the renamed source now matches the
        //            // current schema) instead of re-mapping the column from scratch.
        //            if (driftResult.DriftDetected)
        //            {
        //                foreach (var e in driftResult.Events.Where(ev => ev.DriftType == DriftType.ColumnRename))
        //                {
        //                    // AffectedColumns = [oldName, newName]
        //                    var oldName = e.AffectedColumns[0];
        //                    var newName = e.AffectedColumns[1];
        //                    await _registry.RepointMappingSourceAsync(request.SourceId, oldName, newName);
        //                    _logger.LogInformation("[{RunId}] Re-pointed mapping '{Old}' -> '{New}' (rename)",
        //                                            run.RunId, oldName, newName);
        //                }
        //            }

        //            var existingMappings = await _registry.GetActiveMappingsAsync(request.SourceId);

        //            var matchResult = await _matcher.MatchAsync(
        //                request.SourceId,
        //                request.SourceColumns,
        //                request.TargetColumns,
        //                existingMappings,
        //                ct);

        //            // Count how many new mappings match old ones
        //            // This is your Mapping Stability Rate numerator
        //            var unchanged = CountUnchangedMappings(existingMappings, matchResult.Mappings);

        //            metrics.TotalMappings = matchResult.Mappings.Count;
        //            metrics.UnchangedMappings = unchanged;

        //            // Soft-delete old mappings, save new ones
        //            await _registry.DeactivateMappingsAsync(request.SourceId);

        //            mappings = matchResult.Mappings;

        //            foreach (var m in mappings)
        //                m.SchemaFingerprint = newFingerprint.Hash;

        //            mappings = SanitizeMappings(mappings);

        //            await _registry.SaveMappingsAsync(request.SourceId, mappings);
        //            await _drift.SaveFingerprintAsync(newFingerprint);

        //            // Sanitize mappings before proceeding to transformation

        //            // Log drift events if any
        //            //if (driftResult.DriftDetected)
        //            //{
        //            //    foreach (var e in driftResult.Events)
        //            //    {
        //            //        e.RunId = run.RunId;
        //            //        e.ReMatched = true;
        //            //    }
        //            //    await _drift.LogDriftEventsAsync(run.RunId, driftResult.Events);
        //            //}
        //        }

        //        sw.Stop();
        //        metrics.MatchingTimeMs = sw.ElapsedMilliseconds;

        //        // Guard: nothing to do if no mappings exist
        //        if (mappings.Count == 0)
        //        {
        //            _logger.LogWarning("[{RunId}] No mappings available. Integration cannot proceed.", run.RunId);

        //            return await FinaliseRunAsync(run, metrics, IntegrationStatus.Partial, [], driftResult.Events, "No mapping rules available.");
        //        }

        //        // ── Phase 3: Transformation ───────────────────────────────────
        //        _logger.LogInformation("[{RunId}] Phase 3 — Transforming {Count} rows", run.RunId, request.SourceRows.Count);

        //        sw.Restart();
        //        var transformed = _transformer.TransformBatch(request.SourceRows, mappings);
        //        sw.Stop();
        //        metrics.TransformationTimeMs = sw.ElapsedMilliseconds;

        //        // ── Phase 4: Conflict Detection ───────────────────────────────
        //        _logger.LogInformation("[{RunId}] Phase 4 — Conflict detection", run.RunId);

        //        sw.Restart();
        //        var conflictResult = await _conflict.DetectAndResolveAsync(
        //            run.RunId,
        //            transformed,
        //            request.TargetTable,
        //            request.ResolutionPolicy,
        //            request.SourceId);
        //        sw.Stop();
        //        metrics.ConflictDetectionTimeMs = sw.ElapsedMilliseconds;

        //        // ── Phase 5: Write to Target DB ───────────────────────────────
        //        _logger.LogInformation("[{RunId}] Phase 5 — Writing to target DB", run.RunId);

        //        sw.Restart();
        //        var writeResult = await _writer.UpsertAsync(
        //            run.RunId,
        //            request.SourceId,
        //            mappings.First().MappingId,
        //            request.TargetTable,
        //            conflictResult.ResolvedRows,
        //            request.PrimaryKeyColumn);
        //        sw.Stop();
        //        metrics.InsertionTimeMs = sw.ElapsedMilliseconds;
        //        metrics.RowsInserted = writeResult.Inserted;
        //        metrics.RowsUpdated = writeResult.Updated;
        //        metrics.RowsSkipped = writeResult.Skipped + conflictResult.RejectedRows;
        //        // ── HRR inputs ────────────────────────────────────────────────
        //        // RowsModifiedAfterIntegration = rows this batch UPDATED (rows that
        //        // already existed from a prior batch and were overwritten).
        //        // PreviouslyIntegratedRows = cumulative rows inserted by prior runs.
        //        metrics.RowsModifiedAfterIntegration = writeResult.Updated;
        //        metrics.PreviouslyIntegratedRows = await _runs.GetCumulativeInsertedRowsAsync(request.SourceId, run.RunId);

        //        // ── Persist drift events once, with complete run info ──────────
        //        // Logged here (not per-branch) so RowsAffected and ReMatched are
        //        // final and accurate, and so REUSE-path drift (e.g. LOW-impact
        //        // ColumnAdd) is recorded too — not just the re-match path.
        //        if (driftResult.DriftDetected && driftResult.Events.Count > 0)
        //        {
        //            var rowsAffected = writeResult.Inserted + writeResult.Updated;
        //            foreach (var e in driftResult.Events)
        //            {
        //                e.RunId = run.RunId;
        //                e.ReMatched = reMatched;
        //                e.RowsAffected = rowsAffected;
        //            }
        //            await _drift.LogDriftEventsAsync(run.RunId, driftResult.Events);
        //        }

        //        // ── Phase 6: Finalise ─────────────────────────────────────────
        //        run.RowsInserted = writeResult.Inserted;
        //        run.RowsUpdated = writeResult.Updated;
        //        run.RowsSkipped = writeResult.Skipped + conflictResult.RejectedRows;

        //        _logger.LogInformation("=== Run {RunId} complete | I={I} U={U} S={S} | " + "MSR={Msr:P0} | Latency={L}ms ===", run.RunId,
        //            writeResult.Inserted, writeResult.Updated, run.RowsSkipped,
        //            metrics.MappingStabilityRate,
        //            metrics.TotalLatencyMs);

        //        return await FinaliseRunAsync(run, metrics, IntegrationStatus.Success, conflictResult.Conflicts, driftResult.Events, null);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[{RunId}] Integration failed: {Message}", run.RunId, ex.Message);

        //        return await FinaliseRunAsync(run, metrics, IntegrationStatus.Failed, [], [], ex.Message);
        //    }
        //}

        public async Task<IntegrationResult> RunAsync(IntegrationRequest request, CancellationToken ct = default)
        {
            // ── Resolve primary key column from config if not provided ────────
            // (Needed by BOTH the bootstrap and the normal path, so resolve first.)
            if (string.IsNullOrEmpty(request.PrimaryKeyColumn))
            {
                request.PrimaryKeyColumn = _targetSchemaReader.GetPrimaryKeyColumn(request.TargetTable);
            }

            // ── Infer source columns from row data if not provided ────────────
            // In the API push model, the caller sends rows as dictionaries.
            // Column names are the dictionary keys — no need to declare them separately.
            if (request.SourceColumns.Count == 0 && request.SourceRows.Count > 0)
            {
                request.SourceColumns = request.SourceRows[0].Keys
                    .Select(k => new SchemaColumn(
                        Name: k,
                        DataType: InferDataType(request.SourceRows, k),
                        IsNullable: request.SourceRows.Any(r => r.TryGetValue(k, out var v) && v is null)))
                    .ToList();

                _logger.LogInformation("Inferred {Count} source columns from row data", request.SourceColumns.Count);
            }

            // ── BOOTSTRAP DETECTION ───────────────────────────────────────────
            // "First iteration" for this source ⟺ no stored fingerprint AND no
            // mappings yet. In that case the source schema BECOMES the fixed
            // target (verbatim copy): we create the target table from it, register
            // trivial one-to-one mappings, and skip matching/drift/LLM entirely.
            // From the SECOND iteration onward, the system is target-constrained
            // and runs the full matching / drift / reuse pipeline.
            var storedFp = await _drift.GetStoredFingerprintAsync(request.SourceId);
            var hasMappingsAtStart = await _registry.HasMappingsAsync(request.SourceId);
            bool targetAlreadyExists = await _targetSchemaReader.TargetTableExistsAsync(request.TargetTable);
            bool isFirstIteration = storedFp is null && !hasMappingsAtStart && !targetAlreadyExists;

            if (isFirstIteration)
            {
                // The first source schema defines the target, verbatim.
                _logger.LogInformation(
                    "[BOOTSTRAP] First iteration for {Source}: source schema becomes the fixed target.",
                    request.SourceId);

                // Resolve the PK: configured value wins; otherwise infer from the first batch.
                var resolvedPk = ResolveBootstrapPrimaryKey(request.PrimaryKeyColumn, request.SourceColumns, request.SourceRows);

                // Use the resolved PK for both table creation AND the downstream writer.
                request.PrimaryKeyColumn = resolvedPk ?? request.PrimaryKeyColumn;

                await _targetSchemaReader.EnsureTargetTableAsync(request.TargetTable, request.SourceColumns, resolvedPk);

                // Target columns = source columns (verbatim copy).
                request.TargetColumns = request.SourceColumns.Select(c => new SchemaColumn(c.Name, c.DataType, c.IsNullable)).ToList();
            }
            else
            {
                // Normal path: read the (already-existing) fixed target schema.
                if (request.TargetColumns.Count == 0)
                    request.TargetColumns = await _targetSchemaReader.ReadTargetSchemaAsync(request.TargetTable);

                // Prefer the target table's ACTUAL primary key (set during bootstrap).
                // The table is the source of truth — no config needed. Fall back to
                // config only if the table somehow has no PK defined.
                var actualPk = request.TargetColumns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
                if (!string.IsNullOrEmpty(actualPk))
                    request.PrimaryKeyColumn = actualPk;
                else if (string.IsNullOrEmpty(request.PrimaryKeyColumn))
                    request.PrimaryKeyColumn = _targetSchemaReader.GetPrimaryKeyColumn(request.TargetTable);
            }

            // ── Create run record ─────────────────────────────────────────────
            var batchNumber = await _runs.GetNextBatchNumberAsync(request.SourceId);

            var run = new IntegrationRun
            {
                SourceId = request.SourceId,
                SourceTable = request.SourceTable,
                TriggeredBy = request.TriggeredBy,
                BatchNumber = batchNumber,
                RunMode = request.RunMode,
                RowsSubmitted = request.SourceRows.Count
            };

            await _runs.CreateRunAsync(run);

            _logger.LogInformation("=== Run {RunId} started | Source={Source} | Batch={Batch} ===", run.RunId, run.SourceId, run.BatchNumber);

            var metrics = new IntegrationMetrics
            {
                RunId = run.RunId,
                SourceId = run.SourceId,
                BatchNumber = run.BatchNumber
            };

            try
            {
                // ── Phase 1: Drift Detection ──────────────────────────────────
                _logger.LogInformation("[{RunId}] Phase 1 — Drift detection", run.RunId);

                var newFingerprint = _drift.ComputeFingerprint(request.SourceId, request.SourceColumns);

                // On the first iteration there is no baseline to compare against,
                // so drift analysis is skipped (it would report "new source" anyway).
                var driftResult = isFirstIteration
                    ? new DriftAnalysisResult { DriftDetected = false }
                    : await _drift.AnalyseDriftAsync(request.SourceId, newFingerprint);

                // ── Phase 2: Schema Matching, Bootstrap, or Reuse ────────────
                _logger.LogInformation("[{RunId}] Phase 2 — Schema matching", run.RunId);

                var sw = Stopwatch.StartNew();
                List<MappingRule> mappings;

                // reMatched is meaningful only on the non-bootstrap path.
                bool reMatched = false;

                if (isFirstIteration)
                {
                    // ── BOOTSTRAP MAPPINGS ───────────────────────────────────
                    // Every source column maps to itself (identity). No Python call.
                    _logger.LogInformation("[{RunId}] [BOOTSTRAP] Registering verbatim one-to-one mappings — matching skipped.", run.RunId);

                    mappings = request.SourceColumns.Select(c => new MappingRule
                    {
                        SourceColumns = new List<string> { c.Name },
                        TargetColumn = c.Name,
                        MappingType = MappingType.OneToOne,
                        Expression = c.Name,
                        ConfidenceScore = 1.0,
                        SchemaFingerprint = newFingerprint.Hash
                    }).ToList();

                    mappings = SanitizeMappings(mappings);

                    await _registry.SaveMappingsAsync(request.SourceId, mappings);
                    await _drift.SaveFingerprintAsync(newFingerprint);

                    metrics.TotalMappings = mappings.Count;
                    metrics.UnchangedMappings = mappings.Count;
                }
                else
                {
                    var hasMappings = await _registry.HasMappingsAsync(request.SourceId);

                    // A ColumnAdd is LOW-impact (existing mappings stay valid) BUT the
                    // new column still needs mapping — pure reuse would silently drop
                    // it. So if the only drift is additive, we still route through the
                    // matcher (R3 reuses stable mappings, R1 maps the new column).
                    bool hasColumnAdd = driftResult.Events.Any(e => e.DriftType == DriftType.ColumnAdd);
                    bool pureReuse = driftResult.CanReuseExistingMappings && hasMappings && !hasColumnAdd;

                    reMatched = !(driftResult.CanReuseExistingMappings && hasMappings);

                    if (pureReuse)
                    {
                        // Reuse path — no Python call needed (no new columns to map)
                        _logger.LogInformation("[{RunId}] Reusing existing mappings — Python skipped", run.RunId);

                        mappings = await _registry.GetActiveMappingsAsync(request.SourceId);

                        foreach (var m in mappings)
                            await _registry.UpdateLastValidatedAsync(m.MappingId);

                        metrics.TotalMappings = mappings.Count;
                        metrics.UnchangedMappings = mappings.Count;
                    }
                    else
                    {
                        // Re-match path — call Python
                        _logger.LogInformation("[{RunId}] Calling Python schema matcher", run.RunId);

                        // ── Apply any detected renames to the registry FIRST ──
                        // A rename was detected in Phase 1. Re-point the stored mappings'
                        // source column names from old -> new BEFORE reading them, so the
                        // Python R3 rule can reuse them instead of re-mapping from scratch.
                        if (driftResult.DriftDetected)
                        {
                            foreach (var e in driftResult.Events.Where(ev => ev.DriftType == DriftType.ColumnRename))
                            {
                                var oldName = e.AffectedColumns[0];
                                var newName = e.AffectedColumns[1];
                                await _registry.RepointMappingSourceAsync(request.SourceId, oldName, newName);
                                _logger.LogInformation("[{RunId}] Re-pointed mapping '{Old}' -> '{New}' (rename)", run.RunId, oldName, newName);
                            }
                        }

                        var existingMappings = await _registry.GetActiveMappingsAsync(request.SourceId);

                        var matchResult = await _matcher.MatchAsync(
                            request.SourceId,
                            request.SourceColumns,
                            request.TargetColumns,
                            existingMappings,
                            ct);

                        // Count how many new mappings match old ones (MSR numerator)
                        var unchanged = CountUnchangedMappings(existingMappings, matchResult.Mappings);

                        metrics.TotalMappings = matchResult.Mappings.Count;
                        metrics.UnchangedMappings = unchanged;

                        // Soft-delete old mappings, save new ones
                        await _registry.DeactivateMappingsAsync(request.SourceId);

                        mappings = matchResult.Mappings;

                        foreach (var m in mappings)
                            m.SchemaFingerprint = newFingerprint.Hash;

                        mappings = SanitizeMappings(mappings);

                        await _registry.SaveMappingsAsync(request.SourceId, mappings);
                        await _drift.SaveFingerprintAsync(newFingerprint);
                    }
                }

                sw.Stop();
                metrics.MatchingTimeMs = sw.ElapsedMilliseconds;

                // Guard: nothing to do if no mappings exist
                if (mappings.Count == 0)
                {
                    _logger.LogWarning("[{RunId}] No mappings available. Integration cannot proceed.", run.RunId);

                    return await FinaliseRunAsync(run, metrics, IntegrationStatus.Partial, [], driftResult.Events, "No mapping rules available.");
                }

                // ── Phase 3: Transformation ───────────────────────────────────
                _logger.LogInformation("[{RunId}] Phase 3 — Transforming {Count} rows", run.RunId, request.SourceRows.Count);

                sw.Restart();
                var transformed = _transformer.TransformBatch(request.SourceRows, mappings);
                sw.Stop();
                metrics.TransformationTimeMs = sw.ElapsedMilliseconds;

                // ── Phase 4: Conflict Detection ───────────────────────────────
                _logger.LogInformation("[{RunId}] Phase 4 — Conflict detection", run.RunId);

                sw.Restart();
                var conflictResult = await _conflict.DetectAndResolveAsync(
                    run.RunId,
                    transformed,
                    request.TargetTable,
                    request.ResolutionPolicy,
                    request.SourceId);
                sw.Stop();
                metrics.ConflictDetectionTimeMs = sw.ElapsedMilliseconds;

                // ── Phase 5: Write to Target DB ───────────────────────────────
                _logger.LogInformation("[{RunId}] Phase 5 — Writing to target DB", run.RunId);

                sw.Restart();
                var writeResult = await _writer.UpsertAsync(
                    run.RunId,
                    request.SourceId,
                    mappings.First().MappingId,
                    request.TargetTable,
                    conflictResult.ResolvedRows,
                    request.PrimaryKeyColumn);
                sw.Stop();
                metrics.InsertionTimeMs = sw.ElapsedMilliseconds;
                metrics.RowsInserted = writeResult.Inserted;
                metrics.RowsUpdated = writeResult.Updated;
                metrics.RowsSkipped = writeResult.Skipped + conflictResult.RejectedRows;

                // ── HRR inputs ────────────────────────────────────────────────
                metrics.RowsModifiedAfterIntegration = writeResult.Updated;
                metrics.PreviouslyIntegratedRows = await _runs.GetCumulativeInsertedRowsAsync(request.SourceId, run.RunId);

                // ── Persist drift events once, with complete run info ──────────
                // (Skipped on the bootstrap path, which has no drift events.)
                if (driftResult.DriftDetected && driftResult.Events.Count > 0)
                {
                    var rowsAffected = writeResult.Inserted + writeResult.Updated;
                    foreach (var e in driftResult.Events)
                    {
                        e.RunId = run.RunId;
                        e.ReMatched = reMatched;
                        e.RowsAffected = rowsAffected;
                    }
                    await _drift.LogDriftEventsAsync(run.RunId, driftResult.Events);
                }

                // ── Phase 6: Finalise ─────────────────────────────────────────
                run.RowsInserted = writeResult.Inserted;
                run.RowsUpdated = writeResult.Updated;
                run.RowsSkipped = writeResult.Skipped + conflictResult.RejectedRows;

                _logger.LogInformation("=== Run {RunId} complete | I={I} U={U} S={S} | " + "MSR={Msr:P0} | Latency={L}ms ===", run.RunId,
                    writeResult.Inserted, writeResult.Updated, run.RowsSkipped,
                    metrics.MappingStabilityRate,
                    metrics.TotalLatencyMs);

                return await FinaliseRunAsync(run, metrics, IntegrationStatus.Success, conflictResult.Conflicts, driftResult.Events, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{RunId}] Integration failed: {Message}", run.RunId, ex.Message);

                return await FinaliseRunAsync(run, metrics, IntegrationStatus.Failed, [], [], ex.Message);
            }
        }

        private async Task<IntegrationResult> FinaliseRunAsync(IntegrationRun run, IntegrationMetrics metrics, IntegrationStatus status,
                                                               List<ConflictRecord> conflicts, List<DriftEvent> driftEvents, string? errorMessage)
        {
            run.Status = status;
            run.CompletedAt = DateTime.UtcNow;
            run.ErrorMessage = errorMessage;

            await _runs.UpdateRunAsync(run);

            return new IntegrationResult(
                run.RunId,
                status,
                metrics,
                conflicts,
                driftEvents);
        }
        
        private static int CountUnchangedMappings(List<MappingRule> existing, List<MappingRule> @new)
        {
            // A mapping is "unchanged" if source columns + target column
            // + expression are all identical to a previous mapping
            var existingKeys = existing.Select(MappingKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return @new.Count(m => existingKeys.Contains(MappingKey(m)));
        }

        private static string MappingKey(MappingRule m)
        {
            // Sort source columns so order does not matter
            var sortedSources = string.Join("+", m.SourceColumns.OrderBy(c => c));

            return $"{sortedSources}→{m.TargetColumn}|{m.Expression}";
        }

        /// <summary>
        /// Infers SQL data type from actual row values.
        /// Used when source columns are not explicitly declared.
        /// Simple inference — sufficient for thesis prototype.
        /// </summary>
        private static string InferDataType(List<Dictionary<string, object?>> rows, string columnName)
        {
            var samples = rows
                .Select(r => r.TryGetValue(columnName, out var v) ? v : null)
                .Where(v => v is not null)
                .Take(10)  // sample more rows
                .ToList();

            // If ANY row has null for this column → treat as nullable string
            // This ensures deterministic fingerprints across batches
            bool hasNulls = rows.Any(r => !r.TryGetValue(columnName, out var v) || v is null);

            if (samples.Count == 0) return "nvarchar(max)";

            bool allNumeric = samples.All(v =>
                v is int || v is long || v is double ||
                v is float || v is decimal ||
                (v is System.Text.Json.JsonElement je &&
                 je.ValueKind == System.Text.Json.JsonValueKind.Number));

            // Only return numeric if ALL sampled values are numeric
            // Any mixed type defaults to string for fingerprint stability
            return allNumeric ? "decimal(18,2)" : "nvarchar(max)";
        }

        private static List<MappingRule> SanitizeMappings(List<MappingRule> mappings)
        {
            return mappings
                // 1️ One mapping per source (highest confidence)
                .GroupBy(m => string.Join("|", m.SourceColumns))
                .Select(g => g.OrderByDescending(m => m.ConfidenceScore).First())

                // 2️ One mapping per target (highest confidence)
                .GroupBy(m => m.TargetColumn, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => m.ConfidenceScore).First())

                .ToList();
        }

        /// <summary>
        /// Bootstrap PK inference. Returns the configured PK if it exists among the
        /// source columns; otherwise infers one from the first batch by choosing a
        /// column whose values are non-null and unique across all rows. Prefers
        /// id-like names. Returns null if no single-column key can be found (the
        /// table is then created without a primary key).
        /// </summary>
        private string? ResolveBootstrapPrimaryKey(string configuredPk, List<SchemaColumn> columns, List<Dictionary<string, object?>> rows)
        {
            // 1) Configuration wins if the configured PK actually exists in the source.
            if (!string.IsNullOrWhiteSpace(configuredPk) &&
                columns.Any(c => c.Name.Equals(configuredPk, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("[BOOTSTRAP] Using configured primary key '{Pk}'.", configuredPk);
                return configuredPk;
            }

            if (rows.Count == 0) return null;

            // 2) Build candidate list, preferring id-like names first.
            bool IdLike(string n) =>
                n.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("key", StringComparison.OrdinalIgnoreCase);

            var candidates = columns
                .Select(c => c.Name)
                .OrderByDescending(IdLike)   // id-like names checked first
                .ToList();

            // 3) A column is a key if every value is non-null AND all values are distinct.
            foreach (var col in candidates)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool ok = true;
                foreach (var row in rows)
                {
                    if (!row.TryGetValue(col, out var v) || v is null) { ok = false; break; }
                    var key = ConvertScalar(v);
                    if (key is null || !seen.Add(key)) { ok = false; break; }  // null or duplicate
                }
                if (ok)
                {
                    _logger.LogInformation(
                        "[BOOTSTRAP] Inferred primary key '{Pk}' from first batch (unique, non-null over {N} rows).",
                        col, rows.Count);
                    return col;
                }
            }

            _logger.LogWarning(
                "[BOOTSTRAP] No single-column unique key found in the first batch — " +
                "target will be created WITHOUT a primary key. (Composite-key inference is future work.)");
            return null;
        }

        // Small helper: stringify a scalar (handles JsonElement) for uniqueness testing.
        private static string? ConvertScalar(object? v)
        {
            if (v is null) return null;
            if (v is System.Text.Json.JsonElement je)
                return je.ValueKind == System.Text.Json.JsonValueKind.Null ? null : je.ToString();
            return v.ToString();
        }
    }

    public class IntegrationRequest
    {
        public string SourceId { get; set; } = string.Empty;
        public string SourceTable { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public string PrimaryKeyColumn { get; set; } = string.Empty;
        public List<SchemaColumn> SourceColumns { get; set; } = [];
        public List<SchemaColumn> TargetColumns { get; set; } = [];
        public List<Dictionary<string, object?>> SourceRows { get; set; } = [];
        public ResolutionPolicy ResolutionPolicy { get; set; } = ResolutionPolicy.LastWriteWins;
        public TriggerSource TriggeredBy { get; set; } = TriggerSource.Api;
        public RunMode RunMode { get; set; } = RunMode.Incremental;
    }

    public record IntegrationResult(
        Guid RunId,
        IntegrationStatus Status,
        IntegrationMetrics Metrics,
        List<ConflictRecord> Conflicts,
        List<DriftEvent> DriftEvents
    );
}
