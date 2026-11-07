using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// OPS01: run the compiled CLI (xbrl-studio) as a subprocess and confirm the
/// emitted xBRL-JSON matches the in-process execution byte-for-byte (modulo
/// per-run fact identifiers). Also verifies exit-code contract.
/// </summary>
public sealed class CliParityTests : IAsyncLifetime
{
    private string _dir = "";
    private string _workbookPath = "";

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-cli-" + Guid.NewGuid().ToString("N"));
        _workbookPath = IndependentAcceptanceFixtures.Materialize(_dir, "revenue.xlsx");
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // The CLI is launched as a real subprocess. We run the framework-dependent
    // apphost the SDK emits next to xbrl-studio.dll (xbrl-studio.exe on Windows,
    // xbrl-studio on Unix) so we never depend on locating the `dotnet` muxer —
    // on the Windows vstest host Process.MainModule is testhost.exe, not dotnet,
    // so invoking `dotnet <dll>` via MainModule breaks there. Falls back to
    // `dotnet <dll>` (resolved on PATH) when no apphost is present.
    private static (string FileName, bool IsAppHost) LocateCli()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.Name
            ?? throw new InvalidOperationException("Unable to determine the active test build configuration.");
        var binRoot = Path.Combine(repoRoot, "src", "XBRLMappingStudio.CLI", "bin", configuration);

        string? PickDll() =>
            Directory.EnumerateFiles(binRoot, "xbrl-studio.dll", SearchOption.AllDirectories)
                .OrderBy(p => p.Contains("Release") ? 0 : 1)
                .FirstOrDefault();

        var adjacentDll = Path.Combine(AppContext.BaseDirectory, "xbrl-studio.dll");
        var dll = File.Exists(adjacentDll) ? adjacentDll : PickDll();
        if (dll is null)
            throw new FileNotFoundException("xbrl-studio.dll not built.");

