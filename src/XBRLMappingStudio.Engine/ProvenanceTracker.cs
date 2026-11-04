using System.Collections.Concurrent;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Tracks which source occurrences contributed to each output slot value per section 2
/// (Execution and reproducibility) and MAP03 acceptance.
/// </summary>
public sealed class ProvenanceTracker
{
    // Slot key (nodeId, portName, index) -> contributing sources.
    private readonly ConcurrentDictionary<string, List<SourceReference>> _sources = new();

    public static string SlotKey(string nodeId, string portName, int? rowIndex = null)
        => rowIndex.HasValue ? $"{nodeId}.{portName}[{rowIndex}]" : $"{nodeId}.{portName}";

    public void RecordSource(string slotKey, SourceReference source)
    {
        var list = _sources.GetOrAdd(slotKey, _ => new List<SourceReference>());
        lock (list)
        {
            if (!list.Contains(source)) list.Add(source);
        }
    }

    public void MergeSources(string targetSlotKey, IEnumerable<string> contributorSlotKeys)
    {
        var target = _sources.GetOrAdd(targetSlotKey, _ => new List<SourceReference>());
        lock (target)
        {
            foreach (var k in contributorSlotKeys)
            {
                if (_sources.TryGetValue(k, out var list))
                {
                    lock (list)
                    {
                        foreach (var src in list)
                            if (!target.Contains(src)) target.Add(src);
                    }
                }
            }
        }
    }

    public IReadOnlyList<SourceReference> GetContributors(string slotKey)
        => _sources.TryGetValue(slotKey, out var list)
            ? (IReadOnlyList<SourceReference>)list.ToArray()
            : Array.Empty<SourceReference>();

    public IReadOnlyDictionary<string, IReadOnlyList<SourceReference>> Snapshot()
        => _sources.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SourceReference>)kv.Value.ToArray());
}

/// <summary>
/// Runtime context for a plan execution. Carries frozen parameters, sample limits,
/// diagnostic bag, provenance, and cancellation.
/// </summary>
public sealed class ExecutionContext
{
    public required string OperationId { get; init; }
    public required RunManifest Manifest { get; init; }
    public DiagnosticBag Diagnostics { get; } = new();
    public ProvenanceTracker Provenance { get; } = new();

    /// <summary>Parameters frozen at start: locale, fiscal calendar, entity, dates.</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    /// <summary>
    /// When true, execution is bounded to a small sample and results are labelled partial
    /// per section 2 (Execution and reproducibility).
    /// </summary>
    public bool IsPreview { get; init; }
    public int PreviewRowLimit { get; init; } = 10;

    /// <summary>
    /// Values loaded from each source binding, keyed by binding id. Adapters populate
    /// this prior to plan execution.
    /// </summary>
    public Dictionary<string, IReadOnlyList<DataRecord>> LoadedSources { get; } = new();

    /// <summary>
    /// Bounded exact-decimal domain (section 3): reject values outside the declared
    /// significant-digit / fractional-scale range. Defaults are permissive; profiles
    /// may tighten.
    /// </summary>
    public int MaxSignificantDigits { get; init; } = 28;

    /// <summary>MAP05 resource budget: maximum items in a Collection port.</summary>
    public int MaxCollectionItems { get; init; } = 100_000;

    /// <summary>MAP06 resource budget: maximum many-to-many expansion rows before block.</summary>
    public int MaxJoinExpansionRows { get; init; } = 200_000;

    /// <summary>MAP08: label aggregates computed from preview runs as partial.</summary>
    public List<SampledTotal> SampledTotals { get; } = new();

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Aggregate value produced by a preview run; labelled so the reviewer cannot
/// mistake a partial total for a full-run figure.
/// </summary>
public sealed record SampledTotal(string NodeId, string Label, decimal Value, bool IsPartial);

/// <summary>
/// One row/record from a source, with per-cell source references for provenance.
/// </summary>
public sealed class DataRecord
{
    public required string RowId { get; init; }
    public required IReadOnlyDictionary<string, DataValue> Fields { get; init; }
    public IReadOnlyDictionary<string, SourceReference>? CellSources { get; init; }
    public bool HasStableRowIdentity { get; init; }

    public DataValue this[string column] => Fields.TryGetValue(column, out var v) ? v : DataValue.Missing();
}
