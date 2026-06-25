using ITCDI.Core.Enums;
using ITCDI.Core.Interfaces;
using ITCDI.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Http
{
    public class SchemaMatchingClient : ISchemaMatchingClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<SchemaMatchingClient> _logger;

        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public SchemaMatchingClient(HttpClient http, ILogger<SchemaMatchingClient> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<SchemaMatchingResult> MatchAsync(string sourceId, List<SchemaColumn> sourceColumns, List<SchemaColumn> targetColumns,
                                                            List<MappingRule>? existingMappings = null, CancellationToken ct = default)
        {
            _logger.LogInformation(
                "Calling Python /match for source {SourceId} " +
                "({Src} source cols, {Tgt} target cols)",
                sourceId, sourceColumns.Count, targetColumns.Count);

            var payload = new
            {
                source_id = sourceId,
                source_columns = sourceColumns.Select(c => new
                {
                    name = c.Name,
                    data_type = c.DataType,
                    is_nullable = c.IsNullable
                }),
                target_columns = targetColumns.Select(c => new
                {
                    name = c.Name,
                    data_type = c.DataType,
                    is_nullable = c.IsNullable
                }),
                existing_mappings = existingMappings?.Select(m => new
                {
                    source_columns = m.SourceColumns,
                    target_column = m.TargetColumn,
                    mapping_type = m.MappingType.ToString(),
                    expression = m.Expression
                })
            };

            HttpResponseMessage response;

            try
            {
                response = await _http.PostAsJsonAsync("/match", payload, _json, ct);

                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "Python schema-matching service unreachable at {Url}",
                    _http.BaseAddress);

                throw new InvalidOperationException(
                    "Python schema-matching service is unavailable. " +
                    "Ensure it is running on " + _http.BaseAddress, ex);
            }

            var result = await response.Content
                .ReadFromJsonAsync<MatchResponse>(_json, ct)
                ?? throw new InvalidOperationException(
                    "Empty response from Python matching service.");

            _logger.LogInformation(
                "Python returned {Count} mappings in {Ms}ms",
                result.Mappings.Count, result.ProcessingTimeMs);

            return new SchemaMatchingResult(
                result.Mappings.Select(m => new MappingRule
                {
                    SourceColumns = m.SourceColumns,
                    TargetColumn = m.TargetColumn,
                    MappingType = Enum.TryParse<MappingType>(
                                          m.MappingType, out var t)
                                          ? t : MappingType.OneToOne,
                    Expression = m.Expression,
                    ConfidenceScore = m.ConfidenceScore
                }).ToList(),
                result.UnlinkableSourceColumns,
                result.ProcessingTimeMs
            );
        }

        // ── Response DTOs (mirrors Python Pydantic models) ────────────────────────

        private record MatchResponse(
            List<MappingDto> Mappings,
            List<string> UnlinkableSourceColumns,
            long ProcessingTimeMs
        );

        private record MappingDto(
            List<string> SourceColumns,
            string TargetColumn,
            string MappingType,
            string Expression,
            double ConfidenceScore
        );
    }
}
