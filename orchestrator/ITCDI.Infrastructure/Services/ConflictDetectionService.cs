using Dapper;
using ITCDI.Core.Enums;
using ITCDI.Core.Interfaces;
using ITCDI.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Services
{
    public class ConflictDetectionService : IConflictDetectionService
    {
        private readonly string _registryConnectionString;
        private readonly string _targetConnectionString;
        private readonly ILogger<ConflictDetectionService> _logger;

        public ConflictDetectionService(string registryConnectionString, string targetConnectionString, ILogger<ConflictDetectionService> logger)
        {
            _registryConnectionString = registryConnectionString;
            _targetConnectionString = targetConnectionString;
            _logger = logger;
        }

        //    public async Task<ConflictResolutionResult> DetectAndResolveAsync(Guid runId, List<Dictionary<string, object?>> transformedRows, 
        //                                                                      string targetTable, ResolutionPolicy policy = ResolutionPolicy.LastWriteWins,
        //                                                                      string sourceId = "")
        //    {
        //        var resolvedRows = new List<Dictionary<string, object?>>();
        //        var allConflicts = new List<ConflictRecord>();
        //        var rejectedCount = 0;

        //        foreach (var row in transformedRows)
        //        {
        //            var rowConflicts = new List<ConflictRecord>();

        //            // Run all four checks in order
        //            rowConflicts.AddRange(DetectNullConstraintViolations(runId, row));
        //            rowConflicts.AddRange(await DetectPrimaryKeyCollisionsAsync(runId, row, targetTable, sourceId));
        //            rowConflicts.AddRange(await DetectValueConflictsAsync(runId, row, targetTable, sourceId));

        //            // TODO Phase 4: add ValueConflict and ReferentialIntegrity checks
        //            // These require knowledge of the target schema constraints
        //            // which you will add when building the full target DB writer

        //            if (rowConflicts.Count == 0)
        //            {
        //                // Clean row — no conflicts
        //                resolvedRows.Add(row);
        //            }
        //            else
        //            {
        //                allConflicts.AddRange(rowConflicts);

        //                var (resolvedRow, accepted) = ApplyResolutionPolicy(row, rowConflicts, policy);

        //                if (accepted && resolvedRow is not null)
        //                    resolvedRows.Add(resolvedRow);
        //                else
        //                    rejectedCount++;
        //            }
        //        }

        //        // Persist all conflicts to conflict_log in one batch
        //        await PersistConflictsAsync(allConflicts);

        //        _logger.LogInformation("Conflict detection: {Clean} clean, {Conflicts} conflicts, " + "{Rejected} rejected from {Total} rows.",
        //                                resolvedRows.Count, allConflicts.Count, rejectedCount, transformedRows.Count);

        //        return new ConflictResolutionResult(resolvedRows, allConflicts, rejectedCount);
        //    }

        //    private List<ConflictRecord> DetectNullConstraintViolations(Guid runId, Dictionary<string, object?> row)
        //    {
        //        var conflicts = new List<ConflictRecord>();

        //        var pkColumn = row.Keys.FirstOrDefault(k => k.Equals("id", StringComparison.OrdinalIgnoreCase));

        //        if (pkColumn is null || row[pkColumn] is null)
        //            return conflicts;

        //        var pkValue = row[pkColumn];

        //        foreach (var (column, value) in row)
        //        {
        //            if (value is null)
        //            {
        //                conflicts.Add(new ConflictRecord
        //                {
        //                    RunId = runId,
        //                    TargetColumn = column,
        //                    ConflictType = ConflictType.ConstraintViolation,
        //                    SourceValue = null,
        //                    ExistingValue = null,
        //                    ResolutionPolicy = ResolutionPolicy.Rejected,
        //                    ResolvedValue = null,
        //                    SourceRowKey = ConvertJsonElement(pkValue)?.ToString()
        //                });

        //                _logger.LogWarning("Null value detected for column '{Column}' — " + "ConstraintViolation recorded.", column);
        //            }
        //        }

        //        return conflicts;
        //    }

        //    private async Task<List<ConflictRecord>> DetectValueConflictsAsync(Guid runId, Dictionary<string, object?> row,
        //                                                                       string targetTable, string sourceId)
        //    {
        //        var conflicts = new List<ConflictRecord>();

        //        var pkColumn = row.Keys.FirstOrDefault(k => k.Equals("id", StringComparison.OrdinalIgnoreCase));

        //        if (pkColumn is null || row[pkColumn] is null)
        //            return conflicts;

        //        var pkValue = row[pkColumn];

        //        // Check if this row already exists in the target
        //        var checkSql = $"""
        //                        SELECT * FROM {targetTable}
        //                        WHERE [{pkColumn}] = @PkValue
        //                        """;

        //        using var conn = new SqlConnection(_targetConnectionString);
        //        var existingRow = await conn.QueryFirstOrDefaultAsync(checkSql, new { PkValue = ConvertJsonElement(pkValue) });

        //        if (existingRow is null) return conflicts;

        //        // ── KEY FIX: Check who wrote the existing row ─────────────────────
        //        // If the existing row came from the SAME source, it is a legitimate
        //        // update — not a conflict. Only flag conflicts when DIFFERENT sources
        //        // disagree on the same value.
        //        var provenanceSql = """
        //                            SELECT source_id FROM integration_provenance
        //                            WHERE target_table    = @TargetTable
        //                              AND target_row_key  = @RowKey
        //                            ORDER BY integrated_at DESC
        //                            """;

        //        using var regConn = new SqlConnection(_registryConnectionString);
        //        var existingSourceId = await regConn.QueryFirstOrDefaultAsync<string>(
        //                                provenanceSql, new
        //                                {
        //                                    TargetTable = targetTable,
        //                                    RowKey = pkValue?.ToString()
        //                                });

        //        // Same source sending updated data — allow LastWriteWins, no conflict
        //        if (existingSourceId == sourceId)
        //        {
        //            return conflicts;
        //        }

        //        // Different source — compare each column value
        //        var existingDict = (IDictionary<string, object>)existingRow;

        //        foreach (var (column, newValue) in row)
        //        {
        //            if (column.Equals(pkColumn, StringComparison.OrdinalIgnoreCase))
        //                continue;

        //            if (!existingDict.TryGetValue(column, out var existingValue))
        //                continue;

        //            var newStr = ConvertJsonElement(newValue)?.ToString();
        //            var existingStr = existingValue is DBNull ? null : existingValue?.ToString();

        //            if (newStr != existingStr && newValue is not null && existingValue is not null && existingValue is not DBNull)
        //            {
        //                conflicts.Add(new ConflictRecord
        //                {
        //                    RunId = runId,
        //                    TargetColumn = column,
        //                    ConflictType = ConflictType.ValueConflict,
        //                    SourceValue = newStr,
        //                    ExistingValue = existingStr,
        //                    ResolutionPolicy = ResolutionPolicy.ProvenancePreserved,
        //                    ResolvedValue = null,
        //                    SourceRowKey = ConvertJsonElement(pkValue)?.ToString()
        //                });

        //                _logger.LogWarning(
        //                    "Value conflict from DIFFERENT source on {Table}.{Col}: " +
        //                    "incoming='{New}' existing='{Old}' " +
        //                    "written_by='{ExistingSource}' current_source='{NewSource}'",
        //                    targetTable, column, newStr, existingStr,
        //                    existingSourceId, sourceId);
        //            }
        //        }

        //        return conflicts;
        //    }

        //    // Add this helper to ConflictDetectionService
        //    private static object? ConvertJsonElement(object? value)
        //    {
        //        if (value is System.Text.Json.JsonElement element)
        //        {
        //            return element.ValueKind switch
        //            {
        //                System.Text.Json.JsonValueKind.String => element.GetString(),
        //                System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        //                System.Text.Json.JsonValueKind.True => true,
        //                System.Text.Json.JsonValueKind.False => false,
        //                System.Text.Json.JsonValueKind.Null => null,
        //                _ => element.ToString()
        //            };
        //        }
        //        return value;
        //    }

        //    private async Task<List<ConflictRecord>> DetectPrimaryKeyCollisionsAsync(Guid runId, Dictionary<string, object?> row,
        //                                                                             string targetTable, string sourceId)
        //    {
        //        var conflicts = new List<ConflictRecord>();

        //        // Find the PK column — convention: column named "id" or ending with "_id"
        //        // In a full implementation this comes from schema metadata
        //        // For now we look for a column named "id" as a convention
        //        var pkColumn = row.Keys.FirstOrDefault(k => k.Equals("id", StringComparison.OrdinalIgnoreCase));

        //        if (pkColumn is null || row[pkColumn] is null)
        //            return conflicts;

        //        var pkValue = row[pkColumn];

        //        // Check if this PK already exists in the target table
        //        // NOTE: Dynamic table name is safe here because targetTable
        //        // comes from your own configuration, not from user input
        //        var sql = $"""
        //                    SELECT COUNT(1) FROM {targetTable}
        //                    WHERE id = @PkValue
        //                    """;

        //        using var conn = new SqlConnection(_targetConnectionString);

        //        var exists = await conn.ExecuteScalarAsync<int>(sql, new { PkValue = pkValue }) > 0;

        //        if (!exists) return conflicts;

        //        // ── Check who owns this row ───────────────────────────────────
        //        // Same source updating its own row = legitimate update, not collision
        //        var provenanceSql = """
        //                            SELECT source_id FROM integration_provenance
        //                            WHERE target_table   = @TargetTable
        //                              AND target_row_key = @RowKey
        //                            ORDER BY integrated_at DESC
        //                            """;

        //        using var regConn = new SqlConnection(_registryConnectionString);
        //        var existingSourceId = await regConn.QueryFirstOrDefaultAsync<string>(
        //            provenanceSql, new
        //            {
        //                TargetTable = targetTable,
        //                RowKey = pkValue?.ToString()
        //            });

        //        // Same source — allow update, no conflict
        //        if (existingSourceId == sourceId)
        //            return conflicts;

        //        // Different source — genuine PK collision
        //        conflicts.Add(new ConflictRecord
        //        {
        //            RunId = runId,
        //            TargetColumn = pkColumn,
        //            ConflictType = ConflictType.PrimaryKeyCollision,
        //            SourceValue = ConvertJsonElement(pkValue)?.ToString(),
        //            ExistingValue = ConvertJsonElement(pkValue)?.ToString(),
        //            ResolutionPolicy = ResolutionPolicy.LastWriteWins,
        //            ResolvedValue = ConvertJsonElement(pkValue)?.ToString(),
        //            SourceRowKey = ConvertJsonElement(pkValue)?.ToString()
        //        });

        //        _logger.LogWarning(
        //            "PK collision: {Table}.{Col}={Val} " +
        //            "owned by '{Owner}', claimed by '{Claimer}'",
        //            targetTable, pkColumn, pkValue,
        //            existingSourceId, sourceId);

        //        return conflicts;
        //    }

        //    private static (Dictionary<string, object?>? row, bool accepted) ApplyResolutionPolicy(Dictionary<string, object?> row,
        //                                                                                           List<ConflictRecord> conflicts, ResolutionPolicy policy)
        //    {
        //        foreach (var conflict in conflicts.Where(c => c.ConflictType == ConflictType.ValueConflict))
        //        {
        //            if (row.ContainsKey(conflict.TargetColumn))
        //            {
        //                // Set to null — downstream MERGE will handle NOT NULL
        //                // via ConstraintViolation detection
        //                row[conflict.TargetColumn] = null;
        //                conflict.ResolvedValue = null;
        //                conflict.ResolutionPolicy = ResolutionPolicy.ProvenancePreserved;
        //            }
        //        }

        //        return policy switch
        //        {
        //            ResolutionPolicy.LastWriteWins => (row, true),
        //            ResolutionPolicy.Rejected => (null, false),
        //            ResolutionPolicy.ProvenancePreserved => (row, true),
        //            _ => (row, true)
        //        };
        //    }

        //    private async Task PersistConflictsAsync(List<ConflictRecord> conflicts)
        //    {
        //        if (conflicts.Count == 0) return;

        //        const string sql = """
        //                            INSERT INTO conflict_log
        //                                (run_id, target_column, conflict_type,
        //                                 source_value, existing_value,
        //                                 resolution_policy, resolved_value, detected_at)
        //                            VALUES
        //                                (@RunId, @TargetColumn, @ConflictType,
        //                                 @SourceValue, @ExistingValue,
        //                                 @ResolutionPolicy, @ResolvedValue, @DetectedAt)
        //                            """;

        //        using var conn = new SqlConnection(_registryConnectionString);
        //        await conn.OpenAsync();
        //        using var tx = await conn.BeginTransactionAsync();

        //        try
        //        {
        //            foreach (var c in conflicts)
        //            {
        //                await conn.ExecuteAsync(sql, new
        //                {
        //                    c.RunId,
        //                    c.TargetColumn,
        //                    ConflictType = c.ConflictType.ToString(),
        //                    c.SourceValue,
        //                    c.ExistingValue,
        //                    ResolutionPolicy = c.ResolutionPolicy.ToString(),
        //                    c.ResolvedValue,
        //                    DetectedAt = c.DetectedAt
        //                }, tx);
        //            }

        //            await tx.CommitAsync();
        //        }
        //        catch
        //        {
        //            await tx.RollbackAsync();
        //            throw;
        //        }
        //    }

        //public async Task<ConflictResolutionResult> DetectAndResolveAsync(
        //    Guid runId,
        //    List<Dictionary<string, object?>> transformedRows,
        //    string targetTable,
        //    ResolutionPolicy policy = ResolutionPolicy.LastWriteWins,
        //    string sourceId = "")
        //{
        //    var resolvedRows = new List<Dictionary<string, object?>>();
        //    var allConflicts = new List<ConflictRecord>();
        //    var rejectedCount = 0;

        //    // ── Resolve the real PK column from target schema ─────────────
        //    // Looks up the PRIMARY KEY constraint in the target database.
        //    // This replaces the hardcoded "id" check that failed for
        //    // musicianID, AgencyID, and other non-standard PK names.
        //    var pkColumn = await ResolvePrimaryKeyColumnAsync(targetTable);
        //    if (string.IsNullOrEmpty(pkColumn))
        //    {
        //        _logger.LogWarning("Could not resolve PK column for table {Table}. " + "Skipping conflict detection.", targetTable);
        //        return new ConflictResolutionResult(transformedRows, allConflicts, 0);
        //    }

        //    _logger.LogDebug("Resolved PK column: {PK} for {Table}", pkColumn, targetTable);

        //    // ── Get nullable columns for this table ───────────────────────
        //    // Used to skip null checks on legitimately nullable columns.
        //    // Prevents false positive ConstraintViolation conflicts on
        //    // sparse data (e.g. Wikidata musicians with no fatherName).
        //    var nullableColumns = await GetNullableColumnsAsync(targetTable);

        //    foreach (var row in transformedRows)
        //    {
        //        var rowConflicts = new List<ConflictRecord>();

        //        // Check 1: Null constraint violations
        //        // Only fires for NOT NULL columns — skips nullable ones
        //        rowConflicts.AddRange(DetectNullConstraintViolations(runId, row, pkColumn, nullableColumns));

        //        // Check 2: Primary key collision
        //        rowConflicts.AddRange(await DetectPrimaryKeyCollisionsAsync(runId, row, targetTable, pkColumn, sourceId));

        //        // Check 3: Value conflicts from different sources
        //        rowConflicts.AddRange(await DetectValueConflictsAsync(runId, row, targetTable, pkColumn, sourceId));

        //        if (rowConflicts.Count == 0)
        //        {
        //            resolvedRows.Add(row);
        //        }
        //        else
        //        {
        //            allConflicts.AddRange(rowConflicts);

        //            var (resolvedRow, accepted) = ApplyResolutionPolicy(row, rowConflicts, policy);

        //            if (accepted && resolvedRow is not null)
        //                resolvedRows.Add(resolvedRow);
        //            else
        //                rejectedCount++;
        //        }
        //    }

        //    await PersistConflictsAsync(allConflicts);

        //    _logger.LogInformation(
        //        "Conflict detection: {Clean} clean, {Conflicts} conflicts, "
        //        + "{Rejected} rejected from {Total} rows.",
        //        resolvedRows.Count, allConflicts.Count,
        //        rejectedCount, transformedRows.Count);

        //    return new ConflictResolutionResult(
        //        resolvedRows, allConflicts, rejectedCount);
        //}

        public async Task<ConflictResolutionResult> DetectAndResolveAsync(Guid runId,
                                                                        List<Dictionary<string, object?>> transformedRows,
                                                                        string targetTable,
                                                                        ResolutionPolicy policy = ResolutionPolicy.LastWriteWins,
                                                                        string sourceId = "")
        {
            var resolvedRows = new List<Dictionary<string, object?>>();
            var allConflicts = new List<ConflictRecord>();
            var rejectedCount = 0;

            var pkColumn = await ResolvePrimaryKeyColumnAsync(targetTable);
            if (string.IsNullOrEmpty(pkColumn))
            {
                _logger.LogWarning("Could not resolve PK column for table {Table}. Skipping conflict detection.", targetTable);
                return new ConflictResolutionResult(transformedRows, allConflicts, 0);
            }

            _logger.LogDebug("Resolved PK column: {PK} for {Table}", pkColumn, targetTable);

            var nullableColumns = await GetNullableColumnsAsync(targetTable);

            // ── BULK PRE-LOAD (set-based) — replaces per-row DB queries ──────────
            // Collect all incoming PK values once.
            var pkValues = transformedRows
                .Select(r => r.TryGetValue(pkColumn, out var v) ? ConvertJsonElement(v) : null)
                .Where(v => v is not null)
                .Select(v => v!.ToString()!)
                .Distinct()
                .ToList();

            // One query: which of these PKs already exist in the target (+ their full rows).
            var existingRows = await BulkLoadExistingRowsAsync(targetTable, pkColumn, pkValues);

            // One query: who owns each of these rows (provenance).
            var existingOwners = await BulkLoadOwnersAsync(targetTable, pkValues);

            foreach (var row in transformedRows)
            {
                var rowConflicts = new List<ConflictRecord>();

                // Check 1: null constraints (in-memory, no DB)
                rowConflicts.AddRange(DetectNullConstraintViolations(runId, row, pkColumn, nullableColumns));

                // Resolve this row's PK string for the in-memory lookups
                string? pkStr = row.TryGetValue(pkColumn, out var pkRaw)
                    ? ConvertJsonElement(pkRaw)?.ToString() : null;

                if (pkStr is not null)
                {
                    bool exists = existingRows.ContainsKey(pkStr);
                    existingOwners.TryGetValue(pkStr, out var owner);

                    // Check 2: PK collision (in-memory)
                    if (exists && owner is not null && owner != sourceId)
                    {
                        rowConflicts.Add(new ConflictRecord
                        {
                            RunId = runId,
                            TargetColumn = pkColumn,
                            ConflictType = ConflictType.PrimaryKeyCollision,
                            SourceValue = pkStr,
                            ExistingValue = pkStr,
                            ResolutionPolicy = ResolutionPolicy.LastWriteWins,
                            ResolvedValue = pkStr,
                            SourceRowKey = pkStr,
                        });
                        _logger.LogWarning("PK collision: {Table}.{Col}={Val} owned by '{Owner}', incoming from '{Source}'",
                            targetTable, pkColumn, pkStr, owner, sourceId);
                    }

                    // Check 3: value conflicts (in-memory comparison against pre-loaded row)
                    if (exists && owner is not null && owner != sourceId
                        && existingRows.TryGetValue(pkStr, out var existingDict))
                    {
                        foreach (var (column, newValue) in row)
                        {
                            if (column.Equals(pkColumn, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!existingDict.TryGetValue(column, out var existingValue)) continue;

                            var newStr = ConvertJsonElement(newValue)?.ToString();
                            var existStr = existingValue is DBNull ? null : existingValue?.ToString();

                            if (newStr == existStr) continue;
                            if (newValue is null && existingValue is DBNull) continue;
                            if (newValue is null || existingValue is null || existingValue is DBNull) continue;

                            if (decimal.TryParse(newStr, out var d1) && decimal.TryParse(existStr, out var d2) && d1 == d2)
                                continue;

                            rowConflicts.Add(new ConflictRecord
                            {
                                RunId = runId,
                                TargetColumn = column,
                                ConflictType = ConflictType.ValueConflict,
                                SourceValue = newStr,
                                ExistingValue = existStr,
                                ResolutionPolicy = ResolutionPolicy.ProvenancePreserved,
                                ResolvedValue = null,
                                SourceRowKey = pkStr,
                            });
                            _logger.LogWarning("Value conflict: {Table}.{Col} incoming='{New}' existing='{Old}' source_a='{SA}' source_b='{SB}'",
                                targetTable, column, newStr, existStr, owner, sourceId);
                        }
                    }
                }

                if (rowConflicts.Count == 0)
                {
                    resolvedRows.Add(row);
                }
                else
                {
                    allConflicts.AddRange(rowConflicts);
                    var (resolvedRow, accepted) = ApplyResolutionPolicy(row, rowConflicts, policy);
                    if (accepted && resolvedRow is not null) resolvedRows.Add(resolvedRow);
                    else rejectedCount++;
                }
            }

            await PersistConflictsAsync(allConflicts);

            _logger.LogInformation("Conflict detection: {Clean} clean, {Conflicts} conflicts, {Rejected} rejected from {Total} rows.",
                resolvedRows.Count, allConflicts.Count, rejectedCount, transformedRows.Count);

            return new ConflictResolutionResult(resolvedRows, allConflicts, rejectedCount);
        }

        // ── Private helpers ───────────────────────────────────────────────

        /// <summary>
        /// Reads the PRIMARY KEY column name directly from the target
        /// database schema. No hardcoding — works for any table.
        /// </summary>
        private async Task<string> ResolvePrimaryKeyColumnAsync(
            string targetTable)
        {
            const string sql = """
                                SELECT c.COLUMN_NAME
                                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
                                    ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                                    AND tc.TABLE_NAME = @TableName
                                ORDER BY c.ORDINAL_POSITION
                                """;

            using var conn = new SqlConnection(_targetConnectionString);
            var pk = await conn.QueryFirstOrDefaultAsync<string>(sql, new { TableName = targetTable });
            return pk ?? string.Empty;
        }

        /// <summary>
        /// Returns all columns marked IS_NULLABLE = 'YES' for a table.
        /// Used to skip false-positive null constraint violations.
        /// </summary>
        private async Task<HashSet<string>> GetNullableColumnsAsync(
            string targetTable)
        {
            const string sql = """
                                SELECT COLUMN_NAME
                                FROM INFORMATION_SCHEMA.COLUMNS
                                WHERE TABLE_NAME  = @TableName
                                    AND IS_NULLABLE = 'YES'
                                """;

            using var conn = new SqlConnection(_targetConnectionString);
            var cols = await conn.QueryAsync<string>(sql, new { TableName = targetTable });
            return new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check 1 — Null constraint violations.
        ///
        /// A constraint violation is a null value in a NOT NULL column.
        /// Legitimately nullable columns are skipped to prevent
        /// false positives on sparse real-world data.
        ///
        /// Literature: Abedjan et al. (VLDB 2016) — data error taxonomy,
        /// rule violation family.
        /// </summary>
        private List<ConflictRecord> DetectNullConstraintViolations(Guid runId, Dictionary<string, object?> row, string pkColumn,
                                                                    HashSet<string> nullableColumns)
        {
            var conflicts = new List<ConflictRecord>();

            // Get PK value for traceability
            row.TryGetValue(pkColumn, out var pkRaw);
            var pkValue = ConvertJsonElement(pkRaw)?.ToString();

            foreach (var (column, value) in row)
            {
                // Skip PK column itself
                if (column.Equals(pkColumn,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip legitimately nullable columns
                if (nullableColumns.Contains(column))
                    continue;

                // Skip created_at — auto-populated by DB
                if (column.Equals("created_at",
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (value is null ||
                    (value is System.Text.Json.JsonElement je &&
                        je.ValueKind ==
                        System.Text.Json.JsonValueKind.Null))
                {
                    conflicts.Add(new ConflictRecord
                    {
                        RunId = runId,
                        TargetColumn = column,
                        ConflictType = ConflictType.ConstraintViolation,
                        SourceValue = null,
                        ExistingValue = null,
                        ResolutionPolicy = ResolutionPolicy.Rejected,
                        ResolvedValue = null,
                        SourceRowKey = pkValue,
                    });
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Check 2 — Primary key collision.
        ///
        /// Fires when a row with the same PK already exists in the target
        /// AND was written by a DIFFERENT source.
        /// Same-source updates are legitimate incremental writes — not conflicts.
        ///
        /// Literature: Abedjan et al. (VLDB 2016) — duplicate family.
        /// </summary>
        private async Task<List<ConflictRecord>> DetectPrimaryKeyCollisionsAsync(
            Guid runId,
            Dictionary<string, object?> row,
            string targetTable,
            string pkColumn,
            string sourceId)
        {
            var conflicts = new List<ConflictRecord>();

            if (!row.TryGetValue(pkColumn, out var pkRaw))
                return conflicts;

            var pkValue = ConvertJsonElement(pkRaw);
            if (pkValue is null) return conflicts;

            // Check if row exists
            var existsSql = $"""
            SELECT COUNT(1) FROM [{targetTable}]
            WHERE [{pkColumn}] = @PkValue
            """;

            using var conn = new SqlConnection(_targetConnectionString);
            var exists = await conn.ExecuteScalarAsync<int>(
                existsSql, new { PkValue = pkValue }) > 0;

            if (!exists) return conflicts;

            // Check who wrote it
            var existingSource = await GetExistingSourceIdAsync(
                targetTable, pkValue.ToString()!);

            // Same source — legitimate update, no conflict
            if (existingSource == sourceId)
                return conflicts;

            // Different source — genuine PK collision
            conflicts.Add(new ConflictRecord
            {
                RunId = runId,
                TargetColumn = pkColumn,
                ConflictType = ConflictType.PrimaryKeyCollision,
                SourceValue = pkValue.ToString(),
                ExistingValue = pkValue.ToString(),
                ResolutionPolicy = ResolutionPolicy.LastWriteWins,
                ResolvedValue = pkValue.ToString(),
                SourceRowKey = pkValue.ToString(),
            });

            _logger.LogWarning(
                "PK collision: {Table}.{Col}={Val} "
                + "owned by '{Owner}', incoming from '{Source}'",
                targetTable, pkColumn, pkValue,
                existingSource, sourceId);

            return conflicts;
        }

        /// <summary>
        /// Check 3 — Value conflicts from different sources.
        ///
        /// When a row exists in the target written by SOURCE_A,
        /// and SOURCE_B sends a different value for the same column,
        /// that is a genuine value conflict requiring resolution.
        ///
        /// Same-source updates are NOT conflicts — they are incremental
        /// updates from the same producer.
        ///
        /// Literature: Abedjan et al. (VLDB 2016) — pattern violation family.
        /// Khatiwada et al. (EDBT 2026) — silent failure on value conflicts.
        /// </summary>
        private async Task<List<ConflictRecord>> DetectValueConflictsAsync(
            Guid runId,
            Dictionary<string, object?> row,
            string targetTable,
            string pkColumn,
            string sourceId)
        {
            var conflicts = new List<ConflictRecord>();

            if (!row.TryGetValue(pkColumn, out var pkRaw))
                return conflicts;

            var pkValue = ConvertJsonElement(pkRaw);
            if (pkValue is null) return conflicts;

            // Get existing row from target
            var selectSql = $"""
            SELECT * FROM [{targetTable}]
            WHERE [{pkColumn}] = @PkValue
            """;

            using var conn = new SqlConnection(_targetConnectionString);
            var existingRow = await conn.QueryFirstOrDefaultAsync(
                selectSql, new { PkValue = pkValue });

            if (existingRow is null) return conflicts;

            // Check who wrote the existing row
            var existingSource = await GetExistingSourceIdAsync(
                targetTable, pkValue.ToString()!);

            // Same source — legitimate update, no conflict
            if (existingSource == sourceId)
                return conflicts;

            // Different source — compare values
            var existingDict = (IDictionary<string, object>)existingRow;

            foreach (var (column, newValue) in row)
            {
                if (column.Equals(pkColumn,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!existingDict.TryGetValue(column, out var existingValue))
                    continue;

                var newStr = ConvertJsonElement(newValue)?.ToString();
                var existStr = existingValue is DBNull
                    ? null : existingValue?.ToString();

                // Skip if both null or both same value
                if (newStr == existStr) continue;
                if (newValue is null && existingValue is DBNull) continue;
                if (newValue is null || existingValue is null
                    || existingValue is DBNull) continue;

                // Normalise numbers before comparing
                // Prevents "1.99" vs "1.9900" false positives
                if (decimal.TryParse(newStr, out var d1) &&
                    decimal.TryParse(existStr, out var d2))
                {
                    if (d1 == d2) continue;
                }

                conflicts.Add(new ConflictRecord
                {
                    RunId = runId,
                    TargetColumn = column,
                    ConflictType = ConflictType.ValueConflict,
                    SourceValue = newStr,
                    ExistingValue = existStr,
                    ResolutionPolicy = ResolutionPolicy.ProvenancePreserved,
                    ResolvedValue = null,
                    SourceRowKey = pkValue.ToString(),
                });

                _logger.LogWarning(
                    "Value conflict: {Table}.{Col} "
                    + "incoming='{New}' existing='{Old}' "
                    + "source_a='{SA}' source_b='{SB}'",
                    targetTable, column,
                    newStr, existStr,
                    existingSource, sourceId);
            }

            return conflicts;
        }

        /// <summary>
        /// Looks up which source_id last wrote a given row.
        /// Returns null if no provenance record exists (new row).
        /// </summary>
        private async Task<string?> GetExistingSourceIdAsync(
            string targetTable, string rowKey)
        {
            const string sql = """
            SELECT source_id
            FROM integration_provenance
            WHERE target_table   = @TargetTable
                AND target_row_key = @RowKey
            ORDER BY integrated_at DESC
            """;

            using var conn = new SqlConnection(_registryConnectionString);
            return await conn.QueryFirstOrDefaultAsync<string>(
                sql, new { TargetTable = targetTable, RowKey = rowKey });
        }

        /// <summary>
        /// Applies the configured resolution policy to a conflicted row.
        ///
        /// LastWriteWins — accept the incoming row, overwrite existing
        /// Rejected      — discard the row entirely
        /// ProvenancePreserved — null out the conflicting column,
        ///                       keep other values from incoming row
        /// </summary>
        //private static (Dictionary<string, object?>? row, bool accepted) ApplyResolutionPolicy(Dictionary<string, object?> row,
        //                                                                List<ConflictRecord> conflicts, ResolutionPolicy policy)
        //{
        //    // Null out value-conflicted columns under provenance policy
        //    foreach (var conflict in conflicts.Where(c => c.ConflictType == ConflictType.ValueConflict))
        //    {
        //        if (row.ContainsKey(conflict.TargetColumn))
        //        {
        //            row[conflict.TargetColumn] = null;
        //            conflict.ResolvedValue = null;
        //        }
        //    }

        //    return policy switch
        //    {
        //        ResolutionPolicy.LastWriteWins => (row, true),
        //        ResolutionPolicy.Rejected => (null, false),
        //        ResolutionPolicy.ProvenancePreserved => (row, true),
        //        _ => (row, true)
        //    };
        //}

        private static (Dictionary<string, object?>? row, bool accepted) ApplyResolutionPolicy(Dictionary<string, object?> row,
                                                                        List<ConflictRecord> conflicts, ResolutionPolicy policy)
        {
            var valueConflicts = conflicts
                .Where(c => c.ConflictType == ConflictType.ValueConflict)
                .ToList();

            switch (policy)
            {
                case ResolutionPolicy.LastWriteWins:
                    // The incoming (newer) source wins: keep B's values as-is.
                    // Record what each conflict resolved TO, for the audit log.
                    foreach (var c in valueConflicts)
                        c.ResolvedValue = c.SourceValue;   // incoming value won
                                                           // (PK-collision records already carry ResolvedValue = the key.)
                    return (row, true);

                case ResolutionPolicy.ProvenancePreserved:
                    // Refuse to pick a winner on disputed columns: null them,
                    // keep the rest of the incoming row.
                    foreach (var c in valueConflicts)
                    {
                        if (row.ContainsKey(c.TargetColumn))
                        {
                            row[c.TargetColumn] = null;
                            c.ResolvedValue = null;
                        }
                    }
                    return (row, true);

                case ResolutionPolicy.Rejected:
                    // Discard the whole row; nothing written.
                    foreach (var c in valueConflicts)
                        c.ResolvedValue = null;
                    return (null, false);

                default:
                    return (row, true);
            }
        }

        /// <summary>
        /// Persists all conflict records to conflict_log in one transaction.
        /// </summary>
        private async Task PersistConflictsAsync(
            List<ConflictRecord> conflicts)
        {
            if (conflicts.Count == 0) return;

            const string sql = """
            INSERT INTO conflict_log
                (run_id, target_column, conflict_type,
                    source_value, existing_value,
                    resolution_policy, resolved_value,
                    source_row_key, detected_at)
            VALUES
                (@RunId, @TargetColumn, @ConflictType,
                    @SourceValue, @ExistingValue,
                    @ResolutionPolicy, @ResolvedValue,
                    @SourceRowKey, @DetectedAt)
            """;

            using var conn = new SqlConnection(_registryConnectionString);
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();

            try
            {
                foreach (var c in conflicts)
                {
                    await conn.ExecuteAsync(sql, new
                    {
                        c.RunId,
                        c.TargetColumn,
                        ConflictType = c.ConflictType.ToString(),
                        c.SourceValue,
                        c.ExistingValue,
                        ResolutionPolicy = c.ResolutionPolicy.ToString(),
                        c.ResolvedValue,
                        c.SourceRowKey,
                        DetectedAt = c.DetectedAt,
                    }, tx);
                }

                await tx.CommitAsync();
                _logger.LogDebug(
                    "Persisted {Count} conflict records.", conflicts.Count);
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        /// <summary>
        /// Converts JsonElement values to native .NET types.
        /// Required because the API receives JSON which deserialises
        /// all values as JsonElement rather than native types.
        /// </summary>
        private static object? ConvertJsonElement(object? value)
        {
            if (value is System.Text.Json.JsonElement el)
            {
                return el.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => el.GetString(),
                    System.Text.Json.JsonValueKind.Number =>
                        el.TryGetInt64(out var l) ? l : el.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => el.ToString()
                };
            }
            return value;
        }

        /// <summary>
        /// One query: load all existing target rows whose PK is in the incoming set.
        /// Returns a dictionary keyed by PK string → the row's column/value map.
        /// Replaces the per-row existence + select queries.
        /// Keys are chunked to respect SQL Server's parameter limit (~2100).
        /// </summary>
        private async Task<Dictionary<string, IDictionary<string, object>>> BulkLoadExistingRowsAsync(
            string targetTable, string pkColumn, List<string> pkValues)
        {
            var result = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
            if (pkValues.Count == 0) return result;

            using var conn = new SqlConnection(_targetConnectionString);

            const int chunkSize = 1000; // safely under the 2100 parameter limit
            foreach (var chunk in Chunk(pkValues, chunkSize))
            {
                var sql = $"SELECT * FROM [{targetTable}] WHERE [{pkColumn}] IN @Keys";
                var rows = await conn.QueryAsync(sql, new { Keys = chunk }, commandTimeout: 600);
                foreach (var r in rows)
                {
                    var dict = (IDictionary<string, object>)r;
                    if (dict.TryGetValue(pkColumn, out var keyObj) && keyObj is not null)
                    {
                        var key = keyObj.ToString()!;
                        result[key] = dict;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// One query: load the owning source_id for each incoming PK from provenance.
        /// Returns PK string → source_id. Uses the most recent integration per row.
        /// </summary>
        private async Task<Dictionary<string, string>> BulkLoadOwnersAsync(string targetTable, List<string> pkValues)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pkValues.Count == 0) return result;

            using var conn = new SqlConnection(_registryConnectionString);

            const int chunkSize = 1000;
            foreach (var chunk in Chunk(pkValues, chunkSize))
            {
                // Latest source per target_row_key for this target table.
                const string sql = """
                                    SELECT p.target_row_key, p.source_id
                                    FROM integration_provenance p
                                    INNER JOIN (
                                        SELECT target_row_key, MAX(integrated_at) AS max_at
                                        FROM integration_provenance
                                        WHERE target_table = @TargetTable AND target_row_key IN @Keys
                                        GROUP BY target_row_key
                                    ) latest
                                      ON p.target_row_key = latest.target_row_key
                                     AND p.integrated_at  = latest.max_at
                                    WHERE p.target_table = @TargetTable
                                    """;

                var rows = await conn.QueryAsync<(string RowKey, string SourceId)>(sql, new { TargetTable = targetTable, Keys = chunk }, commandTimeout: 600);
                foreach (var (rowKey, srcId) in rows)
                    if (rowKey is not null) result[rowKey] = srcId;
            }
            return result;
        }

        /// <summary>Splits a list into chunks of the given size.</summary>
        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }
}
