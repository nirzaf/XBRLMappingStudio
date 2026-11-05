using System.Text.Json;
using System.Diagnostics;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
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
            Assert.Single(result.Facts);
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
