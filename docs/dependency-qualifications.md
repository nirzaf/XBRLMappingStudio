# Dependency qualifications

This file records dependency decisions that affect supported input and output
formats. A package upgrade is a deliberate compatibility change: inspect its
license and API, update this record, and run the format-specific fixtures before
changing the pin.

## PDF text extraction

- Package: `PdfPig` 0.1.16, pinned in `XBRLMappingStudio.Adapters.csproj`.
- License: Apache-2.0, as declared by the [NuGet package metadata](https://www.nuget.org/packages/PdfPig/0.1.16).
- API and supported behavior: reads text and per-letter geometry from author-owned
  digital PDFs. Coordinates are PDF page coordinates with a lower-left origin;
  character order in a content stream is not assumed to be visual reading order.
- Scope: digital text extraction only. This dependency does not add rasterization,
  OCR, financial-value interpretation, or a filing-profile qualification.
- Compatibility policy: PdfPig is pre-1.0 and its project documentation warns
  that public API changes may occur between minor versions. Keep the exact version
  pin; review release notes and rerun the qualification probes before any upgrade.
- Fixture rights: qualification samples are generated from synthetic text using
  the PDF's Standard 14 Helvetica font. No customer documents, external taxonomy,
  or separately licensed font files are included.
