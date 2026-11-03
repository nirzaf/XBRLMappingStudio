using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>IO01 — direction gate: a read-only source adapter cannot be bound as a write
/// target, and a write-only sink cannot be bound as a read source.</summary>
public sealed class DirectionGateTests
{
    [Fact]
    public void ReadOnlySourceAdapter_CannotBeWriteTarget()
    {
        var ex = Assert.Throws<AdapterException>(() =>
            MappingJobService.AssertDirection(AdapterDirection.Write, new ExcelAdapter().Capabilities, "excel"));
        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, ex.Code);
    }

    [Fact]
    public void ReadWriteAdapter_CanBeWriteTarget()
    {
        // Should not throw.
        MappingJobService.AssertDirection(AdapterDirection.Write, new CsvAdapter().Capabilities, "csv");
    }

    [Fact]
    public void WriteOnlySink_CannotBeReadSource()
    {
        var ex = Assert.Throws<AdapterException>(() =>
            MappingJobService.AssertDirection(AdapterDirection.Read, new ExcelOutputAdapter().Capabilities, "excel"));
        Assert.Equal(DiagnosticCodes.IoUnsupportedOperation, ex.Code);
    }

    [Fact]
    public void BuildTargetAdapter_ExcelKind_ReturnsWritableAdapter()
    {
        var adapter = MappingJobService.BuildTargetAdapter(
            new SourceConnection("o", "excel", "/tmp/whatever.xlsx"));
        Assert.True(adapter.Capabilities.AllowsWriteTarget);
    }

    [Fact]
    public void BuildTargetAdapter_UnknownKind_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            MappingJobService.BuildTargetAdapter(new SourceConnection("o", "db", "/tmp/x")));
    }
}
