using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class SqliteDestinationWorkflowTests : IAsyncLifetime
{
    private string _directory = "";
    private string _workbookPath = "";
    private string _databasePath = "";
    private readonly FilingProfile _profile = SyntheticProfile.Build();

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "xms-sqlite-destination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _workbookPath = IndependentAcceptanceFixtures.Materialize(_directory, "revenue.xlsx");
        _databasePath = Path.Combine(_directory, "destination.db");
        await CreateDestinationDatabase(_databasePath);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_directory, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExcelToSqlite_PreservesExactDecimalAndReplaysDurableReceipt()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
        var receipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);
        var replay = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);

        Assert.Equal(1, receipt.WrittenRows);
        Assert.Equal(receipt.OperationKey, replay.OperationKey);
        Assert.Equal(receipt.IntentFingerprint, replay.IntentFingerprint);
        Assert.Equal(1, replay.WrittenRows);

        await using var database = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT value,decimals,provenance_json FROM facts";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var fact = Assert.Single(result.Facts);
        Assert.Equal(fact.Value!.Value.ToString(CultureInfo.InvariantCulture), reader.GetString(0));
        Assert.Equal(-3, reader.GetInt32(1));
        var sourceLocator = Assert.Single(fact.ContributingSources).Locator;
        using (var provenance = JsonDocument.Parse(reader.GetString(2)))
        {
            var sourceEntry = Assert.Single(provenance.RootElement.EnumerateArray());
            Assert.Equal(sourceLocator, sourceEntry.GetProperty("Locator").GetString());
            var storedPath = Assert.Single(sourceEntry.GetProperty("TransformationPaths").EnumerateArray())
                .EnumerateArray().Select(node => node.GetString()!).ToArray();
            var factPath = Assert.Single(fact.SourceTransformationPaths);
            Assert.Equal(factPath.TransformationIds, storedPath);
            Assert.Contains(project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField).NodeId,
                storedPath);
            Assert.Contains(project.Nodes.Single(node => node.NodeType == NodeTypeConstants.FactConstruct).NodeId,
                storedPath);
        }
        Assert.False(await reader.ReadAsync());

        var receiptOutput = Assert.Single(result.Manifest.Outputs, output => output.Kind == "db-receipt");
        Assert.Equal(1, receiptOutput.RowsWritten);
        Assert.Equal("facts", receiptOutput.Table);
        Assert.Equal(receipt.OperationKey, receiptOutput.OperationKey);
    }

    [Fact]
    public async Task WorkerFailure_CannotReplaceExistingSqliteFactsOrReceipt()
    {
        var project = ConfiguredProject(_workbookPath);
        var acceptedService = NewService();
        var acceptedRun = await acceptedService.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, acceptedRun.Manifest.Outcome);
        await acceptedService.PublishDestinationAsync(project, acceptedRun, _directory,
            allowSyntheticProfile: true);

        var beforeFacts = await ReadFactsAsync(_databasePath);
        var beforeReceipts = await ReadReceiptCountAsync(_databasePath);
        var beforeBytes = await File.ReadAllBytesAsync(_databasePath);
        Assert.Single(beforeFacts);
        Assert.Equal(1, beforeReceipts);

        var failingService = new MappingJobService(_profile, new CrashOnValidationWorker(_profile));
        var failedRun = await failingService.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.OperationalFailure, failedRun.Manifest.Outcome);
        Assert.False(failedRun.IsPublishable);
        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            failingService.PublishDestinationAsync(project, failedRun, _directory,
                allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Equal(beforeFacts, await ReadFactsAsync(_databasePath));
        Assert.Equal(beforeReceipts, await ReadReceiptCountAsync(_databasePath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(_databasePath));
        Assert.DoesNotContain(failedRun.Manifest.Outputs, output => output.Kind == "db-receipt");
    }

    [Fact]
    public async Task PublishSqliteDestination_RejectsEveryNonSuccessOutcomeBeforeDatabaseMutation()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        using var acceptedRun = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, acceptedRun.Manifest.Outcome);
        await service.PublishDestinationAsync(project, acceptedRun, _directory,
            allowSyntheticProfile: true);

        var acceptedFacts = await ReadFactsAsync(_databasePath);
        var acceptedReceiptCount = await ReadReceiptCountAsync(_databasePath);
        var acceptedBytes = await File.ReadAllBytesAsync(_databasePath);
        Assert.Single(acceptedFacts);
        Assert.Equal(1, acceptedReceiptCount);

        using var candidate = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(candidate.IsPublishable);

        var rejectedOutcomes = Enum.GetValues<RunOutcome>()
            .Where(outcome => outcome is not (RunOutcome.Success or RunOutcome.SuccessWithWarnings))
            .ToArray();
        foreach (var outcome in rejectedOutcomes)
        {
            candidate.Manifest.Outcome = outcome;
            Assert.False(candidate.IsPublishable);

            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                service.PublishDestinationAsync(project, candidate, _directory,
                    allowSyntheticProfile: true));

            Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
            Assert.Equal(acceptedFacts, await ReadFactsAsync(_databasePath));
            Assert.Equal(acceptedReceiptCount, await ReadReceiptCountAsync(_databasePath));
            Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(_databasePath));
            Assert.DoesNotContain(candidate.Manifest.Outputs, output => output.Kind == "db-receipt");
        }

        candidate.Manifest.Outcome = RunOutcome.Success;
        candidate.Manifest.IsPreview = true;
        Assert.False(candidate.IsPublishable);
        var previewError = await Assert.ThrowsAsync<AdapterException>(() =>
            service.PublishDestinationAsync(project, candidate, _directory,
                allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, previewError.Code);
        Assert.Equal(acceptedFacts, await ReadFactsAsync(_databasePath));
        Assert.Equal(acceptedReceiptCount, await ReadReceiptCountAsync(_databasePath));
        Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(_databasePath));
        Assert.DoesNotContain(candidate.Manifest.Outputs, output => output.Kind == "db-receipt");
    }

    [Fact]
    public async Task PublishSqliteDestination_RejectsTamperedOrContradictoryReportIdentity()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        using var acceptedRun = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, acceptedRun.Manifest.Outcome);
        await service.PublishDestinationAsync(project, acceptedRun, _directory,
            allowSyntheticProfile: true);

        var acceptedFacts = await ReadFactsAsync(_databasePath);
        var acceptedReceiptCount = await ReadReceiptCountAsync(_databasePath);
        var acceptedBytes = await File.ReadAllBytesAsync(_databasePath);

        using var candidate = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(candidate.IsPublishable);
        var originalOutputs = candidate.Manifest.Outputs;
        var reportArtifact = Assert.Single(originalOutputs, output => output.Kind == "xbrl-json");
        var tamperedArtifact = reportArtifact with { Digest = reportArtifact.Digest + "-tampered" };
        candidate.Manifest.Outputs = originalOutputs
            .Select(output => output == reportArtifact ? tamperedArtifact : output)
            .ToArray();

        var tamperedError = await Assert.ThrowsAsync<PublicationException>(() =>
            service.PublishDestinationAsync(project, candidate, _directory,
                allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, tamperedError.Code);
        Assert.Equal(acceptedFacts, await ReadFactsAsync(_databasePath));
        Assert.Equal(acceptedReceiptCount, await ReadReceiptCountAsync(_databasePath));
        Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(_databasePath));
        Assert.DoesNotContain(candidate.Manifest.Outputs, output => output.Kind == "db-receipt");

        candidate.Manifest.Outputs = originalOutputs.Append(tamperedArtifact).ToArray();
        var duplicateError = await Assert.ThrowsAsync<PublicationException>(() =>
            service.PublishDestinationAsync(project, candidate, _directory,
                allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, duplicateError.Code);
        Assert.Equal(acceptedFacts, await ReadFactsAsync(_databasePath));
        Assert.Equal(acceptedReceiptCount, await ReadReceiptCountAsync(_databasePath));
        Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(_databasePath));
        Assert.DoesNotContain(candidate.Manifest.Outputs, output => output.Kind == "db-receipt");
    }

    [Fact]
    public async Task CancelledSqlitePublicationPreservesPriorFactsAndReceipt()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        using var acceptedRun = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, acceptedRun.Manifest.Outcome);
        await service.PublishDestinationAsync(project, acceptedRun, _directory,
            allowSyntheticProfile: true);

        var beforeFacts = await ReadFactsAsync(_databasePath);
        var beforeReceipts = await ReadReceiptCountAsync(_databasePath);
        var beforeBytes = await File.ReadAllBytesAsync(_databasePath);
        Assert.Single(beforeFacts);
        Assert.Equal(1, beforeReceipts);

        using var cancelledRun = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, cancelledRun.Manifest.Outcome);
        Assert.DoesNotContain(cancelledRun.Manifest.Outputs, output => output.Kind == "db-receipt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PublishDestinationAsync(project, cancelledRun, _directory,
                allowSyntheticProfile: true, cancellationToken: cancellation.Token));

        Assert.Equal(beforeFacts, await ReadFactsAsync(_databasePath));
        Assert.Equal(beforeReceipts, await ReadReceiptCountAsync(_databasePath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(_databasePath));
        Assert.DoesNotContain(cancelledRun.Manifest.Outputs, output => output.Kind == "db-receipt");
    }

    [Fact]
    public async Task MixedPublication_FileSuccessSqliteRollback_IsManifestedAndRetryDoesNotReplaceFile()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        using var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);

        await using (var target = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await target.OpenAsync();
            await using var trigger = target.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_mixed_insert BEFORE INSERT ON facts BEGIN SELECT RAISE(ABORT,'injected mixed failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        var outputPath = Path.Combine(_directory, "mixed-report.json");
        var localStatePath = Path.Combine(_directory, "mixed-state.db");
        var journal = new RunJournal(databasePath: localStatePath);
        var publisher = new PublicationService(journal);
        var coordinator = new MixedPublicationCoordinator(
            new MixedPublicationStore(new LocalApplicationDatabase(localStatePath)));
        var reportBytes = Encoding.UTF8.GetByteCount(result.XbrlJson);
        var intent = MixedPublicationIntent.Create(project, result, outputPath, reportBytes,
            _databasePath, "facts");
        Func<string, CancellationToken, Task<string>> validate = (stagedPath, token) =>
            service.ValidateStagedArtifactAsync(stagedPath, outputPath, result, token);

        var first = await coordinator.PublishAsync(intent,
            token => publisher.PublishAuthorizedAsync(outputPath, Encoding.UTF8.GetBytes(result.XbrlJson),
                _directory, project, _profile, true, result, validate, token,
                refuseDifferentExisting: true),
            (prepared, token) => service.PublishDestinationAsync(project, result, _directory,
                true, token, prepared));

        Assert.IsType<SqliteException>(first.Failure);
        Assert.Equal(DestinationPublicationState.Confirmed, first.Record.FileState);
        Assert.Equal(DestinationPublicationState.Unknown, first.Record.SqliteState);
        Assert.Equal(Encoding.UTF8.GetBytes(result.XbrlJson), await File.ReadAllBytesAsync(outputPath));
        Assert.Empty(await ReadFactsAsync(_databasePath));

        await using (var target = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await target.OpenAsync();
            await using var drop = target.CreateCommand();
            drop.CommandText = "DROP TRIGGER fail_mixed_insert";
            await drop.ExecuteNonQueryAsync();
        }

        var retry = await coordinator.PublishAsync(intent,
            token => throw new InvalidOperationException("A confirmed file must not be replaced on retry."),
            (prepared, token) => service.PublishDestinationAsync(project, result, _directory,
                true, token, prepared));

        Assert.Null(retry.Failure);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.SqliteState);
        Assert.Equal(Encoding.UTF8.GetBytes(result.XbrlJson), await File.ReadAllBytesAsync(outputPath));
        Assert.Single(await ReadFactsAsync(_databasePath));
        Assert.Equal(1, await ReadReceiptCountAsync(_databasePath));
    }

    [Fact]
    public async Task MixedPublication_ReconcilesCommittedSqliteLegAfterCallerLosesReceipt()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        using var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);

        var outputPath = Path.Combine(_directory, "mixed-recovery-report.json");
        var localStatePath = Path.Combine(_directory, "mixed-recovery-state.db");
        var journal = new RunJournal(databasePath: localStatePath);
        var publisher = new PublicationService(journal);
        var store = new MixedPublicationStore(new LocalApplicationDatabase(localStatePath));
        var coordinator = new MixedPublicationCoordinator(store);
        var intent = MixedPublicationIntent.Create(project, result, outputPath,
            Encoding.UTF8.GetByteCount(result.XbrlJson), _databasePath, "facts");
        Func<string, CancellationToken, Task<string>> validate = (stagedPath, token) =>
            service.ValidateStagedArtifactAsync(stagedPath, outputPath, result, token);

        var interrupted = await coordinator.PublishAsync(intent,
            token => publisher.PublishAuthorizedAsync(outputPath, Encoding.UTF8.GetBytes(result.XbrlJson),
                _directory, project, _profile, true, result, validate, token,
                refuseDifferentExisting: true),
            async (prepared, token) =>
            {
                _ = await service.PublishDestinationAsync(project, result, _directory,
                    true, token, prepared);
                throw new IOException("Injected caller interruption after SQLite commit.");
            });

        Assert.IsType<IOException>(interrupted.Failure);
        Assert.Equal(DestinationPublicationState.Confirmed, interrupted.Record.FileState);
        Assert.Equal(DestinationPublicationState.Unknown, interrupted.Record.SqliteState);
        Assert.Single(await ReadFactsAsync(_databasePath));
        Assert.Equal(1, await ReadReceiptCountAsync(_databasePath));
        var publishedFile = await File.ReadAllBytesAsync(outputPath);

        var reconciled = await coordinator.ReconcileAsync(intent.OperationKey);

        Assert.NotNull(reconciled);
        Assert.Equal(DestinationPublicationState.Confirmed, reconciled!.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, reconciled.SqliteState);
        Assert.NotNull(reconciled.SqliteOperationKey);
        Assert.NotNull(reconciled.SqliteIntentFingerprint);
        Assert.Equal(1, reconciled.SqliteRowsWritten);

        var retry = await coordinator.PublishAsync(intent,
            _ => throw new InvalidOperationException("Confirmed destinations must not be published again."),
            (_, _) => throw new InvalidOperationException("A reconciled SQLite receipt must not be replayed."));

        Assert.Null(retry.Failure);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.SqliteState);
        Assert.Equal(publishedFile, await File.ReadAllBytesAsync(outputPath));
        Assert.Single(await ReadFactsAsync(_databasePath));
        Assert.Equal(1, await ReadReceiptCountAsync(_databasePath));
    }

    [Fact]
    public async Task DestinationChangePreviewMatchesInsertCountAndLeavesDatabaseBytesUnchanged()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(_databasePath);

        var estimate = await service.PreviewDestinationChangesAsync(project, result, _directory);

        Assert.Equal(1, estimate.CandidateRows);
        Assert.Equal(1, estimate.InspectedRows);
        Assert.True(estimate.CountsAreComplete);
        Assert.Equal(1, estimate.Inserts);
        Assert.Empty(estimate.SchemaDiscrepancies);
        Assert.Equal(before, await File.ReadAllBytesAsync(_databasePath));

        var receipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);
        Assert.Equal(estimate.Inserts, receipt.WrittenRows);
    }

    [Fact]
    public async Task DestinationChangePreview_CancellationLeavesDatabaseUnchanged()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(_databasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreviewDestinationChangesAsync(project, result, _directory, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(_databasePath));
    }

    [Fact]
    public async Task DestinationChangePreview_ReportsTargetSchemaDriftWithoutMutation()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        await using (var target = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
        {
            await target.OpenAsync();
            await using var command = target.CreateCommand();
            command.CommandText = "ALTER TABLE facts RENAME TO facts_stale";
            await command.ExecuteNonQueryAsync();
        }
        var beforePreview = await File.ReadAllBytesAsync(_databasePath);

        var estimate = await service.PreviewDestinationChangesAsync(project, result, _directory);

        Assert.Single(estimate.SchemaDiscrepancies);
        Assert.Contains("facts", estimate.SchemaDiscrepancies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, estimate.InspectedRows);
        Assert.Equal(beforePreview, await File.ReadAllBytesAsync(_databasePath));
    }

    [Fact]
    public async Task PreviewAndPublish_RejectSourceChangedAfterRunWithoutWritingDatabase()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);
        var before = await File.ReadAllBytesAsync(_databasePath);

        var changedWorkbook = (await File.ReadAllBytesAsync(_workbookPath)).Concat(new byte[] { 0x58 }).ToArray();
        await File.WriteAllBytesAsync(_workbookPath, changedWorkbook);

        var estimate = await service.PreviewDestinationChangesAsync(project, result, _directory);
        Assert.NotEmpty(estimate.SchemaDiscrepancies);
        Assert.Contains(project.Bindings.Single().BindingId, Assert.Single(estimate.SchemaDiscrepancies),
            StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<PublicationException>(() => service.PublishDestinationAsync(
            project, result, _directory, allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(_databasePath));
    }

    [Fact]
    public async Task SqliteSourceChange_BlocksPreviewAndSqlitePublicationAndPreservesDestination()
    {
        var sourcePath = await CreateSqliteSourceAsync("mutable-source.db", hasPrimaryKey: true);
        var project = ConfiguredSqliteSourceProject(sourcePath, fileDestination: false);
        var service = NewService();
        var binding = Assert.Single(project.Bindings);
        var connection = new SourceConnection(binding.BindingId, "sqlite", sourcePath,
            binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal));
        var result = await service.RunAsync(project,
            new Dictionary<string, SourceConnection>(StringComparer.Ordinal) { [binding.BindingId] = connection },
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.True(result.IsPublishable);
        var unchangedPreview = await service.PreviewDestinationChangesAsync(project, result, _directory);
        Assert.Empty(unchangedPreview.SchemaDiscrepancies);
        Assert.Equal(1, unchangedPreview.CandidateRows);
        var stableReceipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);
        Assert.Equal(1, stableReceipt.WrittenRows);
        var beforeChangedSourceAttempt = await File.ReadAllBytesAsync(_databasePath);

        await ChangeSqliteSourceValueAsync(sourcePath, "1350");

        var changedPreview = await service.PreviewDestinationChangesAsync(project, result, _directory);
        var discrepancy = Assert.Single(changedPreview.SchemaDiscrepancies);
        Assert.Contains(binding.BindingId, discrepancy, StringComparison.Ordinal);
        Assert.Contains(DiagnosticCodes.IoInputIdentityChanged, discrepancy, StringComparison.Ordinal);
        Assert.Equal(0, changedPreview.CandidateRows);

        var error = await Assert.ThrowsAsync<PublicationException>(() => service.PublishDestinationAsync(
            project, result, _directory, allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
        Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
        Assert.Equal(beforeChangedSourceAttempt, await File.ReadAllBytesAsync(_databasePath));
    }

    [Fact]
    public async Task SqliteDestinationCannotAliasSourceDatabase()
    {
        var sourcePath = await CreateSqliteSourceAsync("same-source-target.db", hasPrimaryKey: true);
        await CreateDestinationDatabase(sourcePath);
        var project = ConfiguredSqliteSourceProject(sourcePath, fileDestination: false,
            destinationPath: sourcePath);
        var service = NewService();
        var binding = Assert.Single(project.Bindings);
        var connection = new SourceConnection(binding.BindingId, "sqlite", sourcePath,
            binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal));
        var result = await service.RunAsync(project,
            new Dictionary<string, SourceConnection>(StringComparer.Ordinal) { [binding.BindingId] = connection },
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);
        var before = await File.ReadAllBytesAsync(sourcePath);

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            service.PublishDestinationAsync(project, result, _directory, allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(sourcePath));
    }

    [Fact]
    public async Task SqliteDestinationCannotAliasSourceDatabaseThroughSymbolicLink()
    {
        if (OperatingSystem.IsWindows()) return; // Symlink creation requires privileges on some Windows hosts.

        var sourcePath = await CreateSqliteSourceAsync("same-source-symlink-target.db", hasPrimaryKey: true);
        await CreateDestinationDatabase(sourcePath);
        var destinationAlias = Path.Combine(_directory, "same-source-destination-link.db");
        File.CreateSymbolicLink(destinationAlias, sourcePath);
        var project = ConfiguredSqliteSourceProject(sourcePath, fileDestination: false,
            destinationPath: destinationAlias);
        var service = NewService();
        var binding = Assert.Single(project.Bindings);
        var connection = new SourceConnection(binding.BindingId, "sqlite", sourcePath,
            binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal));
        var result = await service.RunAsync(project,
            new Dictionary<string, SourceConnection>(StringComparer.Ordinal) { [binding.BindingId] = connection },
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);
        var before = await File.ReadAllBytesAsync(sourcePath);

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            service.PublishDestinationAsync(project, result, _directory, allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(sourcePath));
    }

    [Fact]
    public async Task SqliteSourceChange_BlocksFilePublicationAndPreservesPriorOutput()
    {
        var sourcePath = await CreateSqliteSourceAsync("mutable-file-source.db", hasPrimaryKey: true);
        var project = ConfiguredSqliteSourceProject(sourcePath, fileDestination: true);
        var service = NewService();
        var binding = Assert.Single(project.Bindings);
        var connection = new SourceConnection(binding.BindingId, "sqlite", sourcePath,
            binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal));
        var result = await service.RunAsync(project,
            new Dictionary<string, SourceConnection>(StringComparer.Ordinal) { [binding.BindingId] = connection },
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);

        var stableOutput = await service.PublishFileDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true, CancellationToken.None);
        Assert.Equal("xlsx", stableOutput.Kind);
        var priorOutput = await File.ReadAllBytesAsync(stableOutput.Path);
        await ChangeSqliteSourceValueAsync(sourcePath, "1350");

        var error = await Assert.ThrowsAsync<PublicationException>(() => service.PublishFileDestinationAsync(
            project, result, _directory, allowSyntheticProfile: true, CancellationToken.None));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
        Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
        Assert.Equal(priorOutput, await File.ReadAllBytesAsync(stableOutput.Path));
    }

    [Fact]
    public async Task PositionalSqliteSource_FailsClosedForSavedResultReuse()
    {
        var sourcePath = await CreateSqliteSourceAsync("positional-source.db", hasPrimaryKey: false);
        var project = ConfiguredSqliteSourceProject(sourcePath, fileDestination: false);
        var service = NewService();
        var binding = Assert.Single(project.Bindings);
        var connection = new SourceConnection(binding.BindingId, "sqlite", sourcePath,
            binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal));
        var result = await service.RunAsync(project,
            new Dictionary<string, SourceConnection>(StringComparer.Ordinal) { [binding.BindingId] = connection },
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);
        var before = await File.ReadAllBytesAsync(_databasePath);

        var preview = await service.PreviewDestinationChangesAsync(project, result, _directory);
        var discrepancy = Assert.Single(preview.SchemaDiscrepancies);
        Assert.Contains(binding.BindingId, discrepancy, StringComparison.Ordinal);
        Assert.Contains("primary key", discrepancy, StringComparison.OrdinalIgnoreCase);
        var error = await Assert.ThrowsAsync<PublicationException>(() => service.PublishDestinationAsync(
            project, result, _directory, allowSyntheticProfile: true));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, error.Code);
        Assert.Contains(binding.BindingId, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(_databasePath));
    }

    [Fact]
    public async Task XbrlReportToSqlite_PreservesFactValuePeriodAndReportProvenance()
    {
        var reportPath = IndependentAcceptanceFixtures.Materialize(_directory, "revenue-report.json");
        var source = new SourceConnection("report", "xbrl-report", reportPath);
        var adapter = new XbrlReportAdapter();
        var schema = await adapter.InspectSchemaAsync(source, CancellationToken.None);
        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(source, schema, CancellationToken.None))
            rows.Add(row);
        var reportRow = Assert.Single(rows);
        Assert.Equal("1250000.0000", reportRow["value"].LexicalSource);

        var facts = OimSerializer.ReadFacts(await File.ReadAllTextAsync(reportPath));
        var fact = Assert.Single(facts);
        facts = new[]
        {
            new XbrlFact
            {
                ConceptName = fact.ConceptName,
                EntityScheme = fact.EntityScheme,
                EntityIdentifier = fact.EntityIdentifier,
                Period = fact.Period,
                Unit = fact.Unit,
                CompoundUnit = fact.CompoundUnit,
                Value = fact.Value,
                TextValue = fact.TextValue,
                IsNil = fact.IsNil,
                Decimals = fact.Decimals,
                Language = fact.Language,
                Dimensions = fact.Dimensions,
                ContributingSources = new[] { new SourceReference("report", "report#f1") }
            }
        };

        var project = ConfiguredProject(_workbookPath);
        var json = OimSerializer.Serialize(_profile, facts);
        var result = PublishableResult(project, facts, json,
            new InputIdentity("report", "xbrl-report", RunManifest.ComputeSha256(await File.ReadAllBytesAsync(reportPath)),
                IsReplayable: true, OriginalPath: reportPath, RowIdentityStrategy: "report-fact-index"));
        var service = NewService();

        var receipt = await service.PublishDestinationAsync(project, result, _directory,
            allowSyntheticProfile: true);

        Assert.Equal(1, receipt.WrittenRows);
        await using var database = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT value,period_start,period_end,provenance_json FROM facts";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("1250000.0000", reader.GetString(0));
        Assert.Equal("2025-01-01", reader.GetString(1));
        Assert.Equal("2026-01-01", reader.GetString(2));
        Assert.Contains("report#f1", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedReportOccurrences_PublishDistinctExcelAndSqliteRowsAgainstOccurrenceOracle()
    {
        var selectedPeriod = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var nextPeriod = XbrlPeriod.OfDuration(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var axis = SyntheticProfile.Qualified("RegionAxis");
        var north = SyntheticProfile.Qualified("NorthMember");
        var south = SyntheticProfile.Qualified("SouthMember");
        var sourceFacts = new[]
        {
            ReverseFact(100.00m, selectedPeriod, "QAR", axis, north),
            ReverseFact(100.00m, selectedPeriod, "QAR", axis, north),
            ReverseFact(125.00m, nextPeriod, "QAR", axis, north),
            ReverseFact(100.00m, selectedPeriod, "USD", axis, north),
            ReverseFact(100.00m, selectedPeriod, "QAR", axis, south)
        };
        var reportPath = Path.Combine(_directory, "repeated-report.xbrl.xml");
        await File.WriteAllTextAsync(reportPath, XmlSerializer.Serialize(_profile, sourceFacts));

        // Hand-authored occurrence oracle: only the first two report facts have every
        // selected aspect; repeats remain two occurrences, never one set member.
        var occurrenceOracle = new[]
        {
            new { Locator = "report#f1", Period = "2025-01-01/2026-01-01", Unit = "QAR", Member = north, Value = "100.00" },
            new { Locator = "report#f2", Period = "2025-01-01/2026-01-01", Unit = "QAR", Member = north, Value = "100.00" }
        };
        var selector = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["concept"] = SyntheticProfile.Qualified("Revenue"),
            ["period"] = selectedPeriod.ToIsoString(),
            ["unit"] = "QAR",
            ["dimensionAxis"] = axis,
            ["dimensionMember"] = north
        };
        var reportAdapter = new XbrlReportAdapter();
        var reportConnection = new SourceConnection("report", reportAdapter.Kind, reportPath, selector);
        var reportSchema = await reportAdapter.InspectSchemaAsync(reportConnection, CancellationToken.None);
        var selectedRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in reportAdapter.ReadFullAsync(reportConnection, reportSchema, CancellationToken.None))
            selectedRows.Add(row);

        Assert.Equal(occurrenceOracle.Select(item => item.Locator), selectedRows.Select(row => row.RowId));
        Assert.Equal(occurrenceOracle.Select(item => item.Period), selectedRows.Select(row => row["period"].AsString()));
        Assert.Equal(occurrenceOracle.Select(item => item.Unit), selectedRows.Select(row => row["unit"].AsString()));
        Assert.Equal(occurrenceOracle.Select(item => item.Value), selectedRows.Select(row => row["value"].LexicalSource));
        Assert.Equal(occurrenceOracle.Select(item => item.Locator), selectedRows.Select(row => row["occurrenceId"].AsString()));
        Assert.All(selectedRows, row => Assert.Contains(north, row["dimensionsJson"].AsString(), StringComparison.Ordinal));

        // Exercise adapter rows directly through the existing CSV-like target contracts.
        // The occurrence ID is a selected source row identity, so duplicates are upserted
        // separately without deriving or inventing ledger records.
        var outputColumns = reportSchema.Columns.Select(column => column.Name).ToArray();
        var directExcelPath = Path.Combine(_directory, "reverse-source-rows.xlsx");
        var directExcel = new ExcelOutputAdapter();
        var directExcelConnection = new SourceConnection("report-output", "excel", directExcelPath);
        var directExcelPayload = directExcel.WriteWorkbook(outputColumns,
            selectedRows.Select(row => outputColumns.Select(column => ToExcelOutputValue(row[column]))));
        var directExcelStage = await directExcel.StageArtifactAsync(directExcelConnection, directExcelPayload,
            ".xlsx", CancellationToken.None);
        await directExcel.PublishAsync(directExcelStage, directExcelConnection, CancellationToken.None);
        var directExcelReader = new ExcelAdapter();
        var directExcelSchema = await directExcelReader.InspectSchemaAsync(
            new SourceConnection("report-output", "excel", directExcelPath), CancellationToken.None);
        var directExcelRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in directExcelReader.ReadFullAsync(
                           new SourceConnection("report-output", "excel", directExcelPath), directExcelSchema, CancellationToken.None))
            directExcelRows.Add(row);
        Assert.Equal(occurrenceOracle.Length, directExcelRows.Count);
        Assert.Equal(occurrenceOracle.Select(item => item.Locator), directExcelRows.Select(row => row["occurrenceId"].AsString()));

        var sqliteTarget = new SourceConnection("report-output", "sqlite", _databasePath,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = "report_occurrences" });
        var directSqlite = await new SqliteTargetPublisher().PublishAsync(sqliteTarget, selectedRows,
            new[] { "occurrenceId" }, "xms-report-occurrences", "sha256:report-occurrences-fixture");
        Assert.Equal(occurrenceOracle.Length, directSqlite.WrittenRows);
        await using (var reportDatabase = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly"))
        {
            await reportDatabase.OpenAsync();
            await using var command = reportDatabase.CreateCommand();
            command.CommandText = "SELECT occurrenceId,period,unit,dimensionsJson FROM report_occurrences ORDER BY occurrenceId";
            await using var reader = await command.ExecuteReaderAsync();
            var outputOccurrences = new List<string>();
            while (await reader.ReadAsync())
            {
                outputOccurrences.Add(reader.GetString(0));
                Assert.Equal(selectedPeriod.ToIsoString(), reader.GetString(1));
                Assert.Equal("QAR", reader.GetString(2));
                Assert.Contains(north, reader.GetString(3), StringComparison.Ordinal);
            }
            Assert.Equal(occurrenceOracle.Select(item => item.Locator), outputOccurrences);
        }

        var selectedLocators = occurrenceOracle.Select(item => item.Locator).ToHashSet(StringComparer.Ordinal);
        var parsedSourceFacts = XmlSerializer.ReadFacts(await File.ReadAllTextAsync(reportPath));
        var selectedFacts = parsedSourceFacts.Select((fact, index) => (fact, index))
            .Where(item => selectedLocators.Contains($"report#f{item.index + 1}"))
            .Select(item => CopyWithSource(item.fact, "report", $"report#f{item.index + 1}"))
            .ToArray();

        Assert.Equal(occurrenceOracle.Length, selectedFacts.Length);
        var selectedRecords = XbrlFactRecordMapper.ToRecords(selectedFacts, _profile.Digest);
        Assert.Equal(occurrenceOracle.Length,
            selectedRecords.Select(record => record["fact_key"].AsString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(occurrenceOracle.Select(item => item.Locator).Order(StringComparer.Ordinal),
            selectedRecords.SelectMany(record => ReadProvenanceLocators(record["provenance_json"].AsString())).Order(StringComparer.Ordinal));
        var keysByLocator = selectedRecords.ToDictionary(
            record => Assert.Single(ReadProvenanceLocators(record["provenance_json"].AsString())),
            record => record["fact_key"].AsString(), StringComparer.Ordinal);
        var reorderedRecords = XbrlFactRecordMapper.ToRecords(selectedFacts.Reverse().ToArray(), _profile.Digest);
        var reorderedKeysByLocator = reorderedRecords.ToDictionary(
            record => Assert.Single(ReadProvenanceLocators(record["provenance_json"].AsString())),
            record => record["fact_key"].AsString(), StringComparer.Ordinal);
        Assert.Equal(keysByLocator.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            reorderedKeysByLocator.OrderBy(pair => pair.Key, StringComparer.Ordinal));

        var sqliteProject = ReversePublicationProject("sqlite", "destination.db", reportPath, selector);
        var sqliteResult = PublishableResult(sqliteProject, selectedFacts,
            OimSerializer.Serialize(_profile, selectedFacts), new InputIdentity("report", "xbrl-report",
                RunManifest.ComputeSha256(await File.ReadAllBytesAsync(reportPath)), true, reportPath, "report-fact-index"));
        var sqliteReceipt = await NewService().PublishDestinationAsync(sqliteProject, sqliteResult, _directory,
            allowSyntheticProfile: true);
        Assert.Equal(occurrenceOracle.Length, sqliteReceipt.WrittenRows);

        await using (var database = new SqliteConnection($"Data Source={_databasePath};Pooling=False;Mode=ReadOnly"))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "SELECT fact_key,provenance_json,period_start,period_end FROM facts ORDER BY fact_key";
            await using var reader = await command.ExecuteReaderAsync();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var locators = new List<string>();
            while (await reader.ReadAsync())
            {
                keys.Add(reader.GetString(0));
                using var provenance = JsonDocument.Parse(reader.GetString(1));
                locators.Add(provenance.RootElement[0].GetProperty("Locator").GetString()!);
                Assert.Equal("2025-01-01", reader.GetString(2));
                Assert.Equal("2026-01-01", reader.GetString(3));
            }
            Assert.Equal(occurrenceOracle.Length, keys.Count);
            Assert.Equal(occurrenceOracle.Select(item => item.Locator).Order(StringComparer.Ordinal),
                locators.Order(StringComparer.Ordinal));
        }

        var excelProject = ReversePublicationProject("excel", "reverse-occurrences.xlsx", reportPath, selector);
        var excelResult = PublishableResult(excelProject, selectedFacts,
            OimSerializer.Serialize(_profile, selectedFacts), new InputIdentity("report", "xbrl-report",
                RunManifest.ComputeSha256(await File.ReadAllBytesAsync(reportPath)), true, reportPath, "report-fact-index"));
        var excelReceipt = await NewService().PublishFileDestinationAsync(excelProject, excelResult, _directory,
            allowSyntheticProfile: true, CancellationToken.None);
        Assert.Equal("xlsx", excelReceipt.Kind);

        var excelAdapter = new ExcelAdapter();
        var excelConnection = new SourceConnection("reverse-output", "excel", excelReceipt.Path);
        var excelSchema = await excelAdapter.InspectSchemaAsync(excelConnection, CancellationToken.None);
        var excelRows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in excelAdapter.ReadFullAsync(excelConnection, excelSchema, CancellationToken.None))
            excelRows.Add(row);
        Assert.Equal(occurrenceOracle.Length, excelRows.Count);
        Assert.Equal(occurrenceOracle.Length,
            excelRows.Select(row => row["fact_key"].AsString()).Distinct(StringComparer.Ordinal).Count());
        var excelLocators = excelRows.SelectMany(row =>
            ReadProvenanceLocators(row["provenance_json"].AsString())).Order(StringComparer.Ordinal);
        Assert.Equal(occurrenceOracle.Select(item => item.Locator).Order(StringComparer.Ordinal), excelLocators);
    }

    [Fact]
    public async Task PreviewPublicationIsRejectedWithoutChangingDatabaseBytes()
    {
        var project = ConfiguredProject(_workbookPath);
        var service = NewService();
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: true, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(_databasePath);

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            service.PublishDestinationAsync(project, result, _directory, allowSyntheticProfile: true));

        var after = await File.ReadAllBytesAsync(_databasePath);
        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, error.Code);
        Assert.Equal(before, after);
    }

    private MappingProject ConfiguredProject(string workbookPath)
    {
        var project = TestFixtures.BuildRevenueMappingProject(workbookPath);
        project.Destination = new DestinationPlan
        {
            Kind = DestinationPlanValidator.SupportedKind,
            Locator = "destination.db",
            Table = "facts",
            BusinessKey = new List<string> { "fact_key" }
        };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = _profile.Digest
        });
        return project;
    }

    private MappingProject ConfiguredSqliteSourceProject(
        string sourcePath,
        bool fileDestination,
        string? destinationPath = null)
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var binding = Assert.Single(project.Bindings);
        project.Bindings[0] = binding with
        {
            Kind = "sqlite",
            Locator = sourcePath,
            Configuration = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["table"] = "financials",
                ["decimalColumns"] = "Current,Prior"
            }
        };
        project.Destination = fileDestination
            ? new DestinationPlan
            {
                Kind = DestinationPlanValidator.ExcelKind,
                Locator = "sqlite-source-output.xlsx",
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "xlsx-records-v1",
                WriteMode = "replace"
            }
            : new DestinationPlan
            {
                Kind = DestinationPlanValidator.SupportedKind,
                Locator = destinationPath ?? "destination.db",
                Table = "facts",
                BusinessKey = new List<string> { "fact_key" }
            };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = _profile.Digest
        });
        return project;
    }

    private async Task<string> CreateSqliteSourceAsync(string name, bool hasPrimaryKey)
    {
        var path = Path.Combine(_directory, name);
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = hasPrimaryKey
            ? "CREATE TABLE financials (Line TEXT PRIMARY KEY, Current TEXT, Prior TEXT);"
            : "CREATE TABLE financials (Line TEXT, Current TEXT, Prior TEXT);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO financials VALUES ('Revenue','1250','1100');";
        await command.ExecuteNonQueryAsync();
        return path;
    }

    private static async Task ChangeSqliteSourceValueAsync(string path, string value)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "UPDATE financials SET Current=$value WHERE Line='Revenue';";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private MappingJobService NewService() => new(_profile, new LocalSyntheticWorker(_profile));

    private sealed class CrashOnValidationWorker : IWorkerClient
    {
        private readonly LocalSyntheticWorker _inner;

        public CrashOnValidationWorker(FilingProfile profile)
            => _inner = new LocalSyntheticWorker(profile);

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => _inner.HandshakeAsync(cancellationToken);

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
            => request.Operation == WorkerOperations.ValidateReport
                ? Task.FromResult(new WorkerResponse(request.OperationId, WorkerStatus.Crash,
                    Result: null, Array.Empty<Diagnostic>(), Error: "Injected validation worker crash."))
                : _inner.RequestAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static IReadOnlyDictionary<string, SourceConnection> Connections(MappingProject project, string workbookPath)
    {
        var binding = Assert.Single(project.Bindings);
        var configuration = binding.Configuration.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.ToString() ?? "",
            StringComparer.Ordinal);
        return new Dictionary<string, SourceConnection>(StringComparer.Ordinal)
        {
            [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbookPath, configuration)
        };
    }

    private JobResult PublishableResult(
        MappingProject project,
        IReadOnlyList<XbrlFact> facts,
        string json,
        InputIdentity input)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var manifest = new RunManifest
        {
            RunId = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid().ToString("N"),
            ProjectId = project.ProjectId,
            SemanticRevision = project.SemanticRevision,
            ProjectSemanticDigest = project.ComputeSemanticDigest(),
            ProfileDigest = _profile.Digest,
            EngineVersion = "fixture",
            StartedUtc = DateTimeOffset.UtcNow,
            Inputs = new[] { input },
            Outputs = new[]
            {
                new OutputIdentity("<memory>", RunManifest.ComputeSha256(bytes), bytes.Length, "xbrl-json")
            }
        };
        manifest.Outcome = RunOutcome.Success;
        return new JobResult(manifest, facts, json, new DiagnosticBag(), Array.Empty<XBRLMappingStudio.Engine.SampledTotal>());
    }

    private MappingProject ReversePublicationProject(
        string destinationKind,
        string destinationLocator,
        string reportPath,
        IReadOnlyDictionary<string, string> selection)
    {
        var project = TestFixtures.BuildRevenueMappingProject(reportPath);
        var binding = Assert.Single(project.Bindings);
        project.Bindings[0] = binding with
        {
            Kind = "xbrl-report",
            Locator = Path.GetFileName(reportPath),
            Configuration = selection.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)
        };
        project.Destination = destinationKind switch
        {
            "sqlite" => new DestinationPlan
            {
                Kind = DestinationPlanValidator.SupportedKind,
                Locator = destinationLocator,
                Table = "facts",
                BusinessKey = new List<string> { "fact_key" }
            },
            "excel" => new DestinationPlan
            {
                Kind = DestinationPlanValidator.ExcelKind,
                Locator = destinationLocator,
                Table = string.Empty,
                BusinessKey = new List<string>(),
                Format = "xlsx-records-v1",
                WriteMode = "replace"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(destinationKind))
        };
        project.Approvals.Clear();
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = _profile.Digest
        });
        return project;
    }

    private static XbrlFact ReverseFact(decimal value, XbrlPeriod period, string unit, string axis, string member)
        => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = period,
            Unit = new XbrlUnit(unit),
            Value = value,
            Decimals = -2,
            Dimensions = new[] { new Dimension(axis, member) }
        };

    private static object? ToExcelOutputValue(XBRLMappingStudio.Engine.DataValue value)
        => value.Presence switch
        {
            XBRLMappingStudio.Engine.ValuePresence.Missing or XBRLMappingStudio.Engine.ValuePresence.ExplicitNil => null,
            XBRLMappingStudio.Engine.ValuePresence.Blank => string.Empty,
            _ when value.Raw is decimal number => value.LexicalSource ?? number.ToString(CultureInfo.InvariantCulture),
            _ => value.Raw
        };

    private static XbrlFact CopyWithSource(XbrlFact fact, string sourceId, string locator)
        => new()
        {
            ConceptName = fact.ConceptName,
            EntityScheme = fact.EntityScheme,
            EntityIdentifier = fact.EntityIdentifier,
            Period = fact.Period,
            Unit = fact.Unit,
            CompoundUnit = fact.CompoundUnit,
            Value = fact.Value,
            TextValue = fact.TextValue,
            IsNil = fact.IsNil,
            Decimals = fact.Decimals,
            Language = fact.Language,
            Dimensions = fact.Dimensions,
            ContributingSources = new[] { new SourceReference(sourceId, locator) }
        };

    private static IReadOnlyList<string> ReadProvenanceLocators(string provenanceJson)
    {
        using var document = JsonDocument.Parse(provenanceJson);
        return document.RootElement.EnumerateArray()
            .Select(source => source.GetProperty("Locator").GetString()!)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadFactsAsync(string path)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT fact_key FROM facts ORDER BY fact_key";
        await using var reader = await command.ExecuteReaderAsync();
        var keys = new List<string>();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static async Task<int> ReadReceiptCountAsync(string path)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM xms_idempotency_receipts";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task CreateDestinationDatabase(string path)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE facts (
                fact_key TEXT NOT NULL PRIMARY KEY,
                profile_digest TEXT NOT NULL,
                concept TEXT NOT NULL,
                entity_scheme TEXT NOT NULL,
                entity_identifier TEXT NOT NULL,
                period_kind TEXT NOT NULL,
                instant_date TEXT NULL,
                period_start TEXT NULL,
                period_end TEXT NULL,
                unit TEXT NULL,
                compound_unit TEXT NULL,
                value TEXT NULL,
                text_value TEXT NULL,
                is_nil INTEGER NOT NULL,
                decimals INTEGER NULL,
                language TEXT NULL,
                dimensions_json TEXT NOT NULL,
                provenance_json TEXT NOT NULL
            );
            CREATE TABLE report_occurrences (
                occurrenceId TEXT NOT NULL PRIMARY KEY,
                concept TEXT NOT NULL,
                entityScheme TEXT NOT NULL,
                entityIdentifier TEXT NOT NULL,
                period TEXT NOT NULL,
                unit TEXT NULL,
                compoundUnit TEXT NULL,
                value TEXT NULL,
                textValue TEXT NULL,
                decimals TEXT NOT NULL,
                lang TEXT NULL,
                nil INTEGER NOT NULL,
                dimensions TEXT NOT NULL,
                dimensionsJson TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }
}
