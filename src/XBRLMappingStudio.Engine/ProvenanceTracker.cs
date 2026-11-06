using System.Collections.Concurrent;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Tracks which source occurrences and connected transformations contributed to each
/// row- or set-phase output slot per section 2 (Execution and reproducibility) and MAP03.
/// </summary>
public sealed class ProvenanceTracker
{
    // Slot key (nodeId, portName, index) -> contributing sources.
    private readonly ConcurrentDictionary<string, List<SourceReference>> _sources = new();
    // Slot key (nodeId, portName, index) -> row-phase evaluator node IDs in deterministic first-seen order.
    private readonly ConcurrentDictionary<string, List<string>> _transformationIds = new();
    // Slot key (nodeId, portName, index) -> distinct source-specific row-phase paths.
    private readonly ConcurrentDictionary<string, List<SourceTransformationPath>> _sourceTransformationPaths = new();

    public static string SlotKey(string nodeId, string portName, int? rowIndex = null)
        => rowIndex.HasValue ? $"{nodeId}.{portName}[{rowIndex}]" : $"{nodeId}.{portName}";

    /// <summary>
    /// Internal key for a set-phase output or collection item. It is deliberately
    /// disjoint from row slot keys so set-item metadata cannot be mistaken for a row.
    /// </summary>
    public static string SetSlotKey(string nodeId, string portName, int? itemIndex = null, string? fieldName = null)
    {
        var field = fieldName is null ? "-1:" : $"{fieldName.Length}:{fieldName}";
        var index = itemIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "scalar";
        return $"@set:{nodeId.Length}:{nodeId}:{portName.Length}:{portName}:{index}:{field}";
    }

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

    public void RecordTransformation(string slotKey, string nodeId)
    {
        var list = _transformationIds.GetOrAdd(slotKey, _ => new List<string>());
        lock (list)
        {
            if (!list.Contains(nodeId, StringComparer.Ordinal)) list.Add(nodeId);
        }
    }

    public void MergeTransformations(string targetSlotKey, IEnumerable<string> contributorSlotKeys)
    {
        var target = _transformationIds.GetOrAdd(targetSlotKey, _ => new List<string>());
        lock (target)
        {
            foreach (var key in contributorSlotKeys)
            {
                if (!_transformationIds.TryGetValue(key, out var list)) continue;
                lock (list)
                {
                    foreach (var nodeId in list)
                        if (!target.Contains(nodeId, StringComparer.Ordinal)) target.Add(nodeId);
                }
            }
        }
    }

    public void MergeSourceTransformationPaths(string targetSlotKey, IEnumerable<string> contributorSlotKeys)
    {
        var target = _sourceTransformationPaths.GetOrAdd(targetSlotKey, _ => new List<SourceTransformationPath>());
        lock (target)
        {
            foreach (var key in contributorSlotKeys)
            {
                if (!_sourceTransformationPaths.TryGetValue(key, out var list)) continue;
                lock (list)
                {
                    foreach (var path in list)
                        AddPathIfMissing(target, path);
                }
            }
        }
    }

    public void RecordSourceTransformation(string slotKey, SourceReference source, string nodeId)
    {
        var paths = _sourceTransformationPaths.GetOrAdd(slotKey, _ => new List<SourceTransformationPath>());
        lock (paths)
        {
            // A transform may preserve one DataValue.Source even though its connected
            // inputs already supplied the full source-specific path. Use the direct
            // source only to seed adapters/literals that have no inherited path.
            if (paths.Any(path => path.Source == source)) return;
            AddPathIfMissing(paths, new SourceTransformationPath(source, new[] { nodeId }));
        }
    }

    public void RecordSourceTransformationPath(string slotKey, SourceTransformationPath path)
    {
        var paths = _sourceTransformationPaths.GetOrAdd(slotKey, _ => new List<SourceTransformationPath>());
        lock (paths)
            AddPathIfMissing(paths, path);
    }

    public void RecordTransformationOnSourcePaths(string slotKey, string nodeId)
    {
        if (!_sourceTransformationPaths.TryGetValue(slotKey, out var paths)) return;
        lock (paths)
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                if (path.TransformationIds.Contains(nodeId, StringComparer.Ordinal)) continue;
                paths[index] = path with { TransformationIds = path.TransformationIds.Append(nodeId).ToArray() };
            }
        }
    }

    public IReadOnlyList<SourceReference> GetContributors(string slotKey)
        => _sources.TryGetValue(slotKey, out var list)
            ? (IReadOnlyList<SourceReference>)list.ToArray()
            : Array.Empty<SourceReference>();

    public IReadOnlyList<string> GetTransformationIds(string slotKey)
        => _transformationIds.TryGetValue(slotKey, out var list)
            ? list.ToArray()
            : Array.Empty<string>();

    public IReadOnlyList<SourceTransformationPath> GetSourceTransformationPaths(string slotKey)
        => _sourceTransformationPaths.TryGetValue(slotKey, out var list)
            ? list.ToArray()
            : Array.Empty<SourceTransformationPath>();

    public IReadOnlyDictionary<string, IReadOnlyList<SourceReference>> Snapshot()
        => _sources.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SourceReference>)kv.Value.ToArray());

    /// <summary>Release row-local provenance after a bounded row batch has been emitted.</summary>
    public void Clear()
    {
        _sources.Clear();
        _transformationIds.Clear();
        _sourceTransformationPaths.Clear();
    }

    private static void AddPathIfMissing(List<SourceTransformationPath> paths, SourceTransformationPath candidate)
    {
        if (!paths.Any(path => path.Source == candidate.Source
            && path.TransformationIds.SequenceEqual(candidate.TransformationIds, StringComparer.Ordinal)))
            paths.Add(candidate);
    }
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

    /// <summary>MAP05 resource budget: maximum number of GroupBy buckets.</summary>
    public int MaxGroupCount { get; init; } = 100_000;

    /// <summary>
    /// MAP05 resource budget: conservative retained-byte estimate for GroupBy state,
    /// including keys and provenance paths. Exceeding it blocks before adding more state.
    /// </summary>
    public long MaxGroupWorkingBytes { get; init; } = 64L * 1024 * 1024;

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
