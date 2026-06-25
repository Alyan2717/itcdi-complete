using DynamicExpresso;
using ITCDI.Core.Interfaces;
using ITCDI.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Services
{
    public class TransformationEngine : ITransformationEngine
    {
        private readonly ILogger<TransformationEngine> _logger;

        public TransformationEngine(ILogger<TransformationEngine> logger)
        {
            _logger = logger;
        }

        public object? EvaluateExpression(string expression, Dictionary<string, object?> sourceRow)
        {
            var interpreter = new Interpreter();

            foreach (var (columnName, value) in sourceRow)
            {
                // Convert JsonElement to native type before binding
                var nativeValue = ConvertJsonElement(value);

                if (nativeValue is null)
                    interpreter.SetVariable(columnName, null, typeof(object));
                else
                    interpreter.SetVariable(columnName, nativeValue,
                                            nativeValue.GetType());
            }

            return interpreter.Eval(expression);
        }

        private static object? ConvertJsonElement(object? value)
        {
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

        public List<Dictionary<string, object?>> TransformBatch(List<Dictionary<string, object?>> sourceRows, List<MappingRule> mappings)
        {
            var results = new List<Dictionary<string, object?>>(sourceRows.Count);
            var errors = 0;

            foreach (var sourceRow in sourceRows)
            {
                var targetRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var mapping in mappings)
                {
                    try
                    {
                        // Build a scoped row containing only the columns
                        // this mapping needs — cleaner variable binding
                        var scopedRow = BuildScopedRow(mapping.SourceColumns, sourceRow);
                        if(!targetRow.ContainsKey(mapping.TargetColumn))
                        {
                            targetRow[mapping.TargetColumn] = EvaluateExpression(mapping.Expression, scopedRow);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Expression '{Expr}' failed for target column '{Col}'. " + "Null will be written.",
                                            mapping.Expression, mapping.TargetColumn);

                        targetRow[mapping.TargetColumn] = null;
                        errors++;
                    }
                }

                results.Add(targetRow);
            }

            if (errors > 0)
                _logger.LogWarning("{Errors} expression evaluation failures in batch of {Total} rows.", errors, sourceRows.Count);
            else
                _logger.LogInformation("Batch of {Count} rows transformed successfully.", sourceRows.Count);

            return results;
        }

        private static Dictionary<string, object?> BuildScopedRow(List<string> requiredColumns, Dictionary<string, object?> fullRow)
        {
            // Only include columns the mapping actually needs
            // This prevents variable name collisions if two columns
            // have similar names in a large source table
            var scoped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in requiredColumns)
            {
                if (fullRow.TryGetValue(col, out var value))
                    scoped[col] = value;
                else
                    scoped[col] = null; // Column declared but missing from row
            }

            return scoped;
        }
    }
}
