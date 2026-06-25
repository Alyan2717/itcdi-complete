using ITCDI.Application.Services;
using ITCDI.Core.Enums;
using ITCDI.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static ITCDI.API.DTOs;

namespace ITCDI.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class IntegrationController : ControllerBase
    {
        private readonly IntegrationOrchestrator _orchestrator;
        private readonly ILogger<IntegrationController> _logger;

        public IntegrationController(IntegrationOrchestrator orchestrator, ILogger<IntegrationController> logger)
        {
            _orchestrator = orchestrator;
            _logger = logger;
        }

        /// <summary>
        /// Submit a source table batch for integration into the target schema.
        /// Returns all five thesis evaluation metrics in the response.
        /// </summary>
        ///
        [HttpPost("submit")]
        [ProducesResponseType(typeof(IntegrationSubmitResponse), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> Submit([FromBody] IntegrationSubmitRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            _logger.LogInformation("Submit received | Source={Source} | Rows={Rows} | Mode={Mode}", req.SourceId, req.SourceRows.Count, req.RunMode);

            try
            {
                // Map HTTP request to domain request
                var request = new IntegrationRequest
                {
                    SourceId = req.SourceId,
                    SourceTable = req.SourceTable,
                    TargetTable = req.TargetTable,
                    PrimaryKeyColumn = string.Empty,  // resolved automatically
                    //SourceColumns = [], // inferred from row keys below
                    SourceColumns = req.SourceColumns,   // ← was [] ; now honors explicit types if provided
                    TargetColumns = [], // read from DB automatically
                    SourceRows = req.SourceRows,
                    ResolutionPolicy = req.ResolutionPolicy,
                    RunMode = req.RunMode,
                    TriggeredBy = TriggerSource.Api
                };

                var result = await _orchestrator.RunAsync(request, ct);

                return Ok(new IntegrationSubmitResponse(
                    RunId: result.RunId,
                    Status: result.Status.ToString(),
                    BatchNumber: result.Metrics.BatchNumber,
                    RowsInserted: result.Metrics.RowsInserted,   // ← fix
                    RowsUpdated: result.Metrics.RowsUpdated,    // ← fix
                    RowsSkipped: result.Metrics.RowsSkipped,    // ← fix
                    ConflictsDetected: result.Conflicts.Count,
                    DriftEventsDetected: result.DriftEvents.Count,
                    Metrics: new MetricsDto(
                        MappingStabilityRate: result.Metrics.MappingStabilityRate,
                        HistoricalRewriteRatio: result.Metrics.HistoricalRewriteRatio,
                        TotalLatencyMs: result.Metrics.TotalLatencyMs,
                        MatchingTimeMs: result.Metrics.MatchingTimeMs,
                        TransformationTimeMs: result.Metrics.TransformationTimeMs,
                        ConflictDetectionTimeMs: result.Metrics.ConflictDetectionTimeMs,
                        InsertionTimeMs: result.Metrics.InsertionTimeMs,
                        ConflictDetectionRate: result.Metrics.ConflictDetectionRate,
                        MappingPrecision: result.Metrics.Precision,
                        MappingRecall: result.Metrics.Recall,
                        MappingF1: result.Metrics.F1
                    )
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,"Integration failed for source {SourceId}", req.SourceId);

                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Health check — confirms the API is running.
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "ok",
                service = "ITCDI Integration API",
                time = DateTime.UtcNow
            });
        }
    }
}
