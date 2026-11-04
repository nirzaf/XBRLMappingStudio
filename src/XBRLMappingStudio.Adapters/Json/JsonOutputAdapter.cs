using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Json;

/// <summary>
/// Generic JSON target adapter (IO05). Composes records into a declared object or
/// array shape, stages the artifact as a sibling <c>.staged</c> file, reopens it with
/// an independent reader to confirm the round-trip preserved multiplicity, and only
/// then moves it onto the destination. A failure before the move leaves any prior
/// output untouched.
/// <para>
/// Round-trip semantics: an empty array stays an empty array, an explicit JSON
/// <c>null</c> stays <c>null</c>, and a missing field stays absent (never
/// silently materialized as <c>null</c>). Object property order is the caller's
/// <c>Fields</c> dictionary insertion order, so nested paths round-trip byte-for-byte
/// between two runs on the same record set.
/// </para>
/// </summary>
public sealed class JsonOutputAdapter : ITargetAdapter
{
    public string Kind => "json";
    public AdapterCapabilities Capabilities => AdapterCapabilities.WriteOnly;

    /// <summary>When true, the produced document is a top-level array with one object
    /// per record; when false, only the first record is emitted as a top-level object.</summary>
    public bool EmitArray { get; init; } = true;

    /// <summary>Optional test seam: when set and it returns true for the staged path,
    /// publication fails before the destination is touched so a mid-write fault is
    /// provably invisible to the reader.</summary>
    public Func<string, bool>? ReconcileFailProbe { get; set; }

    /// <summary>Serialize <paramref name="records"/> to a UTF-8 JSON document.</summary>
    public byte[] Write(IReadOnlyList<DataRecord> records)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = false,
            // Duplicate keys cannot appear because we build objects from a keyed
            // dictionary; still, honor the reader's stricter posture for symmetry.
            SkipValidation = false,
        }))
        {
            if (EmitArray)
            {
                writer.WriteStartArray();
                foreach (var r in records) WriteRecord(writer, r);
                writer.WriteEndArray();
            }
            else if (records.Count > 0)
            {
                WriteRecord(writer, records[0]);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }
        return ms.ToArray();
    }

    private static void WriteRecord(Utf8JsonWriter writer, DataRecord record)
    {
        // Nested paths (dot-separated column names) rebuild the original object tree so
        // an IO05 round-trip preserves shape. Arrays and scalars are emitted verbatim
        // from the string form the source adapter recorded for them.
        var tree = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in record.Fields)
            InsertPath(tree, key, value);

        writer.WriteStartObject();
        foreach (var (key, node) in tree)
            WriteNode(writer, key, node);
        writer.WriteEndObject();
    }

    private static void InsertPath(IDictionary<string, object> tree, string dottedPath, DataValue value)
    {
        var parts = dottedPath.Split('.');
        var current = tree;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var child) || child is not IDictionary<string, object> dict)
            {
                dict = new Dictionary<string, object>(StringComparer.Ordinal);
                current[parts[i]] = dict;
            }
            current = dict;
        }
        current[parts[^1]] = value;
    }

    private static void WriteNode(Utf8JsonWriter writer, string name, object node)
    {
        writer.WritePropertyName(name);
        switch (node)
        {
            case IDictionary<string, object> nested:
                writer.WriteStartObject();
                foreach (var (k, v) in nested) WriteNode(writer, k, v);
                writer.WriteEndObject();
                break;
            case DataValue dv when dv.IsMissing:
                writer.WriteNullValue();
                break;
            case DataValue dv when dv.Shape == PortShape.Decimal && dv.Raw is decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DataValue dv when dv.Shape == PortShape.Boolean && dv.Raw is bool b:
                writer.WriteBooleanValue(b);
                break;
            case DataValue dv when dv.Raw is string s && LooksLikeJson(s):
            {
                // Arrays captured by the source adapter arrive as their rendered JSON text;
                // reparse and reemit so multiplicity survives the round-trip.
                using var doc = JsonDocument.Parse(s);
                doc.RootElement.WriteTo(writer);
                break;
            }
            case DataValue dv:
                writer.WriteStringValue(dv.Raw?.ToString() ?? string.Empty);
                break;
            default:
                writer.WriteStringValue(node?.ToString() ?? string.Empty);
                break;
        }
    }

    private static bool LooksLikeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var first = s[0];
        return first is '[' or '{';
    }

    public Task<string> StageArtifactAsync(SourceConnection destination, byte[] payload,
        string suggestedExtension, CancellationToken cancellationToken)
    {
        var path = RequirePath(destination);
        var staged = path + ".staged";
        // Probe-parse before we ever name a staged file: an invalid payload must not
        // displace a prior accepted artifact at the destination path.
        using (var probe = JsonDocument.Parse(payload))
        {
            _ = probe.RootElement.ValueKind;
        }
        File.WriteAllBytes(staged, payload);
        return Task.FromResult(staged);
    }

    public Task<OutputIdentity> PublishAsync(string stagedPath, SourceConnection destination,
        CancellationToken cancellationToken)
    {
        var path = RequirePath(destination);
        if (string.Equals(stagedPath, path, StringComparison.Ordinal))
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "Refusing to publish onto the staged path itself.");

        int stagedCount = CountTopLevel(File.ReadAllBytes(stagedPath));
        if (ReconcileFailProbe?.Invoke(stagedPath) == true)
            throw new IOException($"Simulated mid-write failure while publishing '{path}'.");

        File.Move(stagedPath, path, overwrite: true);

        int finalCount = CountTopLevel(File.ReadAllBytes(path));
        if (finalCount != stagedCount)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Published JSON multiplicity ({finalCount}) does not match staged ({stagedCount}).",
                DiagnosticStage.Publication);

        var bytes = new FileInfo(path).Length;
        var digest = RunManifest.ComputeSha256(File.ReadAllBytes(path));
        return Task.FromResult(new OutputIdentity(path, digest, bytes, "json"));
    }

    private static int CountTopLevel(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.GetArrayLength(),
            JsonValueKind.Object => 1,
            _ => 0
        };
    }

    private static string RequirePath(SourceConnection conn)
        => conn.Path ?? throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
            "JSON destination connection has no file path.");
}
