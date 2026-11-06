# Dependency qualifications

This file records dependency decisions that affect supported input and output
formats. A package upgrade is a deliberate compatibility change: inspect its
license and API, update this record, and run the format-specific fixtures before
changing the pin.

## Reproducible .NET and worker dependencies

- Every solution project has a checked-in `packages.lock.json`. Repository-wide
  restore targets `win-x64`, `linux-x64`, and `osx-x64`; the lock files record exact
  package versions, dependency edges, and NuGet content hashes for those targets.
  Desktop also has `packages.Release.lock.json`: its Debug-only Avalonia diagnostics
  package makes the Debug and Release dependency graphs differ.
- CI and release restore use `--locked-mode`. Release publishing restores the
  complete Release-configuration locked RID graph first and publishes each RID with
  `--no-restore`; the Release graph excludes the Debug-only diagnostics package.
- The Arelle worker uses `tools/worker/requirements.lock`, which pins package
  versions and Python distribution hashes; CI installs it with `pip install --require-hashes`.
- `tools/dependency_inventory.py` builds an SPDX 2.3 component inventory from the
  CLI/Desktop release lock graphs and worker lock. NuGet package hashes and
  machine-readable license expressions are included where declared; Python SPDX
  expressions are copied only from installed wheel `License-Expression` metadata.
  Missing license assertions stay `NOASSERTION`, and accepted worker distribution hashes
  remain listed in each Python component's evidence comment.
- CI and release verification upload the SPDX document. The draft GitHub Release
  includes the SBOM and `SHA256SUMS.txt` covering the release assets. CI also uploads
  the direct/transitive NuGet vulnerability report with each OS test artifact.
- This is component-level SBOM and dependency-lock evidence, not per-file license
  analysis or redistribution approval. Package-contained native components are
  represented at package level; no OCR/rasterizer is currently installed or qualified.
  Any `NOASSERTION` entry, per-file license obligations, and redistribution terms
  require review before distribution.

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
