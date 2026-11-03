using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters;

/// <summary>
/// Raised by an adapter when a source or destination violates a stable IO contract
/// (duplicate keys, oversized depth, out-of-budget numbers, unsafe XML, a direction
/// mismatch). Carries the never-reused diagnostic <see cref="Code"/> so callers can map
/// the failure into a <see cref="DiagnosticBag"/> without inspecting exception text.
/// </summary>
public sealed class AdapterException : Exception
{
    public string Code { get; }
    public string Stage { get; }

    public AdapterException(string code, string message,
        string stage = DiagnosticStage.Introspection)
        : base(message)
    {
        Code = code;
        Stage = stage;
    }

    public Diagnostic ToDiagnostic(string? sourceId = null, string? operationId = null)
        => new()
        {
            Code = Code,
            Subsystem = DiagnosticSubsystem.Adapter,
            Stage = Stage,
            Severity = DiagnosticSeverity.Error,
            IsBlocking = true,
            Message = Message,
            Source = sourceId is null ? null : new SourceReference(sourceId, Code),
            OperationId = operationId
        };
}
