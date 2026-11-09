using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Conservative retained-state estimate for an in-memory Join. The estimate charges
/// record/dictionary overhead, field names, values, lexical forms and value-level source
/// references. It is a deterministic budget unit, not a process-heap measurement.
/// </summary>
internal static class JoinWorkingSetEstimator
{
    private const int MaximumDepth = 32;

    public static long EstimateRecord(IReadOnlyDictionary<string, DataValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 512;
        foreach (var (name, value) in fields)
        {
            bytes = Add(bytes, 128 + StringBytes(name));
            bytes = Add(bytes, EstimateValue(value, 0, seen));
        }
        return bytes;
    }

    public static long AddOverhead(long bytes, long overhead) => Add(bytes, overhead);

    public static long EstimateJoinedRecord(
        IReadOnlyDictionary<string, DataValue>? left,
        IReadOnlyDictionary<string, DataValue>? right)
    {
        var bytes = 512L;
        if (left is not null) bytes = Add(bytes, EstimateRecord(left));
        if (right is not null) bytes = Add(bytes, EstimateRecord(right));
        return bytes;
    }

    public static long EstimatePaths(
        IReadOnlyList<SourceTransformationPath> paths,
        CancellationToken cancellationToken)
    {
        long bytes = 128 + (long)paths.Count * 32;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes = Add(bytes, 192 + StringBytes(path.Source.SourceId)
                + StringBytes(path.Source.Locator) + StringBytes(path.Source.DisplayName));
            foreach (var transformationId in path.TransformationIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes = Add(bytes, 32 + StringBytes(transformationId));
            }
        }
        return bytes;
    }

    private static long EstimateValue(DataValue value, int depth, HashSet<object> seen)
    {
        if (depth > MaximumDepth) return long.MaxValue;
        long bytes = 96 + StringBytes(value.LexicalSource);
        if (value.Source is { } source)
            bytes = Add(bytes, 128 + StringBytes(source.SourceId) + StringBytes(source.Locator)
                + StringBytes(source.DisplayName));

        if (value.Raw is null) return bytes;
        switch (value.Raw)
        {
            case string text:
                return Add(bytes, 32 + StringBytes(text));
            case decimal or long or int or short or byte or bool or DateOnly or DateTimeOffset:
                return Add(bytes, 64);
            case IReadOnlyDictionary<string, DataValue> nested:
                if (!seen.Add(nested)) return long.MaxValue;
                bytes = Add(bytes, 256);
                foreach (var (name, nestedValue) in nested)
                {
                    bytes = Add(bytes, 128 + StringBytes(name));
                    bytes = Add(bytes, EstimateValue(nestedValue, depth + 1, seen));
                }
                seen.Remove(nested);
                return bytes;
            case IReadOnlyList<DataValue> items:
                if (!seen.Add(items)) return long.MaxValue;
                bytes = Add(bytes, 128 + (long)items.Count * 16);
                foreach (var item in items)
                    bytes = Add(bytes, EstimateValue(item, depth + 1, seen));
                seen.Remove(items);
                return bytes;
            default:
                // Unknown opaque payload sizes cannot be estimated safely.
                return long.MaxValue;
        }
    }

    private static long StringBytes(string? value) => value is null ? 0 : 32L + value.Length * 2L;

    private static long Add(long left, long right)
    {
        if (left < 0 || right < 0 || left > long.MaxValue - right) return long.MaxValue;
        return left + right;
    }
}
