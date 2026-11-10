using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// OPS04 — workspace path guard, secret redaction, and the assertion that the
/// adapter/engine/project layers carry no outbound HTTP surface at all.
/// </summary>
public class PathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ops-guard-" + Guid.NewGuid().ToString("N"));
        _outside = Path.Combine(Path.GetTempPath(), "ops-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside })
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RelativePathInsideRoot_IsAccepted()
    {
        var resolved = PathGuard.EnsureWithin(_root, "data/out.xlsx");
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "data/out.xlsx")), resolved);
    }

    [Fact]
    public void DotDotTraversal_IsRejected()
    {
        var ex = Assert.Throws<AdapterException>(() =>
            PathGuard.EnsureWithin(_root, "../escape.xlsx"));
        Assert.Equal(DiagnosticCodes.IoPathEscapesWorkspace, ex.Code);
    }

    [Fact]
    public void AbsolutePathOutsideRoot_IsRejected()
    {
        var target = Path.Combine(_outside, "victim.xlsx");
        var ex = Assert.Throws<AdapterException>(() =>
            PathGuard.EnsureWithin(_root, target));
        Assert.Equal(DiagnosticCodes.IoPathEscapesWorkspace, ex.Code);
    }

    [Fact]
    public void InternalSymlinkPointingOutside_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // symlink creation needs privileges there
        var victim = Path.Combine(_outside, "victim.json");
        File.WriteAllText(victim, "{}");
        var link = Path.Combine(_root, "innocent.json");
        Directory.CreateSymbolicLink(link, victim);

        var ex = Assert.Throws<AdapterException>(() => PathGuard.EnsureWithin(_root, "innocent.json"));
        Assert.Equal(DiagnosticCodes.IoPathEscapesWorkspace, ex.Code);
        Assert.False(File.Exists(victim + ".rewrite")); // nothing touched through the link
    }

    [Fact]
    public void InternalSymlinkParentForNewFile_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "external"), _outside);

        var ex = Assert.Throws<AdapterException>(() =>
            PathGuard.EnsureWithin(_root, "external/new-report.json"));

        Assert.Equal(DiagnosticCodes.IoPathEscapesWorkspace, ex.Code);
        Assert.False(File.Exists(Path.Combine(_outside, "new-report.json")));
    }

    [Fact]
    public void IsWithin_ProbeDoesNotThrow()
    {
        Assert.True(PathGuard.IsWithin(_root, "ok.txt"));
        Assert.False(PathGuard.IsWithin(_root, "../../etc/passwd"));
    }

    [Theory]
    [InlineData("conn string: password=hunter2; server=x", "hunter2")]
    [InlineData("Authorization: Bearer token=abc123", "abc123")]
    [InlineData("signing secret=deadbeef rotated", "deadbeef")]
    [InlineData("Authorization: Bearer eyJhbGciOi-example", "eyJhbGciOi-example")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("GET /validate?access_token=oauth-canary-71&mode=offline", "oauth-canary-71")]
    [InlineData("X-API-Key: api-canary-84", "api-canary-84")]
    [InlineData("endpoint https://service-user:url-pass-canary@example.invalid/check", "url-pass-canary")]
    public void Redaction_MasksCredentialAssignments(string line, string secret)
    {
        var redacted = SupportBundle.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Theory]
    [InlineData("assigned to alex.analyst@example.com", "alex.analyst@example.com")]
    [InlineData(@"source C:\Users\mohamed\clients\Northwind Private\FY2026.xlsx", @"C:\Users\mohamed\clients\Northwind Private\FY2026.xlsx")]
    [InlineData("source C:/Users/mohamed/clients/Northwind Private/FY2026.xlsx", "C:/Users/mohamed/clients/Northwind Private/FY2026.xlsx")]
    [InlineData(@"source \\fileserver\finance\Northwind Private\FY2026.xlsx", @"\\fileserver\finance\Northwind Private\FY2026.xlsx")]
    [InlineData("source /home/mohamed/clients/Northwind Private/FY2026.xlsx", "/home/mohamed/clients/Northwind Private/FY2026.xlsx")]
    public void Redaction_MasksEmailAddressesAndLocalPaths(string line, string sensitiveValue)
    {
        var redacted = SupportBundle.Redact(line);

        Assert.DoesNotContain(sensitiveValue, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sensitiveValue.Contains('@') ? "<email>" : "<local-path>", redacted);
    }

    [Fact]
    public void Redaction_LeavesOrdinaryTextAlone()
    {
        const string line = "run run-42 completed with 12 facts";
        Assert.Equal(line, SupportBundle.Redact(line));
    }

    [Fact]
    public void Redaction_MasksUrlUserInfoWithoutCorruptingScheme()
    {
        const string line = "endpoint https://service-user:url-pass-canary@example.invalid/check";
        Assert.Equal("endpoint https://<redacted>@example.invalid/check", SupportBundle.Redact(line));
    }

    [Fact]
    public void CoreLayers_ReferenceNoHttpTransport()
    {
        // OPS04 outbound policy: adapters, engine and project carry no HTTP surface.
        // (The AI provider layer is the single sanctioned egress point, and only in
        // managed modes.)
        foreach (var assembly in new[]
                 {
                     typeof(XBRLMappingStudio.Adapters.ISourceAdapter).Assembly,
                     typeof(XBRLMappingStudio.Engine.GraphValidator).Assembly,
                     typeof(XBRLMappingStudio.Project.MappingProject).Assembly,
                 })
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var f in type.GetFields(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Static))
                {
                    AssertNoHttp(f.FieldType?.FullName, $"field {type.FullName}.{f.Name}", assembly);
                }
                foreach (var m in type.GetMethods(
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Static |
                             System.Reflection.BindingFlags.DeclaredOnly))
                {
                    AssertNoHttp(m.ReturnType?.FullName, $"method return {type.FullName}.{m.Name}", assembly);
                    foreach (var p in m.GetParameters())
                        AssertNoHttp(p.ParameterType?.FullName, $"parameter {type.FullName}.{m.Name}.{p.Name}", assembly);
                }
            }
        }
    }

    private static void AssertNoHttp(string? fullTypeName, string where, System.Reflection.Assembly assembly)
    {
        Assert.False(
            fullTypeName is not null &&
            (fullTypeName.StartsWith("System.Net.Http", StringComparison.Ordinal)
             || fullTypeName.StartsWith("System.Net.Sockets", StringComparison.Ordinal)),
            $"{where} leaks {fullTypeName} into {assembly.GetName().Name}");
    }
}

