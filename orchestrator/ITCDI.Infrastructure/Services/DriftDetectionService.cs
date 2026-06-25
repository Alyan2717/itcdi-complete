using Dapper;
using ITCDI.Core.Enums;
using ITCDI.Core.Interfaces;
using ITCDI.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Services
{
    public class DriftDetectionService : IDriftDetectionService
    {
        private readonly string _connectionString;
        private readonly ILogger<DriftDetectionService> _logger;

        public DriftDetectionService(string connectionString, ILogger<DriftDetectionService> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public SchemaFingerprint ComputeFingerprint(string sourceId, List<SchemaColumn> columns)
        {
            // Sort columns alphabetically before hashing
            // This makes the hash order-independent
            // Same columns in different order = same fingerprint
            var sorted = columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                .Select(c => $"{c.Name.ToLower()}|{c.DataType.ToLower()}|{c.IsNullable}");

            var raw = string.Join(",", sorted);

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            var hash = Convert.ToHexString(hashBytes).ToLower();

            _logger.LogDebug("Fingerprint computed for {SourceId}: {Hash}", sourceId, hash);

            return new SchemaFingerprint(sourceId, hash, DateTime.UtcNow, columns);
        }

        public async Task<SchemaFingerprint?> GetStoredFingerprintAsync(string sourceId)
        {
            const string sql = """
                                SELECT TOP 1 fingerprint, schema_json, captured_at
                                FROM source_schema_snapshot
                                WHERE source_id = @SourceId
                                ORDER BY captured_at DESC
                                """;

            using var conn = new SqlConnection(_connectionString);

            var row = await conn.QueryFirstOrDefaultAsync(sql, new { SourceId = sourceId });

            if (row is null)
                return null;

            var columns = JsonSerializer.Deserialize<List<SchemaColumn>>((string)row.schema_json,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            return new SchemaFingerprint(sourceId, (string)row.fingerprint, (DateTime)row.captured_at, columns);
        }

        public async Task SaveFingerprintAsync(SchemaFingerprint fingerprint)
        {
            const string sql = """
                                IF NOT EXISTS (
                                    SELECT 1 FROM source_schema_snapshot
                                    WHERE source_id = @SourceId AND fingerprint = @Fingerprint)
                                INSERT INTO source_schema_snapshot
                                    (source_id, fingerprint, schema_json, captured_at)
                                VALUES
                                    (@SourceId, @Fingerprint, @SchemaJson, @CapturedAt)
                                """;

            using var conn = new SqlConnection(_connectionString);

            await conn.ExecuteAsync(sql, new
            {
                SourceId = fingerprint.SourceId,
                Fingerprint = fingerprint.Hash,
                SchemaJson = JsonSerializer.Serialize(fingerprint.Columns),
                CapturedAt = fingerprint.CapturedAt
            });

            _logger.LogInformation("Snapshot saved for {SourceId} fingerprint {Hash}", fingerprint.SourceId, fingerprint.Hash);
        }

        public async Task<DriftAnalysisResult> AnalyseDriftAsync(string sourceId, SchemaFingerprint newFingerprint)
        {
            var stored = await GetStoredFingerprintAsync(sourceId);

            // First time this source has ever been integrated
            if (stored is null)
            {
                _logger.LogInformation("{SourceId} is a new source. No drift baseline exists.", sourceId);
                return new DriftAnalysisResult { DriftDetected = false };
            }

            // Fingerprints match — zero drift, skip everything
            if (stored.Hash == newFingerprint.Hash)
            {
                _logger.LogInformation("{SourceId} schema unchanged. Fingerprint: {Hash}", sourceId, stored.Hash);
                return new DriftAnalysisResult { DriftDetected = false };
            }

            // Fingerprints differ — classify exactly what changed
            _logger.LogWarning("{SourceId} drift detected. Old={Old} New={New}", sourceId, stored.Hash, newFingerprint.Hash);

            var events = ClassifyDrift(sourceId, stored, newFingerprint);

            return new DriftAnalysisResult
            {
                DriftDetected = true,
                Events = events
            };
        }

        //private List<DriftEvent> ClassifyDrift(string sourceId, SchemaFingerprint old, SchemaFingerprint @new)
        //{
        //    var events = new List<DriftEvent>();

        //    var oldCols = old.Columns.ToDictionary(c => c.Name.ToLower());
        //    var newCols = @new.Columns.ToDictionary(c => c.Name.ToLower());

        //    var added = newCols.Keys.Except(oldCols.Keys).ToList();
        //    var removed = oldCols.Keys.Except(newCols.Keys).ToList();
        //    var common = oldCols.Keys.Intersect(newCols.Keys).ToList();

        //    // ColumnAdd — LOW impact
        //    // Existing mappings still work. New column is simply unmapped.
        //    foreach (var col in added)
        //    {
        //        events.Add(BuildEvent(sourceId, old, @new, DriftType.ColumnAdd, ImpactLevel.Low, [col]));

        //        _logger.LogInformation("{SourceId}: Column ADDED '{Col}' — LOW impact", sourceId, col);
        //    }

        //    // ColumnRemove — HIGH impact
        //    // Any mapping that referenced this column is now broken.
        //    foreach (var col in removed)
        //    {
        //        events.Add(BuildEvent(sourceId, old, @new, DriftType.ColumnRemove, ImpactLevel.High, [col]));

        //        _logger.LogWarning("{SourceId}: Column REMOVED '{Col}' — HIGH impact", sourceId, col);
        //    }

        //    // Type changes on columns that exist in both versions
        //    foreach (var col in common)
        //    {
        //        var oldType = oldCols[col].DataType.ToLower();
        //        var newType = newCols[col].DataType.ToLower();

        //        if (oldType == newType) continue;

        //        var (driftType, impact) = ClassifyTypeChange(oldType, newType);

        //        events.Add(BuildEvent(sourceId, old, @new, driftType, impact, [col]));

        //        _logger.LogWarning("{SourceId}: Column '{Col}' type changed {Old} -> {New} — {Impact}", sourceId, col, oldType, newType, impact);
        //    }

        //    return events;
        //}

        private List<DriftEvent> ClassifyDrift(string sourceId, SchemaFingerprint old, SchemaFingerprint @new)
        {
            var events = new List<DriftEvent>();

            var oldCols = old.Columns.ToDictionary(c => c.Name.ToLower());
            var newCols = @new.Columns.ToDictionary(c => c.Name.ToLower());

            var added = newCols.Keys.Except(oldCols.Keys).ToList();
            var removed = oldCols.Keys.Except(newCols.Keys).ToList();
            var common = oldCols.Keys.Intersect(newCols.Keys).ToList();

            // ── Rename correlation ──
            // A rename surfaces as one removed + one added column. If a removed
            // column and an added column share the same SQL data type, treat the
            // pair as a ColumnRename (MEDIUM impact) instead of Remove(HIGH)+Add(LOW).
            // AffectedColumns is recorded as [oldName, newName] so the orchestrator
            // can re-point the stored mappings before reading them for reuse.
            foreach (var rem in removed.ToList())
            {
                var match = added.FirstOrDefault(add =>
                    oldCols[rem].DataType.Equals(newCols[add].DataType,
                                                 StringComparison.OrdinalIgnoreCase));

                if (match is null)
                    continue;

                events.Add(BuildEvent(sourceId, old, @new, DriftType.ColumnRename, ImpactLevel.Medium, [rem, match]));

                _logger.LogWarning("{SourceId}: Column RENAMED '{Old}' -> '{New}' — MEDIUM impact",
                                   sourceId, rem, match);

                removed.Remove(rem);    // consumed — not a Remove
                added.Remove(match);    // consumed — not an Add
            }

            // ColumnAdd — LOW impact (only truly new columns remain)
            foreach (var col in added)
            {
                events.Add(BuildEvent(sourceId, old, @new, DriftType.ColumnAdd, ImpactLevel.Low, [col]));
                _logger.LogInformation("{SourceId}: Column ADDED '{Col}' — LOW impact", sourceId, col);
            }

            // ColumnRemove — HIGH impact (only truly removed columns remain)
            foreach (var col in removed)
            {
                events.Add(BuildEvent(sourceId, old, @new, DriftType.ColumnRemove, ImpactLevel.High, [col]));
                _logger.LogWarning("{SourceId}: Column REMOVED '{Col}' — HIGH impact", sourceId, col);
            }

            // Type changes on columns present in both versions
            foreach (var col in common)
            {
                var oldType = oldCols[col].DataType.ToLower();
                var newType = newCols[col].DataType.ToLower();
                if (oldType == newType) continue;

                var (driftType, impact) = ClassifyTypeChange(oldType, newType);
                events.Add(BuildEvent(sourceId, old, @new, driftType, impact, [col]));
                _logger.LogWarning("{SourceId}: Column '{Col}' type changed {Old} -> {New} — {Impact}",
                                   sourceId, col, oldType, newType, impact);
            }

            return events;
        }

        private static (DriftType driftType, ImpactLevel impact) ClassifyTypeChange(string oldType, string newType)
        {
            // Widening: safe direction, data fits in the larger type
            bool isWidening =
                                (oldType == "int" && newType == "bigint") ||
                                (oldType == "float" && newType == "real") ||
                                (oldType.StartsWith("varchar") && newType.StartsWith("varchar") &&
                                    ExtractVarcharLength(newType) > ExtractVarcharLength(oldType)) ||
                                (oldType == "date" && newType == "datetime2") ||
                                (oldType == "datetime" && newType == "datetime2");

            if (isWidening)
                return (DriftType.TypeWidening, ImpactLevel.Low);

            // Everything else is narrowing — treat as HIGH impact
            return (DriftType.TypeNarrowing, ImpactLevel.High);
        }

        private static int ExtractVarcharLength(string dataType)
        {
            // Parses "varchar(255)" → 255
            var open = dataType.IndexOf('(');
            var close = dataType.IndexOf(')');

            if (open < 0 || close < 0) return 0;

            return int.TryParse(dataType[(open + 1)..close], out var len) ? len : 0;
        }

        private static DriftEvent BuildEvent(string sourceId, SchemaFingerprint old, SchemaFingerprint @new, DriftType type, ImpactLevel impact,
        List<string> affectedColumns) => new()
        {
            SourceId = sourceId,
            DetectedAt = DateTime.UtcNow,
            DriftType = type,
            OldFingerprint = old.Hash,
            NewFingerprint = @new.Hash,
            ImpactLevel = impact,
            AffectedColumns = affectedColumns
        };

        public async Task LogDriftEventsAsync(Guid runId, List<DriftEvent> events)
        {
            if (events.Count == 0) 
                return;

            const string sql = """
                                INSERT INTO drift_log
                                    (source_id, run_id, detected_at, drift_type,
                                     old_fingerprint, new_fingerprint,
                                     impact_level, affected_columns,
                                     rows_affected, re_matched)
                                VALUES
                                    (@SourceId, @RunId, @DetectedAt, @DriftType,
                                     @OldFp, @NewFp,
                                     @Impact, @AffectedCols,
                                     @RowsAffected, @ReMatched)
                                """;

            using var conn = new SqlConnection(_connectionString);

            foreach (var e in events)
            {
                await conn.ExecuteAsync(sql, new
                {
                    SourceId = e.SourceId,
                    RunId = runId,
                    DetectedAt = e.DetectedAt,
                    DriftType = e.DriftType.ToString(),
                    OldFp = e.OldFingerprint,
                    NewFp = e.NewFingerprint,
                    Impact = e.ImpactLevel.ToString(),
                    AffectedCols = JsonSerializer.Serialize(e.AffectedColumns),
                    RowsAffected = e.RowsAffected,
                    ReMatched = e.ReMatched
                });
            }

            _logger.LogInformation("Logged {Count} drift events for run {RunId}", events.Count, runId);
        }
    }
}
