# Installed filing profile catalog

The desktop app and CLI read local profile manifests from:

```text
%LOCALAPPDATA%/XBRLMappingStudio/profiles/<profile-id>/<version>/profile.json
```

Set `XMS_PROFILE_CATALOG` to use a different catalog root. The loader is offline-only. Each manifest names an entry point, package files and SHA-256 hashes, namespace mappings, concept metadata, supported units, required checks, and exact HTTP(S)-URI-to-local-file mappings. Every package file is checked before the profile can be resolved; paths must remain under the version directory and cannot pass through symbolic links. The application and Arelle worker repeat the package hash checks before using profile files.

The manifest digest is computed by `FilingProfileManifest.ComputeDigest()` over canonical metadata, package names, local paths, hashes, and offline references. Changing any covered field requires a new digest. The profile ID and version must match their catalog directory names. The built-in `synthetic-oim-v1` ID is reserved.

## Trust and release status

The SHA-256 digest provides content identity and tamper detection. It is not a signature and does not establish the source, redistribution rights, regulator acceptance, or qualification of a package. No real taxonomy is included in this repository. Installed catalog profiles remain blocked from normal CLI and desktop validation until the owner selects an authority and approves taxonomy rights (GAT-01 and GAT-02). The worker factory exposes an authorization policy hook for that future decision; the current application composition does not authorize any real profile.

Do not install customer or regulator assets unless their use and redistribution are authorized. The catalog format is developer infrastructure; it is not yet a profile download, trust, or upgrade service. Arelle profile execution also requires a separately provisioned Python runtime with the pinned worker dependencies.

The current loader limits manifests to 16 MiB, concepts to 250,000, package files to 16,384, each package file to 512 MiB, and the combined package set to 2 GiB. It rejects duplicate JSON properties (case-insensitive), unknown manifest fields, path traversal, unpinned offline-reference targets, inconsistent qualified names, and modified package files.
