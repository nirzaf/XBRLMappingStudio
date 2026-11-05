using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>
/// A single proposed source-field → concept mapping emitted by the suggestion layer.
/// <see cref="SourceShape"/> and <see cref="NodeOperation"/> exist so hostile or
/// malformed provider output (incompatible port shape, disallowed operation) can be
/// fed through the same structural validation as well-formed drafts (AI03/AI04).
/// </summary>
public sealed record DraftMapping(
    string MappingId,
    string SourceField,
    string? Concept,
    string? Unit,
    string? PeriodType,
    int? Decimals = null,
    string? Language = null,
    string SourceShape = "Decimal",
    string? NodeOperation = null,
    string? DimensionName = null,
    string? DimensionMember = null);

/// <summary>Outcome of validating one set of draft mappings (AI04).</summary>
public sealed record DraftValidationResult(bool Accepted, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Runs proposed drafts through the same gates a human-authored graph must pass:
/// <see cref="GraphValidator"/> for structural integrity (unknown/disallowed node
/// operations, incompatible port shapes, dangling required inputs) and
/// <see cref="FilingRuleValidator"/> for semantic fitness (invented concept,
/// abstract target, unsupported unit, period mismatch, invalid dimension member,
/// duplicates). Missing required aspects surface before approval (AI04).
/// </summary>
public sealed class DraftValidator
{
    private readonly TaxonomyIndex _taxonomy;
    private readonly FilingProfile _profile;
    private readonly IReadOnlySet<string>? _knownDimensionMembers;

    public DraftValidator(
        TaxonomyIndex taxonomy,
        FilingProfile profile,
        IReadOnlySet<string>? knownDimensionMembers = null)
    {
        _taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (!_taxonomy.IsCurrentFor(_profile))
            throw new ArgumentException("Draft validation requires the exact selected taxonomy profile and digest.", nameof(profile));
        _knownDimensionMembers = knownDimensionMembers;
    }

    public DraftValidationResult Validate(IReadOnlyList<DraftMapping> drafts)
    {
        var bag = new DiagnosticBag();

        foreach (var d in drafts)
        {
            CheckAspects(d, bag);
            CheckConcept(d, bag);
        }

        // Structural pass: build the proposed subgraph and run the ordinary graph validator.
        var project = BuildDraftGraph(drafts, _profile);
        var structural = new GraphValidator(FunctionCatalog.Default).Validate(new MappingGraph(project));
        foreach (var diag in structural.Items)
            bag.Error(diag.Code, diag.Subsystem, diag.Stage, diag.Message,
                nodeId: diag.NodeId, portName: diag.PortName, edgeId: diag.EdgeId);

        // Semantic pass: materialize one representative fact per draft and run the filing rules.
        var facts = new List<XbrlFact>();
        foreach (var d in drafts)
        {
            var fact = TryMaterializeFact(d);
            if (fact is not null) facts.Add(fact);
        }
        if (facts.Count > 0)
        {
            var filing = new FilingRuleValidator(_profile, _knownDimensionMembers).Validate(facts);
            foreach (var diag in filing.Diagnostics)
                bag.Error(diag.Code, diag.Subsystem, diag.Stage, diag.Message,
                    nodeId: diag.NodeId, details: diag.Details);
        }

        return new DraftValidationResult(!bag.HasBlockingErrors, bag.Items);
    }

    private void CheckAspects(DraftMapping d, DiagnosticBag bag)
    {
        if (string.IsNullOrWhiteSpace(d.SourceField))
            Aspect(d, bag, "source field");
        if (string.IsNullOrWhiteSpace(d.Concept))
            Aspect(d, bag, "concept");
        if (string.IsNullOrWhiteSpace(d.PeriodType))
            Aspect(d, bag, "period type");
        else if (d.PeriodType is not ("instant" or "duration"))
            bag.Error(DiagnosticCodes.AiInvalidDraft, DiagnosticSubsystem.AI,
                DiagnosticStage.Validation,
                $"Draft '{d.MappingId}' declares unknown period type '{d.PeriodType}'.",
                nodeId: FactNodeId(d));
        var concept = d.Concept is null ? null : _taxonomy.Get(d.Concept);
        if (concept is { DataType: "monetary" } && string.IsNullOrWhiteSpace(d.Unit))
            Aspect(d, bag, "unit");
    }

    private static void Aspect(DraftMapping d, DiagnosticBag bag, string name) =>
        bag.Error(DiagnosticCodes.AiInvalidDraft, DiagnosticSubsystem.AI,
            DiagnosticStage.Validation,
            $"Draft '{d.MappingId}' is missing required aspect '{name}'; approval is blocked until it is supplied.",
            nodeId: FactNodeId(d));

    private void CheckConcept(DraftMapping d, DiagnosticBag bag)
    {
        if (string.IsNullOrWhiteSpace(d.Concept)) return;
        var concept = _taxonomy.Get(d.Concept);
        if (concept is null)
        {
            bag.Error(DiagnosticCodes.AiInvalidDraft, DiagnosticSubsystem.AI,
                DiagnosticStage.Validation,
                $"Draft '{d.MappingId}' proposes invented concept '{d.Concept}' that is not in the taxonomy.",
                nodeId: FactNodeId(d));
            return;
        }
        if (!TaxonomyIndex.IsFactTargetable(concept))
        {
            bag.Error(DiagnosticCodes.AiInvalidDraft, DiagnosticSubsystem.AI,
                DiagnosticStage.Validation,
                $"Draft '{d.MappingId}' proposes abstract concept '{d.Concept}' as a fact target.",
                nodeId: FactNodeId(d));
        }
    }

    private static string FactNodeId(DraftMapping d) => $"fact-{d.MappingId}";
    private static string SourceNodeId(DraftMapping d) => $"src-{d.MappingId}";

    /// <summary>
    /// Each draft becomes excelField(binding,column) → factConstruct(value,entity).
    /// A shared run-parameter supplies the entity so the required input is bound.
    /// Hostile drafts may declare a bogus <see cref="DraftMapping.NodeOperation"/> or an
    /// incompatible <see cref="DraftMapping.SourceShape"/>; those flow through the same
    /// graph construction so the structural validator rejects them.
    /// </summary>
    private static MappingProject BuildDraftGraph(IReadOnlyList<DraftMapping> drafts, FilingProfile profile)
    {
        var project = new MappingProject
        {
            ProjectId = "draft-" + Guid.Empty.ToString("N"),
            Name = "ai-draft",
            ProfileId = profile.ProfileId,
            ProfileDigest = profile.Digest,
        };

        project.Nodes.Add(new MappingNode
        {
            NodeId = "entity-param",
            NodeType = NodeTypeConstants.RunParameter,
            DisplayName = "draft entity",
            Parameters = { ["name"] = "entityIdentifier", ["shape"] = "String" },
            OutputPorts = { new PortDefinition("value", PortShape.String, PortCardinality.Single) },
        });

        foreach (var d in drafts)
        {
            var sourceShape = Enum.TryParse<PortShape>(d.SourceShape, ignoreCase: true, out var ps)
                ? ps : PortShape.Decimal;

            project.Nodes.Add(new MappingNode
            {
                NodeId = SourceNodeId(d),
                // A disallowed operation lands here verbatim and is rejected as unknown (MAP01 behavior).
                NodeType = d.NodeOperation ?? NodeTypeConstants.ExcelField,
                DisplayName = d.SourceField,
                Parameters = { ["bindingId"] = "draft-binding", ["column"] = d.SourceField },
                OutputPorts = { new PortDefinition("value", sourceShape, PortCardinality.Single) },
            });

            var fact = new MappingNode
            {
                NodeId = FactNodeId(d),
                NodeType = NodeTypeConstants.FactConstruct,
                DisplayName = $"fact {d.Concept ?? "(missing)"}",
                InputPorts =
                {
                    new PortDefinition("value", PortShape.Decimal, PortCardinality.Single, IsRequired: false),
                    new PortDefinition("entity", PortShape.String, PortCardinality.Single, IsRequired: true),
                },
                OutputPorts = { new PortDefinition("fact", PortShape.Fact, PortCardinality.Single) },
            };
            if (d.Concept is not null) fact.Parameters["concept"] = d.Concept;
            if (d.Unit is not null) fact.Parameters["unit"] = d.Unit;
            if (d.PeriodType is not null) fact.Parameters["periodType"] = d.PeriodType;
            if (d.Decimals is not null) fact.Parameters["decimals"] = d.Decimals;
            if (d.Language is not null) fact.Parameters["language"] = d.Language;
            project.Nodes.Add(fact);

            project.Edges.Add(new MappingEdge
            {
                EdgeId = $"e-src-{d.MappingId}",
                SourceNodeId = SourceNodeId(d),
                SourcePortName = "value",
                TargetNodeId = FactNodeId(d),
                TargetPortName = "value",
            });
            project.Edges.Add(new MappingEdge
            {
                EdgeId = $"e-ent-{d.MappingId}",
                SourceNodeId = "entity-param",
                SourcePortName = "value",
                TargetNodeId = FactNodeId(d),
                TargetPortName = "entity",
            });
        }

        return project;
    }

    /// <summary>
    /// Build one representative fact so the filing-rule validator can judge semantics.
    /// Drafts too malformed to materialize are already blocked by the aspect checks.
    /// </summary>
    private XbrlFact? TryMaterializeFact(DraftMapping d)
    {
        if (string.IsNullOrWhiteSpace(d.Concept)
            || string.IsNullOrWhiteSpace(d.Unit)
            || d.PeriodType is not ("instant" or "duration"))
            return null;

        var period = d.PeriodType == "instant"
            ? XbrlPeriod.OfInstant(new DateOnly(2024, 12, 31))
            : XbrlPeriod.OfDuration(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));

        var dimensions = d.DimensionName is not null && d.DimensionMember is not null
            ? new[] { new Dimension(d.DimensionName, d.DimensionMember) }
            : Array.Empty<Dimension>();

        return new XbrlFact
        {
            ConceptName = d.Concept,
            EntityScheme = "https://xbrl.org/entity/identifier-scheme",
            EntityIdentifier = "draft-entity",
            Period = period,
            Unit = new XbrlUnit(d.Unit),
            Value = 0m,
            Decimals = d.Decimals,
            Language = d.Language,
            Dimensions = dimensions,
        };
    }
}
