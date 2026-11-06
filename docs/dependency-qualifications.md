# Dependency qualifications

This file records dependency decisions that affect supported input and output
formats. A package upgrade is a deliberate compatibility change: inspect its
license and API, update this record, and run the format-specific fixtures before
changing the pin.

## Reproducible .NET and worker dependencies

- Every solution project has a checked-in `packages.lock.json`. Repository-wide
  restore targets `win-x64`, `linux-x64`, and `osx-x64`; the lock files record exact
  package versions, dependency edges, and NuGet content hashes for those targets.
- CI and release restore use `--locked-mode`. Release publishing restores the
  complete locked RID graph first and publishes each RID with `--no-restore`.
- The Arelle worker uses `tools/worker/requirements.lock`, which pins package
  versions and wheel hashes; CI installs it with `pip install --require-hashes`.
- CI uploads the machine-readable direct/transitive NuGet vulnerability report with
  each OS test artifact. PDF extraction's Apache-2.0 license and API qualification
  are recorded below.
- This is dependency-lock and vulnerability-review evidence, not a complete SPDX
  inventory. Per-file license manifests for every transitive .NET/Python/native
  component, OCR/rasterizer dependencies, platform artifact hashes, and redistribution
  review remain open.

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
