using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

public enum TaxonomyMigrationSeverity
{
    Information,
    Review,
    Incompatible
}

public sealed record TaxonomyMigrationChange(
    string Code,
    string Subject,
    string Description,
    TaxonomyMigrationSeverity Severity,
    IReadOnlyList<string> AffectedNodeIds)
{
    public string Display => AffectedNodeIds.Count == 0
        ? $"{Severity}: {Description}"
        : $"{Severity}: {Description} ({AffectedNodeIds.Count} mapped node(s))";
}

public sealed record TaxonomyMigrationTargetOption(FilingProfile Profile)
{
    public string Display => $"{Profile.TaxonomyVersion} · {Profile.Digest}";
    public override string ToString() => Display;
}

/// <summary>
/// Read-only comparison of one exact project/profile revision with a candidate profile.
/// The serialized source snapshot pins the old project identity and approvals for replay.
/// </summary>
public sealed class TaxonomyMigrationProposal
{
    internal TaxonomyMigrationProposal(
        string sourceProjectSnapshotJson,
        string sourceProjectDigest,
        string sourceProfileId,
        string sourceProfileDigest,
        FilingProfile targetProfile,
        IReadOnlyList<TaxonomyMigrationChange> changes,
        IReadOnlyList<TaxonomyMigrationService.SafeNodeUpdate> safeNodeUpdates,
        bool isNoOp)
    {
        SourceProjectSnapshotJson = sourceProjectSnapshotJson;
        SourceProjectDigest = sourceProjectDigest;
        SourceProfileId = sourceProfileId;
        SourceProfileDigest = sourceProfileDigest;
        TargetProfile = targetProfile;
        Changes = changes;
        SafeNodeUpdates = safeNodeUpdates;
        IsNoOp = isNoOp;
        Summary = BuildSummary(sourceProfileDigest, targetProfile, changes, isNoOp);
    }

    public string SourceProjectSnapshotJson { get; }
    public string SourceProjectDigest { get; }
    public string SourceProfileId { get; }
    public string SourceProfileDigest { get; }
    public FilingProfile TargetProfile { get; }
    public IReadOnlyList<TaxonomyMigrationChange> Changes { get; }
    public bool IsNoOp { get; }
    public bool HasSemanticIncompatibilities =>
        Changes.Any(change => change.Severity == TaxonomyMigrationSeverity.Incompatible);
    public bool RequiresReapproval => HasSemanticIncompatibilities
        || Changes.Any(change => change.Severity == TaxonomyMigrationSeverity.Review);
    public string Summary { get; }

    internal IReadOnlyList<TaxonomyMigrationService.SafeNodeUpdate> SafeNodeUpdates { get; }

    private static string BuildSummary(
        string sourceDigest,
        FilingProfile targetProfile,
        IReadOnlyList<TaxonomyMigrationChange> changes,
        bool isNoOp)
    {
        if (isNoOp) return "The selected taxonomy digest is unchanged; no migration is needed.";
        var affected = changes.SelectMany(change => change.AffectedNodeIds)
            .Distinct(StringComparer.Ordinal).Count();
        var incompatible = changes.Count(change => change.Severity == TaxonomyMigrationSeverity.Incompatible);
        var review = changes.Count(change => change.Severity == TaxonomyMigrationSeverity.Review);
        return $"Profile digest {sourceDigest} → {targetProfile.Digest}. "
            + $"{affected} mapped node(s) are affected; {incompatible} incompatible change(s), "
            + $"{review} change(s) require review. The source revision remains pinned and unchanged.";
    }
}

/// <summary>
/// Compares profile-bound project semantics and creates an independent migration draft.
/// Applying a proposal never edits the source project or silently transfers its approval.
/// </summary>
public static class TaxonomyMigrationService
{
    internal sealed record SafeNodeUpdate(string NodeId, string Parameter, string Before, string After);

    private readonly record struct QNameIdentity(string? NamespaceUri, string LocalName);

    public static TaxonomyMigrationProposal Analyze(
        MappingProject project,
        FilingProfile sourceProfile,
        FilingProfile targetProfile)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(targetProfile);

