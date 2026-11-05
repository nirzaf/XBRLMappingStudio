using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Desktop.ViewModels;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Desktop.Tests;

public sealed class PdfReviewViewModelTests
{
    [Fact]
    public async Task PdfReview_LinksValueSelectionToPageEvidenceAndRequiresExplicitApproval()
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("XMS_PDF_REVIEW_FIXTURE_DIR");
        var directory = string.IsNullOrWhiteSpace(fixtureDirectory)
            ? Path.Combine(Path.GetTempPath(), "xms-pdf-review-ui-tests", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(fixtureDirectory);
        Directory.CreateDirectory(directory);
        var pdfPath = Path.Combine(directory, "review-sample.pdf");
        var projectPath = Path.Combine(directory, "mapping.xmap");
        var statePath = Path.Combine(directory, "application-state.db");
        try
        {
            var pdfBytes = BuildReviewPdf();
            await File.WriteAllBytesAsync(pdfPath, pdfBytes);
            var previousManifestPath = pdfPath + ".xms-review.json";
            const string previousReviewContent = "preserve this existing review artifact";
            await File.WriteAllTextAsync(previousManifestPath, previousReviewContent);
            var project = BuildProject();
            var viewModel = new MainWindowViewModel(project, null,
                (_, _) => Task.FromException<ISourceSchema>(new InvalidOperationException("PDF does not use the SQLite schema loader.")),
                pdfDocumentLoader: async (path, token) => PdfDigitalDocumentExtractor.Extract(
                    await File.ReadAllBytesAsync(path, token)),
                pdfEvidenceStoreFactory: () => new PdfEvidenceStore(new LocalApplicationDatabase(statePath)));
            viewModel.SaveProjectTo(projectPath);

            await viewModel.LoadSourceAsync(pdfPath);

            Assert.True(viewModel.HasPdfDocument);
            Assert.Equal(2, viewModel.PdfPageCount);
            Assert.Empty(viewModel.SourceColumns);
            Assert.Null(viewModel.Session.Project.Bindings.Single().SchemaFingerprint);
            Assert.Contains("not a verified report", viewModel.PdfReviewStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(viewModel.PdfPageWords, word => word.Text == "Description");

            viewModel.PdfRowLabelHeader = "Description";
            viewModel.PdfPeriodHeaders = "2025=2025; 2024=2024";
            viewModel.SelectedPdfRowDirection = viewModel.PdfRowDirections[0];
            viewModel.PdfDecimalSeparator = ".";
            viewModel.PdfGroupSeparator = ",";
            viewModel.PdfAllowGrouping = true;
            viewModel.PdfScaleFactor = "1";
            viewModel.PdfCurrencyCode = "USD";
            viewModel.BuildPdfTemplateCommand.Execute(null);

            Assert.Equal(4, viewModel.PdfReviewValues.Count);
            Assert.All(viewModel.PdfReviewValues, value => Assert.True(value.CanApprove));
            Assert.All(viewModel.PdfReviewValues, value => Assert.False(value.IsApproved));
            Assert.False(viewModel.CanSavePdfReview);

            var selected = viewModel.PdfReviewValues.Last();
            viewModel.SelectedPdfReviewValue = selected;
            Assert.Equal(2, viewModel.CurrentPdfPageNumber);
            Assert.Equal(2, viewModel.CurrentPdfPage?.PageNumber);
            Assert.Equal(selected.Evidence, viewModel.SelectedPdfEvidence);
            Assert.Contains(viewModel.PdfPageWords, word => word.Text == "Profit");
            Assert.Contains(viewModel.PdfPageWords, word => word.AccessibleName.Contains("PDF page 2", StringComparison.Ordinal));

            viewModel.CurrentPdfPageNumber = 1;
            viewModel.NextPdfPageCommand.Execute(null);
            Assert.Equal(2, viewModel.CurrentPdfPageNumber);
            viewModel.PreviousPdfPageCommand.Execute(null);
            Assert.Equal(1, viewModel.CurrentPdfPageNumber);
            viewModel.PdfReviewZoom = 1.75;
            Assert.Equal(1.75, viewModel.PdfReviewZoom);

            foreach (var value in viewModel.PdfReviewValues) value.IsApproved = true;
            Assert.False(viewModel.CanSavePdfReview);
            viewModel.PdfReviewer = "fixture-reviewer";
            Assert.True(viewModel.CanSavePdfReview);

            viewModel.PdfCurrencyCode = "EUR";
            Assert.Empty(viewModel.PdfReviewValues);
            Assert.False(viewModel.CanSavePdfReview);
            viewModel.PdfCurrencyCode = "USD";
            viewModel.BuildPdfTemplateCommand.Execute(null);
            foreach (var value in viewModel.PdfReviewValues) value.IsApproved = true;
            Assert.True(viewModel.CanSavePdfReview);

            await viewModel.SavePdfReviewAsync();

            var savedBinding = Assert.Single(viewModel.Session.Project.Bindings);
            Assert.True(savedBinding.SchemaFingerprint is not null, $"{viewModel.StatusText} {viewModel.PdfReviewStatus}");
            Assert.Equal(5, viewModel.SourceColumns.Count);
            var configuredManifest = savedBinding.Configuration[PdfSourceAdapter.ReviewManifestConfigurationKey]?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(configuredManifest));
            Assert.NotEqual(Path.GetFileName(previousManifestPath), configuredManifest);
            Assert.True(File.Exists(Path.Combine(directory, configuredManifest!)));
            Assert.Equal(previousReviewContent, await File.ReadAllTextAsync(previousManifestPath));
            Assert.Contains("validated the PDF review sidecar", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            viewModel.SaveProjectTo(projectPath);

            var csvPath = Path.Combine(directory, "replacement.csv");
            await File.WriteAllTextAsync(csvPath, "value\r\n1\r\n");
            await viewModel.LoadSourceAsync(csvPath);
            Assert.False(viewModel.HasPdfDocument);
            Assert.Empty(viewModel.PdfPageWords);
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(fixtureDirectory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static MappingProject BuildProject()
    {
        var profile = SyntheticProfile.Build();
        var project = new MappingProject
        {
            ProjectId = Id.New(),
            Name = "PDF review UI test",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = profile.Digest
        };
        project.Bindings.Add(new SourceBinding
        {
            BindingId = "source",
            Kind = "csv",
            DisplayName = "Source"
        });
        return project;
    }

    private static byte[] BuildReviewPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PageSize.A4);
        AddHeader(first, font);
        first.AddText("Revenue", 10, new PdfPoint(50, 680), font);
        first.AddText("1,250.00", 10, new PdfPoint(250, 680), font);
        first.AddText("900.50", 10, new PdfPoint(410, 680), font);

        var second = builder.AddPage(PageSize.A4);
        AddHeader(second, font);
        second.AddText("Profit", 10, new PdfPoint(50, 680), font);
        second.AddText("500.25", 10, new PdfPoint(250, 680), font);
        second.AddText("250.00", 10, new PdfPoint(410, 680), font);
        return builder.Build();
    }

    private static void AddHeader(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font)
    {
        page.AddText("Description", 10, new PdfPoint(50, 720), font);
        page.AddText("2025", 10, new PdfPoint(250, 720), font);
        page.AddText("2024", 10, new PdfPoint(410, 720), font);
    }
}
