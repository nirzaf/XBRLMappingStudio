using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ArelleExecutionTests
{
    [RequiresArelleRuntimeFact]
    public async Task AuthorizedPublicationRevalidatesExactStagedArtifactWithOfflineArelle()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");

        var workerScript = Path.Combine(AppContext.BaseDirectory, "worker", "arelle_worker.py");
        var taxonomyRoot = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
        Assert.True(File.Exists(workerScript), $"Arelle worker script is missing: {workerScript}");

        var workspace = Path.Combine(Path.GetTempPath(), "xms-arelle-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var workbook = IndependentAcceptanceFixtures.Materialize(workspace, "revenue.xlsx");
            var project = TestFixtures.BuildRevenueMappingProject(workbook, approved: true);
            var binding = Assert.Single(project.Bindings);
            var configuration = binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal);
            var connections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal)
            {
                [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
            };

            await using var worker = new ArelleWorker(python!, new[]
            {
                workerScript,
                "--taxonomy-root",
                taxonomyRoot
            });
            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, worker);
            var result = await service.RunAsync(project, connections, new Dictionary<string, string>(),
                preview: false, CancellationToken.None);
            Assert.True(result.IsPublishable,
                $"Run outcome was {result.Manifest.Outcome}. Diagnostics: {string.Join(Environment.NewLine, result.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
            var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
            Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, Assert.Single(result.Facts)));
            Assert.Empty(IndependentAcceptanceFixtures.CompareSerializedOim(mappingOracle, result.XbrlJson));

            var destination = Path.Combine(workspace, "published-report.xbrl.json");
            var prior = System.Text.Encoding.UTF8.GetBytes("previous-accepted-report");
            await File.WriteAllBytesAsync(destination, prior);
            var receipt = await new PublicationService(new RunJournal(Path.Combine(workspace, "journal")))
                .PublishAuthorizedAsync(destination,
                    System.Text.Encoding.UTF8.GetBytes(result.XbrlJson), workspace, project, profile,
                    allowSyntheticProfile: true,
                    result: result,
                    validateStagedArtifactAsync: (stagedPath, token) => service.ValidateStagedArtifactAsync(
                        stagedPath, destination, result, token),
                    cancellationToken: CancellationToken.None);

            var output = Assert.Single(result.Manifest.Outputs);
            var finalDigest = RunManifest.ComputeSha256(await File.ReadAllBytesAsync(destination));
            Assert.Equal(output.Digest, receipt.ValidatedDigest);
            Assert.Equal(receipt.ValidatedDigest, receipt.FinalDigest);
            Assert.Equal(receipt.FinalDigest, finalDigest);
            Assert.Equal(receipt.FinalDigest, result.Manifest.PublishedArtifactDigest);
            Assert.Equal(Path.GetFullPath(destination), output.Path);
            var publishedBytes = await File.ReadAllBytesAsync(destination);
            Assert.False(prior.SequenceEqual(publishedBytes));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [RequiresArelleRuntimeFact]
    public async Task CliPreviewUsesArelleToValidateExcelMappingOutput()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "xbrl-studio.dll");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly is missing: {cliAssembly}");

        var workspace = Path.Combine(Path.GetTempPath(), "xms-arelle-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var workbook = IndependentAcceptanceFixtures.Materialize(workspace, "revenue.xlsx");
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            var projectPath = Path.Combine(workspace, "mapping.xmap");
            await File.WriteAllTextAsync(projectPath, ProjectSerializer.Serialize(project));

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                cliAssembly,
                "preview",
                projectPath,
                "--allow-synthetic-profile",
                "--arelle",
                "--python",
                python!,
                "--rows",
                "10"
            })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the CLI process.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var stdout = await standardOutput;
            var stderr = await standardError;

            Assert.True(new[] { 0, 1 }.Contains(process.ExitCode),
                $"CLI exited with {process.ExitCode}. stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
            Assert.Contains(SyntheticProfile.TaxonomyFileName, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Arelle validation could not start", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [RequiresArelleRuntimeFact]
    public async Task CliValidateArtifactUsesLocalSyntheticRuleCompletionWithArelle()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "xbrl-studio.dll");
        var taxonomyRoot = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
        Assert.True(File.Exists(cliAssembly), $"CLI assembly is missing: {cliAssembly}");
        Assert.True(File.Exists(Path.Combine(taxonomyRoot, SyntheticProfile.TaxonomyFileName)),
            $"Synthetic development taxonomy is missing from {taxonomyRoot}.");

        var workspace = Path.Combine(Path.GetTempPath(), "xms-arelle-cli-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var profile = SyntheticProfile.Build();
            var fact = new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "cli-validation-fixture",
                Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                Unit = new XbrlUnit("USD"),
                Value = 123m,
                Decimals = 0
            };
            var reportPath = Path.Combine(workspace, "report.xbrl.json");
            await File.WriteAllTextAsync(reportPath, OimSerializer.Serialize(profile, new[] { fact }),
                new UTF8Encoding(false));

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                cliAssembly,
                "validate-artifact",
                reportPath,
                "--arelle",
                "--python",
                python!,
                "--taxonomy-root",
                taxonomyRoot
            })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the CLI process.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var stdout = await standardOutput;
            var stderr = await standardError;

            Assert.Equal(0, process.ExitCode);
            using var response = JsonDocument.Parse(stdout);
            Assert.Equal("Success", response.RootElement.GetProperty("status").GetString());
            Assert.Equal("Complete", response.RootElement.GetProperty("completion").GetString());
            Assert.DoesNotContain("Arelle validation could not start", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [RequiresArelleRuntimeFact]
    public async Task ExcelToXbrlMappingIsAdjudicatedByOfflineArelleWorker()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");

        var workerScript = Path.Combine(AppContext.BaseDirectory, "worker", "arelle_worker.py");
        var taxonomyRoot = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
        Assert.True(File.Exists(workerScript), $"Arelle worker script is missing: {workerScript}");
        Assert.True(File.Exists(Path.Combine(taxonomyRoot, SyntheticProfile.TaxonomyFileName)),
            $"Synthetic development taxonomy is missing from {taxonomyRoot}.");

        var workspace = Path.Combine(Path.GetTempPath(), "xms-arelle-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var workbook = IndependentAcceptanceFixtures.Materialize(workspace, "revenue.xlsx");
            var project = TestFixtures.BuildRevenueMappingProject(workbook);
            var binding = Assert.Single(project.Bindings);
            var configuration = binding.Configuration.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString() ?? "",
                StringComparer.Ordinal);
            var connections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal)
            {
                [binding.BindingId] = new SourceConnection(binding.BindingId, binding.Kind, workbook, configuration)
            };

            await using var worker = new ArelleWorker(python!, new[]
            {
                workerScript,
                "--taxonomy-root",
                taxonomyRoot
            });
            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, worker);
            var result = await service.RunAsync(
                project,
                connections,
                new Dictionary<string, string>(),
                preview: false,
                CancellationToken.None);

            Assert.True(result.Manifest.Outcome is RunOutcome.Success or RunOutcome.SuccessWithWarnings,
                $"Run outcome was {result.Manifest.Outcome}. Diagnostics: {string.Join(Environment.NewLine, result.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
            var mappingOracle = IndependentAcceptanceFixtures.ReadFactOracle("workbookMapping");
            Assert.Empty(IndependentAcceptanceFixtures.CompareFact(mappingOracle, Assert.Single(result.Facts)));
            Assert.Empty(IndependentAcceptanceFixtures.CompareSerializedOim(mappingOracle, result.XbrlJson));
            Assert.DoesNotContain(result.Manifest.Diagnostics, diagnostic => diagnostic.IsBlocking);
            using var report = JsonDocument.Parse(result.XbrlJson);
            Assert.Equal(SyntheticProfile.TaxonomyFileName,
                report.RootElement.GetProperty("documentInfo").GetProperty("taxonomy")[0].GetString());
            var output = Assert.Single(result.Manifest.Outputs);
            Assert.Equal(RunManifest.ComputeSha256(result.XbrlJson), output.Digest);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [RequiresArelleRuntimeFact]
    public async Task ReviewedDigitalPdf_MapsOnlyTargetYearAndBlocksWrongYearOnlySource()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");
        var workerScript = Path.Combine(AppContext.BaseDirectory, "worker", "arelle_worker.py");
        var taxonomyRoot = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
        var workspace = Path.Combine(Path.GetTempPath(), "xms-arelle-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var statePath = Path.Combine(workspace, "application-state.db");
        try
        {
            var currentYearPdf = await CreateReviewedPdfAsync(
                workspace, "current-year.pdf", statePath, "1,250.00", "1,100.00");
            var priorYearOnlyPdf = await CreateReviewedPdfAsync(
                workspace, "prior-year-only.pdf", statePath, null, "1,100.00");

            await using var worker = new ArelleWorker(python!, new[]
            {
                workerScript,
                "--taxonomy-root",
                taxonomyRoot
            });
            var profile = SyntheticProfile.Build();
            var service = new MappingJobService(profile, worker,
                applicationDatabaseFactory: () => new LocalApplicationDatabase(statePath));

            var positiveInput = CreatePdfMappingInput(currentYearPdf);
            var positive = await service.RunAsync(positiveInput.Project,
                new Dictionary<string, SourceConnection> { [positiveInput.Connection.SourceId] = positiveInput.Connection },
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.True(positive.IsPublishable,
                $"Run outcome was {positive.Manifest.Outcome}. Diagnostics: {string.Join(Environment.NewLine, positive.Manifest.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}");
            var fact = Assert.Single(positive.Facts);
            Assert.Equal(SyntheticProfile.Qualified("Revenue"), fact.ConceptName);
            Assert.Equal(1_250_000m, fact.Value);
            Assert.Equal(new DateOnly(2025, 1, 1), fact.Period.Start);
            Assert.Equal(new DateOnly(2026, 1, 1), fact.Period.End);
            Assert.Contains(fact.ContributingSources, source =>
                source.SourceId == positiveInput.Connection.SourceId
                && source.Locator.StartsWith("pdf-evidence:", StringComparison.Ordinal)
                && source.Locator.Contains("page=1", StringComparison.Ordinal));
            Assert.DoesNotContain(positive.Manifest.Diagnostics, diagnostic => diagnostic.IsBlocking);
            Assert.Contains(positive.Manifest.Inputs, input =>
                input.SourceId == positiveInput.Connection.SourceId
                && input.Digest == currentYearPdf.SourceSha256);

            var negativeInput = CreatePdfMappingInput(priorYearOnlyPdf);
            var wrongYear = await service.RunAsync(negativeInput.Project,
                new Dictionary<string, SourceConnection> { [negativeInput.Connection.SourceId] = negativeInput.Connection },
                new Dictionary<string, string>(), preview: false, CancellationToken.None);

            Assert.Empty(wrongYear.Facts);
            Assert.False(wrongYear.IsPublishable);
            Assert.NotEqual(RunOutcome.Success, wrongYear.Manifest.Outcome);
            Assert.Empty(wrongYear.XbrlJson);
            Assert.Contains(wrongYear.Manifest.Diagnostics, diagnostic => diagnostic.IsBlocking);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static (MappingProject Project, SourceConnection Connection) CreatePdfMappingInput(
        ReviewedPdfFixture fixture)
    {
        var project = TestFixtures.BuildRevenueMappingProject();
        var binding = Assert.Single(project.Bindings);
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PdfSourceAdapter.ReviewManifestConfigurationKey] = Path.GetFileName(fixture.ManifestPath)
        };
        project.Bindings[0] = binding with
        {
            Kind = "pdf-digital",
            DisplayName = Path.GetFileName(fixture.SourcePath),
            Locator = fixture.SourcePath,
            Configuration = configuration.ToDictionary(pair => pair.Key,
                pair => (object?)pair.Value, StringComparer.Ordinal)
        };

        var sourceNode = project.Nodes.Single(node => node.NodeType == NodeTypeConstants.ExcelField);
        sourceNode.Parameters["column"] = "value";
        sourceNode.Parameters["rowFilterColumn"] = "reporting_year";
        sourceNode.Parameters["rowFilterValue"] = "2025";
        project.Nodes.Single(node => node.NodeType == NodeTypeConstants.Scale).Parameters["factor"] = "1";

        return (project, new SourceConnection(binding.BindingId, "pdf-digital", fixture.SourcePath, configuration));
    }

    private static async Task<ReviewedPdfFixture> CreateReviewedPdfAsync(
        string workspace,
        string fileName,
        string statePath,
        string? currentYearValue,
        string? priorYearValue)
    {
        var sourcePath = Path.Combine(workspace, fileName);
        var pdfBytes = BuildDigitalPdf(currentYearValue, priorYearValue);
        await File.WriteAllBytesAsync(sourcePath, pdfBytes);
        var document = PdfDigitalDocumentExtractor.Extract(pdfBytes);
        var headers = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["2025"] = 2025,
            ["2024"] = 2024
        };
        var geometryTemplate = PdfTableTemplate.CreateFromFirstPage(
            document, 1, "Description", headers, rowsAtLowLineCoordinate: true);
        var policy = new PdfNumericFormatPolicy('.', ',', true, 1000, "QAR",
            AllowLeadingSign: true,
            AllowParenthesizedNegative: true,
            AllowTrailingMinus: false,
            PdfNumericPolicyBasis.ExplicitTemplate);
        var templateRevision = CreateReviewRevision(geometryTemplate, policy);
        var template = geometryTemplate with { Revision = templateRevision };
        var reconstruction = PdfTableTemplateReconstructor.Reconstruct(document, template);
        Assert.Equal(PdfTableReconstructionState.Anchored, reconstruction.State);
        var sourceRow = Assert.Single(reconstruction.Rows);
        var captures = new[] { new PdfEvidenceCapture(sourceRow.Evidence, sourceRow.LabelText) }
            .Concat(sourceRow.Cells.Select(cell => new PdfEvidenceCapture(cell.Evidence, cell.RawText)))
            .ToArray();
        var evidenceStore = new PdfEvidenceStore(new LocalApplicationDatabase(statePath));
        var evidence = await evidenceStore.CaptureAsync(sourcePath, document, captures,
            templateRevision, CancellationToken.None);
        var reviewedCells = sourceRow.Cells.Select((cell, index) =>
        {
            var parsed = PdfNumericInterpreter.Parse(cell, policy);
            if (parsed.State != PdfNumericParseState.ParsedRequiresReview || parsed.CanonicalValue is null)
                throw new InvalidDataException($"Synthetic PDF fixture cell {cell.ReportingYear} did not parse.");
            var reference = evidence[index + 1];
            return new PdfReviewedSourceCell
            {
                ReportingYear = cell.ReportingYear,
                RawText = cell.RawText,
                CanonicalValue = parsed.CanonicalValue,
                EvidenceId = reference.EvidenceId,
                Evidence = cell.Evidence,
                IsApproved = true
            };
        }).ToArray();
        var manifest = new PdfReviewedSourceManifest
        {
            FormatVersion = 1,
            SourceSha256 = document.SourceSha256,
            TemplateRevision = templateRevision,
            Reviewer = "authorized-synthetic-fixture-reviewer",
            ReviewedUtc = DateTimeOffset.UtcNow,
            IsApproved = true,
            RowLabelHeader = "Description",
            TemplatePageNumber = 1,
            RowsAtLowLineCoordinate = true,
            NumericPolicy = policy,
            PeriodColumns = template.PeriodColumns,
            Rows = new[]
            {
                new PdfReviewedSourceRow
                {
                    Occurrence = sourceRow.Occurrence,
                    LabelText = sourceRow.LabelText,
                    LabelEvidenceId = evidence[0].EvidenceId,
                    LabelEvidence = sourceRow.Evidence,
                    Cells = reviewedCells
                }
            }
        };
        var manifestPath = sourcePath + ".xms-review.json";
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        }));
        return new ReviewedPdfFixture(sourcePath, manifestPath, document.SourceSha256);
    }

    private static string CreateReviewRevision(PdfTableTemplate geometryTemplate, PdfNumericFormatPolicy policy)
    {
        var policyRevision = string.Join("|", new[]
        {
            policy.DecimalSeparator.ToString(),
            policy.GroupSeparator?.ToString() ?? "",
            policy.AllowGrouping.ToString(CultureInfo.InvariantCulture),
            policy.ScaleFactor.ToString(CultureInfo.InvariantCulture),
            policy.CurrencyCode,
            policy.AllowLeadingSign.ToString(CultureInfo.InvariantCulture),
            policy.AllowParenthesizedNegative.ToString(CultureInfo.InvariantCulture),
            policy.AllowTrailingMinus.ToString(CultureInfo.InvariantCulture),
            policy.Basis.ToString()
        });
        var revisionInput = $"pdf-review-v1|{geometryTemplate.Revision}|{policyRevision}";
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput))).ToLowerInvariant();
    }

    [RequiresArelleRuntimeFact]
    public async Task CancellingArelleRequestKillsTheWorkerProcess()
    {
        var python = Environment.GetEnvironmentVariable("XMS_ARELLE_PYTHON");
        Assert.False(string.IsNullOrWhiteSpace(python), "XMS_ARELLE_PYTHON must point to the pinned Python runtime.");

        var workspace = Path.Combine(Path.GetTempPath(), "xms-worker-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var scriptPath = Path.Combine(workspace, "blocked_worker.py");
        var pidPath = Path.Combine(workspace, "worker.pid");
        await File.WriteAllTextAsync(scriptPath,
            "import os, sys, time\n"
            + "temporary = sys.argv[1] + '.tmp'\n"
            + "with open(temporary, 'w', encoding='ascii') as marker: marker.write(str(os.getpid()))\n"
            + "os.replace(temporary, sys.argv[1])\n"
            + "while True: time.sleep(1)\n");

        ArelleWorker? worker = null;
        try
        {
            worker = new ArelleWorker(python!, new[] { scriptPath, pidPath });
            using var cancellation = new CancellationTokenSource();
            var handshake = worker.HandshakeAsync(cancellation.Token);
            Assert.True(await WaitForAsync(() => File.Exists(pidPath), TimeSpan.FromSeconds(5)),
                "The test worker did not start and write its process ID.");
            var processId = int.Parse(await File.ReadAllTextAsync(pidPath), CultureInfo.InvariantCulture);

            cancellation.Cancel();
            var startupFailure = await Assert.ThrowsAsync<WorkerStartupException>(
                () => handshake.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(WorkerStatus.Cancelled, startupFailure.Status);
            await worker.DisposeAsync();

            Assert.True(await WaitForAsync(() => !IsProcessAlive(processId), TimeSpan.FromSeconds(5)),
                "The worker process remained alive after its request was cancelled.");
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] BuildDigitalPdf(string? currentYearValue, string? priorYearValue)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Description", 10, new PdfPoint(50, 720), font);
        page.AddText("2025", 10, new PdfPoint(250, 720), font);
        page.AddText("2024", 10, new PdfPoint(410, 720), font);
        page.AddText("Revenue", 10, new PdfPoint(50, 680), font);
        if (currentYearValue is not null)
            page.AddText(currentYearValue, 10, new PdfPoint(250, 680), font);
        if (priorYearValue is not null)
            page.AddText(priorYearValue, 10, new PdfPoint(410, 680), font);
        return builder.Build();
    }

    private sealed record ReviewedPdfFixture(string SourcePath, string ManifestPath, string SourceSha256);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresArelleRuntimeFactAttribute : FactAttribute
{
    public RequiresArelleRuntimeFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("XMS_ARELLE_E2E"), "1", StringComparison.Ordinal))
            Skip = "Set XMS_ARELLE_E2E=1 after installing the pinned Arelle worker runtime.";
    }
}
