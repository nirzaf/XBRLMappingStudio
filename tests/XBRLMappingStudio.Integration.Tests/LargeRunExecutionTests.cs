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

            Assert.True(small.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", small.Manifest.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.True(large.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", large.Manifest.Diagnostics.Select(diagnostic => diagnostic.Message)));
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
    public async Task LargeRun_KeepsWalSnapshotForFactsAndFingerprintWhileWriterCommits()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateWalSourceDatabase(root, rowCount: 8);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 2);

            using var baseline = await Run(project, database, profile, budget);
            Assert.Equal(RunOutcome.Success, baseline.Manifest.Outcome);
            var baselineArtifact = Assert.IsType<LargeRunArtifact>(baseline.LargeArtifact);
            var expectedDigest = Assert.Single(baseline.Manifest.Inputs).Digest;

            var writerCommitted = false;
            var progress = new InlineProgress<MappingJobProgress>(stage =>
            {
                if (writerCommitted || stage.Stage != "Executing bounded row batch" || stage.Current != 2)
                    return;

                using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
                writer.Open();
                using var transaction = writer.BeginTransaction();
                using var command = writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE financials SET Current='9000' WHERE Id >= 3; "
                    + "INSERT INTO financials(Id, Line, Current, Prior) VALUES(9, 'Revenue', '7777', '1100');";
                command.ExecuteNonQuery();
                transaction.Commit();
                writerCommitted = true;
            });

            using var observed = await Run(project, database, profile, budget, progress: progress);

            Assert.True(writerCommitted, "The WAL writer did not commit between source batches.");
            Assert.True(observed.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", observed.Manifest.Diagnostics.Select(item => item.Message)));
            var observedArtifact = Assert.IsType<LargeRunArtifact>(observed.LargeArtifact);
            var input = Assert.Single(observed.Manifest.Inputs);
            Assert.Equal(expectedDigest, input.Digest);
            Assert.False(input.IsReplayable);
            Assert.Equal("sqlite-read-transaction", input.SnapshotStrategy);
            Assert.Equal(SqliteSourceAdapter.StablePrimaryKeyIdentityStrategy, input.RowIdentityStrategy);
            Assert.Equal(baselineArtifact.Sha256, observedArtifact.Sha256);
            Assert.Equal(baselineArtifact.Facts.Select(FactSnapshot), observedArtifact.Facts.Select(FactSnapshot));
            Assert.Equal(Enumerable.Range(0, 8).Select(row => (decimal?)((1_250 + row % 3) * 1_000m)),
                observedArtifact.Facts.Select(fact => fact.Value));

            await using var verify = new SqliteConnection(
                $"Data Source={database};Pooling=False;Mode=ReadOnly");
            await verify.OpenAsync();
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM financials";
            Assert.Equal(9L, await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = "SELECT Current FROM financials WHERE Id=3";
            Assert.Equal("9000", await verifyCommand.ExecuteScalarAsync());
        }
        finally { TryDeleteDirectory(root); }
    }

    private static (string SemanticKey, decimal? Value, string SourceRow) FactSnapshot(XbrlFact fact)
        => (fact.SemanticKey(), fact.Value,
            Assert.Single(fact.ContributingSources).DisplayName ?? "");

    [Fact]
    public async Task LargeXmlRun_StreamsEquivalentFactsAndPublishesOnlyTheValidatedXmlArtifact()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2, variedValues: true);
            var project = BuildProject(database, approved: true);
            var profile = SyntheticProfile.Build();
            using var small = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 10, RowBatchSize: 3));
            await using var worker = new LocalSyntheticWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 1));
            using var large = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None,
                outputKind: "xbrl-xml");

            Assert.Equal(RunOutcome.Success, small.Manifest.Outcome);
            Assert.True(large.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", large.Manifest.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var artifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal("xbrl-xml", artifact.OutputKind);
            Assert.EndsWith(".xml", artifact.ReportPath, StringComparison.OrdinalIgnoreCase);
            var identity = Assert.Single(large.Manifest.Outputs);
            Assert.Equal("xbrl-xml", identity.Kind);
            Assert.Equal(artifact.Sha256, identity.Digest);

            var xml = await File.ReadAllTextAsync(artifact.ReportPath);
            var roundTripped = XmlSerializer.ReadFacts(xml, profile);
            Assert.Equal(
                small.Facts.Select(fact => fact.SemanticKey()).Order(StringComparer.Ordinal),
                roundTripped.Select(fact => fact.SemanticKey()).Order(StringComparer.Ordinal));

            var destination = Path.Combine(root, "published-report.xml");
            var publisher = new PublicationService(new RunJournal());
            var receipt = await publisher.PublishAuthorizedLargeArtifactAsync(
                destination, artifact, root, project, profile, allowSyntheticProfile: true,
                result: large,
                validateStagedArtifactAsync: (stagedPath, token) => service.ValidateStagedArtifactAsync(
                    stagedPath, destination, large, token, "xbrl-xml"),
                outputKind: "xbrl-xml");

            Assert.Equal(artifact.Sha256, receipt.FinalDigest);
            Assert.Equal(await File.ReadAllBytesAsync(artifact.ReportPath), await File.ReadAllBytesAsync(destination));
            Assert.Equal(destination, Assert.Single(large.Manifest.Outputs).Path);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task CompoundUnitsSurviveSmallAndLargeFactFolds()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2, variedValues: true);
            var project = BuildProject(database);
            var factConstruct = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            factConstruct.Parameters["concept"] = "synthetic:UnitsPerShare";
            factConstruct.Parameters["unit"] = "USD/shares";
            var profile = AddNumericCompoundUnitConcept(SyntheticProfile.Build());

            await using var smallWorker = new LocalSyntheticWorker(profile);
            using var small = await new MappingJobService(profile, smallWorker,
                    executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 20, RowBatchSize: 3))
                .RunAsync(project, Connections(project, database), new Dictionary<string, string>(),
                    preview: false, CancellationToken.None);
            await using var largeWorker = new LocalSyntheticWorker(profile);
            using var large = await new MappingJobService(profile, largeWorker,
                    executionBudget: new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 1))
                .RunAsync(project, Connections(project, database), new Dictionary<string, string>(),
                    preview: false, CancellationToken.None);

            Assert.True(small.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", small.Manifest.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.True(large.Manifest.Outcome == RunOutcome.Success,
                string.Join(" | ", large.Manifest.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.Equal(2, small.Facts.Count);
            Assert.All(small.Facts, fact => Assert.Equal("USD/shares", fact.CompoundUnit?.CanonicalKey));
            var largeArtifact = Assert.IsType<LargeRunArtifact>(large.LargeArtifact);
            Assert.Equal(2, largeArtifact.Facts.Count);
            Assert.All(largeArtifact.Facts, fact => Assert.Equal("USD/shares", fact.CompoundUnit?.CanonicalKey));
            Assert.All(OimSerializer.ReadFacts(small.XbrlJson),
                fact => Assert.Equal("USD/shares", fact.CompoundUnit?.CanonicalKey));
            Assert.All(OimSerializer.ReadFacts(await File.ReadAllTextAsync(largeArtifact.XbrlJsonPath)),
                fact => Assert.Equal("USD/shares", fact.CompoundUnit?.CanonicalKey));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task LargeSyntheticRun_StreamsLocalRuleCompletionForRunAndStagedArtifact()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2, variedValues: true);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var budget = new ExecutionBudget(InMemorySourceRecordLimit: 1, RowBatchSize: 1);
            await using var worker = new LocalSyntheticWorker(profile);
            var service = new MappingJobService(profile, worker, executionBudget: budget);

            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
            var artifact = Assert.IsType<LargeRunArtifact>(result.LargeArtifact);
            Assert.Equal(2, artifact.FactCount);
            Assert.Equal(artifact.Sha256, result.Manifest.ValidatedArtifactDigest);

            var stagedPath = Path.Combine(root, "staged-large-report.json");
            using (var report = artifact.OpenReportRead())
            using (var staged = File.Create(stagedPath))
                await report.CopyToAsync(staged);
            var validatedDigest = await service.ValidateStagedArtifactAsync(
                stagedPath, Path.Combine(root, "published-report.json"), result);

            Assert.Equal(artifact.Sha256, validatedDigest);
            Assert.Equal(validatedDigest, result.Manifest.ValidatedArtifactDigest);
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
    public async Task LargeRunWorkerFindings_LinkExactDuplicateFactLocatorsToSpooledSources()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: false);
            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            using var small = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 20, RowBatchSize: 3));
            var expectedByLocator = OimSerializer.GetFactLocatorMap(small.Facts);
            var expectedLocators = OimSerializer.GetFactLocators(small.Facts);
            var worker = new DigestOnlyArtifactWorker(profile, emitFactLocatorFindings: true);

            using var large = await Run(project, database, profile,
                new ExecutionBudget(InMemorySourceRecordLimit: 2, RowBatchSize: 3), worker);

            Assert.Equal(RunOutcome.InvalidMapping, large.Manifest.Outcome);
            var linked = large.Manifest.Diagnostics
                .Where(diagnostic => diagnostic.Message.StartsWith("large fact finding", StringComparison.Ordinal))
                .OrderBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, linked.Length);
            Assert.Equal(new[] { expectedLocators[0], expectedLocators[^1] },
                linked.Select(diagnostic => diagnostic.FactLocator));
            foreach (var diagnostic in linked)
            {
                var locator = Assert.IsType<string>(diagnostic.FactLocator);
                var expectedFact = expectedByLocator[locator];
                var expectedPaths = expectedFact.SourceTransformationPaths;
                var actualPaths = Assert.IsAssignableFrom<IReadOnlyList<SourceTransformationPath>>(
                    diagnostic.SourceTransformationPaths);
                Assert.Equal(expectedPaths.Count, actualPaths.Count);
                for (var pathIndex = 0; pathIndex < expectedPaths.Count; pathIndex++)
                {
                    Assert.Equal(expectedPaths[pathIndex].Source, actualPaths[pathIndex].Source);
                    Assert.Equal(expectedPaths[pathIndex].TransformationIds,
                        actualPaths[pathIndex].TransformationIds);
                }
                Assert.Equal(expectedFact.SourceTransformationPaths[0].Source, diagnostic.Source);
            }
            Assert.NotEqual(linked[0].Source, linked[1].Source);
            Assert.Contains(large.Manifest.Diagnostics, diagnostic => diagnostic.Message == "unknown large fact locator"
                && diagnostic.FactLocator == "f-not-in-this-report"
                && diagnostic.Source is null
                && diagnostic.SourceTransformationPaths is null);
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
    public async Task OversizedSqliteCell_FailsBeforeAdapterMaterializationUsingExecutionBudget()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 2, variedValues: false);
            await using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "UPDATE financials SET Current=$value WHERE Id=1";
                command.Parameters.AddWithValue("$value", new string('9', 8_192));
                await command.ExecuteNonQueryAsync();
            }

            var project = BuildProject(database);
            var profile = SyntheticProfile.Build();
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(MaxRowBytes: 1_024));

            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoNumberOutOfBudget && diagnostic.IsBlocking);
            Assert.Equal(0, worker.ValidationRequests);
            Assert.Empty(result.Manifest.Outputs);
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
    public async Task JoinExpansionBudget_BlocksPublicationAndPreservesPriorFile()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: true);
            var project = BuildProject(database);
            var bindingId = project.Bindings[0].BindingId;
            var excel = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
            var scale = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
            var fact = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            project.Nodes.Remove(excel);
            project.Nodes.Remove(scale);
            project.Edges.RemoveAll(edge => edge.SourceNodeId == excel.NodeId
                || edge.TargetNodeId == excel.NodeId
                || edge.SourceNodeId == scale.NodeId
                || edge.TargetNodeId == scale.NodeId);

            var valuePortIndex = fact.InputPorts.FindIndex(port => port.Name == "value");
            fact.InputPorts[valuePortIndex] = fact.InputPorts[valuePortIndex] with
            {
                Cardinality = PortCardinality.Collection
            };
            MappingNode Collect(string displayName) => new()
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.CollectRows,
                DisplayName = displayName,
                Parameters = new() { ["binding"] = bindingId },
                OutputPorts = new() { new PortDefinition("items", PortShape.Record, PortCardinality.Collection) }
            };
            var left = Collect("Collect left rows");
            var right = Collect("Collect right rows");
            var join = new MappingNode
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.Join,
                DisplayName = "Join duplicate source rows",
                Parameters = new()
                {
                    ["leftKey"] = "Line", ["rightKey"] = "Line",
                    ["cardinality"] = "Inner", ["duplicateKey"] = "All"
                },
                InputPorts = new()
                {
                    new PortDefinition("left", PortShape.Record, PortCardinality.Collection),
                    new PortDefinition("right", PortShape.Record, PortCardinality.Collection)
                },
                OutputPorts = new() { new PortDefinition("rows", PortShape.Record, PortCardinality.Collection) }
            };
            var group = new MappingNode
            {
                NodeId = Id.New(), NodeType = NodeTypeConstants.GroupBy,
                DisplayName = "Group bounded join output",
                Parameters = new() { ["keys"] = "Line", ["sumField"] = "Current" },
                InputPorts = new() { new PortDefinition("items", PortShape.Record, PortCardinality.Collection) },
                OutputPorts = new()
                {
                    new PortDefinition("values", PortShape.Decimal, PortCardinality.Collection),
                    new PortDefinition("keys", PortShape.String, PortCardinality.Collection)
                }
            };
            project.Nodes.Add(left);
            project.Nodes.Add(right);
            project.Nodes.Add(join);
            project.Nodes.Add(group);
            project.Edges.AddRange(new[]
            {
                new MappingEdge
                {
                    EdgeId = Id.New(), SourceNodeId = left.NodeId, SourcePortName = "items",
                    TargetNodeId = join.NodeId, TargetPortName = "left"
                },
                new MappingEdge
                {
                    EdgeId = Id.New(), SourceNodeId = right.NodeId, SourcePortName = "items",
                    TargetNodeId = join.NodeId, TargetPortName = "right"
                },
                new MappingEdge
                {
                    EdgeId = Id.New(), SourceNodeId = join.NodeId, SourcePortName = "rows",
                    TargetNodeId = group.NodeId, TargetPortName = "items"
                },
                new MappingEdge
                {
                    EdgeId = Id.New(), SourceNodeId = group.NodeId, SourcePortName = "values",
                    TargetNodeId = fact.NodeId, TargetPortName = "value"
                }
            });
            project.Destination = new DestinationPlan
            {
                Kind = "json", Locator = "published.json", Table = string.Empty,
                Format = "json-records-v1", BusinessKey = new(), WriteMode = "replace"
            };
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "ENG-04 regression fixture",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "test-fixture"
            });

            var priorBytes = Encoding.UTF8.GetBytes("prior accepted output");
            var destination = Path.Combine(root, "published.json");
            await File.WriteAllBytesAsync(destination, priorBytes);
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(MaxJoinExpansionRows: 2));
            var compile = service.Validate(project);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));

            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.InvalidMapping, result.Manifest.Outcome);
            Assert.False(result.IsPublishable);
            var diagnostic = Assert.Single(result.Manifest.Diagnostics,
                item => item.Code == DiagnosticCodes.MapJoinExpansionExceeded);
            Assert.True(diagnostic.IsBlocking);
            Assert.Contains("MaxJoinExpansionRows=2", diagnostic.Message, StringComparison.Ordinal);
            Assert.Single(result.Facts);
            Assert.Equal(2501m, result.Facts[0].Value!.Value);
            Assert.Equal(string.Empty, result.XbrlJson);
            Assert.Empty(result.Manifest.Outputs);
            Assert.Null(result.Manifest.ValidatedArtifactDigest);
            Assert.Equal(0, worker.ValidationRequests);

            await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                project, result, root, allowSyntheticProfile: true));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));

            join.Parameters["fieldConflict"] = "Fail";
            project.Approvals.Clear();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "ENG-04 field conflict regression fixture",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "test-fixture"
            });
            var conflictWorker = new DigestOnlyArtifactWorker(profile);
            var conflictService = new MappingJobService(profile, conflictWorker);
            var conflictCompile = conflictService.Validate(project);
            Assert.True(conflictCompile.Success,
                string.Join(" | ", conflictCompile.Diagnostics.Items.Select(item => item.Message)));

            using var conflictResult = await conflictService.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.InvalidMapping, conflictResult.Manifest.Outcome);
            Assert.False(conflictResult.IsPublishable);
            Assert.Contains(conflictResult.Manifest.Diagnostics,
                item => item.Code == DiagnosticCodes.MapJoinFieldConflict && item.IsBlocking);
            Assert.Single(conflictResult.Facts);
            Assert.Equal(1250m, conflictResult.Facts[0].Value!.Value);
            Assert.Equal(string.Empty, conflictResult.XbrlJson);
            Assert.Empty(conflictResult.Manifest.Outputs);
            Assert.Equal(0, conflictWorker.ValidationRequests);
            await Assert.ThrowsAsync<AdapterException>(() => conflictService.PublishFileDestinationAsync(
                project, conflictResult, root, allowSyntheticProfile: true));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public async Task GroupByBudgetOverflow_BlocksPublicationAndPreservesPriorFile()
    {
        var root = NewDirectory();
        try
        {
            var database = await CreateSourceDatabase(root, 9, variedValues: true);
            var project = BuildProject(database);
            var bindingId = project.Bindings[0].BindingId;
            var excel = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
            var scale = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale);
            var fact = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct);
            project.Nodes.Remove(excel);
            project.Nodes.Remove(scale);
            project.Edges.RemoveAll(edge => edge.SourceNodeId == excel.NodeId
                || edge.TargetNodeId == excel.NodeId
                || edge.SourceNodeId == scale.NodeId
                || edge.TargetNodeId == scale.NodeId);

            var valuePortIndex = fact.InputPorts.FindIndex(port => port.Name == "value");
            fact.InputPorts[valuePortIndex] = fact.InputPorts[valuePortIndex] with
            {
                Cardinality = PortCardinality.Collection
            };
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
                DisplayName = "Group financial values",
                Parameters = new() { ["keys"] = "Id", ["sumField"] = "Current" },
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
            project.Edges.Add(new MappingEdge
            {
                EdgeId = Id.New(), SourceNodeId = group.NodeId, SourcePortName = "values",
                TargetNodeId = fact.NodeId, TargetPortName = "value"
            });
            project.Destination = new DestinationPlan
            {
                Kind = "json", Locator = "published.json", Table = string.Empty,
                Format = "json-records-v1", BusinessKey = new(), WriteMode = "replace"
            };
            var profile = SyntheticProfile.Build();
            project.Approvals.Add(new ApprovalRecord
            {
                SemanticRevision = project.SemanticRevision,
                SemanticDigest = project.ComputeSemanticDigest(),
                ApprovedBy = "ENG-02 regression fixture",
                ApprovedUtc = DateTimeOffset.UtcNow,
                ProfileDigest = profile.Digest,
                Authority = "test-fixture"
            });

            var priorBytes = Encoding.UTF8.GetBytes("prior accepted output");
            var destination = Path.Combine(root, "published.json");
            await File.WriteAllBytesAsync(destination, priorBytes);
            var worker = new DigestOnlyArtifactWorker(profile);
            var service = new MappingJobService(profile, worker,
                executionBudget: new ExecutionBudget(MaxGroupCount: 2));
            var compile = service.Validate(project);
            Assert.True(compile.Success, string.Join(" | ", compile.Diagnostics.Items.Select(item => item.Message)));

            using var result = await service.RunAsync(project, Connections(project, database),
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Equal(RunOutcome.InvalidMapping, result.Manifest.Outcome);
            Assert.False(result.IsPublishable);
            var diagnostic = Assert.Single(result.Manifest.Diagnostics,
                item => item.Code == DiagnosticCodes.MapCollectionBudgetExceeded);
            Assert.True(diagnostic.IsBlocking);
            Assert.Contains("MaxGroupCount=2", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(2, result.Facts.Count);
            Assert.Equal(new[] { 1250m, 1251m },
                result.Facts.Select(item => item.Value!.Value).OrderBy(value => value));
            Assert.Equal(string.Empty, result.XbrlJson);
            Assert.Equal(0, worker.ValidationRequests);

            await Assert.ThrowsAsync<AdapterException>(() => service.PublishFileDestinationAsync(
                project, result, root, allowSyntheticProfile: true));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));

            var memoryWorker = new DigestOnlyArtifactWorker(profile);
            var memoryLimitedService = new MappingJobService(profile, memoryWorker,
                executionBudget: new ExecutionBudget(MaxGroupWorkingBytes: 1));
            using var memoryLimited = await memoryLimitedService.RunAsync(project,
                Connections(project, database), new Dictionary<string, string>(),
                preview: false, CancellationToken.None);

            Assert.False(memoryLimited.IsPublishable);
            Assert.Contains(memoryLimited.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.MapCollectionBudgetExceeded
                && diagnostic.Message.Contains("MaxGroupWorkingBytes=1", StringComparison.Ordinal));
            Assert.Empty(memoryLimited.Facts);
            Assert.Equal(0, memoryWorker.ValidationRequests);
            await Assert.ThrowsAsync<AdapterException>(() => memoryLimitedService.PublishFileDestinationAsync(
                project, memoryLimited, root, allowSyntheticProfile: true));
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));
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

    private static FilingProfile AddNumericCompoundUnitConcept(FilingProfile source)
        => new()
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts.Append(new ConceptMetadata(
                "synthetic:UnitsPerShare", SyntheticProfile.Namespace, "UnitsPerShare",
                "Units per share", "decimal", "duration", IsAbstract: false)).ToArray(),
            SupportedUnits = source.SupportedUnits,
            TaxonomyVersion = source.TaxonomyVersion,
            DimensionDomains = source.DimensionDomains,
            PresentationLinks = source.PresentationLinks,
            CalculationLinks = source.CalculationLinks,
            DefinitionLinks = source.DefinitionLinks,
            OutputFormat = source.OutputFormat,
            NamespaceUris = source.NamespaceUris,
            TaxonomyUrls = source.TaxonomyUrls,
            PackageHashes = source.PackageHashes,
            RequiredPlugins = source.RequiredPlugins,
            RequiredChecks = source.RequiredChecks
        };

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
            Parameters = new()
            {
                ["divideByZero"] = "Diagnostic",
                ["digits"] = "2",
                ["mode"] = "HalfEven"
            },
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

    private static async Task<string> CreateWalSourceDatabase(string root, int rowCount)
    {
        var path = Path.Combine(root, "wal-source.db");
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using (var journalMode = database.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", (string?)await journalMode.ExecuteScalarAsync());
        }

        await using var transaction = database.BeginTransaction();
        await using (var create = database.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TABLE financials (Id INTEGER PRIMARY KEY, Line TEXT NOT NULL, "
                + "Current TEXT NOT NULL, Prior TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }
        await using (var insert = database.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO financials(Id, Line, Current, Prior) "
                + "VALUES($id, 'Revenue', $current, '1100');";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var current = insert.Parameters.Add("$current", SqliteType.Text);
            for (var row = 0; row < rowCount; row++)
            {
                id.Value = row + 1;
                current.Value = (1_250 + row % 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await insert.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
        return path;
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
        private readonly bool _emitFactLocatorFindings;
        public int ValidationRequests { get; private set; }
        public string? LastValidationPath { get; private set; }
        public WorkerStatus ValidationStatus { get; set; } = WorkerStatus.Success;

        public DigestOnlyArtifactWorker(FilingProfile profile, bool emitFactLocatorFindings = false)
        {
            _inner = new LocalSyntheticWorker(profile);
            _emitFactLocatorFindings = emitFactLocatorFindings;
        }
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
            if (actual != expected) return Failure(request, "Report digest mismatch.");

            var diagnostics = _emitFactLocatorFindings
                ? await ReadFactLocatorFindingsAsync(path, request.OperationId, cancellationToken)
                : Array.Empty<Diagnostic>();
            return new WorkerResponse(request.OperationId, ValidationStatus,
                CompletionResult(request, ValidationStatus == WorkerStatus.Success), diagnostics,
                ValidationStatus == WorkerStatus.Success ? null : "Synthetic worker rejection.");
        }

        private static async Task<Diagnostic[]> ReadFactLocatorFindingsAsync(
            string reportPath, string operationId, CancellationToken cancellationToken)
        {
            var report = await File.ReadAllTextAsync(reportPath, cancellationToken);
            var entries = OimSerializer.ReadFactEntries(report);
            var first = Assert.IsType<string>(entries.FirstOrDefault()?.Locator);
            var last = Assert.IsType<string>(entries.LastOrDefault()?.Locator);
            return new[]
            {
                FactFinding("large fact finding 1", first, operationId),
                FactFinding("large fact finding 2", last, operationId),
                FactFinding("unknown large fact locator", "f-not-in-this-report", operationId)
            };
        }

        private static Diagnostic FactFinding(string message, string locator, string operationId) => new()
        {
            Code = DiagnosticCodes.XbrValidationFailed,
            Subsystem = DiagnosticSubsystem.XBRL,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Validation,
            IsBlocking = true,
            Message = message,
            OperationId = operationId,
            FactLocator = locator
        };

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
