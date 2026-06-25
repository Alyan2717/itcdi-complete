using ITCDI.Core.Enums;
using ITCDI.Core.Models;

namespace ITCDI.API
{
    public class DTOs
    {

        // ── Request DTOs ──────────────────────────────────────────────────────────────

        public class ColumnRequest
        {
            public string Name { get; set; } = string.Empty;
            public string DataType { get; set; } = string.Empty;
            public bool IsNullable { get; set; }
        }

        public class IntegrationSubmitRequest
        {
            public string SourceId { get; set; } = string.Empty;
            public string SourceTable { get; set; } = string.Empty;
            public string TargetTable { get; set; } = string.Empty;
            public List<SchemaColumn> SourceColumns { get; set; } = [];   // ← NEW: optional explicit types
            public List<Dictionary<string, object?>> SourceRows { get; set; } = [];
            public ResolutionPolicy ResolutionPolicy { get; set; } = ResolutionPolicy.LastWriteWins;
            public RunMode RunMode { get; set; } = RunMode.Incremental;
        }

        // ── Response DTOs ─────────────────────────────────────────────────────────────

        public record MetricsDto(
            double MappingStabilityRate,
            double HistoricalRewriteRatio,
            long TotalLatencyMs,
            long MatchingTimeMs,
            long TransformationTimeMs,
            long ConflictDetectionTimeMs,
            long InsertionTimeMs,
            double ConflictDetectionRate,
            double MappingPrecision,
            double MappingRecall,
            double MappingF1
        );

        public record IntegrationSubmitResponse(
            Guid RunId,
            string Status,
            int BatchNumber,
            int RowsInserted,
            int RowsUpdated,
            int RowsSkipped,
            int ConflictsDetected,
            int DriftEventsDetected,
            MetricsDto Metrics
        );
    }
}
