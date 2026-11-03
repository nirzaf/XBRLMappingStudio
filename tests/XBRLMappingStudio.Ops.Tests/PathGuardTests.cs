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
/// OPS08 (partial) — redacted support bundle for one run. Scheduler integration
/// remains owner-gated (needs the customer scheduler to drive unattended runs).
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
}