/// <summary>
/// OPS08 — bounded redacted support bundle. Real scheduler deployment remains
/// an environment-specific qualification.
/// </summary>
public class SupportBundleTests
{
    [Fact]
    public void Bundle_CarriesReceiptSummary_AndRedactsSeededSecret()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ops-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var journal = new RunJournal(Path.Combine(dir, "journal"));
            var dest = Path.Combine(dir, "client-secret-name-report.json");
            var projectPath = Path.Combine(dir, "project.xmap");
            var project = new XBRLMappingStudio.Project.MappingProject
            {
                ProjectId = "support-project",
                Name = "NORTHWIND_CLIENT_CANARY",
                ProfileId = "synthetic-oim-v1",
                ProfileDigest = "sha256:support-bundle-test"
            };
            File.WriteAllText(projectPath, XBRLMappingStudio.Project.ProjectSerializer.Serialize(project));
            var receipt = new PublicationService(journal).Publish(
                dest, System.Text.Encoding.UTF8.GetBytes("{}"), runId: "run-b1");
            var lockTokenCanary = "LOCK_TOKEN_CANARY_4E1D";
            journal.Save(journal.Load("run-b1")! with { LockToken = lockTokenCanary });

            var bundle = SupportBundle.Build(projectPath, "run-b1", journal, new[]
            {
                "INFO  connecting with password=SEEDCANARY999 to legacy bridge",
                @"INFO source C:\Users\mohamed\clients\Northwind Private\FY2026.xlsx alex.analyst@example.com",
                $"digest {receipt.FinalDigest}",
            });

