using System.Text.Json;

namespace XBRLMappingStudio.Project;

/// <summary>Portable, data-only package of immutable function revisions.</summary>
public sealed record FunctionPackageDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required string RootFunctionId { get; init; }
    public int RootRevision { get; init; }
    public List<FunctionDefinition> Functions { get; init; } = new();
}

/// <summary>Bounded JSON serialization for importable function packages.</summary>
public static class FunctionPackageSerializer
{
    public const int MaximumPackageBytes = 16 * 1024 * 1024;

    public static string Serialize(FunctionPackageDocument package)
    {
        ArgumentNullException.ThrowIfNull(package);
        Validate(package);
        var json = JsonSerializer.Serialize(package, ProjectSerializer.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumPackageBytes)
            throw new InvalidOperationException("Function package exceeds the 16 MiB size limit.");
        return json;
    }

    public static FunctionPackageDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumPackageBytes)
            throw new InvalidOperationException("Function package exceeds the 16 MiB size limit.");

        FunctionPackageDocument package;
        try
        {
            package = JsonSerializer.Deserialize<FunctionPackageDocument>(json, ProjectSerializer.Options)
                ?? throw new InvalidOperationException("Function package is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Function package JSON is malformed or incomplete.", ex);
        }

        Validate(package);
        return package;
    }

    private static void Validate(FunctionPackageDocument package)
    {
        if (package.SchemaVersion != 1)
            throw new InvalidOperationException($"Function package schema v{package.SchemaVersion} is not supported.");
        if (string.IsNullOrWhiteSpace(package.RootFunctionId) || package.RootRevision < 1)
            throw new InvalidOperationException("Function package root identity is invalid.");
        if (package.Functions is not { Count: > 0 })
            throw new InvalidOperationException("Function package does not contain any revisions.");

        var revisions = new HashSet<(string FunctionId, int Revision)>();
        foreach (var function in package.Functions)
        {
            if (function is null
                || string.IsNullOrWhiteSpace(function.FunctionId)
                || string.IsNullOrWhiteSpace(function.Name)
                || string.IsNullOrWhiteSpace(function.CatalogVersion)
                || function.Inputs is null
                || function.Outputs is null
                || function.Nodes is null
                || function.Edges is null
                || function.Revision < 1
                || !revisions.Add((function.FunctionId, function.Revision)))
                throw new InvalidOperationException("Function package contains an invalid or duplicate revision.");

            ValidatePorts(function.Inputs);
            ValidatePorts(function.Outputs);
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in function.Nodes)
            {
                if (node is null
                    || string.IsNullOrWhiteSpace(node.NodeId)
                    || string.IsNullOrWhiteSpace(node.NodeType)
                    || string.IsNullOrWhiteSpace(node.DisplayName)
                    || node.Parameters is null
                    || node.InputPorts is null
                    || node.OutputPorts is null
                    || !nodeIds.Add(node.NodeId))
                    throw new InvalidOperationException($"Function '{function.FunctionId}@{function.Revision}' contains an invalid node.");
                ValidatePorts(node.InputPorts);
                ValidatePorts(node.OutputPorts);
            }

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in function.Edges)
            {
                if (edge is null
                    || string.IsNullOrWhiteSpace(edge.EdgeId)
                    || string.IsNullOrWhiteSpace(edge.SourceNodeId)
                    || string.IsNullOrWhiteSpace(edge.SourcePortName)
                    || string.IsNullOrWhiteSpace(edge.TargetNodeId)
                    || string.IsNullOrWhiteSpace(edge.TargetPortName)
                    || !edgeIds.Add(edge.EdgeId))
                    throw new InvalidOperationException($"Function '{function.FunctionId}@{function.Revision}' contains an invalid edge.");
            }
        }

        if (!revisions.Contains((package.RootFunctionId, package.RootRevision)))
            throw new InvalidOperationException("Function package does not include its declared root revision.");
    }

    private static void ValidatePorts(IEnumerable<PortDefinition>? ports)
    {
        if (ports is null) throw new InvalidOperationException("Function package contains a missing port list.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            if (port is null
                || string.IsNullOrWhiteSpace(port.Name)
                || !Enum.IsDefined(port.Shape)
                || !Enum.IsDefined(port.Cardinality)
                || !names.Add(port.Name))
                throw new InvalidOperationException("Function package contains an invalid or duplicate port.");
        }
    }
}
