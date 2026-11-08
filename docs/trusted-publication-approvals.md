# Trusted publication approvals

Non-synthetic publication fails closed unless the application host supplies a `PublicationApprovalTrustPolicy` and the project contains a valid signed approval for the current executable revision, semantic digest, and filing profile. Project approval text alone does not establish trust. XBR016 reports missing, invalid, expired, or unauthorized approval evidence.

The unsigned development exception applies only to the immutable `SyntheticProfile.Build()` instance. The publication gate rejects a copied, deserialized, or modified profile that reuses the reserved synthetic profile ID, even when the project repeats its ID and digest. The built-in fixture's nested collections are read-only so a caller cannot change its validation rules in place.

The host enrolls `TrustedPublicationReviewer` entries containing a unique key ID, exact reviewer identity, authority, and Base64 DER SubjectPublicKeyInfo public key. Enrollment must come from an owner-controlled source outside the project. Pass the policy to `MappingJobService` and `PublicationService`; direct gate users pass `approvalTrustPolicy` to `PublicationGate.Evaluate`. Supplying a policy also requires signed approvals for synthetic profiles. Without a policy, legacy synthetic development approvals remain available only with explicit synthetic opt-in.

## Signing contract

An external reviewer workflow sets `SigningKeyId`, `SignatureAlgorithm` (`ECDSA-P256-SHA256-P1363-v1`), `ApprovedBy`, `Authority`, `ApprovedUtc`, `ExpiresUtc`, `SemanticRevision`, `SemanticDigest`, and `ProfileDigest`. Sign the exact bytes returned by `PublicationApprovalSignature.CreatePayload(project, profile, approval)` with ECDSA P-256/SHA-256 and `IeeeP1363FixedFieldConcatenation`; store the Base64-encoded 64-byte signature in `SignatureBase64`.

The payload is compact UTF-8 JSON written in fixed property order by the helper. It includes a versioned purpose, algorithm, key ID, project ID, revision, semantic digest, profile ID and digest, reviewer, authority, and UTC-normalized timestamps in invariant round-trip format. Do not reserialize or reformat the payload before signing. Notes and layout are not approval claims. The gate independently compares the signed executable digest/revision and profile digest against the current project and resolved profile.

Approval time must not be in the future. Expiry is mandatory, strictly after approval, and strictly later than the host's current time. There is no implicit clock-skew allowance. Duplicate key IDs, unsupported curves/algorithms, trailing key bytes, malformed signatures/keys, and unmatched reviewer/authority fail closed. Public keys are snapshotted when the policy is constructed. Removing or rotating an enrolled key requires constructing a new policy/service instance; hosts own their enrollment and revocation lifecycle and must maintain a reliable clock.

Publication authorization is rechecked at late write boundaries, not just when publication starts: after file staging and input identity verification, inside the SQLite transaction immediately before committing rows and the receipt (including receipt replays), and under the XBRL artifact destination lock. The current approval must remain valid and the project must still match the executable digest and revision captured for the publication attempt. A new valid approval for a changed project cannot authorize stale run output. A denied SQLite callback rolls back both rows and receipt.

## Deployment boundary

This is signature verification infrastructure, not production identity qualification. CLI/Desktop trust enrollment, reviewer authentication, authorization governance, secure signing, key custody, rotation/revocation delivery, and regulatory acceptance remain external and owner-gated. No production private keys are generated or stored. Tests use ephemeral keys. Existing production surfaces that do not inject a trusted policy cannot publish non-synthetic mappings.