        if (!StringComparer.Ordinal.Equals(project.ProfileId, sourceProfile.ProfileId)
            || !StringComparer.Ordinal.Equals(project.ProfileDigest, sourceProfile.Digest))
            throw new ArgumentException("The source profile must match the project's exact pinned identity.", nameof(sourceProfile));
        if (!StringComparer.Ordinal.Equals(sourceProfile.ProfileId, targetProfile.ProfileId))
            throw new ArgumentException("A taxonomy migration must keep the same filing profile ID.", nameof(targetProfile));

        var sourceSnapshot = ProjectSerializer.Serialize(project);
        var sourceDigest = project.ComputeSemanticDigest();
        var changes = new List<TaxonomyMigrationChange>();
        var safeUpdates = new List<SafeNodeUpdate>();

        if (StringComparer.Ordinal.Equals(sourceProfile.Digest, targetProfile.Digest))
        {
            if (!StringComparer.Ordinal.Equals(ProfileMetadataFingerprint(sourceProfile), ProfileMetadataFingerprint(targetProfile)))
                throw new InvalidOperationException("Profiles with the same digest contain different taxonomy metadata.");
            return new TaxonomyMigrationProposal(sourceSnapshot, sourceDigest,
                sourceProfile.ProfileId, sourceProfile.Digest, targetProfile,
                Array.Empty<TaxonomyMigrationChange>(), Array.Empty<SafeNodeUpdate>(), isNoOp: true);
        }

        Add(changes, "profile-digest-changed", "package",
            $"The package digest changed from {sourceProfile.Digest} to {targetProfile.Digest}.",
            TaxonomyMigrationSeverity.Information);

        ComparePackageHashes(sourceProfile, targetProfile, changes);
        CompareConcepts(project, sourceProfile, targetProfile, changes, safeUpdates);
        CompareDimensionNetworks(project, sourceProfile, targetProfile, changes, safeUpdates);
        CompareSet("required-check-added", "required-check-removed", "required check",
            sourceProfile.RequiredChecks, targetProfile.RequiredChecks, changes,
            addedSeverity: TaxonomyMigrationSeverity.Review,
            removedSeverity: TaxonomyMigrationSeverity.Review);
        CompareSet("required-plugin-added", "required-plugin-removed", "required plugin",
            sourceProfile.RequiredPlugins, targetProfile.RequiredPlugins, changes,
            addedSeverity: TaxonomyMigrationSeverity.Review,
            removedSeverity: TaxonomyMigrationSeverity.Review);

        if (!StringComparer.Ordinal.Equals(sourceProfile.TaxonomyVersion, targetProfile.TaxonomyVersion))
            Add(changes, "taxonomy-version-changed", "version",
                $"Taxonomy version changed from '{sourceProfile.TaxonomyVersion}' to '{targetProfile.TaxonomyVersion}'.",
                TaxonomyMigrationSeverity.Information);

        if (!StringComparer.Ordinal.Equals(sourceProfile.OutputFormat, targetProfile.OutputFormat))
            Add(changes, "output-format-changed", "output format",
                $"Output format changed from '{sourceProfile.OutputFormat}' to '{targetProfile.OutputFormat}'.",
                TaxonomyMigrationSeverity.Review);

