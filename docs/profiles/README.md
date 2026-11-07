# Installed filing profile catalog

The desktop app and CLI read local profile manifests from:

```text
%LOCALAPPDATA%/XBRLMappingStudio/profiles/<profile-id>/<version>/profile.json
```

Set `XMS_PROFILE_CATALOG` to use a different catalog root. The loader is offline-only. Each manifest names an entry point, package files and SHA-256 hashes, namespace mappings, concept metadata, supported units, required checks, and exact HTTP(S)-URI-to-local-file mappings. Every package file is checked before the profile can be resolved; paths must remain under the version directory and cannot pass through symbolic links. The application and Arelle worker repeat the package hash checks before using profile files.

The current manifest uses schema version 4; versions 2 and 3 remain readable with their original digest contracts. The digest is computed by `FilingProfileManifest.ComputeDigest()` over canonical metadata, package names, local paths, hashes, offline references, and required plugin/check IDs. Version 3 adds dimension domains, and version 4 adds presentation relationships. Changing any covered field requires a new digest. The profile ID and version must match their catalog directory names. The built-in `synthetic-oim-v1` ID is reserved. Required Arelle plugin IDs are bounded, validated, passed as separate worker arguments, and joined with Arelle's documented `|` separator in runtime options.

To install a previously obtained offline package, pass its `<profile-id>/<version>` directory to `xbrl-studio install-profile`; use `--catalog <directory>` to select a catalog other than `%LOCALAPPDATA%/XBRLMappingStudio/profiles` or `XMS_PROFILE_CATALOG`. The installer validates the source and copied bytes against the manifest and package hashes, refuses to replace an installed version, and commits the verified directory atomically. It copies only the manifest and pinned package files. Every installed version receives a regular `.disabled` marker, so installation alone cannot authorize or enable a filing profile. Remove that marker only under the applicable owner approval and profile authorization process.

To disable one installed version locally, place a regular `.disabled` file beside its `profile.json`. The marker does not alter the profile digest or other installed versions. Symlink/reparse-point and directory markers are rejected. CLI and desktop resolution fail closed with `XBR018` before source access while the marker exists; removing it re-enables the version only subject to all normal profile authorization gates.

The worker handshake returns the protocol version, supported operations, Arelle availability, and the exact installed profile schema, ID, version, digest, entry point, and required plugins. Before reading source data, `MappingJobService` checks that identity and asks the worker to load the selected taxonomy entry point. A successful load returns the same versioned profile metadata and concept count; an identity mismatch, unsupported protocol, missing runtime/plugin, timeout, crash, or taxonomy-load diagnostic stops the run before source inspection. Worker requests are serialized per process and each process owns its Arelle session state.

## Trust and release status

The SHA-256 digest provides content identity and tamper detection. It is not a signature and does not establish the source, redistribution rights, regulator acceptance, or qualification of a package. No real taxonomy is included in this repository. Installed catalog profiles remain blocked from normal CLI and desktop validation until the owner selects an authority and approves taxonomy rights (GAT-01 and GAT-02). The worker factory exposes an authorization policy hook for that future decision; the current application composition does not authorize any real profile.

Do not install customer or regulator assets unless their use and redistribution are authorized. The installer accepts a local directory only; it does not download packages, establish trust, qualify them for regulator filing, or upgrade/replace installed versions. Arelle profile execution also requires a separately provisioned Python runtime with the pinned worker dependencies.

The current loader limits manifests to 16 MiB, concepts to 250,000, package files to 16,384, required plugins to 128, each package file to 512 MiB, and the combined package set to 2 GiB. It rejects unsupported schema versions, duplicate JSON properties (case-insensitive), unknown manifest fields, path traversal, unpinned offline-reference targets, inconsistent qualified names, and modified package files.
