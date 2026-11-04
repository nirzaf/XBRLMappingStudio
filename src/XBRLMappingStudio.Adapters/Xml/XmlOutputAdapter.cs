using System.Text;
using System.Xml;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Xml;

/// <summary>
/// Generic XML target adapter (IO06). Writes records under a caller-declared root
/// element, with each record emitted as a similarly-declared row element. Column
/// names may be either a local name (written with the row's default namespace) or
/// a <c>{namespaceUri}localName</c> qualified form (written with an explicit prefix
/// bound at the root), so two rows sharing a local name in different namespaces
/// remain distinct on round-trip — the exact acceptance the guide's IO06 fixture
/// requires.
/// <para>
/// Mixed content (text interleaved with element children inside a single field)
/// is intentionally <em>not</em> written by this adapter: the guide declares it
/// unsupported until a preservation fixture passes, and no such fixture is
/// authored here. Attempting to emit one throws <c>IO011</c> rather than
/// silently flattening markup into text.
/// </para>
/// <para>
/// Staging + reopen reconciliation mirrors <c>ExcelOutputAdapter</c>: bytes are
/// first written to a sibling <c>.staged</c> file, re-parsed with an
/// <see cref="XmlReader"/> configured to prohibit DTDs and refuse external
/// entities, and only moved onto the destination if the reopened document
/// carries the expected row count. A failure before the move leaves any prior
/// output untouched.
/// </para>
/// </summary>
public sealed class XmlOutputAdapter : ITargetAdapter
{
    public string Kind => "xml";
    public AdapterCapabilities Capabilities => AdapterCapabilities.WriteOnly;

    /// <summary>Root element local name. Defaults to <c>rows</c>.</summary>
    public string RootLocalName { get; init; } = "rows";

    /// <summary>Namespace URI bound to the root element (optional).</summary>
    public string? RootNamespace { get; init; }

    /// <summary>Row element local name. Defaults to <c>row</c>.</summary>
    public string RowLocalName { get; init; } = "row";

    /// <summary>Optional test seam: when set and it returns true for the staged path,
    /// publication fails before the destination is touched.</summary>
    public Func<string, bool>? ReconcileFailProbe { get; set; }

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        OmitXmlDeclaration = false,
        NewLineHandling = NewLineHandling.Entitize,
    };

    /// <summary>Serialize <paramref name="records"/> to a UTF-8 XML document.</summary>
    public byte[] Write(IReadOnlyList<DataRecord> records)
    {
        var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, WriterSettings))
        {
            writer.WriteStartDocument();
            if (RootNamespace is null)
                writer.WriteStartElement(RootLocalName);
            else
                writer.WriteStartElement(RootLocalName, RootNamespace);

            foreach (var record in records)
            {
                if (RootNamespace is null)
                    writer.WriteStartElement(RowLocalName);
                else
                    writer.WriteStartElement(RowLocalName, RootNamespace);

                foreach (var (name, value) in record.Fields)
                {
                    if (value.IsMissing) continue;

                    var (ns, local) = SplitQualifiedName(name);
                    if (ns is null) writer.WriteStartElement(local);
                    else writer.WriteStartElement(local, ns);

                    if (value.IsNil)
                    {
                        writer.WriteAttributeString("xsi", "nil",
                            "http://www.w3.org/2001/XMLSchema-instance", "true");
                    }
                    else
                    {
                        writer.WriteString(FormatValue(value));
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Interpret a column name as either a plain local name or a CLR-style
    /// <c>{uri}local</c> qualified name. Mixed content in the value is not
    /// supported; only leaf text values are written.
    /// </summary>
    private static (string? Namespace, string Local) SplitQualifiedName(string name)
    {
        if (name.StartsWith("{", StringComparison.Ordinal))
        {
            var close = name.IndexOf('}');
            if (close > 0 && close < name.Length - 1)
                return (name.Substring(1, close - 1), name.Substring(close + 1));
        }
        return (null, name);
    }

    private static string FormatValue(DataValue value) => value.Raw switch
    {
        null => string.Empty,
        string s when s.Length > 0 && (s[0] == '<' || s[0] == '&') =>
            throw new AdapterException(DiagnosticCodes.IoUnsafeXmlConstruct,
                "Mixed-content markup on an outbound XML field is unsupported; "
                + "preserve the original text or emit a distinct child element instead."),
        string s => s,
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        System.IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        var other => other.ToString() ?? string.Empty
    };

    public Task<string> StageArtifactAsync(SourceConnection destination, byte[] payload,
        string suggestedExtension, CancellationToken cancellationToken)
    {
        var path = RequirePath(destination);
        var staged = path + ".staged";
        // Probe-parse with the same hardened reader the source adapter uses so a
        // payload carrying DOCTYPE or an external-entity reference never reaches disk.
        _ = Reconcile(payload, expectedRows: null);
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

        int stagedRows = Reconcile(File.ReadAllBytes(stagedPath), expectedRows: null);
        if (ReconcileFailProbe?.Invoke(stagedPath) == true)
            throw new IOException($"Simulated mid-write failure while publishing '{path}'.");

        File.Move(stagedPath, path, overwrite: true);

        int finalRows = Reconcile(File.ReadAllBytes(path), expectedRows: null);
        if (finalRows != stagedRows)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Published XML row count ({finalRows}) does not match staged ({stagedRows}).",
                DiagnosticStage.Publication);

        var bytes = new FileInfo(path).Length;
        var digest = RunManifest.ComputeSha256(File.ReadAllBytes(path));
        return Task.FromResult(new OutputIdentity(path, digest, bytes, "xml"));
    }

    /// <summary>Count top-level row elements using a DTD-prohibited, resolver-less reader.</summary>
    private int Reconcile(byte[] payload, int? expectedRows)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = true,
            IgnoreWhitespace = true,
        };
        int rows = 0;
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == RowLocalName
                && (RootNamespace is null || reader.NamespaceURI == RootNamespace))
                rows++;
        }
        if (expectedRows is int expected && rows != expected)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"Expected {expected} row elements but found {rows}.",
                DiagnosticStage.Publication);
        return rows;
    }

    private static string RequirePath(SourceConnection conn)
        => conn.Path ?? throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
            "XML destination connection has no file path.");
}
