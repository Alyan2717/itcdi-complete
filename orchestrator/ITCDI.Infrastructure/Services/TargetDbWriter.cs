using Dapper;
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
    public class TargetDbWriter : ITargetDbWriter
    {
        private readonly string _targetConnectionString;
        private readonly string _registryConnectionString;
        private readonly ILogger<TargetDbWriter> _logger;

        public TargetDbWriter(string targetConnectionString, string registryConnectionString, ILogger<TargetDbWriter> logger)
        {
            _targetConnectionString = targetConnectionString;
            _registryConnectionString = registryConnectionString;
            _logger = logger;
        }

        //public async Task<WriteResult> UpsertAsync(Guid runId, string sourceId, Guid mappingId, string targetTable,
        //                                           List<Dictionary<string, object?>> rows, string primaryKeyColumn)
        //{
        //    if (rows.Count == 0)
        //        return new WriteResult(0, 0, 0);

        //    int inserted = 0, updated = 0, skipped = 0;

        //    // All rows in a batch share the same target columns
        //    // Take column names from the first row
        //    var columns = rows[0].Keys.ToList();

        //    // Build the MERGE statement once — reuse for every row
        //    var mergeSql = BuildMergeSql(targetTable, columns, primaryKeyColumn);

        //    using var targetConn = new SqlConnection(_targetConnectionString);
        //    await targetConn.OpenAsync();

        //    // Single transaction for the whole batch: one log flush instead of
        //    // one per row. This is the dominant speedup at scale.
        //    using var tx = (SqlTransaction)await targetConn.BeginTransactionAsync();

        //    foreach (var row in rows)
        //    {
        //        if (!row.TryGetValue(primaryKeyColumn, out var pkVal) || pkVal is null)
        //        {
        //            _logger.LogWarning("Row skipped — PK column '{PK}' missing or null.", primaryKeyColumn);
        //            skipped++;
        //            continue;
        //        }

        //        try
        //        {
        //            //// Get all columns from this row
        //            //columns = row.Keys.ToList();

        //            //// Build dynamic MERGE SQL
        //            //mergeSql = BuildMergeSql(targetTable, columns, primaryKeyColumn);

        //            //// Build parameters
        //            //var parameters = BuildParameters(row, columns);

        //            //// Execute MERGE and capture action
        //            //var action = await targetConn.ExecuteScalarAsync<string>(mergeSql, parameters);
                    
        //            // Columns are identical across the batch; mergeSql already built.
        //            var parameters = BuildParameters(row, columns);

        //            // Execute MERGE on the shared transaction.
        //            var action = await targetConn.ExecuteScalarAsync<string>(mergeSql, parameters, transaction: tx);

        //            if (action == "INSERT")
        //            {
        //                inserted++;
        //                _logger.LogDebug("Inserted row PK={PK} into {Table}", pkVal, targetTable);
        //            }
        //            else if (action == "UPDATE")
        //            {
        //                updated++;
        //                _logger.LogDebug("Updated row PK={PK} in {Table}", pkVal, targetTable);
        //            }

        //            // Write provenance for this row
        //            await WriteProvenanceAsync(runId, sourceId, mappingId, targetTable, pkVal.ToString()!, pkVal.ToString()!);
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError(ex, "Failed to upsert row PK={PK} into {Table}", pkVal, targetTable);
        //            skipped++;
        //        }
        //    }

        //    await tx.CommitAsync();

        //    _logger.LogInformation("Upsert complete on {Table}: " + "inserted={I} updated={U} skipped={S}", 
        //                            targetTable, inserted, updated, skipped);

        //    return new WriteResult(inserted, updated, skipped);
        //}

        public async Task<WriteResult> UpsertAsync(Guid runId, string sourceId, Guid mappingId, string targetTable,
                                                   List<Dictionary<string, object?>> rows, string primaryKeyColumn)
        {
            if (rows.Count == 0)
                return new WriteResult(0, 0, 0);

            int inserted = 0, updated = 0, skipped = 0;

            // All rows in a batch share the same target columns
            // Take column names from the first row
            var columns = rows[0].Keys.ToList();

            // Build the MERGE statement once — reuse for every row
            var mergeSql = BuildMergeSql(targetTable, columns, primaryKeyColumn);

            using var targetConn = new SqlConnection(_targetConnectionString);
            await targetConn.OpenAsync();
            using var tx = (SqlTransaction)await targetConn.BeginTransactionAsync();

            // ONE registry connection for the whole batch (provenance is in a different DB)
            using var regConn = new SqlConnection(_registryConnectionString);
            await regConn.OpenAsync();
            using var regTx = (SqlTransaction)await regConn.BeginTransactionAsync();

            foreach (var row in rows)
            {
                if (!row.TryGetValue(primaryKeyColumn, out var pkVal) || pkVal is null)
                {
                    _logger.LogWarning("Row skipped — PK column '{PK}' missing or null.", primaryKeyColumn);
                    skipped++;
                    continue;
                }

                try
                {
                    var parameters = BuildParameters(row, columns);
                    var action = await targetConn.ExecuteScalarAsync<string>(
                        mergeSql, parameters, transaction: tx, commandTimeout: 600);

                    if (action == "INSERT") inserted++;
                    else if (action == "UPDATE") updated++;

                    await WriteProvenanceAsync(regConn, regTx, runId, sourceId, mappingId,
                                               targetTable, pkVal.ToString()!, pkVal.ToString()!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upsert row PK={PK} into {Table}", pkVal, targetTable);
                    skipped++;
                }
            }

            await tx.CommitAsync();
            await regTx.CommitAsync();

            _logger.LogInformation("Upsert complete on {Table}: " + "inserted={I} updated={U} skipped={S}",
                                    targetTable, inserted, updated, skipped);

            return new WriteResult(inserted, updated, skipped);
        }


        private static string BuildMergeSql(string targetTable, List<string> columns, string primaryKeyColumn)
        {
            var nonPkColumns = columns.Where(c => !c.Equals(primaryKeyColumn, StringComparison.OrdinalIgnoreCase)).ToList();

            // Wrap column names in brackets to handle reserved words
            var insertCols = string.Join(", ", columns.Select(c => $"[{c}]"));

            var insertVals = string.Join(", ", columns.Select(c => $"@{c}"));

            var updateSet = string.Join(", ", nonPkColumns.Select(c => $"Target.[{c}] = Source.[{c}]"));

            var sourceCols = string.Join(", ", columns.Select(c => $"@{c} AS [{c}]"));

            return $"""
                    MERGE {targetTable} AS Target
                    USING (SELECT {sourceCols}) AS Source
                    ON Target.[{primaryKeyColumn}] = Source.[{primaryKeyColumn}]
                    WHEN MATCHED THEN
                        UPDATE SET {updateSet}
                    WHEN NOT MATCHED THEN
                        INSERT ({insertCols})
                        VALUES ({insertVals})
                    OUTPUT $action;
                    """;
        }

        private static DynamicParameters BuildParameters(Dictionary<string, object?> row, List<string> columns)
        {
            var parameters = new DynamicParameters();

            foreach (var col in columns)
            {
                row.TryGetValue(col, out var value);
                parameters.Add($"@{col}", ConvertToNativeType(value));
            }

            return parameters;
        }

        /// <summary>
        /// Converts JsonElement values from System.Text.Json into
        /// native CLR types that Dapper can send to SQL Server.
        /// When the API receives JSON, all values deserialise as
        /// JsonElement — Dapper cannot map these directly.
        /// </summary>
        private static object? ConvertToNativeType(object? value)
        {
            if (value is null) return null;

            if (value is System.Text.Json.JsonElement element)
            {
                return element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => element.GetString(),
                    System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => element.ToString()
                };
            }

            return value;
        }

        //private async Task WriteProvenanceAsync(Guid runId, string sourceId, Guid mappingId, string targetTable, string targetRowKey, string sourceRowKey)
        //{
        //    const string sql = """
        //                        MERGE integration_provenance AS Target
        //                        USING (SELECT @TargetTable AS target_table,
        //                                      @TargetRowKey AS target_row_key,
        //                                      @SourceId AS source_id) AS Source
        //                        ON  Target.target_table    = Source.target_table
        //                        AND Target.target_row_key  = Source.target_row_key
        //                        AND Target.source_id       = Source.source_id
        //                        WHEN MATCHED THEN
        //                            UPDATE SET
        //                                last_updated_at = SYSUTCDATETIME(),
        //                                run_id          = @RunId,
        //                                mapping_id      = @MappingId
        //                        WHEN NOT MATCHED THEN
        //                            INSERT (target_table, target_row_key, source_id,
        //                                    source_row_key, mapping_id, run_id)
        //                            VALUES (@TargetTable, @TargetRowKey, @SourceId,
        //                                    @SourceRowKey, @MappingId, @RunId);
        //                        """;

        //    using var conn = new SqlConnection(_registryConnectionString);

        //    await conn.ExecuteAsync(sql, new
        //    {
        //        TargetTable = targetTable,
        //        TargetRowKey = targetRowKey,
        //        SourceId = sourceId,
        //        SourceRowKey = sourceRowKey,
        //        MappingId = mappingId,
        //        RunId = runId
        //    });
        //}

        private async Task WriteProvenanceAsync(SqlConnection conn, SqlTransaction tx, Guid runId, string sourceId, Guid mappingId,
                                                string targetTable, string targetRowKey, string sourceRowKey)
        {
            const string sql = """
                        MERGE integration_provenance AS Target
                        USING (SELECT @TargetTable AS target_table,
                                      @TargetRowKey AS target_row_key,
                                      @SourceId AS source_id) AS Source
                        ON  Target.target_table    = Source.target_table
                        AND Target.target_row_key  = Source.target_row_key
                        AND Target.source_id       = Source.source_id
                        WHEN MATCHED THEN
                            UPDATE SET last_updated_at = SYSUTCDATETIME(),
                                       run_id     = @RunId,
                                       mapping_id = @MappingId
                        WHEN NOT MATCHED THEN
                            INSERT (target_table, target_row_key, source_id,
                                    source_row_key, mapping_id, run_id)
                            VALUES (@TargetTable, @TargetRowKey, @SourceId,
                                    @SourceRowKey, @MappingId, @RunId);
                        """;

            await conn.ExecuteAsync(sql, new
            {
                TargetTable = targetTable,
                TargetRowKey = targetRowKey,
                SourceId = sourceId,
                SourceRowKey = sourceRowKey,
                MappingId = mappingId,
                RunId = runId
            }, transaction: tx, commandTimeout: 600);
        }
    }
}
