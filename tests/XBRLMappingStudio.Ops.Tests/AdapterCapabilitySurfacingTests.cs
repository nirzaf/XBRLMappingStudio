using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// IO01 capability surfacing. The designer asks the shared job service whether a
/// connector is eligible as a target before mapping starts; a read-only source
/// adapter must be refused with a stable reason so the UI can disable the option
/// in place rather than fail at publication time.
/// </summary>
public sealed class AdapterCapabilitySurfacingTests
{
    [Theory]
    // Supported sinks: transactional SQLite publication and staged CSV/XLSX/JSON/XML files.
    [InlineData("sqlite", true)]
    [InlineData("csv", true)]
    [InlineData("excel", true)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    // Read-only kinds exposed by the pipeline today, plus conditional/unknown providers.
    [InlineData("xbrl-report", false)]
    [InlineData("pdf-digital", false)]
    [InlineData("oracle", false)]
    // Unknown kind: no target adapter exists.
    [InlineData("db2", false)]
    public void DescribeTargetSuitability_ReflectsDeclaredCapabilities(string kind, bool expected)
    {
        var conn = new SourceConnection("b1", kind, Path: null);
        var result = MappingJobService.DescribeTargetSuitability(conn);
        Assert.Equal(expected, result.Allowed);
        if (!expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Reason),
                $"Refusing '{kind}' as a target must carry a stable reason for the UI.");
        }
        else
        {
            Assert.Null(result.Reason);
        }
    }

    [Fact]
    public void DescribeTargetSuitability_ReadExcel_StillAllowedBecauseWriteIsSeparateKind()
    {
        // The Excel source adapter is read-only; the Excel *output* adapter is the sink.
        // Both live under kind 'excel' but only the target resolution path is consulted,
        // which returns the write-capable ExcelOutputAdapter.
        var conn = new SourceConnection("b1", "excel", Path: null);
        var result = MappingJobService.DescribeTargetSuitability(conn);
        Assert.True(result.Allowed);
        Assert.Equal("excel", result.Kind);
    }

    [Fact]
    public void DescribeTargetSuitability_UsesDedicatedTransactionalPublisherForSqlite()
    {
        var result = MappingJobService.DescribeTargetSuitability(
            new SourceConnection("b1", "sqlite", Path: null));

        Assert.True(result.Allowed);
        Assert.Null(result.Reason);
        Assert.NotNull(result.Capabilities);
        Assert.True(result.Capabilities!.AllowsWriteTarget);
        Assert.True(result.Capabilities.SupportsPublication);
    }

    [Fact]
    public void DescribeSourceSuitability_ReflectsDeclaredCapabilities()
    {
        foreach (var kind in new[] { "excel", "csv", "json", "xml", "xbrl-report", "sqlite", "pdf-digital" })
        {
            var conn = new SourceConnection("b1", kind, Path: null);
            var result = MappingJobService.DescribeSourceSuitability(conn);
            Assert.True(result.Allowed, $"Expected '{kind}' to be readable.");
            Assert.Null(result.Reason);
            Assert.NotNull(result.Capabilities);
            Assert.True(result.Capabilities!.SupportsReading);
        }

        var unknown = new SourceConnection("b1", "oracle", Path: null);
        var unavailableProvider = MappingJobService.DescribeSourceSuitability(unknown);
        Assert.False(unavailableProvider.Allowed);
        Assert.Contains("not implemented or qualified", unavailableProvider.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSuitability_EnforcesDirectionForReportAndSqlite()
    {
        var report = new SourceConnection("b1", "xbrl-report", Path: null);
        Assert.True(MappingJobService.DescribeSourceSuitability(report).Allowed);
        Assert.False(MappingJobService.DescribeTargetSuitability(report).Allowed);

        var sqlite = new SourceConnection("b1", "sqlite", Path: null);
        Assert.True(MappingJobService.DescribeSourceSuitability(sqlite).Allowed);
        Assert.True(MappingJobService.DescribeTargetSuitability(sqlite).Allowed);
    }
}
