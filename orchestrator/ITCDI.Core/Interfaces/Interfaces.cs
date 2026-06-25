using ITCDI.Core.Enums;
using ITCDI.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Core.Interfaces
{
    public interface IIntegrationRunRepository
    {
        Task<Guid> CreateRunAsync(IntegrationRun run);
        Task UpdateRunAsync(IntegrationRun run);
        Task<IntegrationRun?> GetRunAsync(Guid runId);
        Task<List<IntegrationRun>> GetRunsBySourceAsync(string sourceId, int limit = 20);
        Task<int> GetNextBatchNumberAsync(string sourceId);
        Task<int> GetCumulativeInsertedRowsAsync(string sourceId, Guid excludeRunId);
    }

    public interface IMappingRegistry
    {
        Task<bool> HasMappingsAsync(string sourceId);
        Task<List<MappingRule>> GetActiveMappingsAsync(string sourceId);
        Task<MappingRule?> GetMappingByIdAsync(Guid mappingId);
        Task SaveMappingsAsync(string sourceId, List<MappingRule> mappings);
        Task DeactivateMappingsAsync(string sourceId);
        Task UpdateLastValidatedAsync(Guid mappingId);
        Task RepointMappingSourceAsync(string sourceId, string oldName, string newName);
    }

    public interface IDriftDetectionService
    {
        SchemaFingerprint ComputeFingerprint(string sourceId, List<SchemaColumn> columns);
        Task<DriftAnalysisResult> AnalyseDriftAsync(string sourceId, SchemaFingerprint newFingerprint);
        Task<SchemaFingerprint?> GetStoredFingerprintAsync(string sourceId);
        Task SaveFingerprintAsync(SchemaFingerprint fingerprint);
        Task LogDriftEventsAsync(Guid runId, List<DriftEvent> events);
    }

    public interface ISchemaMatchingClient
    {
        Task<SchemaMatchingResult> MatchAsync(string sourceId, List<SchemaColumn> sourceColumns, List<SchemaColumn> targetColumns,
                                              List<MappingRule>? existingMappings = null, CancellationToken ct = default);
    }

    public interface ITransformationEngine
    {
        List<Dictionary<string, object?>> TransformBatch(List<Dictionary<string, object?>> sourceRows, List<MappingRule> mappings);
        object? EvaluateExpression(string expression, Dictionary<string, object?> sourceRow);
    }

    public interface IConflictDetectionService
    {
        Task<ConflictResolutionResult> DetectAndResolveAsync(Guid runId, List<Dictionary<string, object?>> transformedRows, string targetTable,
                                                            ResolutionPolicy policy = ResolutionPolicy.LastWriteWins, string sourceId = "");
    }

    public interface ITargetDbWriter
    {
        Task<WriteResult> UpsertAsync(Guid runId, string sourceId, Guid mappingId, string targetTable,
                                      List<Dictionary<string, object?>> rows, string primaryKeyColumn);
    }

    public interface IProvenanceRepository
    {
        Task WriteAsync(IntegrationProvenance provenance);
        Task<List<IntegrationProvenance>> GetBySourceAsync(string sourceId);
        Task<int> CountRowsFromSourceAsync(string sourceId, string targetTable);
    }

    public interface ITargetSchemaReader
    {
        Task<List<SchemaColumn>> ReadTargetSchemaAsync(string targetTable);
        string GetPrimaryKeyColumn(string targetTable);
        Task EnsureTargetTableAsync(string targetTable, List<SchemaColumn> columns, string primaryKeyColumn);
        Task<bool> TargetTableExistsAsync(string targetTable);
    }
}
