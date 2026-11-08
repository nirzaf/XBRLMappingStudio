namespace XBRLMappingStudio.Project;

/// <summary>
/// Named, typed function boundaries. A body has one input marker exposing the input
/// signature and one output marker consuming the output signature. Legacy single-port
/// markers may use a local name (usually "value") instead of the public port name.
/// </summary>
public static class FunctionSignatures
{
    public static bool AreValid(IReadOnlyList<PortDefinition>? ports)
        => ports is not null
            && ports.All(port => port is not null && !string.IsNullOrWhiteSpace(port.Name)
                && Enum.IsDefined(port.Shape) && Enum.IsDefined(port.Cardinality))
            && ports.Select(port => port.Name).Distinct(StringComparer.Ordinal).Count() == ports.Count;

    // Requiredness is enforced from the pinned definition, not a caller's editable flag.
    public static bool Matches(IReadOnlyList<PortDefinition>? declared, IReadOnlyList<PortDefinition>? actual,
        bool allowSinglePortAlias = false)
    {
        if (!AreValid(declared) || !AreValid(actual) || declared!.Count != actual!.Count) return false;
        return declared.All(port => actual.Any(candidate =>
            (candidate.Name == port.Name || (allowSinglePortAlias && declared.Count == 1))
            && candidate.Shape == port.Shape && candidate.Cardinality == port.Cardinality));
    }

    public static void Validate(FunctionDefinition definition)
    {
        if (!AreValid(definition.Inputs) || !AreValid(definition.Outputs) || definition.Outputs.Count == 0)
            throw new InvalidOperationException("Function signatures require unique named ports with valid shapes and cardinalities, and at least one output.");
    }
}