        return new TaxonomyMigrationProposal(
            sourceSnapshot,
            sourceDigest,
            sourceProfile.ProfileId,
            sourceProfile.Digest,
            targetProfile,
            Array.AsReadOnly(changes.ToArray()),
            Array.AsReadOnly(safeUpdates.ToArray()),
            isNoOp: false);
    }

    /// <summary>Create an independent draft after checking that the proposal is still current.</summary>
    public static MappingProject CreateDraft(TaxonomyMigrationProposal proposal, MappingProject currentProject)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(currentProject);
        if (proposal.IsNoOp)
            throw new InvalidOperationException("An unchanged profile does not produce a migration draft.");
        if (!StringComparer.Ordinal.Equals(
                ProjectSerializer.Serialize(currentProject), proposal.SourceProjectSnapshotJson)
            || !StringComparer.Ordinal.Equals(currentProject.ComputeSemanticDigest(), proposal.SourceProjectDigest))
            throw new InvalidOperationException("The project changed after this migration proposal was created; preview it again.");

        var source = ProjectSerializer.Deserialize(proposal.SourceProjectSnapshotJson);
        if (!StringComparer.Ordinal.Equals(source.ProfileId, proposal.SourceProfileId)
            || !StringComparer.Ordinal.Equals(source.ProfileDigest, proposal.SourceProfileDigest)
            || !StringComparer.Ordinal.Equals(proposal.TargetProfile.ProfileId, proposal.SourceProfileId))
            throw new InvalidOperationException("The migration proposal no longer matches its pinned profile identities.");

        var nodes = source.Nodes.Select(CloneNode).ToList();
        var nodesById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        foreach (var update in proposal.SafeNodeUpdates)
        {
            if (!nodesById.TryGetValue(update.NodeId, out var node))
                throw new InvalidOperationException("A safely migratable mapping node is missing from the source snapshot.");
            if (!node.Parameters.TryGetValue(update.Parameter, out var value)
                || !StringComparer.Ordinal.Equals(ReadString(value), update.Before))
                throw new InvalidOperationException("A safely migratable mapping parameter changed after proposal analysis.");
            node.Parameters[update.Parameter] = update.After;
        }

        var nextRevision = checked(source.SemanticRevision + 1);
        var draft = new MappingProject
        {
            SchemaVersion = source.SchemaVersion,
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = proposal.TargetProfile.ProfileId,
            ProfileDigest = proposal.TargetProfile.Digest,
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = nextRevision,
            LayoutGeneration = source.LayoutGeneration,
            Metadata = source.Metadata with { },
            Nodes = nodes,
            Edges = source.Edges.Select(edge => edge with { }).ToList(),
            Bindings = source.Bindings.Select(binding => binding with
            {
                Configuration = new Dictionary<string, object?>(binding.Configuration, StringComparer.Ordinal)
            }).ToList(),
            Functions = source.Functions.ToList(),
            Destination = source.Destination is null
                ? null
                : source.Destination with { BusinessKey = source.Destination.BusinessKey.ToList() },
            // Preserve approvals as history, but never attach an old approval to the new identity.
            Approvals = source.Approvals.Where(approval =>
                approval.SemanticRevision != nextRevision
                || !StringComparer.Ordinal.Equals(approval.ProfileDigest, proposal.TargetProfile.Digest)).ToList()
        };
        return draft;
    }

    /// <summary>Restores an independent copy of the exact source project retained by the proposal.</summary>
    public static MappingProject RestoreSourceRevision(TaxonomyMigrationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return ProjectSerializer.Deserialize(proposal.SourceProjectSnapshotJson);
    }

    private static void ComparePackageHashes(
        FilingProfile source,
        FilingProfile target,
        List<TaxonomyMigrationChange> changes)
    {
        foreach (var name in source.PackageHashes.Keys.Union(target.PackageHashes.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            source.PackageHashes.TryGetValue(name, out var before);
            target.PackageHashes.TryGetValue(name, out var after);
            if (StringComparer.Ordinal.Equals(before, after)) continue;
            var action = before is null ? "added" : after is null ? "removed" : "changed";
            Add(changes, "package-hash-" + action, name,
                $"Package asset '{name}' was {action} ({before ?? "absent"} → {after ?? "absent"}).",
                TaxonomyMigrationSeverity.Information);
        }
    }

    private static void CompareConcepts(
        MappingProject project,
        FilingProfile source,
        FilingProfile target,
        List<TaxonomyMigrationChange> changes,
        List<SafeNodeUpdate> safeUpdates)
    {
        var sourceConcepts = IndexConcepts(source);
        var targetConcepts = IndexConcepts(target);
        var mappedNodes = FactNodes(project).ToArray();
        var mappedNodesByQName = mappedNodes
            .Select(node => (Node: node, QName: ReadString(node.Parameters.GetValueOrDefault("concept"))))
            .Where(item => !string.IsNullOrWhiteSpace(item.QName))
            .GroupBy(item => item.QName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.Node.NodeId)
                    .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var added = 0;
        var removed = 0;

        foreach (var oldConcept in sourceConcepts.Values.OrderBy(concept => concept.Namespace, StringComparer.Ordinal)
                     .ThenBy(concept => concept.LocalName, StringComparer.Ordinal))
        {
            var matchingNodes = mappedNodesByQName.TryGetValue(oldConcept.QualifiedName, out var mapped)
                ? mapped
                : Array.Empty<string>();
            if (!targetConcepts.TryGetValue(oldConcept.ExpandedName, out var newConcept))
            {
                removed++;
                Add(changes, matchingNodes.Count == 0 ? "concept-removed" : "mapped-concept-removed-or-renamed",
                    oldConcept.QualifiedName,
                    matchingNodes.Count == 0
                        ? $"Concept '{oldConcept.QualifiedName}' is absent from the target taxonomy."
                        : $"Mapped concept '{oldConcept.QualifiedName}' is absent or renamed; choose a replacement explicitly.",
                    matchingNodes.Count == 0 ? TaxonomyMigrationSeverity.Information : TaxonomyMigrationSeverity.Incompatible,
                    matchingNodes);
                continue;
            }

            if (!StringComparer.Ordinal.Equals(oldConcept.QualifiedName, newConcept.QualifiedName))
            {
                Add(changes, "concept-qname-prefix-changed", oldConcept.QualifiedName,
                    $"The expanded concept identity is unchanged; mapping QName can be updated to '{newConcept.QualifiedName}'.",
                    TaxonomyMigrationSeverity.Information, matchingNodes);
                foreach (var nodeId in matchingNodes)
                    safeUpdates.Add(new SafeNodeUpdate(nodeId, "concept", oldConcept.QualifiedName, newConcept.QualifiedName));
            }

            CompareConceptProperty("concept-period-type-changed", "period type", oldConcept.PeriodType,
                newConcept.PeriodType, oldConcept.QualifiedName, matchingNodes, changes);
            CompareConceptProperty("concept-data-type-changed", "data type", oldConcept.DataType,
                newConcept.DataType, oldConcept.QualifiedName, matchingNodes, changes);
            if (oldConcept.IsAbstract != newConcept.IsAbstract)
            {
                var isBreaking = newConcept.IsAbstract && matchingNodes.Count > 0;
                Add(changes, "concept-abstractness-changed", oldConcept.QualifiedName,
                    $"Abstract state changed from {oldConcept.IsAbstract} to {newConcept.IsAbstract}.",
                    isBreaking ? TaxonomyMigrationSeverity.Incompatible : TaxonomyMigrationSeverity.Information,
                    matchingNodes);
            }

            if (!LabelsEqual(oldConcept, newConcept) && matchingNodes.Count > 0)
                Add(changes, "concept-label-changed", oldConcept.QualifiedName,
                    $"Display label changed to '{newConcept.Label}'; the expanded concept identity and data contract are unchanged.",
                    TaxonomyMigrationSeverity.Information, matchingNodes);
        }

        added = targetConcepts.Keys.Count(name => !sourceConcepts.ContainsKey(name));
        if (added > 0)
            Add(changes, "concepts-added", "concept index",
                $"The target taxonomy adds {added} concept(s).", TaxonomyMigrationSeverity.Information);
        if (removed > 0)
            Add(changes, "concepts-removed", "concept index",
                $"The target taxonomy removes {removed} concept(s); mapped removals are listed individually.",
                TaxonomyMigrationSeverity.Information);

        foreach (var node in mappedNodes)
        {
            var name = ReadString(node.Parameters.GetValueOrDefault("concept"));
            if (string.IsNullOrWhiteSpace(name) || source.FindConcept(name) is not null) continue;
            Add(changes, "mapped-concept-unresolved", name,
                $"Fact node '{node.DisplayName}' maps to '{name}', which is not present in the pinned source profile.",
                TaxonomyMigrationSeverity.Incompatible, new[] { node.NodeId });
        }
    }

    private static void CompareConceptProperty(
        string code,
        string property,
        string before,
        string after,
        string subject,
        IReadOnlyList<string> nodeIds,
        List<TaxonomyMigrationChange> changes)
    {
        if (StringComparer.Ordinal.Equals(before, after)) return;
        Add(changes, code, subject,
            $"Concept {property} changed from '{before}' to '{after}'.",
            nodeIds.Count == 0 ? TaxonomyMigrationSeverity.Information : TaxonomyMigrationSeverity.Incompatible,
            nodeIds);
    }

    private static void CompareDimensionNetworks(
        MappingProject project,
        FilingProfile source,
        FilingProfile target,
        List<TaxonomyMigrationChange> changes,
        List<SafeNodeUpdate> safeUpdates)
    {
        var nodes = FactNodes(project).ToArray();
        var uses = new Dictionary<(QNameIdentity Axis, QNameIdentity Member), HashSet<string>>();
        var axisUses = new Dictionary<QNameIdentity, HashSet<string>>();
        foreach (var node in nodes)
        {
            var text = ReadString(node.Parameters.GetValueOrDefault("dimensions"));
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!FactDimensionParser.TryParse(text, source.DimensionDomains, out var dimensions, out var error))
            {
                Add(changes, "source-dimension-unresolved", node.NodeId,
                    $"Fact node '{node.DisplayName}' dimensions cannot be compared against the source profile: {error}",
                    TaxonomyMigrationSeverity.Review, new[] { node.NodeId });
                continue;
            }
            foreach (var dimension in dimensions)
            {
                var identity = (Identity(source, dimension.Name), Identity(source, dimension.MemberName));
                if (!uses.TryGetValue(identity, out var nodeIds)) uses.Add(identity, nodeIds = new HashSet<string>(StringComparer.Ordinal));
                nodeIds.Add(node.NodeId);
                if (!axisUses.TryGetValue(identity.Item1, out var axisNodeIds))
                    axisUses.Add(identity.Item1, axisNodeIds = new HashSet<string>(StringComparer.Ordinal));
                axisNodeIds.Add(node.NodeId);
            }

            var migrated = new List<Dimension>(dimensions.Count);
            var changed = false;
            foreach (var dimension in dimensions)
            {
                var axis = EquivalentQName(source, target, dimension.Name, target.DimensionDomains.Keys);
                var members = axis is not null && target.DimensionDomains.TryGetValue(axis, out var declaredMembers)
                    ? declaredMembers
                    : Array.Empty<string>();
                var member = EquivalentQName(source, target, dimension.MemberName, members);
                if (axis is null || member is null)
                {
                    migrated.Add(dimension);
                    continue;
                }
                migrated.Add(new Dimension(axis, member));
                changed |= !StringComparer.Ordinal.Equals(axis, dimension.Name)
                    || !StringComparer.Ordinal.Equals(member, dimension.MemberName);
            }
            if (changed)
            {
                var before = text;
                var after = FactDimensionParser.Format(migrated);
                Add(changes, "dimension-qname-prefix-changed", node.NodeId,
                    $"Dimension QNames retain their expanded identities and can be updated to '{after}'.",
                    TaxonomyMigrationSeverity.Information, new[] { node.NodeId });
                safeUpdates.Add(new SafeNodeUpdate(node.NodeId, "dimensions", before, after));
            }
        }

        var sourceDomains = IndexDomains(source);
        var targetDomains = IndexDomains(target);
        foreach (var oldDomain in sourceDomains.Values)
        {
            if (!targetDomains.TryGetValue(oldDomain.Identity, out var newDomain))
            {
                var nodeIds = axisUses.TryGetValue(oldDomain.Identity, out var foundAxisUses)
                    ? foundAxisUses.Order(StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
                Add(changes, nodeIds.Length == 0 ? "dimension-removed" : "mapped-dimension-removed",
                    oldDomain.Name,
                    nodeIds.Length == 0
                        ? $"Dimension '{oldDomain.Name}' is absent from the target member network."
                        : $"Mapped dimension '{oldDomain.Name}' is absent from the target member network.",
                    nodeIds.Length == 0 ? TaxonomyMigrationSeverity.Information : TaxonomyMigrationSeverity.Incompatible,
                    nodeIds);
                continue;
            }

            var newMembers = newDomain.Members.Select(member => Identity(target, member)).ToHashSet();
            foreach (var oldMember in oldDomain.Members)
            {
                var memberIdentity = Identity(source, oldMember);
                if (newMembers.Contains(memberIdentity)) continue;
                var nodeIds = uses.TryGetValue((oldDomain.Identity, memberIdentity), out var found)
                    ? found.Order(StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
                Add(changes, nodeIds.Length == 0 ? "dimension-member-removed" : "mapped-dimension-member-removed",
                    $"{oldDomain.Name}={oldMember}",
                    nodeIds.Length == 0
                        ? $"Member '{oldMember}' was removed from dimension '{oldDomain.Name}'."
                        : $"Mapped member '{oldMember}' was removed from dimension '{oldDomain.Name}'.",
                    nodeIds.Length == 0 ? TaxonomyMigrationSeverity.Information : TaxonomyMigrationSeverity.Incompatible,
                    nodeIds);
            }
        }

        var addedAxes = targetDomains.Keys.Count(identity => !sourceDomains.ContainsKey(identity));
        if (addedAxes > 0)
            Add(changes, "dimensions-added", "member network",
                $"The target profile adds {addedAxes} dimension axis/axes.", TaxonomyMigrationSeverity.Information);
        var addedMembers = 0;
        foreach (var domain in targetDomains.Values)
        {
            var oldMembers = sourceDomains.TryGetValue(domain.Identity, out var oldDomain)
                ? oldDomain.Members.Select(member => Identity(source, member)).ToHashSet()
                : new HashSet<QNameIdentity>();
            addedMembers += domain.Members.Count(member => !oldMembers.Contains(Identity(target, member)));
        }
        if (addedMembers > 0)
            Add(changes, "dimension-members-added", "member network",
                $"The target profile adds {addedMembers} dimension member(s).", TaxonomyMigrationSeverity.Information);
    }

    private sealed record Domain(QNameIdentity Identity, string Name, IReadOnlyList<string> Members);

    private static Dictionary<QNameIdentity, Domain> IndexDomains(FilingProfile profile)
    {
        var domains = new Dictionary<QNameIdentity, Domain>();
        foreach (var (axis, members) in profile.DimensionDomains)
        {
            var identity = Identity(profile, axis);
            if (!domains.TryAdd(identity, new Domain(identity, axis, members ?? Array.Empty<string>())))
                throw new InvalidOperationException($"Profile '{profile.ProfileId}' declares the same expanded dimension more than once.");
        }
        return domains;
    }

    private static void CompareSet(
        string addedCode,
        string removedCode,
        string label,
        IReadOnlySet<string> source,
        IReadOnlySet<string> target,
        List<TaxonomyMigrationChange> changes,
        TaxonomyMigrationSeverity addedSeverity,
        TaxonomyMigrationSeverity removedSeverity)
    {
        foreach (var value in target.Except(source, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Add(changes, addedCode, value, $"Required {label} '{value}' was added.", addedSeverity);
        foreach (var value in source.Except(target, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Add(changes, removedCode, value, $"Required {label} '{value}' was removed.", removedSeverity);
    }

    private static Dictionary<ExpandedConceptName, ConceptMetadata> IndexConcepts(FilingProfile profile)
    {
        var concepts = new Dictionary<ExpandedConceptName, ConceptMetadata>();
        foreach (var concept in profile.Concepts)
        {
            if (!concepts.TryAdd(concept.ExpandedName, concept))
                throw new InvalidOperationException($"Profile '{profile.ProfileId}' contains duplicate expanded concept names.");
        }
        return concepts;
    }

    private static bool LabelsEqual(ConceptMetadata left, ConceptMetadata right)
    {
        if (!StringComparer.Ordinal.Equals(left.Label, right.Label)
            || !left.SupportedLanguages.Order(StringComparer.Ordinal).SequenceEqual(
                right.SupportedLanguages.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || left.LabelsByLanguage.Count != right.LabelsByLanguage.Count) return false;
        return left.LabelsByLanguage.All(pair => right.LabelsByLanguage.TryGetValue(pair.Key, out var label)
            && StringComparer.Ordinal.Equals(pair.Value, label));
    }

    private static IEnumerable<MappingNode> FactNodes(MappingProject project)
        => project.Nodes.Where(node => StringComparer.Ordinal.Equals(node.NodeType, NodeTypeConstants.FactConstruct));

    private static string? ReadString(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        _ => value.ToString()
    };

    private static QNameIdentity Identity(FilingProfile profile, string qname)
    {
        var concept = profile.FindConcept(qname);
        if (concept is not null) return new QNameIdentity(concept.Namespace, concept.LocalName);
        var separator = qname.IndexOf(':');
        if (separator > 0 && separator == qname.LastIndexOf(':')
            && profile.NamespaceUris.TryGetValue(qname[..separator], out var namespaceUri))
            return new QNameIdentity(namespaceUri, qname[(separator + 1)..]);
        return new QNameIdentity(null, qname);
    }

    private static string? EquivalentQName(
        FilingProfile source,
        FilingProfile target,
        string oldQName,
        IEnumerable<string> targetQNames)
    {
        var identity = Identity(source, oldQName);
        return targetQNames.Where(qname => Identity(target, qname) == identity)
            .Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static void Add(
        List<TaxonomyMigrationChange> changes,
        string code,
        string subject,
        string description,
        TaxonomyMigrationSeverity severity,
        IEnumerable<string>? nodeIds = null)
        => changes.Add(new TaxonomyMigrationChange(code, subject, description, severity,
            Array.AsReadOnly((nodeIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray())));

    private static MappingNode CloneNode(MappingNode node)
        => node with
        {
            Parameters = new Dictionary<string, object?>(node.Parameters, StringComparer.Ordinal),
            InputPorts = node.InputPorts.ToList(),
            OutputPorts = node.OutputPorts.ToList(),
            Layout = node.Layout is null ? null : node.Layout with { }
        };

    private static string ProfileMetadataFingerprint(FilingProfile profile)
    {
        var builder = new StringBuilder();
        Part(builder, profile.ProfileId);
        Part(builder, profile.TaxonomyVersion);
        Part(builder, profile.OutputFormat);
        foreach (var concept in profile.Concepts.OrderBy(item => item.Namespace, StringComparer.Ordinal)
                     .ThenBy(item => item.LocalName, StringComparer.Ordinal))
        {
            Part(builder, concept.Namespace);
            Part(builder, concept.LocalName);
            Part(builder, concept.QualifiedName);
            Part(builder, concept.Label);
            Part(builder, concept.DataType);
            Part(builder, concept.PeriodType);
            Part(builder, concept.IsAbstract ? "true" : "false");
            foreach (var language in concept.SupportedLanguages.Order(StringComparer.Ordinal)) Part(builder, language);
            foreach (var label in concept.LabelsByLanguage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Part(builder, label.Key);
                Part(builder, label.Value);
            }
            Part(builder, "concept-end");
        }
        foreach (var (axis, members) in profile.DimensionDomains.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Part(builder, axis);
            foreach (var member in members.Order(StringComparer.Ordinal)) Part(builder, member);
            Part(builder, "dimension-end");
        }
        foreach (var unit in profile.SupportedUnits.Order(StringComparer.Ordinal)) Part(builder, unit);
        foreach (var check in profile.RequiredChecks.Order(StringComparer.Ordinal)) Part(builder, "check:" + check);
        foreach (var plugin in profile.RequiredPlugins.Order(StringComparer.Ordinal)) Part(builder, "plugin:" + plugin);
        foreach (var item in profile.NamespaceUris.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Part(builder, item.Key);
            Part(builder, item.Value);
        }
        foreach (var item in profile.PackageHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Part(builder, item.Key);
            Part(builder, item.Value);
        }
        foreach (var url in profile.TaxonomyUrls.Order(StringComparer.Ordinal)) Part(builder, url);
        foreach (var link in profile.PresentationLinks.OrderBy(item => item.RoleUri, StringComparer.Ordinal)
                     .ThenBy(item => item.ParentQName, StringComparer.Ordinal)
                     .ThenBy(item => item.ChildQName, StringComparer.Ordinal).ThenBy(item => item.Order))
        {
            Part(builder, link.RoleUri);
            Part(builder, link.ParentQName);
            Part(builder, link.ChildQName);
            Part(builder, link.Order.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Part(StringBuilder builder, string? value)
    {
        var text = value ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append('|');
    }
}
