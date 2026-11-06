using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class LargeRunExecutionTests
{
    [Fact]
    public async Task SmallAndQualifiedLargeModes_ProduceByteIdenticalReportsAndSourceDigests()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: true);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            using var small = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 20, RowBatchSize: 3));
            using var large = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 2, RowBatchSize: 3));

            Assert.Equal(RunOutcome.Success, small.Manifest.Outcome);
            Assert.Equal(RunOutcome.Success, large.Manifest.Outcome);
            Assert.Null(small.LargeArtifact);
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal(string.Empty, large.XbrlJson);
            Assert.Equal(small.XbrlJson, await File.ReadAllTextAsync(artifact.XbrlJsonPath));
            Assert.Equal(small.Manifest.Outputs.Single().Digest, large.Manifest.Outputs.Single().Digest);
            Assert.Equal(small.Manifest.Inputs.Single().Digest, large.Manifest.Inputs.Single().Digest);
            Assert.Equal(artifact.Sha256, large.Manifest.ValidatedArtifactDigest);
            Assert.NotEmpty(small.Facts[0].TransformationIds);
            Assert.Contains(project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField).NodeId,
                small.Facts[0].TransformationIds);
            Assert.Contains(project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct).NodeId,
                small.Facts[0].TransformationIds);
            Assert.Equal(small.Facts[0].TransformationIds, artifact.Facts[0].TransformationIds);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task TwoInputRatio_SourceTransformationPathsSurviveLargeFactSpool()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: true);
            var project = BuildRatioProject(database);
            var profile = SyntheticProfile.Build();
            using var small = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 64, RowBatchSize: 8));
            using var large = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 8, RowBatchSize: 8));

            Assert.Equal(RunOutcome.Success, small.Manifest.Outcome);
            Assert.Equal(RunOutcome.Success, large.Manifest.Outcome);
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal(small.XbrlJson, await File.ReadAllTextAsync(artifact.XbrlJsonPath));
            Assert.DoesNotContain(nameof(XbrlFact.SourceTransformationPaths), small.XbrlJson, StringComparison.Ordinal);

            var numerator = project.Nodes.Single(node => node.DisplayName == "Current revenue");
            var denominator = project.Nodes.Single(node => node.DisplayName == "Prior revenue");
            var ratio = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Ratio);
            var factConstruct = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            var smallFact = small.Facts[0];
            var largeFact = artifact.Facts[0];

            Assert.Equal(2, smallFact.SourceTransformationPaths.Count);
            Assert.Equal(2, largeFact.SourceTransformationPaths.Count);
            AssertTransformationPath(smallFact, "financials.Current",
                numerator.NodeId, ratio.NodeId, factConstruct.NodeId);
            AssertTransformationPath(smallFact, "financials.Prior",
                denominator.NodeId, ratio.NodeId, factConstruct.NodeId);
            AssertTransformationPath(largeFact, "financials.Current",
                numerator.NodeId, ratio.NodeId, factConstruct.NodeId);
            AssertTransformationPath(largeFact, "financials.Prior",
                denominator.NodeId, ratio.NodeId, factConstruct.NodeId);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task LargeSpoolBudgetExceeded_FailsClosedBeforeWorkerValidation()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: false);
            var project = BuildRatioProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var priorDirectories = LargeRunDirectories();

            using var result = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 8, RowBatchSize: 8,
                    SqliteCacheKiB: 256, MaxSpoolBytes: 1024), worker);

            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IoNumberOutOfBudget);
            Assert.Empty(result.Facts);
            Assert.Null(result.LargeArtifact);
            Assert.Empty(result.Manifest.Outputs);
            Assert.Null(result.Manifest.ValidatedArtifactDigest);
            Assert.Null(worker.LastValidationPath);
            Assert.Equal(priorDirectories, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task HundredThousandRows_StreamsExactOutputAndKeepsBatchWorkingSetBounded()
    {
        const int rowCount = 100_000;
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, rowCount, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var samples = new List<long>();
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                if (stage.Stage == "Executing bounded row batch" && stage.Current is > 0
                    && stage.Current % (256 * 20) == 0)
                    samples.Add(GC.GetTotalMemory(forceFullCollection: true));
            });
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 128, RowBatchSize: 256,
                SqliteCacheKiB: 256, MaxSpoolBytes: 384 * 1024 * 1024,
                MaxReportBytes: 256 * 1024 * 1024);
            using var result = await Run(project, database, profile, budget, worker, progress);

            Assert.True(result.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", result.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            Assert.Equal(rowCount, artifact.FactCount);
            Assert.Equal(artifact.XbrlJsonPath, worker.LastValidationPath);
            Assert.StartsWith("sha256:", artifact.Sha256, StringComparison.Ordinal);

            Assert.True(samples.Count >= 5, "Expected multiple steady-state batch memory samples.");
            Assert.True(samples.Max() - samples.Min() < 64L * 1024 * 1024,
                $"Retained heap changed by more than the 64 MiB batch budget: {samples.Min()}..{samples.Max()} bytes.");

            var smallWorker = new DigestOnlyArtifactWorker(profile);
            using var small = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: rowCount, RowBatchSize: 256,
                    MaxProbeBytes: 256L * 1024 * 1024), smallWorker);
            Assert.Equal(RunOutcome.Success, small.Manifest.Outcome);
            Assert.Null(small.LargeArtifact);
            Assert.Equal(artifact.Sha256, RunManifest.ComputeSha256(small.XbrlJson));
            Assert.Equal(Encoding.UTF8.GetByteCount(small.XbrlJson), artifact.Bytes);
            var smallReportPath = Path.Combine(root, "small-report.json");
            await File.WriteAllTextAsync(smallReportPath, small.XbrlJson, new UTF8Encoding(false));
            using (var expectedStream = File.OpenRead(smallReportPath))
            using (var actualStream = artifact.OpenReportRead())
                AssertStreamsEqual(expectedStream, actualStream);

            var path = artifact.XbrlJsonPath;
            result.Dispose();
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task CancellationDuringBatchExecution_DeletesPrivateSpoolAndSkipsValidation()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2_000, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            using var cancellation = new CancellationTokenSource();
            var priorDirectories = LargeRunDirectories();
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                if (stage.Stage == "Executing bounded row batch" && stage.Current >= 32)
                    cancellation.Cancel();
            });
            using var result = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 16, RowBatchSize: 32),
                worker, progress, cancellation.Token);

            Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Equal(priorDirectories, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task CancellationDuringSpoolFinalization_ReturnsCancelledAndCleansPrivateSpool()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2_000, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            using var cancellation = new CancellationTokenSource();
            var priorDirectories = LargeRunDirectories();
            var progressStages = new List<string>();
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                progressStages.Add(stage.Stage);
                if (stage.Stage == "Computing bounded source digest") cancellation.Cancel();
            });

            using var result = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 16, RowBatchSize: 32),
                worker, progress, cancellation.Token);

            Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Empty(result.Facts);
            Assert.Contains("Finalizing bounded source spool indexes", progressStages);
            Assert.Contains("Computing bounded source digest", progressStages);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Equal(priorDirectories, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task AdapterFailureDuringBoundedProbe_ReturnsDataErrorInsteadOfEscaping()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateBinaryFloatSourceDatabase(root);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);

            using var result = await Run(project, database, profile, ExecutionBudget.Default, worker);

            Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics,
                diagnostic => diagnostic.Code == DiagnosticCodes.IoNumberOutOfBudget);
            Assert.Empty(result.Facts);
            Assert.Equal(0, worker.ValidationRequests);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task CancellationDuringSourceInspection_ReturnsCancelledInsteadOfEscaping()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                if (stage.Stage.StartsWith("Inspecting source ", StringComparison.Ordinal))
                    cancellation.Cancel();
            });

            using var result = await Run(project, database, profile, ExecutionBudget.Default,
                worker, progress, cancellation.Token);

            Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
            Assert.Empty(result.Facts);
            Assert.Equal(0, worker.ValidationRequests);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task UnsupportedLargeDestination_FailsAfterBoundedProbeWithoutWorkerReportOrArtifact()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2_000, variedValues: false);
            var project = BuildProject(database);
            project.Destination = new DestinationPlan
            {
                Kind = DestinationPlanValidator.CsvKind,
                Locator = "unused.csv",
                Table = "",
                Format = DestinationPlanValidator.FileFormatFor(DestinationPlanValidator.CsvKind)!,
                WriteMode = "replace"
            };
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var priorDirectories = LargeRunDirectories();
            using var result = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 16, RowBatchSize: 8), worker);

            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Equal(priorDirectories, LargeRunDirectories());
            Assert.Contains(result.Manifest.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IoUnsupportedOperation);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task OversizedInMemorySource_FailsBeforeSourceIdentityReadOrSchemaInspection()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2, variedValues: false);
            var project = BuildProject(database);
            var oversizedPath = Path.Combine(root, "oversized.csv");
            await File.WriteAllBytesAsync(oversizedPath, Encoding.UTF8.GetBytes(new string('x', 1_024)));
            var binding = project.Bindings.Single();
            var connection = new SourceConnection(binding.BindingId, "csv", oversizedPath,
                new Dictionary<string, string> { ["delimiter"] = "," });
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var progressStages = new List<string>();
            var progress = new InlineProgress<MappingJobProgress>(stage => progressStages.Add(stage.Stage));
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(MaxInMemorySourceBytes: 128,
                    MaxExpandedInMemorySourceBytes: 128));

            using var result = await service.RunAsync(project,
                new Dictionary<string, SourceConnection> { [binding.BindingId] = connection },
                new Dictionary<string, string>(), preview: false, CancellationToken.None, progress);

            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics,
                diagnostic => diagnostic.Code == DiagnosticCodes.IoNumberOutOfBudget && diagnostic.IsBlocking);
            Assert.DoesNotContain("Recording source identities", progressStages);
            Assert.DoesNotContain(progressStages, stage => stage.StartsWith("Inspecting source ", StringComparison.Ordinal));
            Assert.Empty(result.Manifest.Inputs);
            Assert.Equal(0, worker.ValidationRequests);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task WorkerRejection_CleansLargeArtifactAndReturnsNoOutputIdentity()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2_000, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile) { ValidationStatus = WorkerStatus.DomainFailure };
            var priorDirectories = LargeRunDirectories();
            using var result = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 16, RowBatchSize: 32), worker);

            Assert.Equal(RunOutcome.InvalidMapping, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Empty(result.Facts);
            Assert.Empty(result.Manifest.Outputs);
            Assert.Null(result.Manifest.ValidatedArtifactDigest);
            Assert.NotNull(worker.LastValidationPath);
            Assert.False(File.Exists(worker.LastValidationPath));
            Assert.Equal(priorDirectories, LargeRunDirectories());
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task LargeArtifactPublication_StreamsAndValidatesTheStagedBytes()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: false);
            var profile = SyntheticProfile.Build();
            var project = BuildProject(database, approved: true);
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 8, RowBatchSize: 8));
            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            var destination = Path.Combine(root, "published.xbrl.json");
            var publisher = new PublicationService(new RunJournal(root));

            var receipt = await publisher.PublishAuthorizedLargeArtifactAsync(destination, artifact, root,
                project, profile, allowSyntheticProfile: true, result,
                (stagedPath, token) => service.ValidateStagedArtifactAsync(stagedPath, destination, result, token),
                CancellationToken.None);

            Assert.Equal(artifact.Sha256, receipt.FinalDigest);
            Assert.Equal(artifact.Sha256, receipt.ValidatedDigest);
            Assert.Equal(artifact.Sha256, result.Manifest.PublishedArtifactDigest);
            Assert.Equal(artifact.Bytes, new FileInfo(destination).Length);
            Assert.Equal(2, worker.ValidationRequests);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task LargeArtifactPublication_WorkerRejectionPreservesPriorDestination()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 40, variedValues: false);
            var profile = SyntheticProfile.Build();
            var project = BuildProject(database, approved: true);
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 8, RowBatchSize: 8));
            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            var destination = Path.Combine(root, "published.xbrl.json");
            await File.WriteAllTextAsync(destination, "prior accepted bytes");
            worker.ValidationStatus = WorkerStatus.DomainFailure;
            var publisher = new PublicationService(new RunJournal(root));

            await Assert.ThrowsAsync<PublicationException>(() => publisher.PublishAuthorizedLargeArtifactAsync(
                destination, artifact, root, project, profile, allowSyntheticProfile: true, result,
                (stagedPath, token) => service.ValidateStagedArtifactAsync(stagedPath, destination, result, token),
                CancellationToken.None));

            Assert.Equal("prior accepted bytes", await File.ReadAllTextAsync(destination));
            Assert.Null(result.Manifest.PublishedArtifactDigest);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task SetPhasePlan_RemainsOnSmallInMemoryPathAndKeepsGroupExecutionAvailable()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: true);
            var project = BuildProject(database);
            var bindingId = project.Bindings[0].BindingId;
            var collect = new MappingNode
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.CollectRows,
                DisplayName = "Collect source rows",
                Parameters = new() { ["binding"] = bindingId },
                OutputPorts = new() { new PortDefinition("items", PortShape.Record, PortCardinality.Collection) }
            };
            var group = new MappingNode
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.GroupBy,
                DisplayName = "Group source rows",
                Parameters = new() { ["keys"] = "Line", ["sumField"] = "Current" },
                InputPorts = new() { new PortDefinition("items", PortShape.Record, PortCardinality.Collection) },
                OutputPorts = new()
                {
                    new PortDefinition("values", PortShape.Decimal, PortCardinality.Collection),
                    new PortDefinition("keys", PortShape.String, PortCardinality.Collection)
                }
            };
            project.Nodes.Add(collect);
            project.Nodes.Add(group);
            project.Edges.Add(new MappingEdge
            {
                EdgeId = Id.New(), SourceNodeId = collect.NodeId, SourcePortName = "items",
                TargetNodeId = group.NodeId, TargetPortName = "items"
            });

            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, new DigestOnlyArtifactWorker(profile),
                executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 20, RowBatchSize: 3));
            var compile = service.Validate(project);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));
            Assert.NotEmpty(compile.Plan!.SetSteps);
            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
            Assert.Null(result.LargeArtifact);
            Assert.Equal(9, result.Facts.Count);
        }
        finally { TryDeleteDirectory(root); }
    }

    private static async Task<JobResult> Run(MappingProject project, string database, FilingProfile profile,
        ExecutionBudget budget, DigestOnlyArtifactWorker? worker = null,
        IProgress<MappingJobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        worker ??= new DigestOnlyArtifactWorker(profile);
        return await new MappingJobService(profile, worker, executionBudget: budget)
            .RunAsync(project, Connections(project, database), new Dictionary<string, string>(),
                preview: false, cancellationToken, progress);
    }

    private static MappingProject BuildProject(string database, bool approved = false)
    {
        var project = TestFixtures.BuildRevenueMappingProject(database);
        var binding = project.Bindings[0];
        project.Bindings[0] = binding with
        {
            Kind = "sqlite",
            Locator = database,
            Configuration = new Dictionary<string, object?>
            {
                ["table"] = "financials",
                ["decimalColumns"] = "Current,Prior"
            }
        };
        if (approved)
        {
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "ENG-01 test approver",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "test-fixture"
            });
        }
        return project;
    }

    private static MappingProject BuildRatioProject(string database)
    {
        var project = BuildProject(database);
        var current = project.Nodes.Single(node => node.DisplayName == "Current revenue");
        var scale = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
        var factConstruct = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
        var bindingId = project.Bindings[0].BindingId;
        var denominator = new MappingNode
        {
            NodeId = Id.New(),
            NodeType = NodeTypeConstants.ExcelField,
            DisplayName = "Prior revenue",
            Parameters = new() { ["bindingId"] = bindingId, ["column"] = "Prior" },
            OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
        };
        var ratio = new MappingNode
        {
            NodeId = Id.New(),
            NodeType = NodeTypeConstants.Ratio,
            DisplayName = "Current / prior ratio",
            Parameters = new() { ["divideByZero"] = "Diagnostic" },
            InputPorts = new()
            {
                new PortDefinition("numerator", PortShape.Decimal, PortCardinality.Single),
                new PortDefinition("denominator", PortShape.Decimal, PortCardinality.Single)
            },
            OutputPorts = new() { new PortDefinition("value", PortShape.Decimal, PortCardinality.Single) }
        };

        project.Nodes.Remove(scale);
        project.Nodes.Add(denominator);
        project.Nodes.Add(ratio);
        project.Edges.RemoveAll(edge => edge.SourceNodeId == scale.NodeId || edge.TargetNodeId == scale.NodeId);
        project.Edges.Add(new MappingEdge
        {
            EdgeId = Id.New(), SourceNodeId = current.NodeId, SourcePortName = "value",
            TargetNodeId = ratio.NodeId, TargetPortName = "numerator"
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = Id.New(), SourceNodeId = denominator.NodeId, SourcePortName = "value",
            TargetNodeId = ratio.NodeId, TargetPortName = "denominator"
        });
        project.Edges.Add(new MappingEdge
        {
            EdgeId = Id.New(), SourceNodeId = ratio.NodeId, SourcePortName = "value",
            TargetNodeId = factConstruct.NodeId, TargetPortName = "value"
        });
        return project;
    }

    private static void AssertTransformationPath(XbrlFact fact, string locator, params string[] expectedIds)
    {
        var path = Assert.Single(fact.SourceTransformationPaths,
            candidate => StringComparer.Ordinal.Equals(candidate.Source.Locator, locator));
        Assert.Equal(expectedIds, path.TransformationIds);
    }

    private static IReadOnlyDictionary<string, SourceConnection> Connections(MappingProject project, string database)
    {
        var binding = project.Bindings[0];
        return new Dictionary<string, SourceConnection>
        {
            [binding.BindingId] = new SourceConnection(binding.BindingId, "sqlite", database,
                new Dictionary<string, string> { ["table"] = "financials", ["decimalColumns"] = "Current,Prior" })
        };
    }

    private static async Task<string> CreateSourceDatabase(string root, int rowCount, bool variedValues)
    {
        var path = Path.Combine(root, "source.db");
        await using var database = new SqliteConnection($"Data Source={path}");
        await database.OpenAsync();
        await using var transaction = database.BeginTransaction();
        await using (var create = database.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TABLE financials (Id INTEGER PRIMARY KEY, Line TEXT NOT NULL, Current TEXT NOT NULL, Prior TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var insert = database.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO financials(Id, Line, Current, Prior) VALUES($id, 'Revenue', $current, '1100');";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var current = insert.Parameters.Add("$current", SqliteType.Text);
            for (var row = 0; row < rowCount; row++)
            {
                id.Value = row + 1;
                current.Value = variedValues ? (1250 + row % 3).ToString(System.Globalization.CultureInfo.InvariantCulture) : "1250";
                await insert.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
        return path;
    }

    private static async Task<string> CreateBinaryFloatSourceDatabase(string root)
    {
        var path = Path.Combine(root, "binary-float-source.db");
        await using var database = new SqliteConnection($"Data Source={path}");
        await database.OpenAsync();
        await using (var create = database.CreateCommand())
        {
            create.CommandText = "CREATE TABLE financials (Id INTEGER PRIMARY KEY, Line TEXT NOT NULL, Current REAL NOT NULL, Prior TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var insert = database.CreateCommand())
        {
            insert.CommandText = "INSERT INTO financials(Id, Line, Current, Prior) VALUES(1, 'Revenue', 1250.5, '1100');";
            await insert.ExecuteNonQueryAsync();
        }
        return path;
    }

    private static void AssertStreamsEqual(Stream expected, Stream actual)
    {
        var expectedBuffer = new byte[64 * 1024];
        var actualBuffer = new byte[64 * 1024];
        while (true)
        {
            var expectedRead = expected.Read(expectedBuffer, 0, expectedBuffer.Length);
            var actualRead = actual.Read(actualBuffer, 0, actualBuffer.Length);
            Assert.Equal(expectedRead, actualRead);
            if (expectedRead == 0) return;
            Assert.True(expectedBuffer.AsSpan(0, expectedRead).SequenceEqual(actualBuffer.AsSpan(0, actualRead)),
                "Streamed large report bytes differed from the in-memory OIM oracle.");
        }
    }

    private static HashSet<string> LargeRunDirectories()
        => Directory.EnumerateDirectories(Path.GetTempPath(), "xms-large-*")
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "xms-eng01-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class DigestOnlyArtifactWorker : IWorkerClient
    {
        private readonly LocalSyntheticWorker _inner;
        public int ValidationRequests { get; private set; }
        public string? LastValidationPath { get; private set; }
        public WorkerStatus ValidationStatus { get; set; } = WorkerStatus.Success;

        public DigestOnlyArtifactWorker(FilingProfile profile) => _inner = new LocalSyntheticWorker(profile);
        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => _inner.HandshakeAsync(cancellationToken);

        public async Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (request.Operation != WorkerOperations.ValidateReport)
                return await _inner.RequestAsync(request, cancellationToken);
            ValidationRequests++;
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Payload is not { } payload)
                return Failure(request, "Missing validation payload.");
            if (payload.ValueKind == JsonValueKind.String)
            {
                return Success(request);
            }
            if (payload.ValueKind != JsonValueKind.Object)
                return Failure(request, "Large report payload must be a path/digest object.");
            var path = payload.GetProperty("xbrlJsonPath").GetString()!;
            var expected = payload.GetProperty("sha256").GetString()!;
            LastValidationPath = path;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var actual = "sha256:" + Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken))
                .ToLowerInvariant();
            return actual == expected
                ? new WorkerResponse(request.OperationId, ValidationStatus,
                    CompletionResult(request, ValidationStatus == WorkerStatus.Success),
                    Array.Empty<Diagnostic>(), ValidationStatus == WorkerStatus.Success ? null : "Synthetic worker rejection.")
                : Failure(request, "Report digest mismatch.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static WorkerResponse Success(WorkerRequest request)
            => new(request.OperationId, WorkerStatus.Success, CompletionResult(request, completed: true),
                Array.Empty<Diagnostic>(), null);

        private static JsonElement CompletionResult(WorkerRequest request, bool completed)
        {
            var requirements = request.ValidationRequirements
                ?? throw new InvalidOperationException("Validation request omitted profile requirements.");
            return JsonSerializer.SerializeToElement(new
            {
                completed,
                validationCompletion = new
                {
                    profileId = requirements.ProfileId,
                    profileDigest = requirements.ProfileDigest,
                    requiredRules = requirements.RequiredRules.Select(identifier => new { identifier, status = "pass" }),
                    requiredPlugins = requirements.RequiredPlugins.Select(identifier => new { identifier, status = "pass" })
                }
            });
        }
        private static WorkerResponse Failure(WorkerRequest request, string error)
            => new(request.OperationId, WorkerStatus.DomainFailure, null, Array.Empty<Diagnostic>(), error);
    }
}
