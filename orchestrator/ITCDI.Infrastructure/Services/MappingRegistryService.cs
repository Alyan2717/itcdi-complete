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
using System.Text.Json;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Services
{
    public class MappingRegistryService : IMappingRegistry
    {
        private readonly string _connectionString;
        private readonly ILogger<MappingRegistryService> _logger;

        public MappingRegistryService(string connectionString, ILogger<MappingRegistryService> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public async Task<bool> HasMappingsAsync(string sourceId)
        {
            const string sql = """
                                SELECT COUNT(1) 
                                FROM mapping_registry 
                                WHERE source_id = @SourceId 
                                  AND is_active = 1
                                """;

            using var conn = new SqlConnection(_connectionString);

            var count = await conn.ExecuteScalarAsync<int>(sql, new { SourceId = sourceId });

            return count > 0;
        }

        public async Task<List<MappingRule>> GetActiveMappingsAsync(string sourceId)
        {
            const string sql = """
                                SELECT mapping_id, source_id, source_columns, target_column,
                                       mapping_type, mapping_expression, confidence_score,
                                       schema_fingerprint, created_at, last_validated_at,
                                       is_active, notes
                                FROM mapping_registry
                                WHERE source_id = @SourceId
                                  AND is_active = 1
                                ORDER BY confidence_score DESC
                                """;

            using var conn = new SqlConnection(_connectionString);

            var rows = await conn.QueryAsync(sql, new { SourceId = sourceId });

            return rows.Select(r => (MappingRule)MapToDomain(r)).ToList();
        }

        private static MappingRule MapToDomain(dynamic row) => new()
        {
            MappingId = (Guid)row.mapping_id,
            SourceId = (string)row.source_id,
            SourceColumns = JsonSerializer.Deserialize<List<string>>((string)row.source_columns) ?? [],
            TargetColumn = (string)row.target_column,
            MappingType = Enum.Parse<MappingType>((string)row.mapping_type),
            Expression = (string)row.mapping_expression,
            ConfidenceScore = (double)row.confidence_score,
            SchemaFingerprint = (string)row.schema_fingerprint,
            CreatedAt = (DateTime)row.created_at,
            LastValidatedAt = (DateTime)row.last_validated_at,
            IsActive = (bool)row.is_active,
            Notes = row.notes is DBNull ? null : (string?)row.notes
        };

        public async Task<MappingRule?> GetMappingByIdAsync(Guid mappingId)
        {
            const string sql = """
                                SELECT mapping_id, source_id, source_columns, target_column,
                                       mapping_type, mapping_expression, confidence_score,
                                       schema_fingerprint, created_at, last_validated_at,
                                       is_active, notes
                                FROM mapping_registry
                                WHERE mapping_id = @MappingId
                                """;

            using var conn = new SqlConnection(_connectionString);

            var row = await conn.QueryFirstOrDefaultAsync(sql, new { MappingId = mappingId });

            return row is null ? null : MapToDomain(row);
        }

        public async Task SaveMappingsAsync(string sourceId, List<MappingRule> mappings)
        {
            const string sql = """
                                INSERT INTO mapping_registry
                                    (mapping_id, source_id, source_columns, target_column,
                                     mapping_type, mapping_expression, confidence_score,
                                     schema_fingerprint, created_at, last_validated_at,
                                     is_active, notes)
                                VALUES
                                    (@MappingId, @SourceId, @SourceColumns, @TargetColumn,
                                     @MappingType, @Expression, @ConfidenceScore,
                                     @SchemaFingerprint, @CreatedAt, @LastValidatedAt,
                                     1, @Notes)
                                """;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var transaction = await conn.BeginTransactionAsync();

            try
            {
                foreach (var m in mappings)
                {
                    await conn.ExecuteAsync(sql, new
                    {
                        m.MappingId,
                        SourceId = sourceId,
                        SourceColumns = JsonSerializer.Serialize(m.SourceColumns),
                        m.TargetColumn,
                        MappingType = m.MappingType.ToString(),
                        Expression = m.Expression,
                        m.ConfidenceScore,
                        m.SchemaFingerprint,
                        m.CreatedAt,
                        m.LastValidatedAt,
                        m.Notes
                    }, transaction);
                }

                await transaction.CommitAsync();

                _logger.LogInformation("Saved {Count} mappings for source {SourceId}", mappings.Count, sourceId);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task DeactivateMappingsAsync(string sourceId)
        {
            const string sql = """
                                UPDATE mapping_registry
                                SET is_active = 0
                                WHERE source_id = @SourceId
                                  AND is_active = 1
                                """;

            using var conn = new SqlConnection(_connectionString);

            var affected = await conn.ExecuteAsync(sql, new { SourceId = sourceId });

            _logger.LogInformation("Deactivated {Count} mappings for source {SourceId}", affected, sourceId);
        }

        public async Task UpdateLastValidatedAsync(Guid mappingId)
        {
            const string sql = """
                                UPDATE mapping_registry
                                SET last_validated_at = SYSUTCDATETIME()
                                WHERE mapping_id = @MappingId
                                """;

            using var conn = new SqlConnection(_connectionString);

            await conn.ExecuteAsync(sql, new { MappingId = mappingId });
        }

        public async Task RepointMappingSourceAsync(string sourceId, string oldName, string newName)
        {
            const string sql = """
                                UPDATE mapping_registry
                                SET source_columns     = REPLACE(source_columns, @OldQuoted, @NewQuoted),
                                    mapping_expression = REPLACE(mapping_expression, @OldName, @NewName)
                                WHERE source_id = @SourceId
                                  AND is_active = 1
                                  AND source_columns LIKE @OldLike
                                """;

            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(sql, new
            {
                SourceId = sourceId,
                OldQuoted = $"\"{oldName}\"",
                NewQuoted = $"\"{newName}\"",
                OldName = oldName,
                NewName = newName,
                OldLike = $"%\"{oldName}\"%"
            });
        }
    }
}
