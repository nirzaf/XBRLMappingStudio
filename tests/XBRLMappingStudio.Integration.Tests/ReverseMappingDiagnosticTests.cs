using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ReverseMappingDiagnosticTests
{
    [Fact]
    public void ReportSource_RecordsNonBlockingReconstructionLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("report-binding", "xbrl-report", "synthetic-report.json"),
            "operation-123");

        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal(DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.IsBlocking);
        Assert.Equal("operation-123", warning.OperationId);
        Assert.Equal(new SourceReference("report-binding", "fact-occurrences"), warning.Source);
        Assert.Equal("source-detail-not-present-in-report", warning.Details!["limitation"]);
        Assert.Contains("cannot reconstruct source detail absent from the report", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonReportSource_DoesNotRecordReverseMappingLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("workbook-binding", "excel", "synthetic.xlsx"),
            "operation-123");

        Assert.Empty(diagnostics.Items);
    }
}