        var appHost = Path.Combine(Path.GetDirectoryName(dll)!,
            OperatingSystem.IsWindows() ? "xbrl-studio.exe" : "xbrl-studio");
        return File.Exists(appHost) ? (appHost, true) : (dll, false);
    }

    private static ProcessStartInfo BuildStartInfo(
        (string FileName, bool IsAppHost) cli, string verb, params string[] extra)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (cli.IsAppHost)
        {
            psi.FileName = cli.FileName;
        }
        else
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(cli.FileName);
        }
        psi.ArgumentList.Add(verb);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task Cli_ExecuteMatchesInProcess_WhenGivenSavedProject()
    {
        // A production (non-preview) execute must clear the EG03/XG01 publication gate:
        // an approved project plus an explicit synthetic-profile acknowledgment.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "revenue.xmap");
        var pm = new ProjectManager(projectPath);
        pm.Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "cli-out.json");
        var manifestPath = Path.Combine(_dir, "cli-manifest.json");

        var psi = BuildStartInfo(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("CLI process failed to start.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0,
            $"Expected exit 0 but got {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.True(File.Exists(outputPath),
            $"Expected output file at {outputPath}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        // Reopen and compare with the in-process service result.
        var cliJson = await File.ReadAllTextAsync(outputPath);
        var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
        Assert.Empty(IndependentAcceptanceFixtures.CompareSerializedOim(mappingOracle, cliJson));
        Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle,
            Assert.Single(OimSerializer.ReadFacts(cliJson)), requireSourceEvidence: false));

        var profile = SyntheticProfile.Build();
        var inProcess = await new MappingJobService(profile, new LocalSyntheticWorker(profile)).RunAsync(
            project,
            new Dictionary<string, SourceConnection>
            {
                [project.Bindings[0].BindingId] = new SourceConnection(
                    project.Bindings[0].BindingId,
                    project.Bindings[0].Kind,
                    _workbookPath,
                    project.Bindings[0].Configuration.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value?.ToString() ?? "",
                        StringComparer.Ordinal))
            },
            new Dictionary<string, string>(),
            preview: false,
            CancellationToken.None);
        Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, Assert.Single(inProcess.Facts)));
        Assert.Empty(IndependentAcceptanceFixtures.CompareSerializedOim(mappingOracle, inProcess.XbrlJson));

        using var doc = JsonDocument.Parse(cliJson);
        Assert.True(doc.RootElement.TryGetProperty("facts", out var facts));
        var first = facts.EnumerateObject().First();
        var actual = first.Value;
        var dimensions = actual.GetProperty("dimensions");
        Assert.Equal("synthetic:Revenue", dimensions.GetProperty("concept").GetString());
        Assert.Equal("1250000", actual.GetProperty("value").GetString());
        Assert.Equal("iso4217:QAR", dimensions.GetProperty("unit").GetString());
        Assert.Equal(-3, actual.GetProperty("decimals").GetInt32());
    }

    [Fact]
    public async Task Cli_InspectSourceReportsSqliteIdentityDecimalsAndReadOnlyCapabilities()
    {
        var databasePath = Path.Combine(_dir, "source.sqlite");
        await using (var database = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (tenant TEXT NOT NULL, account TEXT NOT NULL, amount TEXT, PRIMARY KEY(tenant, account));";
            await command.ExecuteNonQueryAsync();
        }

        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        project.Bindings[0] = project.Bindings[0] with
        {
            Kind = "sqlite",
            Locator = "source.sqlite",
            Configuration = new Dictionary<string, object?>
            {
                ["table"] = "ledger",
                ["decimalColumns"] = "amount"
            }
        };
        var projectPath = Path.Combine(_dir, "sqlite-source.xmap");
        new ProjectManager(projectPath).Save(project);

        var (exit, stdout, stderr) = await RunCliAsync(LocateCli(), "inspect-source", projectPath);

        Assert.Equal(0, exit);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("ledger", root.GetProperty("table").GetString());
        Assert.Equal("sqlite-primary-key-sha256", root.GetProperty("rowIdentityStrategy").GetString());
        Assert.True(root.GetProperty("hasStableRowIdentity").GetBoolean());
        Assert.Equal("InvariantCulture", root.GetProperty("decimalCulture").GetString());
        Assert.Equal(new[] { "tenant", "account" }, root.GetProperty("primaryKeyColumns").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "amount" }, root.GetProperty("configuredExactDecimalColumns").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        var capabilities = root.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("reading").GetBoolean());
        Assert.True(capabilities.GetProperty("lineage").GetBoolean());
        Assert.False(capabilities.GetProperty("publication").GetBoolean());
    }

    [Fact]
    public async Task Cli_ConfigureDestinationPersistsPlanAndInvalidatesPriorApproval()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "configure-target.xmap");
        new ProjectManager(projectPath).Save(project);
        await CreateSqliteDatabaseAsync(Path.Combine(_dir, "target.db"), includeFactTable: false);

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "configure-destination", projectPath,
            "--kind", "sqlite", "--path", "target.db", "--table", "facts",
            "--key", "fact_key", "--write-mode", "upsert");

        Assert.Equal(0, exit);
        Assert.Contains("must approve this destination-bound revision", stderr, StringComparison.Ordinal);
        var saved = new ProjectManager(projectPath).Load();
        Assert.NotNull(saved.Destination);
        Assert.Equal("sqlite", saved.Destination.Kind);
        Assert.Equal("target.db", saved.Destination.Locator);
        Assert.Equal("facts", saved.Destination.Table);
        Assert.Equal(new[] { "fact_key" }, saved.Destination.BusinessKey);
        Assert.Equal("upsert", saved.Destination.WriteMode);
        Assert.DoesNotContain(saved.Approvals, approval => approval.SemanticDigest == saved.ComputeSemanticDigest());
    }

    [Fact]
    public async Task Cli_ExecutePublishesSavedSqlitePlanAndRecordsDatabaseReceipt()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "sqlite-publish.xmap");
        var manager = new ProjectManager(projectPath);
        manager.Save(project);
        var targetPath = Path.Combine(_dir, "target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: true);

        var (configureExit, _, configureError) = await RunCliAsync(LocateCli(), "configure-destination", projectPath,
            "--kind", "sqlite", "--path", "target.db", "--table", "facts",
            "--key", "fact_key", "--write-mode", "upsert");
        Assert.True(configureExit == 0, $"Destination configuration failed ({configureExit}).\n{configureError}");

        project = manager.Load();
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest
        });
        manager.Save(project, expectedLayoutGeneration: project.LayoutGeneration);

        var manifestPath = Path.Combine(_dir, "sqlite-publish-manifest.json");
        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--publish-destination", "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.True(exit == 0, $"Expected SQLite execution to succeed, got {exit}.\nstderr:\n{stderr}");
        Assert.Contains("Published SQLite", stderr, StringComparison.Ordinal);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var receipt = Assert.Single(manifest.RootElement.GetProperty("Outputs").EnumerateArray(),
                output => output.GetProperty("Kind").GetString() == "db-receipt");
            Assert.Equal(1, receipt.GetProperty("RowsWritten").GetInt32());
            Assert.Equal("facts", receipt.GetProperty("Table").GetString());
            Assert.StartsWith("xms-", receipt.GetProperty("OperationKey").GetString(), StringComparison.Ordinal);
        }

        await using var database = new SqliteConnection($"Data Source={targetPath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT value FROM facts";
        Assert.Equal("1250000", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Cli_CombinedOutputAndSqlitePublicationPersistsAndReconcilesEachOutcome()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "mixed-publish.xmap");
        var manager = new ProjectManager(projectPath);
        manager.Save(project);
        var targetPath = Path.Combine(_dir, "target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: true);

        var (configureExit, _, configureError) = await RunCliAsync(LocateCli(), "configure-destination", projectPath,
            "--kind", "sqlite", "--path", "target.db", "--table", "facts",
            "--key", "fact_key", "--write-mode", "upsert");
        Assert.True(configureExit == 0, $"Destination configuration failed ({configureExit}).\n{configureError}");

        project = manager.Load();
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest
        });
        manager.Save(project, expectedLayoutGeneration: project.LayoutGeneration);

        var outputPath = Path.Combine(_dir, "mixed-report.json");
        var manifestPath = Path.Combine(_dir, "mixed-manifest.json");
        var cli = LocateCli();
        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--publish-destination", "--manifest", manifestPath,
            "--allow-synthetic-profile");

        Assert.True(exit == 0, $"Expected mixed publication to succeed, got {exit}.\nstderr:\n{stderr}");
        Assert.Contains("Published mixed operation", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(outputPath));
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        var root = manifest.RootElement;
        var reconciliationId = root.GetProperty("PublicationReconciliationId").GetString();
        Assert.StartsWith("mix-", reconciliationId, StringComparison.Ordinal);
        var outcomes = root.GetProperty("PublicationOutcomes").EnumerateArray().ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.Contains(outcomes, outcome => outcome.GetProperty("Kind").GetString() == "xbrl-json"
            && outcome.GetProperty("State").GetString() == "Confirmed"
            && outcome.GetProperty("Digest").GetString()!.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.Contains(outcomes, outcome => outcome.GetProperty("Kind").GetString() == "sqlite"
            && outcome.GetProperty("State").GetString() == "Confirmed"
            && outcome.GetProperty("OperationKey").GetString()!.StartsWith("xms-", StringComparison.Ordinal));

        var retryManifestPath = Path.Combine(_dir, "mixed-retry-manifest.json");
        var (retryExit, _, retryError) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--publish-destination", "--manifest", retryManifestPath,
            "--allow-synthetic-profile");
        Assert.True(retryExit == 0, $"Expected same-intent retry to succeed, got {retryExit}.\n{retryError}");
        using (var retryManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(retryManifestPath)))
        {
            var retryRoot = retryManifest.RootElement;
            Assert.Equal(reconciliationId, retryRoot.GetProperty("PublicationReconciliationId").GetString());
            Assert.Single(retryRoot.GetProperty("Outputs").EnumerateArray(), output =>
                output.GetProperty("Kind").GetString() == "db-receipt");
            Assert.All(retryRoot.GetProperty("PublicationOutcomes").EnumerateArray(), outcome =>
                Assert.Equal("Confirmed", outcome.GetProperty("State").GetString()));
        }

        var (reconcileExit, reconcileStdout, reconcileStderr) = await RunCliAsync(cli,
            "reconcile-publication", reconciliationId!);
        Assert.True(reconcileExit == 0, $"Reconciliation failed ({reconcileExit}).\n{reconcileStderr}\n{reconcileStdout}");
        using var reconciled = JsonDocument.Parse(reconcileStdout);
        Assert.All(reconciled.RootElement.GetProperty("outcomes").EnumerateArray(), outcome =>
            Assert.Equal("Confirmed", outcome.GetProperty("State").GetString()));

        await using var database = new SqliteConnection($"Data Source={targetPath};Pooling=False;Mode=ReadOnly");
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM facts),(SELECT COUNT(*) FROM xms_idempotency_receipts)";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task Cli_MixedPublicationRejectsXmlWithoutChangingEitherDestination()
    {
        var targetPath = Path.Combine(_dir, "xml-mixed-target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: true);
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        project.Destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "xml-mixed-target.db",
            Table = "facts",
            BusinessKey = new List<string> { "fact_key" },
            WriteMode = "upsert"
        };
        var projectPath = Path.Combine(_dir, "xml-mixed-publication.xmap");
        new ProjectManager(projectPath).Save(project);
        var targetBefore = await File.ReadAllBytesAsync(targetPath);
        var outputPath = Path.Combine(_dir, "xml-mixed-report.xml");
        var manifestPath = Path.Combine(_dir, "xml-mixed-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--output", outputPath, "--output-format", "xbrl-xml", "--publish-destination",
            "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.Equal(2, exit);
        Assert.Contains("cannot be combined with preview or --publish-destination", stderr,
            StringComparison.Ordinal);
        Assert.Equal(targetBefore, await File.ReadAllBytesAsync(targetPath));
        Assert.False(File.Exists(outputPath));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public async Task Cli_ConfigureAndExecutePublishesExplicitJsonDestination()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "json-publish.xmap");
        var manager = new ProjectManager(projectPath);
        manager.Save(project);

        var (configureExit, _, configureError) = await RunCliAsync(LocateCli(), "configure-destination", projectPath,
            "--kind", "json", "--path", "target.json", "--format", "json-records-v1");
        Assert.True(configureExit == 0, $"Destination configuration failed ({configureExit}).\n{configureError}");

        project = manager.Load();
        Assert.NotNull(project.Destination);
        Assert.Equal("json", project.Destination.Kind);
        Assert.Equal("json-records-v1", project.Destination.Format);
        Assert.Equal("replace", project.Destination.WriteMode);
        Assert.Empty(project.Destination.BusinessKey);
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest
        });
        manager.Save(project, expectedLayoutGeneration: project.LayoutGeneration);

        var manifestPath = Path.Combine(_dir, "json-publish-manifest.json");
        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--publish-destination", "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.True(exit == 0, $"Expected JSON execution to succeed, got {exit}.\nstderr:\n{stderr}");
        Assert.Contains("Published json", stderr, StringComparison.Ordinal);
        var outputPath = Path.Combine(_dir, "target.json");
        Assert.True(File.Exists(outputPath));
        using (var report = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath)))
        {
            var fact = Assert.Single(report.RootElement.EnumerateArray());
            Assert.Equal("1250000", fact.GetProperty("value").GetRawText());
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        Assert.Contains(manifest.RootElement.GetProperty("Outputs").EnumerateArray(), output =>
            output.GetProperty("Kind").GetString() == "json"
            && output.GetProperty("Digest").GetString()!.StartsWith("sha256:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cli_InvalidSqliteTableReturnsDataErrorWithoutMutatingDatabase()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var targetPath = Path.Combine(_dir, "invalid-target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: false);
        project.Destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "invalid-target.db",
            Table = "missing_facts",
            BusinessKey = new List<string> { "fact_key" }
        };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest
        });
        var projectPath = Path.Combine(_dir, "invalid-target.xmap");
        new ProjectManager(projectPath).Save(project);
        var before = await File.ReadAllBytesAsync(targetPath);
        var manifestPath = Path.Combine(_dir, "invalid-target-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--publish-destination", "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.Equal(3, exit);
        Assert.Contains("IO001", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(targetPath));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Contains(manifest.RootElement.GetProperty("Diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("Code").GetString() == DiagnosticCodes.IoSchemaMismatch);
    }

    [Fact]
    public async Task Cli_UnindexedSqliteBusinessKeyReturnsDataErrorWithoutMutatingDatabase()
    {
        var targetPath = Path.Combine(_dir, "unindexed-key-target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: true);
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        project.Destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "unindexed-key-target.db",
            Table = "facts",
            BusinessKey = new List<string> { "value" },
            WriteMode = "upsert"
        };
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-reviewer",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest
        });
        var projectPath = Path.Combine(_dir, "unindexed-key-target.xmap");
        new ProjectManager(projectPath).Save(project);
        var before = await File.ReadAllBytesAsync(targetPath);
        var manifestPath = Path.Combine(_dir, "unindexed-key-target-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--publish-destination", "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.Equal(3, exit);
        Assert.Contains(DiagnosticCodes.IoSchemaMismatch, stderr, StringComparison.Ordinal);
        Assert.Contains("unique index", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(targetPath));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Contains(manifest.RootElement.GetProperty("Diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("Code").GetString() == DiagnosticCodes.IoSchemaMismatch);
    }

    [Fact]
    public async Task Cli_PreviewWithSavedSqliteDestinationDoesNotMutateTarget()
    {
        var targetPath = Path.Combine(_dir, "preview-target.db");
        await CreateSqliteDatabaseAsync(targetPath, includeFactTable: true);
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        project.Destination = new DestinationPlan
        {
            Kind = "sqlite",
            Locator = "preview-target.db",
            Table = "facts",
            BusinessKey = new List<string> { "fact_key" },
            WriteMode = "upsert"
        };
        var projectPath = Path.Combine(_dir, "preview-target.xmap");
        new ProjectManager(projectPath).Save(project);
        var before = await File.ReadAllBytesAsync(targetPath);

        var (exit, stdout, stderr) = await RunCliAsync(LocateCli(), "preview", projectPath,
            "--rows", "10", "--allow-synthetic-profile");

        Assert.True(exit is 0 or 1, $"Expected preview to succeed, got {exit}.\nstderr:\n{stderr}");
        Assert.Contains("synthetic:Revenue", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Published SQLite", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(targetPath));
    }

    [Fact]
    public async Task Cli_ValidateReturnsExitCode_Two_ForInvalidMapping()
    {
        // Introduce an unknown node type so validation blocks with a stable diagnostic.
        var seed = Guid.Parse("44444444-1111-2222-3333-444444444444");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed, workbookPath: _workbookPath);
        project.Nodes.Add(new MappingNode
        {
            NodeId = Id.New(),
            NodeType = "quantumFlanger",
            DisplayName = "Unknown"
        });
        var projectPath = Path.Combine(_dir, "bad.xmap");
        var pm = new ProjectManager(projectPath);
        pm.Save(project);

        var cli = LocateCli();
        var psi = BuildStartInfo(cli, "validate", projectPath, "--allow-synthetic-profile");

        using var proc = Process.Start(psi)!;
        await proc.StandardOutput.ReadToEndAsync();
        await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // Invalid mapping exit code = 2 per ExitCodes contract.
        Assert.Equal(2, proc.ExitCode);
    }

    [Fact]
    public async Task Cli_ValidateRequiresExplicitSyntheticProfileOptIn()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var projectPath = Path.Combine(_dir, "validate-synthetic.xmap");
        new ProjectManager(projectPath).Save(project);

        var (blockedExit, _, blockedError) = await RunCliAsync(LocateCli(), "validate", projectPath);
        Assert.Equal(4, blockedExit);
        Assert.Contains("XBR017", blockedError, StringComparison.Ordinal);

        var (allowedExit, _, allowedError) = await RunCliAsync(
            LocateCli(), "validate", projectPath, "--allow-synthetic-profile");
        Assert.True(allowedExit == 0, $"Expected opted-in validation to pass, got {allowedExit}.\n{allowedError}");
    }

    [Fact]
    public async Task Cli_TaxonomySearchRequiresSyntheticProfileOptIn()
    {
        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "taxonomy-search", "--query", "revenue");

        Assert.Equal(4, exit);
        Assert.Contains("--allow-synthetic-profile", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_InstallProfileInstallsLocalPackageDisabledByDefault()
    {
        var sourceRoot = FilingProfileResolverTests.NewCatalogDirectory();
        var catalogRoot = FilingProfileResolverTests.NewCatalogDirectory();
        try
        {
            var source = FilingProfileResolverTests.WriteInstalledProfile(
                sourceRoot, "fixture-cli-offline-profile", "1.0.0", "Revenue");

            var (exit, stdout, stderr) = await RunCliAsync(LocateCli(), "install-profile",
                source.InstallationDirectory, "--catalog", catalogRoot);

            Assert.Equal(0, exit);
            Assert.Empty(stderr);
            using var result = JsonDocument.Parse(stdout);
            Assert.Equal("fixture-cli-offline-profile", result.RootElement.GetProperty("ProfileId").GetString());
            Assert.Equal("1.0.0", result.RootElement.GetProperty("Version").GetString());
            Assert.False(result.RootElement.GetProperty("IsEnabled").GetBoolean());
            var installed = Assert.Single(InstalledFilingProfileCatalog.Load(catalogRoot).Profiles);
            Assert.False(installed.IsEnabled);
            Assert.True(File.Exists(Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName)));

            var markerPath = Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName);
            const string markerContent = "preserve existing disabled profile";
            await File.WriteAllTextAsync(markerPath, markerContent);
            var (duplicateExit, duplicateStdout, duplicateStderr) = await RunCliAsync(LocateCli(),
                "install-profile", source.InstallationDirectory, "--catalog", catalogRoot);
            Assert.Equal(3, duplicateExit);
            Assert.Empty(duplicateStdout);
            Assert.Contains("already present", duplicateStderr, StringComparison.Ordinal);
            Assert.Equal(markerContent, await File.ReadAllTextAsync(markerPath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_InstallProfileMissingSourceReturnsDataErrorWithoutCatalogMutation()
    {
        var catalogRoot = FilingProfileResolverTests.NewCatalogDirectory();
        try
        {
            var missingSource = Path.Combine(_dir, "missing-offline-profile", "1.0.0");

            var (exit, stdout, stderr) = await RunCliAsync(LocateCli(), "install-profile",
                missingSource, "--catalog", catalogRoot);

            Assert.Equal(3, exit);
            Assert.Empty(stdout);
            Assert.Contains("source", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(InstalledFilingProfileCatalog.Load(catalogRoot).Profiles);
            Assert.False(Directory.Exists(Path.Combine(catalogRoot, "missing-offline-profile")));
        }
        finally
        {
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_LocalArtifactValidationRequiresSyntheticProfileOptIn()
    {
        var artifactPath = Path.Combine(_dir, "local-validation.xbrl.json");
        await File.WriteAllTextAsync(artifactPath, "{}");

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "validate-artifact", artifactPath);

        Assert.Equal(4, exit);
        Assert.Contains("--allow-synthetic-profile", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ValidateWithSubstitutedProfileReturnsIncompleteProfile()
    {
        var source = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var project = new MappingProject
        {
            SchemaVersion = source.SchemaVersion,
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = "qatar-real-taxonomy-v1",
            ProfileDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = source.SemanticRevision,
            LayoutGeneration = source.LayoutGeneration,
            Metadata = source.Metadata,
            Nodes = source.Nodes,
            Edges = source.Edges,
            Bindings = source.Bindings,
            Functions = source.Functions,
            Approvals = source.Approvals
        };
        var projectPath = Path.Combine(_dir, "substituted-profile.xmap");
        new ProjectManager(projectPath).Save(project);

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "validate", projectPath);

        Assert.Equal(4, exit); // IncompleteProfile
        Assert.Contains("XBR015", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteWithUnknownProfileFailsBeforeOpeningSource()
    {
        var source = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var project = new MappingProject
        {
            SchemaVersion = source.SchemaVersion,
            ProjectId = source.ProjectId,
            Name = source.Name,
            ProfileId = "uninstalled-profile-v1",
            ProfileDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            FunctionCatalogVersion = source.FunctionCatalogVersion,
            SemanticRevision = source.SemanticRevision,
            LayoutGeneration = source.LayoutGeneration,
            Metadata = source.Metadata,
            Nodes = source.Nodes,
            Edges = source.Edges,
            Bindings = source.Bindings.Select(b => b with { Locator = Path.Combine(_dir, "missing-source.xlsx") }).ToList(),
            Functions = source.Functions,
            Approvals = source.Approvals
        };
        var projectPath = Path.Combine(_dir, "unknown-profile-before-source.xmap");
        new ProjectManager(projectPath).Save(project);

        var (exit, _, stderr) = await RunCliAsync(
            LocateCli(), "execute", projectPath, "--allow-synthetic-profile");

        Assert.Equal(4, exit);
        Assert.Contains("XBR015", stderr, StringComparison.Ordinal);
        Assert.Contains("no source data was read", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IO001", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteWithMissingArelleRuntimeFailsBeforeOpeningSource()
    {
        var missingSource = Path.Combine(_dir, "source-must-not-be-opened.xlsx");
        var project = TestFixtures.BuildRevenueMappingProject(missingSource, approved: true);
        var projectPath = Path.Combine(_dir, "missing-arelle-runtime.xmap");
        new ProjectManager(projectPath).Save(project);
        var missingPython = Path.Combine(_dir, "missing-python-runtime.exe");
        var outputPath = Path.Combine(_dir, "existing-report.json");
        var priorOutput = System.Text.Encoding.UTF8.GetBytes("prior-accepted-report");
        await File.WriteAllBytesAsync(outputPath, priorOutput);

        var (exit, stdout, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--output", outputPath, "--allow-synthetic-profile", "--arelle", "--python", missingPython);

        Assert.Equal(4, exit);
        Assert.Contains(DiagnosticCodes.XbrWorkerUnavailable, stderr, StringComparison.Ordinal);
        Assert.Contains("Could not start the selected profile worker", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("IO001", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(missingSource), stderr, StringComparison.Ordinal);
        Assert.Empty(stdout);
        Assert.Equal(priorOutput, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task Cli_ExecuteBlocksUnqualifiedCatalogProfileBeforeOpeningSource()
    {
        var catalogRoot = FilingProfileResolverTests.NewCatalogDirectory();
        try
        {
            var installed = FilingProfileResolverTests.WriteInstalledProfile(
                catalogRoot, "fixture-unqualified-cli-profile", "1.0.0", "Revenue");
            var source = TestFixtures.BuildRevenueMappingProject(_workbookPath);
            var project = FilingProfileResolverTests.WithProfile(
                source, installed.Profile.ProfileId, installed.Profile.Digest);
            project.Bindings[0] = project.Bindings[0] with
            {
                Locator = Path.Combine(_dir, "missing-source.xlsx")
            };
            var projectPath = Path.Combine(_dir, "unqualified-profile-before-source.xmap");
            new ProjectManager(projectPath).Save(project);

            var psi = BuildStartInfo(LocateCli(), "execute", projectPath,
                "--output", Path.Combine(_dir, "must-not-be-created.json"));
            psi.Environment[InstalledFilingProfileCatalog.CatalogEnvironmentVariable] = catalogRoot;
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("CLI process failed to start.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(4, process.ExitCode);
            Assert.Contains("XBR018", stderr, StringComparison.Ordinal);
            Assert.Contains("GAT-01 and GAT-02", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("IO001", stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_dir, "must-not-be-created.json")),
                $"The unqualified profile created output. stdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    // ---- OG01/OG02: fail-closed publishing through the real CLI process. --------

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        (string FileName, bool IsAppHost) cli, string verb, params string[] extra)
    {
        var psi = BuildStartInfo(cli, verb, extra);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("CLI process failed to start.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadRunIdFromManifestAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("RunId").GetString()
            ?? throw new InvalidOperationException("Manifest missing RunId.");
    }

    private static async Task CreateSqliteDatabaseAsync(string path, bool includeFactTable)
    {
        await using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        await database.OpenAsync();
        if (!includeFactTable) return;

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
            """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Cli_ExecutePublishes_ThroughPublicationService_AndReceiptIsInspectable()
    {
        // OG02: a valid, approved production run writes through the four-phase publisher,
        // and its receipt must be retrievable by run id via the inspect-run-result verb.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "publish-ok.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "publish-ok-out.json");
        var manifestPath = Path.Combine(_dir, "publish-ok-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");
        Assert.True(exit == 0, $"Expected exit 0, got {exit}.\nstderr:\n{stderr}");
        Assert.True(File.Exists(outputPath));

        var runId = await ReadRunIdFromManifestAsync(manifestPath);
        var (inspectExit, inspectStdout, inspectStderr) = await RunCliAsync(cli, "inspect-run-result", runId);
        Assert.True(inspectExit == 0,
            $"inspect-run-result should find the receipt, got exit {inspectExit}.\nstderr:\n{inspectStderr}");
        using var doc = JsonDocument.Parse(inspectStdout);
        // JournalState.Finalized == 3: the run completed every publication phase.
        Assert.Equal(3, doc.RootElement.GetProperty("State").GetInt32());
        Assert.Equal(Path.GetFullPath(outputPath).Replace('\\', '/'),
            doc.RootElement.GetProperty("DestinationPath").GetString()?.Replace('\\', '/'));
    }

    [Fact]
    public async Task Cli_ExecutePublishesExplicitXbrlXml_AfterValidatingExactStagedArtifact()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "publish-xml.xmap");
        new ProjectManager(projectPath).Save(project);
        var outputPath = Path.Combine(_dir, "publish-xml-output.any-extension");
        var manifestPath = Path.Combine(_dir, "publish-xml-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(LocateCli(), "execute", projectPath,
            "--output", outputPath, "--output-format", "xbrl-xml", "--manifest", manifestPath,
            "--allow-synthetic-profile");

        Assert.True(exit == 0, $"Expected exit 0, got {exit}.\nstderr:\n{stderr}");
        var xmlBytes = await File.ReadAllBytesAsync(outputPath);
        var xml = new System.Text.UTF8Encoding(false, true).GetString(xmlBytes);
        var facts = XmlSerializer.ReadFacts(xml, SyntheticProfile.Build());
        var fact = Assert.Single(facts);
        Assert.Equal(SyntheticProfile.Qualified("Revenue"), fact.ConceptName);
        Assert.Equal(1_250_000.0000m, fact.Value);

        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        var xmlIdentity = Assert.Single(manifest.RootElement.GetProperty("Outputs").EnumerateArray(),
            output => output.GetProperty("Kind").GetString() == "xbrl-xml");
        var digest = RunManifest.ComputeSha256(xmlBytes);
        Assert.Equal(digest, xmlIdentity.GetProperty("Digest").GetString());
        Assert.Equal(xmlBytes.LongLength, xmlIdentity.GetProperty("Bytes").GetInt64());
        Assert.Equal(Path.GetFullPath(outputPath), xmlIdentity.GetProperty("Path").GetString());
        Assert.Equal(digest, manifest.RootElement.GetProperty("ValidatedArtifactDigest").GetString());
        Assert.Equal(digest, manifest.RootElement.GetProperty("PublishedArtifactDigest").GetString());
    }

    [Fact]
    public async Task Cli_ExecuteInvalidMapping_PreservesPriorOutput_AndIssuesNoReceipt()
    {
        // OG01 core sentinel: seed a prior accepted destination. An invalid graph must
        // leave it byte-identical and must not create a run-journal entry.
        var priorBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
        var outputPath = Path.Combine(_dir, "fail-closed-out.json");
        await File.WriteAllBytesAsync(outputPath, priorBytes);

        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        project.Nodes.Add(new MappingNode
        {
            NodeId = Id.New(),
            NodeType = "quantumFlanger",
            DisplayName = "Unknown"
        });
        project.Approvals.Clear();
        project.Approvals.Add(new ApprovalRecord
        {
            SemanticRevision = project.SemanticRevision,
            SemanticDigest = project.ComputeSemanticDigest(),
            ApprovedBy = "fixture-approver",
            ApprovedUtc = DateTimeOffset.UtcNow,
            ProfileDigest = SyntheticProfile.Build().Digest,
            Authority = "fixture-harness",
            Notes = "Adversarial fixture to prove invalid mappings still fail after publication authorization."
        });
        var projectPath = Path.Combine(_dir, "fail-closed.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var manifestPath = Path.Combine(_dir, "fail-closed-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.Equal(2, exit); // InvalidMapping
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(outputPath));
        Assert.Contains("no publication receipt issued", stderr, StringComparison.Ordinal);

        var runId = await ReadRunIdFromManifestAsync(manifestPath);
        var (inspectExit, _, _) = await RunCliAsync(cli, "inspect-run-result", runId);
        Assert.Equal(3, inspectExit); // DataError: "No journal entry for run" — never published.
    }

    // ---- EG03/XG01: profile identity + approval gate before production publish. ----

    [Fact]
    public async Task Cli_ExecuteWithoutApproval_IsBlockedAsAuthorizationFailure_AndWritesNothing()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var projectPath = Path.Combine(_dir, "no-approval.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "no-approval-out.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--allow-synthetic-profile");

        Assert.Equal(5, exit); // AuthorizationFailure
        Assert.False(File.Exists(outputPath));
        Assert.Contains("XBR016", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteWithoutSyntheticOptIn_IsBlocked_AndWritesNothing()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "no-synthetic-flag.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "no-synthetic-flag-out.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath, "--output", outputPath);

        Assert.Equal(4, exit); // IncompleteProfile
        Assert.False(File.Exists(outputPath));
        Assert.Contains("XBR017", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteDisabledInstalledProfile_IsBlockedBeforeSourceReadAndPreservesOutput()
    {
        var catalogRoot = FilingProfileResolverTests.NewCatalogDirectory();
        try
        {
            var installed = FilingProfileResolverTests.WriteInstalledProfile(
                catalogRoot, "fixture-disabled-cli-profile", "1.0.0", "Revenue");
            File.WriteAllText(Path.Combine(installed.InstallationDirectory,
                InstalledFilingProfileCatalog.DisabledMarkerFileName), "disabled by local administrator");

            var baseProject = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
            var identifiedProject = ProfileIdentityTests.ProjectWithProfile(
                baseProject, installed.Profile.ProfileId, installed.Profile.Digest);
            var projectJson = JsonNode.Parse(ProjectSerializer.Serialize(identifiedProject))!;
            projectJson["bindings"]![0]!["locator"] = Path.Combine(_dir, "source-must-not-be-read.xlsx");
            var project = ProjectSerializer.Deserialize(projectJson.ToJsonString());
            var projectPath = Path.Combine(_dir, "disabled-profile.xmap");
            new ProjectManager(projectPath).Save(project);

            var outputPath = Path.Combine(_dir, "disabled-profile-output.json");
            const string priorContent = "PRIOR-ACCEPTED";
            File.WriteAllText(outputPath, priorContent);
            var start = BuildStartInfo(LocateCli(), "execute", projectPath,
                "--output", outputPath, "--allow-synthetic-profile");
            start.Environment[InstalledFilingProfileCatalog.CatalogEnvironmentVariable] = catalogRoot;

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("CLI process failed to start.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(4, process.ExitCode);
            Assert.Contains("disabled", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("source-must-not-be-read.xlsx", stderr, StringComparison.Ordinal);
            Assert.Equal(priorContent, await File.ReadAllTextAsync(outputPath));
            Assert.Empty(stdout);
        }
        finally
        {
            Directory.Delete(catalogRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_ExecuteWithSubstitutedProfile_IsBlocked_AndPreservesPriorOutput()
    {
        const string priorContent = "PRIOR-ACCEPTED";
        var outputPath = Path.Combine(_dir, "substituted-out.json");
        File.WriteAllText(outputPath, priorContent);

        var approved = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        // A project created against a different (e.g. a real, non-synthetic) taxonomy
        // profile must be refused outright rather than executed under the synthetic one.
        var substituted = new MappingProject
        {
            ProjectId = approved.ProjectId,
            Name = approved.Name,
            ProfileId = "qatar-real-taxonomy-v1",
            ProfileDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            FunctionCatalogVersion = approved.FunctionCatalogVersion,
            SemanticRevision = approved.SemanticRevision,
            Metadata = approved.Metadata,
            Nodes = approved.Nodes,
            Edges = approved.Edges,
            Bindings = approved.Bindings,
            Functions = approved.Functions,
            Approvals = approved.Approvals,
        };
        var projectPath = Path.Combine(_dir, "substituted.xmap");
        new ProjectManager(projectPath).Save(substituted);

        var cli = LocateCli();
        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--allow-synthetic-profile");

        Assert.Equal(4, exit); // IncompleteProfile (profile identity mismatch)
        Assert.Equal(priorContent, File.ReadAllText(outputPath)); // unchanged
        Assert.Contains("XBR015", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_PreviewRequiresSyntheticOptIn_ButNotApproval()
    {
        // EG03 boundary: a draft preview stays available on an unapproved, unacknowledged
        // revision so a reviewer can inspect before accepting — only publish gates close.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var projectPath = Path.Combine(_dir, "preview-no-approval.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var (exit, stdout, stderr) = await RunCliAsync(
            cli, "preview", projectPath, "--allow-synthetic-profile");

        Assert.True(exit is 0 or 1, $"Expected preview to run, got exit {exit}.\nstderr:\n{stderr}");
        Assert.Contains("synthetic:Revenue", stdout, StringComparison.Ordinal);
    }
}
