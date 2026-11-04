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
    // Write-capable kinds: CSV, Excel output, JSON (IO05 writer) and XML (IO06 writer).
    [InlineData("csv", true)]
    [InlineData("excel", true)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    // Read-only kinds exposed by the pipeline today.
    [InlineData("xbrl-report", false)]
    [InlineData("sqlite", false)]
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
    public void DescribeSourceSuitability_ReflectsDeclaredCapabilities()
    {
        // Every built-in kind supports reading; the sqlite target publisher does not
        // advertise itself as a source.
        foreach (var kind in new[] { "excel", "csv", "json", "xml", "xbrl-report", "sqlite" })
        {
            var conn = new SourceConnection("b1", kind, Path: null);
            Assert.True(MappingJobService.DescribeSourceSuitability(conn).Allowed,
                $"Expected '{kind}' to be readable.");
        }
        var unknown = new SourceConnection("b1", "oracle", Path: null);
        Assert.False(MappingJobService.DescribeSourceSuitability(unknown).Allowed);
    }
}