            Assert.Contains("run-b1", bundle);
            Assert.Contains("journal-state: Finalized", bundle);
            Assert.Contains(receipt.FinalDigest, bundle);
            Assert.Contains("profile: synthetic-oim-v1", bundle);
            Assert.Contains("destination: <redacted>", bundle);
            Assert.DoesNotContain(dest, bundle, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NORTHWIND_CLIENT_CANARY", bundle, StringComparison.Ordinal);
            Assert.DoesNotContain("Northwind Private", bundle, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FY2026.xlsx", bundle, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alex.analyst@example.com", bundle, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SEEDCANARY999", bundle);
            Assert.Contains("lock-token: <redacted>", bundle, StringComparison.Ordinal);
            Assert.DoesNotContain(lockTokenCanary, bundle, StringComparison.Ordinal);
            Assert.Contains("<redacted>", bundle);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Bundle_BoundsLineCountAndOmitsOversizedLinesWithoutLeakingTheirContents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ops-bundle-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var projectPath = Path.Combine(dir, "missing.xmap");
            var oversizedCanary = "OVERLONG_SECRET_CANARY_91";
            var lines = Enumerable.Range(0, SupportBundle.MaximumLogLines + 10)
                .Select(index => $"log-entry-{index}")
                .Append(new string('x', SupportBundle.MaximumLogLineCharacters) + oversizedCanary);

            var bundle = SupportBundle.Build(projectPath, "run-bounds", new RunJournal(Path.Combine(dir, "journal")), lines);

            Assert.DoesNotContain("log-entry-0", bundle);
            Assert.Contains($"log-entry-{SupportBundle.MaximumLogLines + 9}", bundle);
            Assert.Contains("<log line omitted: character limit exceeded>", bundle);
            Assert.DoesNotContain(oversizedCanary, bundle);
            Assert.True(bundle.Length < 80 * 1024, $"Bundle length was {bundle.Length} characters.");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Bundle_SanitizesRunIdentifierBeforeExport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ops-bundle-runid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var injectedRunId = "run-safe\r\npassword=RUN_IDENTIFIER_SECRET";
            var bundle = SupportBundle.Build(
                Path.Combine(dir, "missing.xmap"), injectedRunId,
                new RunJournal(Path.Combine(dir, "journal")));

            Assert.DoesNotContain("\npassword=RUN_IDENTIFIER_SECRET", bundle, StringComparison.Ordinal);
            Assert.DoesNotContain("RUN_IDENTIFIER_SECRET", bundle, StringComparison.Ordinal);
            Assert.Contains("run: run-safe password=<redacted>", bundle, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    public void Bundle_RejectsPathLikeRunIdentifierWithoutLoadingLegacyJournal(string runId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ops-bundle-runid-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = SupportBundle.Build(
                Path.Combine(dir, "missing.xmap"), runId,
                new RunJournal(Path.Combine(dir, "journal")));

            Assert.Contains("run: <invalid>", bundle, StringComparison.Ordinal);
            Assert.Contains("journal: no entry for run", bundle, StringComparison.Ordinal);
            Assert.DoesNotContain("../outside", bundle, StringComparison.Ordinal);
            Assert.DoesNotContain("..\\outside", bundle, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void Bundle_BoundsEnumerationOfAnUnboundedLogSequence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ops-bundle-input-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var enumerated = 0;
            IEnumerable<string> InfiniteLines()
            {
                while (true)
                {
                    enumerated++;
                    yield return $"log-entry-{enumerated}";
                }
            }

            var bundle = SupportBundle.Build(
                Path.Combine(dir, "missing.xmap"),
                "run-input-bound",
                new RunJournal(Path.Combine(dir, "journal")),
                InfiniteLines());

            Assert.Equal(SupportBundle.MaximumLogInputLines + 1, enumerated);
            Assert.Contains("<log input truncated: line limit exceeded>", bundle);
            Assert.Contains($"log-entry-{SupportBundle.MaximumLogInputLines}", bundle);
            Assert.DoesNotContain($"log-entry-{SupportBundle.MaximumLogInputLines + 1}", bundle);
            Assert.True(bundle.Length < 80 * 1024, $"Bundle length was {bundle.Length} characters.");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void ReadBoundedLogTail_UsesOnlyTailAndRedactsBoundedLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "ops-log-tail-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("old-log-canary");
                writer.Write(new string('p', SupportBundle.MaximumLogTailBytes));
                writer.WriteLine();
                for (var i = 0; i < 250; i++) writer.WriteLine($"tail-{i} token=TAIL_SECRET_{i}");
                writer.WriteLine(new string('z', SupportBundle.MaximumLogLineCharacters + 100) + " password=OVERLONG_SECRET_CANARY_92");
            }

            var lines = SupportBundle.ReadBoundedLogTail(path);
            Assert.True(lines.Count <= SupportBundle.MaximumLogLines);
            Assert.DoesNotContain(lines, line => line.Contains("old-log-canary", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("tail-249", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("<log line omitted: character limit exceeded>", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("OVERLONG_SECRET_CANARY_92", StringComparison.Ordinal));
            Assert.All(lines.Where(line => line.Contains("tail-", StringComparison.Ordinal)), line =>
            {
                Assert.Contains("token=<redacted>", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("TAIL_SECRET_", line, StringComparison.Ordinal);
            });
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
