using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Adapters.Sqlite;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Validates the versioned destination contract before any source data is opened.
/// SQLite and explicit staged file-output contracts each define their own write semantics.
/// </summary>
public static class DestinationPlanValidator
{
    public const string SupportedKind = "sqlite";
    public const string SupportedFormat = "xbrl-facts-v1";
    public const string CsvKind = "csv";
    public const string ExcelKind = "excel";
    public const string JsonKind = "json";
    public const string XmlKind = "xml";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> FileFormats =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [CsvKind] = new HashSet<string>(StringComparer.Ordinal)
                { "csv-records-v1", "csv-records-v2" },
            [ExcelKind] = new HashSet<string>(StringComparer.Ordinal)
                { "xlsx-records-v1", "xlsx-records-v2" },
            [JsonKind] = new HashSet<string>(StringComparer.Ordinal) { "json-records-v1" },
            [XmlKind] = new HashSet<string>(StringComparer.Ordinal) { "xml-records-v1" }
        };

    private static readonly IReadOnlyDictionary<string, string> DefaultFileFormats =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CsvKind] = "csv-records-v2",
            [ExcelKind] = "xlsx-records-v2",
            [JsonKind] = "json-records-v1",
            [XmlKind] = "xml-records-v1"
        };

    private static readonly IReadOnlyDictionary<string, string> FileFormatKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csv-records-v1"] = CsvKind,
            ["csv-records-v2"] = CsvKind,
            ["xlsx-records-v1"] = ExcelKind,
            ["xlsx-records-v2"] = ExcelKind,
            ["json-records-v1"] = JsonKind,
            ["xml-records-v1"] = XmlKind
        };

    public static IReadOnlyList<string> SupportedFileKinds { get; } = Array.AsReadOnly(
        new[] { CsvKind, ExcelKind, JsonKind, XmlKind });

    public static bool IsFileKind(string? kind) => kind is not null && FileFormats.ContainsKey(kind);

    /// <summary>Default contract for newly configured destinations. Existing v1 plans remain supported.</summary>
    public static string? FileFormatFor(string kind)
        => DefaultFileFormats.TryGetValue(kind, out var format) ? format : null;

    public static bool TryResolveFileFormat(string? format, out string kind)
    {
        if (format is not null && FileFormatKinds.TryGetValue(format, out var resolvedKind))
        {
            kind = resolvedKind;
            return true;
        }
        kind = string.Empty;
        return false;
    }

    public static bool UsesFieldPresenceMetadata(DestinationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return UsesFieldPresenceMetadata(plan.Kind, plan.Format);
    }

    public static bool UsesFieldPresenceMetadata(string kind, string format)
        => (StringComparer.Ordinal.Equals(kind, CsvKind)
                && StringComparer.Ordinal.Equals(format, "csv-records-v2"))
            || (StringComparer.Ordinal.Equals(kind, ExcelKind)
                && StringComparer.Ordinal.Equals(format, "xlsx-records-v2"));

    public static DiagnosticBag ValidateFileDestinationPath(string? path)
    {
        var diagnostics = new DiagnosticBag();
        if (string.IsNullOrWhiteSpace(path)) return diagnostics;

        var fullPath = Path.GetFullPath(path);
        var protectedSuffixes = new[]
        {
            ProjectManager.ProjectExtension,
            ProjectManager.ProjectExtension + ProjectManager.LockSuffix,
            ProjectManager.ProjectExtension + ProjectManager.BackupSuffix
        };
        if (protectedSuffixes.Any(suffix => fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                "A file destination cannot replace a project file or its lock or backup.");
        }
        return diagnostics;
    }

    public static DiagnosticBag Validate(DestinationPlan? plan, string? operationId = null)
    {
        var diagnostics = new DiagnosticBag();
        if (plan is null)
            return diagnostics;

        if (plan.Version != DestinationPlan.CurrentVersion)
        {
            Unsupported($"Destination plan version {plan.Version} is unsupported; supported version is {DestinationPlan.CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(plan.Locator))
            Invalid("Destination locator is required.");
        if (StringComparer.Ordinal.Equals(plan.Kind, SupportedKind))
        {
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
                Unsupported($"Destination format '{plan.Format}' is unsupported. SQLite requires '{SupportedFormat}'.");
            if (!StringComparer.Ordinal.Equals(plan.WriteMode, "upsert"))
                Unsupported($"Destination write mode '{plan.WriteMode}' is unsupported for SQLite; use 'upsert'.");
        }
        else if (FileFormats.TryGetValue(plan.Kind, out var supportedFileFormats))
        {
            if (!string.IsNullOrWhiteSpace(plan.Table))
                Invalid("File destinations do not use a table name.");
            if (plan.BusinessKey is { Count: > 0 })
                Invalid("File destinations do not use a SQLite business key.");
            if (!supportedFileFormats.Contains(plan.Format))
                Unsupported($"Destination format '{plan.Format}' is unsupported for '{plan.Kind}'. Supported contracts: "
                    + string.Join(", ", supportedFileFormats.Order(StringComparer.Ordinal).Select(format => $"'{format}'")) + ".");
            if (!StringComparer.Ordinal.Equals(plan.WriteMode, "replace"))
                Unsupported($"Destination write mode '{plan.WriteMode}' is unsupported for file targets; use 'replace'.");
        }
        else
        {
            Unsupported($"Destination kind '{plan.Kind}' is unsupported. Supported kinds are '{SupportedKind}', {string.Join(", ", FileFormats.Keys)}.");
        }

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
        if (!StringComparer.Ordinal.Equals(plan.Kind, SupportedKind)) return diagnostics;

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
