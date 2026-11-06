using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.TaxonomyBenchmark;

internal static class GraphCanvasFixtureGenerator
{
    private const string GeneratorVersion = "graph-canvas-synthetic-v1";
    private const double LayoutOrigin = 48;
    private const double LayoutStepX = 260;
    private const double LayoutStepY = 150;
    private static readonly int[] NodeCounts = { 100, 500, 1_000, 2_000 };
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static GraphCanvasFixtureManifest Generate(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("A new output directory is required.", nameof(outputDirectory));

        var targetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
            throw new IOException($"Graph fixture output already exists and was not changed: {targetDirectory}");

        var parentDirectory = Path.GetDirectoryName(targetDirectory)
            ?? throw new ArgumentException("The output directory must have a parent directory.", nameof(outputDirectory));
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(targetDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var entries = new List<GraphCanvasFixtureEntry>(NodeCounts.Length);
            foreach (var nodeCount in NodeCounts)
            {
                var project = CreateProject(nodeCount);
                var json = ProjectSerializer.Serialize(project);
                var fileName = $"graph-canvas-{nodeCount.ToString(CultureInfo.InvariantCulture)}.xmap";
                File.WriteAllBytes(Path.Combine(stagingDirectory, fileName), Utf8Bytes(json));

                var columns = ColumnsFor(nodeCount);
                entries.Add(new GraphCanvasFixtureEntry
                {
                    File = fileName,
                    NodeCount = project.Nodes.Count,
                    EdgeCount = project.Edges.Count,
                    GridColumns = columns,
                    GridRows = (nodeCount + columns - 1) / columns,
                    EdgesPerNode = (double)project.Edges.Count / project.Nodes.Count,
                    FixtureSha256 = Sha256(Utf8Bytes(json)),
                    SemanticDigest = project.ComputeSemanticDigest(),
                    ProfileId = project.ProfileId,
                    ProfileDigest = project.ProfileDigest
                });
            }

            var manifest = new GraphCanvasFixtureManifest
            {
                SchemaVersion = 1,
                Generator = GeneratorVersion,
                FixtureStatus = "synthetic_fixture_only",
                LayoutOrigin = LayoutOrigin,
                LayoutStepX = LayoutStepX,
                LayoutStepY = LayoutStepY,
                Fixtures = entries
            };
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestOptions);
            File.WriteAllBytes(
                Path.Combine(stagingDirectory, "graph-canvas-fixtures.manifest.json"),
                Utf8Bytes(manifestJson));

            Directory.Move(stagingDirectory, targetDirectory);
            return manifest;
        }
        catch
        {
            DeleteStagingDirectory(parentDirectory, stagingDirectory);
            throw;
        }
    }

    public static void RunSelfTest()
    {
        foreach (var nodeCount in NodeCounts)
        {
            var first = CreateProject(nodeCount);
            var second = CreateProject(nodeCount);
            var firstJson = ProjectSerializer.Serialize(first);
            var secondJson = ProjectSerializer.Serialize(second);
            if (!StringComparer.Ordinal.Equals(firstJson, secondJson))
                throw new InvalidOperationException($"The {nodeCount} node graph fixture is not byte-stable across generation.");

            VerifyProject(first, nodeCount, firstJson);
        }

        var root = NewTemporaryDirectory();
        try
        {
            var outputDirectory = Path.Combine(root, "fixtures");
            var manifest = Generate(outputDirectory);
            var manifestPath = Path.Combine(outputDirectory, "graph-canvas-fixtures.manifest.json");
            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            var restoredManifest = JsonSerializer.Deserialize<GraphCanvasFixtureManifest>(manifestJson, ManifestOptions)
                ?? throw new InvalidOperationException("The generated graph fixture manifest deserialized to null.");
            if (restoredManifest.Fixtures.Count != NodeCounts.Length
                || !restoredManifest.Fixtures.Select(entry => entry.NodeCount).SequenceEqual(NodeCounts)
                || !StringComparer.Ordinal.Equals(
                    JsonSerializer.Serialize(manifest, ManifestOptions),
                    JsonSerializer.Serialize(restoredManifest, ManifestOptions)))
            {
                throw new InvalidOperationException("The graph fixture manifest did not round-trip with all four sizes.");
            }

            foreach (var entry in restoredManifest.Fixtures)
            {
                var path = Path.Combine(outputDirectory, entry.File);
                var bytes = File.ReadAllBytes(path);
                if (!StringComparer.Ordinal.Equals(Sha256(bytes), entry.FixtureSha256))
                    throw new InvalidOperationException($"Fixture digest mismatch for '{entry.File}'.");

                var json = Encoding.UTF8.GetString(bytes);
                var project = ProjectSerializer.Deserialize(json);
                VerifyProject(project, entry.NodeCount, json);
                if (project.Edges.Count != entry.EdgeCount
                    || !StringComparer.Ordinal.Equals(project.ComputeSemanticDigest(), entry.SemanticDigest)
                    || !StringComparer.Ordinal.Equals(project.ProfileDigest, entry.ProfileDigest))
                {
                    throw new InvalidOperationException($"Manifest identity did not match '{entry.File}'.");
                }
            }

            try
            {
                _ = Generate(outputDirectory);
                throw new InvalidOperationException("Graph fixture generation overwrote an existing output directory.");
            }
            catch (IOException)
            {
                // Existing fixture sets must be left untouched.
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MappingProject CreateProject(int nodeCount)
    {
        if (!NodeCounts.Contains(nodeCount))
            throw new ArgumentOutOfRangeException(nameof(nodeCount), "Only the four published graph sizes are supported.");

        var profile = SyntheticProfile.Build();
        var constantSignature = FunctionCatalog.Default.Find(NodeTypeConstants.Constant)
            ?? throw new InvalidOperationException("The Constant node is missing from the default function catalog.");
        var scaleSignature = FunctionCatalog.Default.Find(NodeTypeConstants.Scale)
            ?? throw new InvalidOperationException("The Scale node is missing from the default function catalog.");

        var columns = ColumnsFor(nodeCount);
        var nodes = new List<MappingNode>(nodeCount);
        for (var ordinal = 0; ordinal < nodeCount; ordinal++)
        {
            var row = ordinal / columns;
            var offset = ordinal % columns;
            var column = row % 2 == 0 ? offset : columns - 1 - offset;
            var isRoot = ordinal == 0;
            var signature = isRoot ? constantSignature : scaleSignature;
            var parameters = isRoot
                ? new Dictionary<string, object?>
                {
                    ["literal"] = "1",
                    ["shape"] = "Decimal"
                }
                : new Dictionary<string, object?>
                {
                    ["factor"] = "1"
                };

            nodes.Add(new MappingNode
            {
                NodeId = StableId("node", ordinal),
                NodeType = signature.NodeType,
                DisplayName = isRoot
                    ? "Synthetic decimal source"
                    : $"Synthetic scale {ordinal.ToString("D4", CultureInfo.InvariantCulture)}",
                Parameters = parameters,
                InputPorts = signature.Inputs.ToList(),
                OutputPorts = signature.Outputs.ToList(),
                Layout = new LayoutPosition(
                    LayoutOrigin + column * LayoutStepX,
                    LayoutOrigin + row * LayoutStepY)
            });
        }

        var edges = new List<MappingEdge>(nodeCount - 1);
        for (var ordinal = 0; ordinal < nodeCount - 1; ordinal++)
        {
            edges.Add(new MappingEdge
            {
                EdgeId = StableId("edge", ordinal),
                SourceNodeId = nodes[ordinal].NodeId,
                SourcePortName = "value",
                TargetNodeId = nodes[ordinal + 1].NodeId,
                TargetPortName = "input"
            });
        }

        return new MappingProject
        {
            ProjectId = StableId("project", nodeCount),
            Name = $"Synthetic Graph Canvas {nodeCount.ToString(CultureInfo.InvariantCulture)}",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
            FunctionCatalogVersion = FunctionCatalog.Default.Version,
            SemanticRevision = 1,
            LayoutGeneration = 1,
            Metadata = new ProjectMetadata
            {
                Author = "XBRLMappingStudio synthetic benchmark fixture generator",
                Description = "A deterministic typed constant-to-scale graph for UI rendering experiments. It is synthetic and is not a filing example.",
                CreatedUtc = FixtureTimestamp,
                LastModifiedUtc = FixtureTimestamp
            },
            Nodes = nodes,
            Edges = edges
        };
    }

    private static void VerifyProject(MappingProject project, int expectedNodeCount, string json)
    {
        if (project.Nodes.Count != expectedNodeCount
            || project.Edges.Count != expectedNodeCount - 1
            || !StringComparer.Ordinal.Equals(project.ProfileId, SyntheticProfile.ProfileId)
            || !StringComparer.Ordinal.Equals(project.ProfileDigest, SyntheticProfile.Build().Digest)
            || !StringComparer.Ordinal.Equals(project.FunctionCatalogVersion, FunctionCatalog.Default.Version)
            || project.Nodes[0].NodeType != NodeTypeConstants.Constant
            || project.Nodes.Skip(1).Any(node => node.NodeType != NodeTypeConstants.Scale)
            || project.Nodes.Any(node => node.Layout is null)
            || project.Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != expectedNodeCount
            || project.Nodes.Any(node => !Id.IsValid(node.NodeId))
            || project.Edges.Select(edge => edge.EdgeId).Distinct(StringComparer.Ordinal).Count() != expectedNodeCount - 1
            || project.Edges.Any(edge => !Id.IsValid(edge.EdgeId))
            || project.Nodes.Select(node => (node.Layout!.X, node.Layout.Y)).Distinct().Count() != expectedNodeCount)
        {
            throw new InvalidOperationException($"The {expectedNodeCount} node graph fixture has unexpected graph structure.");
        }

        for (var ordinal = 0; ordinal < project.Edges.Count; ordinal++)
        {
            var edge = project.Edges[ordinal];
            if (!StringComparer.Ordinal.Equals(edge.SourceNodeId, project.Nodes[ordinal].NodeId)
                || !StringComparer.Ordinal.Equals(edge.SourcePortName, "value")
                || !StringComparer.Ordinal.Equals(edge.TargetNodeId, project.Nodes[ordinal + 1].NodeId)
                || !StringComparer.Ordinal.Equals(edge.TargetPortName, "input"))
            {
                throw new InvalidOperationException($"The {expectedNodeCount} node graph fixture has a malformed chain edge.");
            }
        }

        var graph = new MappingGraph(project);
        var diagnostics = new GraphValidator(FunctionCatalog.Default).Validate(graph);
        if (diagnostics.Items.Count != 0)
            throw new InvalidOperationException($"The {expectedNodeCount} node graph fixture failed typed graph validation.");
        if (!ProjectSerializer.VerifyRoundTrip(json))
            throw new InvalidOperationException($"The {expectedNodeCount} node graph fixture failed project round-trip verification.");

        var restored = ProjectSerializer.Deserialize(json);
        if (!StringComparer.Ordinal.Equals(project.ComputeSemanticDigest(), restored.ComputeSemanticDigest()))
            throw new InvalidOperationException($"The {expectedNodeCount} node graph fixture changed semantic identity after reopening.");
    }

    private static int ColumnsFor(int nodeCount) => (int)Math.Ceiling(Math.Sqrt(nodeCount));

    private static string StableId(string kind, int ordinal)
    {
        var value = $"{GeneratorVersion}|{kind}|{ordinal.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..32];
    }

    private static byte[] Utf8Bytes(string value) => new UTF8Encoding(false).GetBytes(value);

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string NewTemporaryDirectory()
    {
        while (true)
        {
            var path = Path.Combine(Path.GetTempPath(), $"xbrlms-graph-fixtures-{Guid.NewGuid():N}");
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static void DeleteStagingDirectory(string parentDirectory, string stagingDirectory)
    {
        if (!Directory.Exists(stagingDirectory)) return;

        var parent = Path.GetFullPath(parentDirectory);
        var staging = Path.GetFullPath(stagingDirectory);
        var stagingParent = Directory.GetParent(staging)?.FullName;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, stagingParent, comparison))
            throw new IOException("Refusing to remove a graph fixture staging directory outside its output parent.");

        Directory.Delete(staging, recursive: true);
    }
}

internal sealed class GraphCanvasFixtureManifest
{
    public required int SchemaVersion { get; init; }
    public required string Generator { get; init; }
    public required string FixtureStatus { get; init; }
    public required double LayoutOrigin { get; init; }
    public required double LayoutStepX { get; init; }
    public required double LayoutStepY { get; init; }
    public required List<GraphCanvasFixtureEntry> Fixtures { get; init; }
}

internal sealed class GraphCanvasFixtureEntry
{
    public required string File { get; init; }
    public required int NodeCount { get; init; }
    public required int EdgeCount { get; init; }
    public required int GridColumns { get; init; }
    public required int GridRows { get; init; }
    public required double EdgesPerNode { get; init; }
    public required string FixtureSha256 { get; init; }
    public required string SemanticDigest { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileDigest { get; init; }
}
