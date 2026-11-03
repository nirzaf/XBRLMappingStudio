using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Project;

/// <summary>
/// Port cardinality per MAP01. Collection-to-single conversion is rejected unless
/// an explicit aggregation node bridges the two.
/// </summary>
public enum PortCardinality
{
    Single,
    Collection
}

/// <summary>
/// Declared shape of a port. Two ports connect only when their shapes unify.
/// </summary>
public enum PortShape
{
    /// <summary>Exact numeric with declared precision and scale.</summary>
    Decimal,
    Integer,
    String,
    Boolean,
    Date,
    Instant,
    /// <summary>Qualified name (namespace + local).</summary>
    QualifiedName,
    /// <summary>Opaque identifier such as source row key.</summary>
    Identifier,
    /// <summary>Composite record with named sub-fields.</summary>
    Record,
    /// <summary>Fact occurrence ready for XBRL serialization.</summary>
    Fact
}

/// <summary>
/// A port declaration on a node. Port names are unique within the node.
/// </summary>
public sealed record PortDefinition(
    string Name,
    PortShape Shape,
    PortCardinality Cardinality,
    bool IsRequired = true,
    string? Description = null);

/// <summary>
/// Layout position for a node in the desktop graph. Excluded from semantic digest.
/// </summary>
public sealed record LayoutPosition(double X, double Y);

/// <summary>
/// Stable identifier type. Guid-like string so projects remain diff-friendly.
/// </summary>
public static class Id
{
    public static string New() => Guid.NewGuid().ToString("N");
    public static bool IsValid(string? s) =>
        !string.IsNullOrEmpty(s) && s!.Length == 32;
}

/// <summary>
/// Node in the mapping graph. NodeId is stable across saves; type-specific parameters
/// are stored as JSON-shaped values so unknown node types can be preserved for inspection
/// even when execution is blocked (per MAP01).
/// </summary>
public sealed record MappingNode
{
    public required string NodeId { get; init; }
    public required string NodeType { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Node-specific parameters (constant values, culture, concept name, etc.).</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    public List<PortDefinition> InputPorts { get; init; } = new();
    public List<PortDefinition> OutputPorts { get; init; } = new();

    /// <summary>Layout position, excluded from semantic revision.</summary>
    public LayoutPosition? Layout { get; set; }

    public MappingNode ShallowCopy() => this with
    {
        Parameters = new Dictionary<string, object?>(Parameters),
        InputPorts = InputPorts.ToList(),
        OutputPorts = OutputPorts.ToList(),
        Layout = Layout is null ? null : Layout with { }
    };
}

/// <summary>
/// Directed edge connecting an output port on one node to an input port on another.
/// </summary>
public sealed record MappingEdge
{
    public required string EdgeId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string SourcePortName { get; init; }
    public required string TargetNodeId { get; init; }
    public required string TargetPortName { get; init; }
}

/// <summary>
/// Source binding describes which input artifact and how it is discovered.
/// Adapters interpret this; the graph itself stays format-agnostic.
/// </summary>
public sealed record SourceBinding
{
    public required string BindingId { get; init; }
    public required string Kind { get; init; }        // "excel", "csv", "db", "param"
    public required string DisplayName { get; init; }

    /// <summary>Locator relative to workspace when portable; absolute when host-supplied.</summary>
    public string? Locator { get; init; }

    /// <summary>Adapter-specific configuration (sheet, header row, query, etc.).</summary>
    public Dictionary<string, object?> Configuration { get; init; } = new();

    /// <summary>Schema fingerprint recorded when the mapping was last validated.</summary>
    public string? SchemaFingerprint { get; init; }
}

/// <summary>
/// Reusable subgraph definition per MAP07. Pinned by callers so upgrades preview
/// changes before adoption.
/// </summary>
public sealed record FunctionDefinition
{
    public required string FunctionId { get; init; }
    public required string Name { get; init; }
    public int Revision { get; init; } = 1;
    public required string CatalogVersion { get; init; }

    public List<PortDefinition> Inputs { get; init; } = new();
    public List<PortDefinition> Outputs { get; init; } = new();
    public List<MappingNode> Nodes { get; init; } = new();
    public List<MappingEdge> Edges { get; init; } = new();
}

/// <summary>
/// Approval record per section 2 (Approval and data ownership). Records who accepted
/// which revision, when, and under which authority.
/// </summary>
public sealed record ApprovalRecord
{
    public required int SemanticRevision { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedUtc { get; init; }
    public required string ProfileDigest { get; init; }
    public string? Authority { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Free-form metadata about the project.
/// </summary>
public sealed record ProjectMetadata
{
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Organization { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; }
}

/// <summary>
/// Root of a saved mapping. Semantic revision (executable content) is maintained
/// independently from the layout generation (full-document save) per BASE03.
/// </summary>
public sealed class MappingProject
{
    /// <summary>Current serialization schema version. Unknown future versions open read-only.</summary>
    public int SchemaVersion { get; init; } = ProjectSchemaVersions.Current;

    public required string ProjectId { get; init; }
    public required string Name { get; init; }

    /// <summary>Immutable identifier of the filing profile this project targets (XBR01).</summary>
    public required string ProfileId { get; init; }

    /// <summary>Digest of the profile assets (taxonomy package, plugins, rules).</summary>
    public required string ProfileDigest { get; init; }

    /// <summary>Function catalog version used by the project.</summary>
    public string FunctionCatalogVersion { get; init; } = "1";

    /// <summary>Changes only when executable content changes (nodes, edges, bindings, functions).</summary>
    public int SemanticRevision { get; set; } = 1;

    /// <summary>Changes on every full-document save (including layout-only edits).</summary>
    public int LayoutGeneration { get; set; } = 1;

    public ProjectMetadata Metadata { get; set; } = new()
    {
        CreatedUtc = DateTimeOffset.UtcNow,
        LastModifiedUtc = DateTimeOffset.UtcNow
    };

    public List<MappingNode> Nodes { get; init; } = new();
    public List<MappingEdge> Edges { get; init; } = new();
    public List<SourceBinding> Bindings { get; init; } = new();
    public List<FunctionDefinition> Functions { get; init; } = new();

    /// <summary>Approvals recorded per semantic revision.</summary>
    public List<ApprovalRecord> Approvals { get; init; } = new();

    /// <summary>
    /// Compute a stable digest of the executable content (nodes, edges, parameters,
    /// bindings, functions) excluding layout positions. Per BASE03 acceptance: moving
    /// a node must not change this digest.
    /// </summary>
    public string ComputeSemanticDigest() => SemanticRevisionCalculator.Compute(this);
}

public static class ProjectSchemaVersions
{
    public const int Current = 1;
    public const int MinimumReadable = 1;
}
