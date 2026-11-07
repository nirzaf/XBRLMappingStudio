using System.Globalization;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Project;

/// <summary>
/// Project-level lifecycle operations for revisioned declarative function libraries.
/// Function bodies remain data-only mapping subgraphs and are never interpreted as scripts.
/// </summary>
public static class FunctionLibraryService
{
    private const string FunctionCallNodeType = "functionCall";

    /// <summary>Appends the first immutable revision of a named function.</summary>
    public static MappingProject CreateFunction(MappingProject project, FunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.FunctionId))
            throw new ArgumentException("Function ID is required.", nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Function name is required.", nameof(definition));
        if (definition.Revision != 1)
            throw new ArgumentException("A new function must start at revision 1.", nameof(definition));
        if (project.Functions.Any(existing =>
                StringComparer.Ordinal.Equals(existing.FunctionId, definition.FunctionId)))
            throw new InvalidOperationException($"Function '{definition.FunctionId}' already exists.");
        if (!FunctionCatalogVersions.IsCompatible(project.FunctionCatalogVersion, definition.CatalogVersion))
            throw new InvalidOperationException($"Function '{definition.FunctionId}@1' uses catalog "
                + $"'{definition.CatalogVersion}', which does not match project catalog "
                + $"'{project.FunctionCatalogVersion}'.");

        var firstRevision = definition with
        {
            Inputs = definition.Inputs.ToList(),
            Outputs = definition.Outputs.ToList(),
            Nodes = definition.Nodes.Select(node => node.ShallowCopy()).ToList(),
            Edges = definition.Edges.Select(edge => edge with { }).ToList()
        };
        return CopyProject(project, project.Functions.Append(firstRevision).ToList());
    }

    /// <summary>
    /// Creates the next immutable revision of a function. <paramref name="expectedRevision"/>
    /// must be the current latest revision so stale editors cannot overwrite a newer draft.
    /// </summary>
    public static MappingProject CreateRevision(
        MappingProject project,
        string functionId,
        int expectedRevision,
        IEnumerable<MappingNode> nodes,
        IEnumerable<MappingEdge> edges,
        string? name = null,
        IEnumerable<PortDefinition>? inputs = null,
        IEnumerable<PortDefinition>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        if (string.IsNullOrWhiteSpace(functionId))
            throw new ArgumentException("Function ID is required.", nameof(functionId));

        var revisions = project.Functions
            .Where(definition => string.Equals(definition.FunctionId, functionId, StringComparison.Ordinal))
            .OrderBy(definition => definition.Revision)
            .ToArray();
        if (revisions.Length == 0)
            throw new KeyNotFoundException($"Function '{functionId}' is not present in the project.");
        if (revisions.Select(definition => definition.Revision).Distinct().Count() != revisions.Length)
            throw new InvalidOperationException($"Function '{functionId}' contains duplicate revisions.");

        var latest = revisions[^1];
        if (expectedRevision != latest.Revision)
            throw new ProjectSaveException(DiagnosticCodes.PrjRevisionConflict,
                $"Function '{functionId}' changed while it was being edited. Expected revision "
                + $"{expectedRevision}, current revision is {latest.Revision}.");
        if (!FunctionCatalogVersions.IsCompatible(project.FunctionCatalogVersion, latest.CatalogVersion))
            throw new InvalidOperationException($"Function '{functionId}@{latest.Revision}' uses catalog "
                + $"'{latest.CatalogVersion}', which does not match project catalog '{project.FunctionCatalogVersion}'.");
        if (name is not null && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Function name cannot be empty.", nameof(name));

        var next = latest with
        {
            Revision = checked(latest.Revision + 1),
            Name = name ?? latest.Name,
            Inputs = (inputs ?? latest.Inputs).ToList(),
            Outputs = (outputs ?? latest.Outputs).ToList(),
            Nodes = nodes.Select(node => node.ShallowCopy()).ToList(),
            Edges = edges.Select(edge => edge with { }).ToList()
        };
        return CopyProject(project, project.Functions.Append(next).ToList());
    }

    /// <summary>
    /// Imports all revisions in a function package under a new root ID. Package function
    /// IDs are namespaced beneath the root ID; nested calls are rewritten to the imported
    /// IDs, while dependencies already present in the destination keep their existing IDs.
    /// </summary>
    public static MappingProject ImportAs(
        MappingProject destination,
        IEnumerable<FunctionDefinition> package,
        string sourceFunctionId,
        string newFunctionId,
        string? newName = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(sourceFunctionId))
            throw new ArgumentException("Source function ID is required.", nameof(sourceFunctionId));
        if (string.IsNullOrWhiteSpace(newFunctionId))
            throw new ArgumentException("Imported function ID is required.", nameof(newFunctionId));
        if (newName is not null && string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Imported function name cannot be empty.", nameof(newName));

        var definitions = package.OrderBy(definition => definition.FunctionId, StringComparer.Ordinal)
            .ThenBy(definition => definition.Revision).ToArray();
        if (definitions.Length == 0 || !definitions.Any(definition => definition.FunctionId == sourceFunctionId))
            throw new InvalidOperationException($"Package does not contain function '{sourceFunctionId}'.");

        var packageByKey = new Dictionary<(string FunctionId, int Revision), FunctionDefinition>();
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.FunctionId) || definition.Revision < 1
                || !packageByKey.TryAdd((definition.FunctionId, definition.Revision), definition))
                throw new InvalidOperationException("Function package contains an invalid or duplicate revision.");
            if (!FunctionCatalogVersions.IsCompatible(destination.FunctionCatalogVersion, definition.CatalogVersion))
                throw new InvalidOperationException($"Function '{definition.FunctionId}@{definition.Revision}' uses "
                    + $"catalog '{definition.CatalogVersion}', which does not match destination catalog "
                    + $"'{destination.FunctionCatalogVersion}'.");
        }

        var idMap = definitions.Select(definition => definition.FunctionId).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id,
                id => string.Equals(id, sourceFunctionId, StringComparison.Ordinal)
                    ? newFunctionId
                    : newFunctionId + "__" + id,
                StringComparer.Ordinal);
        if (idMap.Values.Distinct(StringComparer.Ordinal).Count() != idMap.Count)
            throw new InvalidOperationException("Imported function IDs are not unique after namespacing.");

        var existingIds = destination.Functions.Select(definition => definition.FunctionId)
            .ToHashSet(StringComparer.Ordinal);
        var collision = idMap.Values.FirstOrDefault(existingIds.Contains);
        if (collision is not null)
            throw new InvalidOperationException($"Destination already contains function ID '{collision}'.");

        bool DestinationHas((string FunctionId, int Revision) key)
            => destination.Functions.Any(definition => definition.FunctionId == key.FunctionId
                && definition.Revision == key.Revision);

        var imported = new List<FunctionDefinition>(definitions.Length);
        foreach (var definition in definitions)
        {
            var rewrittenNodes = new List<MappingNode>(definition.Nodes.Count);
            foreach (var node in definition.Nodes)
            {
                if (node.NodeType != FunctionCallNodeType)
                {
                    rewrittenNodes.Add(node.ShallowCopy());
                    continue;
                }

                var calledId = ReadString(node, "functionId");
                var revisionText = ReadString(node, "revision") ?? "1";
                if (string.IsNullOrWhiteSpace(calledId)
                    || !int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                    || revision < 1)
                    throw new InvalidOperationException($"Function '{definition.FunctionId}@{definition.Revision}' "
                        + $"contains an invalid nested function reference on node '{node.NodeId}'.");

                var key = (calledId, revision);
                string mappedId;
                if (packageByKey.ContainsKey(key))
                    mappedId = idMap[calledId];
                else if (DestinationHas(key))
                    mappedId = calledId;
                else
                    throw new InvalidOperationException($"Function package dependency '{calledId}@{revision}' "
                        + $"is not included and is absent from the destination project.");

                var parameters = new Dictionary<string, object?>(node.Parameters, StringComparer.Ordinal)
                {
                    ["functionId"] = mappedId,
                    ["revision"] = revisionText
                };
                rewrittenNodes.Add(node.ShallowCopy() with { Parameters = parameters });
            }

            imported.Add(definition with
            {
                FunctionId = idMap[definition.FunctionId],
                Name = definition.FunctionId == sourceFunctionId ? newName ?? definition.Name : definition.Name,
                Nodes = rewrittenNodes,
                Edges = definition.Edges.Select(edge => edge with { }).ToList(),
                Inputs = definition.Inputs.ToList(),
                Outputs = definition.Outputs.ToList()
            });
        }

        return CopyProject(destination, destination.Functions.Concat(imported).ToList());
    }

    private static MappingProject CopyProject(MappingProject source, List<FunctionDefinition> functions)
        => new()
        {
            SchemaVersion = source.SchemaVersion,
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = source.ProfileId,
            ProfileDigest = source.ProfileDigest,
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = source.SemanticRevision,
            LayoutGeneration = source.LayoutGeneration,
            Metadata = source.Metadata,
            Nodes = source.Nodes.Select(node => node.ShallowCopy()).ToList(),
            Edges = source.Edges.Select(edge => edge with { }).ToList(),
            Bindings = source.Bindings.Select(binding => binding with
            {
                Configuration = new Dictionary<string, object?>(binding.Configuration, StringComparer.Ordinal)
            }).ToList(),
            Functions = functions,
            Destination = source.Destination,
            Approvals = source.Approvals.ToList()
        };

    private static string? ReadString(MappingNode node, string name)
    {
        if (!node.Parameters.TryGetValue(name, out var value) || value is null) return null;
        return value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()
            : value.ToString();
    }
}
