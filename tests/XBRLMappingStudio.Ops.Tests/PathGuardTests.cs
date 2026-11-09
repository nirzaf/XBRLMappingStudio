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
    public void Redaction_MasksCredentialAssignments(string line, string secret)
    {
        var redacted = SupportBundle.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void Redaction_LeavesOrdinaryTextAlone()
    {
        const string line = "run run-42 completed with 12 facts";
        Assert.Equal(line, SupportBundle.Redact(line));
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
            var dest = Path.Combine(dir, "out.json");
            var receipt = new PublicationService(journal).Publish(
                dest, System.Text.Encoding.UTF8.GetBytes("{}"), runId: "run-b1");

            var bundle = SupportBundle.Build(dest, "run-b1", journal, new[]
            {
                "INFO  connecting with password=SEEDCANARY999 to legacy bridge",
                $"digest {receipt.FinalDigest}",
            });

            Assert.Contains("run-b1", bundle);
            Assert.Contains("journal-state: Finalized", bundle);
            Assert.Contains(receipt.FinalDigest, bundle);
            Assert.DoesNotContain("SEEDCANARY999", bundle);
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
