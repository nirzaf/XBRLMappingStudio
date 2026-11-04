using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Adapters.Sqlite;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Validates the versioned destination contract before any source data is opened.
/// The current implementation accepts only the explicit transactional SQLite fact
/// contract; other adapters must be added with their own validated write semantics.
/// </summary>
public static class DestinationPlanValidator
{
    public const string SupportedKind = "sqlite";
    public const string SupportedFormat = "xbrl-facts-v1";

    public static DiagnosticBag Validate(DestinationPlan? plan, string? operationId = null)
    {
        var diagnostics = new DiagnosticBag();
        if (plan is null)
            return diagnostics;

        if (plan.Version != DestinationPlan.CurrentVersion)
        {
            Unsupported($"Destination plan version {plan.Version} is unsupported; supported version is {DestinationPlan.CurrentVersion}.");
        }

        if (!StringComparer.Ordinal.Equals(plan.Kind, SupportedKind))
        {
            Unsupported($"Destination kind '{plan.Kind}' is unsupported. Version 1 supports only '{SupportedKind}'.");
        }

        if (string.IsNullOrWhiteSpace(plan.Locator))
            Invalid("Destination locator is required.");
        if (string.IsNullOrWhiteSpace(plan.Table))
            Invalid("SQLite destination table is required.");
        if (plan.BusinessKey is null || plan.BusinessKey.Count == 0)
        {
            Invalid("At least one SQLite business-key column is required.");
        }
        else
        {
            if (plan.BusinessKey.Any(string.IsNullOrWhiteSpace))
                Invalid("SQLite business-key column names cannot be empty.");
            if (plan.BusinessKey.Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != plan.BusinessKey.Count(key => !string.IsNullOrWhiteSpace(key)))
            {
                Invalid("SQLite business-key column names must be unique, ignoring SQLite identifier case.");
            }
        }

        if (!StringComparer.Ordinal.Equals(plan.Format, SupportedFormat))
            Unsupported($"Destination format '{plan.Format}' is unsupported. Version 1 requires '{SupportedFormat}'.");
        if (!StringComparer.Ordinal.Equals(plan.WriteMode, "upsert"))
            Unsupported($"Destination write mode '{plan.WriteMode}' is unsupported. Version 1 requires 'upsert'.");
        if (!StringComparer.Ordinal.Equals(plan.Compatibility, "strict"))
            Unsupported($"Destination compatibility mode '{plan.Compatibility}' is unsupported. Version 1 requires 'strict'.");

        return diagnostics;

        void Invalid(string message)
            => diagnostics.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation, message, operationId: operationId);

        void Unsupported(string message)
            => diagnostics.Error(DiagnosticCodes.IoUnsupportedOperation, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation, message, operationId: operationId);
    }

    public static DiagnosticBag ValidateSchema(DestinationPlan plan, SqliteTargetTableSchema schema)
    {
        var diagnostics = Validate(plan);
        if (diagnostics.HasBlockingErrors) return diagnostics;

        var missingColumns = XbrlFactRecordMapper.OutputColumns
            .Where(column => !schema.Columns.ContainsKey(column))
            .ToArray();
        if (missingColumns.Length > 0)
        {
            diagnostics.Error(DiagnosticCodes.IoColumnMissing, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                $"SQLite target '{schema.Name}' is missing fact columns: {string.Join(", ", missingColumns)}.");
        }

        var unsupportedKeys = plan.BusinessKey
            .Where(key => !XbrlFactRecordMapper.OutputColumns.Contains(key, StringComparer.Ordinal))
            .ToArray();
        if (unsupportedKeys.Length > 0)
        {
            diagnostics.Error(DiagnosticCodes.IoColumnMissing, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                $"Business-key columns are not part of the XBRL fact row contract: {string.Join(", ", unsupportedKeys)}.");
        }

        if (schema.Columns.TryGetValue("value", out var valueType)
            && !StringComparer.Ordinal.Equals(GetSqliteAffinity(valueType), "TEXT"))
        {
            diagnostics.Error(DiagnosticCodes.IoNumberOutOfBudget, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                $"Exact decimal column 'value' must have TEXT affinity; '{valueType}' has {GetSqliteAffinity(valueType)} affinity.");
        }

        if (!schema.UniqueKeys.Any(key => key.SequenceEqual(plan.BusinessKey, StringComparer.Ordinal)))
        {
            diagnostics.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                $"SQLite target '{schema.Name}' must have a primary or unique index on the selected business key.");
        }

        return diagnostics;
    }

    private static string GetSqliteAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal)
            || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal)) return "BLOB";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        return "NUMERIC";
    }
}
