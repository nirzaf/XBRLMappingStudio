using System.Globalization;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Engine;

/// <summary>A stable node/port identity for a value observed during plan execution.</summary>
public readonly record struct ExecutionOutputKey(string NodeId, string PortName);

/// <summary>
/// Bounded summary of a node port. Counts describe every output observed in the run;
/// <see cref="Samples"/> contains only the first few outputs in deterministic order.
/// </summary>
public sealed record NodeOutputPreview(
    string NodeId,
    string PortName,
    long ValueCount,
    long PresentCount,
    long BlankCount,
    long MissingCount,
    long ExplicitNilCount,
    bool IsPartial,
    IReadOnlyList<NodeOutputSample> Samples);

/// <summary>A bounded value sample and its bounded source-contribution paths.</summary>
public sealed record NodeOutputSample(
    string Label,
    string Value,
    IReadOnlyList<SourceTransformationPath> Contributors,
    int OmittedContributorCount);

/// <summary>
/// Collects deterministic per-node output counts while retaining only a small number
/// of value and contributor samples per port. The collector survives row batches, so
/// large-run counts cover the complete run without retaining each row frame.
/// </summary>
public sealed class NodeOutputPreviewTracker
{
    public const int MaximumSamplesPerPort = 3;
    public const int MaximumContributorPathsPerSample = 4;
    public const int MaximumSampleValueCharacters = 160;

    private readonly Dictionary<ExecutionOutputKey, OutputAccumulator> _outputs = new();

    public void RecordRow(EvaluationFrame frame, ProvenanceTracker provenance)
    {
        foreach (var (output, value) in frame.OutputValues)
        {
            var slotKey = ProvenanceTracker.SlotKey(output.NodeId, output.PortName, frame.RowIndex);
            Add(output, value, $"Row {frame.RowIndex + 1}", slotKey, provenance);
        }
    }

    public void RecordSetOutput(
        string nodeId,
        string portName,
        DataValue value,
        ProvenanceTracker provenance)
    {
        var output = new ExecutionOutputKey(nodeId, portName);
        if (value.Raw is IReadOnlyList<DataValue> items)
        {
            if (items.Count == 0)
            {
                EnsureAccumulator(output);
                return;
            }
            for (var index = 0; index < items.Count; index++)
            {
                var slotKey = ProvenanceTracker.SetSlotKey(nodeId, portName, index);
                Add(output, items[index], $"Item {index + 1}", slotKey, provenance);
            }
            return;
        }

        Add(output, value, "Set value", ProvenanceTracker.SetSlotKey(nodeId, portName), provenance);
    }

    public IReadOnlyList<NodeOutputPreview> Snapshot(bool isPartial)
        => _outputs
            .OrderBy(pair => pair.Key.NodeId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.PortName, StringComparer.Ordinal)
            .Select(pair => pair.Value.Snapshot(pair.Key, isPartial))
            .ToArray();

    private void Add(
        ExecutionOutputKey output,
        DataValue value,
        string label,
        string provenanceKey,
        ProvenanceTracker provenance)
    {
        var accumulator = EnsureAccumulator(output);
        accumulator.Add(value, label, provenanceKey, provenance);
    }

    private OutputAccumulator EnsureAccumulator(ExecutionOutputKey output)
    {
        if (_outputs.TryGetValue(output, out var accumulator)) return accumulator;
        accumulator = new OutputAccumulator();
        _outputs.Add(output, accumulator);
        return accumulator;
    }

    private static NodeOutputSample CreateSample(
        DataValue value,
        string label,
        string provenanceKey,
        ProvenanceTracker provenance)
    {
        var paths = provenance.SampleSourceTransformationPaths(provenanceKey, MaximumContributorPathsPerSample);
        if (paths.TotalCount > 0)
        {
            return new NodeOutputSample(label, FormatValue(value), paths.Items,
                Math.Max(0, paths.TotalCount - paths.Items.Count));
        }

        var sources = provenance.SampleContributors(provenanceKey, MaximumContributorPathsPerSample);
        var transformationIds = provenance.GetTransformationIds(provenanceKey);
        var contributors = sources.Items.Select(source =>
            new SourceTransformationPath(source, transformationIds)).ToArray();
        return new NodeOutputSample(label, FormatValue(value), contributors,
            Math.Max(0, sources.TotalCount - contributors.Length));
    }

    private static string FormatValue(DataValue value) => OutputAccumulator.FormatValue(value);

    private sealed class OutputAccumulator
    {
        private long _present;
        private long _blank;
        private long _missing;
        private long _explicitNil;
        private readonly List<NodeOutputSample> _samples = new(MaximumSamplesPerPort);

        public long ValueCount { get; private set; }

        public void Add(DataValue value, string label, string provenanceKey, ProvenanceTracker provenance)
        {
            ValueCount++;
            switch (value.Presence)
            {
                case ValuePresence.Present: _present++; break;
                case ValuePresence.Blank: _blank++; break;
                case ValuePresence.Missing: _missing++; break;
                case ValuePresence.ExplicitNil: _explicitNil++; break;
                default: throw new ArgumentOutOfRangeException(nameof(value), value.Presence, "Unknown value presence.");
            }

            if (_samples.Count >= MaximumSamplesPerPort) return;
            _samples.Add(CreateSample(value, label, provenanceKey, provenance));
        }

        public NodeOutputPreview Snapshot(ExecutionOutputKey output, bool isPartial)
            => new(output.NodeId, output.PortName, ValueCount, _present, _blank, _missing, _explicitNil,
                isPartial, _samples.ToArray());

        internal static string FormatValue(DataValue value)
        {
            string display = value.Presence switch
            {
                ValuePresence.Missing => "(missing)",
                ValuePresence.ExplicitNil => "(nil)",
                ValuePresence.Blank => "(blank)",
                _ when value.Raw is IReadOnlyList<DataValue> items
                    => $"collection ({items.Count.ToString(CultureInfo.InvariantCulture)} item(s))",
                _ when value.Raw is IReadOnlyDictionary<string, DataValue> fields
                    => $"record ({fields.Count.ToString(CultureInfo.InvariantCulture)} field(s))",
                _ when value.Raw is PendingFact fact => FormatFact(fact),
                _ => value.AsString()
            };

            if (display.Length <= MaximumSampleValueCharacters) return display;
            return display[..(MaximumSampleValueCharacters - 1)] + "…";
        }

        private static string FormatFact(PendingFact fact)
        {
            var identity = fact.ConceptName ?? "fact";
            if (fact.IsNil) return $"{identity} = nil";
            if (fact.Value is { } numeric)
                return $"{identity} = {numeric.ToString(CultureInfo.InvariantCulture)}";
            return $"{identity} = {fact.TextValue ?? "(empty)"}";
        }
    }
}
