using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>Immutable proof of one completed publication (OPS02).</summary>
public sealed record PublicationReceipt(
    string RunId,
    string DestinationPath,
    string StagedDigest,
    string FinalDigest,
    string LockToken,
    DateTimeOffset CompletedUtc)
{
    /// <summary>Digest of the exact staged artifact accepted by the profile worker.</summary>
    public string? ValidatedDigest { get; init; }
}

/// <summary>
/// Five-phase destination publication: Stage → Verify → Validate → Replace → Receipt
/// (OPS02). The payload is written to a private staging sidecar, re-opened and
/// digest-checked, adjudicated by the selected profile worker, and rechecked under a
/// <see cref="DestinationLock"/> before an atomic replacement via <see cref="File.Replace"/>.
/// The journal persists the validated digest and final receipt.
/// A failure injected at any phase leaves the previously accepted destination intact;
/// a crash between phases is adjudicated by <see cref="RunJournal.RecoverUnfinalized"/>.
/// </summary>
public sealed class PublicationService
{
    private readonly RunJournal _journal;

    private readonly PublicationApprovalTrustPolicy? _approvalTrustPolicy;

    public PublicationService(RunJournal journal, PublicationApprovalTrustPolicy? approvalTrustPolicy = null)
    {
        _journal = journal;
        _approvalTrustPolicy = approvalTrustPolicy;
    }

    /// <summary>
    /// Authorize, validate the exact staged report, and publish it at the final write
    /// boundary. The validator must return the digest of the bytes it adjudicated.
    /// </summary>
    public async Task<PublicationReceipt> PublishAuthorizedAsync(
        string destinationPath,
        byte[] payload,
        string workspaceRoot,
        MappingProject project,
        FilingProfile profile,
        bool allowSyntheticProfile,
        JobResult result,
        Func<string, CancellationToken, Task<string>> validateStagedArtifactAsync,
        CancellationToken cancellationToken = default,
        bool refuseDifferentExisting = false,
        string outputKind = "xbrl-json")
    {
        ArgumentNullException.ThrowIfNull(validateStagedArtifactAsync);
        var manifest = result.Manifest;
        var gate = PublicationGate.Evaluate(project, profile, allowSyntheticProfile, manifest.OperationId, _approvalTrustPolicy);
        if (gate.HasBlockingErrors)
        {
            var first = gate.Items.First(d => d.IsBlocking);
            throw PublicationFailed(first.Code, string.Join(Environment.NewLine, gate.Items.Select(d => d.Message)),
                manifest.RunId, destinationPath);
        }

        var graphDigest = project.ComputeSemanticDigest();
        if (manifest.ProjectId != project.ProjectId
            || manifest.SemanticRevision != project.SemanticRevision
            || manifest.ProjectSemanticDigest != graphDigest
            || manifest.ProfileDigest != profile.Digest
            || manifest.IsPreview
            || manifest.Outcome is not (RunOutcome.Success or RunOutcome.SuccessWithWarnings))
            throw PublicationFailed(DiagnosticCodes.XbrApprovalRequired,
                "Run evidence no longer matches the approved project/profile, or the run is not publishable.",
                manifest.RunId, destinationPath);

        if (outputKind is not ("xbrl-json" or "xbrl-xml"))
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                $"Publication does not support XBRL artifact kind '{outputKind}'.",
                manifest.RunId, destinationPath);

        var expectedDigest = RunManifest.ComputeSha256(payload);
        if (!manifest.Outputs.Any(output => output.Kind == outputKind
                && output.Digest == expectedDigest && output.Bytes == payload.Length))
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                "Publication bytes do not match the report artifact recorded by the run.",
                manifest.RunId, destinationPath);

        var safeDestination = PathGuard.EnsureWithin(workspaceRoot, destinationPath);
        EnsureDestinationDoesNotReplaceInput(safeDestination, workspaceRoot, project, manifest);
        var payloadDigest = RunManifest.ComputeSha256(payload);
        var receipt = await PublishCoreAsync(safeDestination,
            async (stream, token) => await stream.WriteAsync(payload.AsMemory(), token).ConfigureAwait(false),
            payloadDigest, payload.LongLength, manifest.RunId, validateStagedArtifactAsync,
            cancellationToken, refuseDifferentExisting: refuseDifferentExisting,
            authorizeBeforeReplace: () => RecheckApproval(project, profile, allowSyntheticProfile, manifest, graphDigest, safeDestination)).ConfigureAwait(false);
        manifest.PublishedArtifactDigest = receipt.FinalDigest;
        manifest.Outputs = manifest.Outputs
            .Select(output => output.Kind == outputKind
                ? output with { Path = safeDestination }
                : output)
            .ToArray();
        return receipt;
    }

    /// <summary>Authorize and stream a disk-backed XBRL artifact through the same staged publication boundary.</summary>
    public async Task<PublicationReceipt> PublishAuthorizedLargeArtifactAsync(
        string destinationPath,
        LargeRunArtifact artifact,
        string workspaceRoot,
        MappingProject project,
        FilingProfile profile,
        bool allowSyntheticProfile,
        JobResult result,
        Func<string, CancellationToken, Task<string>> validateStagedArtifactAsync,
        CancellationToken cancellationToken = default,
        bool refuseDifferentExisting = false,
        string? outputKind = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(validateStagedArtifactAsync);
        var manifest = result.Manifest;
        var selectedOutputKind = outputKind ?? artifact.OutputKind;
        if (selectedOutputKind is not ("xbrl-json" or "xbrl-xml")
            || !StringComparer.Ordinal.Equals(selectedOutputKind, artifact.OutputKind))
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                $"Large-run artifact kind '{artifact.OutputKind}' does not match requested output kind '{selectedOutputKind}'.",
                manifest.RunId, destinationPath);
        var gate = PublicationGate.Evaluate(project, profile, allowSyntheticProfile, manifest.OperationId, _approvalTrustPolicy);
        if (gate.HasBlockingErrors)
        {
            var first = gate.Items.First(diagnostic => diagnostic.IsBlocking);
            throw PublicationFailed(first.Code, string.Join(Environment.NewLine, gate.Items.Select(diagnostic => diagnostic.Message)),
                manifest.RunId, destinationPath);
        }
        var graphDigest = project.ComputeSemanticDigest();
        if (manifest.ProjectId != project.ProjectId
            || manifest.SemanticRevision != project.SemanticRevision
            || manifest.ProjectSemanticDigest != graphDigest
            || manifest.ProfileDigest != profile.Digest
            || manifest.IsPreview
            || manifest.Outcome is not (RunOutcome.Success or RunOutcome.SuccessWithWarnings))
            throw PublicationFailed(DiagnosticCodes.XbrApprovalRequired,
                "Run evidence no longer matches the approved project/profile, or the run is not publishable.",
                manifest.RunId, destinationPath);

        if (!manifest.Outputs.Any(output => output.Kind == selectedOutputKind
                && output.Digest == artifact.Sha256 && output.Bytes == artifact.Bytes))
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                "Publication artifact does not match the report recorded by the run.", manifest.RunId, destinationPath);

        var safeDestination = PathGuard.EnsureWithin(workspaceRoot, destinationPath);
        EnsureDestinationDoesNotReplaceInput(safeDestination, workspaceRoot, project, manifest);
        var receipt = await PublishCoreAsync(safeDestination,
            (stream, token) => artifact.CopyReportToAsync(stream, token),
            artifact.Sha256, artifact.Bytes, manifest.RunId, validateStagedArtifactAsync,
            cancellationToken, refuseDifferentExisting: refuseDifferentExisting,
            authorizeBeforeReplace: () => RecheckApproval(project, profile, allowSyntheticProfile, manifest, graphDigest, safeDestination)).ConfigureAwait(false);
        manifest.PublishedArtifactDigest = receipt.FinalDigest;
        manifest.Outputs = manifest.Outputs.Select(output => output.Kind == selectedOutputKind
            ? output with { Path = safeDestination } : output).ToArray();
        return receipt;
    }

    /// <summary>
    /// Publish <paramref name="payload"/> to <paramref name="destinationPath"/>.
    /// <paramref name="beforeReplaceFault"/> exists for fault-injection tests that must
    /// prove the prior accepted output survives a mid-replace failure; production callers
    /// pass null.
    /// </summary>
    internal PublicationReceipt Publish(
        string destinationPath,
        byte[] payload,
        string? runId = null,
        Action<string>? beforeReplaceFault = null,
        TimeSpan? lockTimeout = null,
        Func<string, CancellationToken, Task<string>>? validateStagedArtifactAsync = null,
        CancellationToken cancellationToken = default,
        bool refuseDifferentExisting = false,
        Action<string>? afterAtomicReplaceForTest = null)
        => PublishCoreAsync(destinationPath,
            async (stream, token) => await stream.WriteAsync(payload.AsMemory(), token).ConfigureAwait(false),
            RunManifest.ComputeSha256(payload), payload.LongLength, runId, validateStagedArtifactAsync,
            cancellationToken, beforeReplaceFault, lockTimeout, refuseDifferentExisting,
            afterAtomicReplaceForTest).GetAwaiter().GetResult();

    private async Task<PublicationReceipt> PublishCoreAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writePayloadAsync,
        string expectedPayloadDigest,
        long expectedPayloadBytes,
        string? runId,
        Func<string, CancellationToken, Task<string>>? validateStagedArtifactAsync,
        CancellationToken cancellationToken,
        Action<string>? beforeReplaceFault = null,
        TimeSpan? lockTimeout = null,
        bool refuseDifferentExisting = false,
        Action<string>? afterAtomicReplaceForTest = null,
        Action? authorizeBeforeReplace = null)
    {
        runId ??= "run-" + Guid.NewGuid().ToString("N");
        var full = Path.GetFullPath(destinationPath);
        var stagedPath = full + $".xbsl-staging-{runId}";

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // ---- Stage: private sidecar write, never the destination itself.
        string stagedDigest;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var output = new FileStream(stagedPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await writePayloadAsync(output, cancellationToken).ConfigureAwait(false);
            stagedDigest = DestinationLock.DigestFile(stagedPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagedPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(stagedPath);
            throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                $"Staging write for run '{runId}' failed: {ex.Message}", runId, full);
        }
        try
        {
            _journal.Save(new JournalEntry(runId, full, JournalState.Staged,
                stagedDigest, null, null, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            TryDelete(stagedPath);
            throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                $"Could not record the staged publication in the run journal: {ex.Message}", runId, full);
        }

        // ---- Verify: re-open the staged bytes and confirm the digest matches the intent.
        var stagedBytes = new FileInfo(stagedPath).Length;
        if (stagedDigest != expectedPayloadDigest || stagedBytes != expectedPayloadBytes)
        {
            Abort(runId, full, stagedPath, stagedDigest, null);
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                $"Staged artifact digest or byte count does not match the expected payload {expectedPayloadDigest} ({expectedPayloadBytes} bytes).",
                runId, full);
        }
        try
        {
            _journal.Save(new JournalEntry(runId, full, JournalState.Verified,
                stagedDigest, null, null, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Abort(runId, full, stagedPath, stagedDigest, null);
            throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                $"Could not record staged-byte verification: {ex.Message}", runId, full);
        }

        var validatedDigest = stagedDigest;
        if (validateStagedArtifactAsync is not null)
        {
            try
            {
                validatedDigest = await validateStagedArtifactAsync(stagedPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Abort(runId, full, stagedPath, stagedDigest, null);
                if (ex is PublicationException or OperationCanceledException) throw;
                throw PublicationFailed(DiagnosticCodes.XbrValidationIncomplete,
                    $"Staged artifact validation did not complete: {ex.Message}", runId, full);
            }

            var postValidationDigest = TryDigest(stagedPath);
            if (!StringComparer.Ordinal.Equals(validatedDigest, stagedDigest)
                || !StringComparer.Ordinal.Equals(postValidationDigest, stagedDigest))
            {
                Abort(runId, full, stagedPath, stagedDigest, null,
                    StringComparer.Ordinal.Equals(validatedDigest, stagedDigest) ? stagedDigest : null);
                throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                    "The staged artifact changed during validation or the validator reported a different digest.",
                    runId, full);
            }

            try
            {
                _journal.Save(new JournalEntry(runId, full, JournalState.Validated,
                    stagedDigest, null, null, DateTimeOffset.UtcNow)
                { ValidatedDigest = validatedDigest });
            }
            catch (Exception ex)
            {
                Abort(runId, full, stagedPath, stagedDigest, null, validatedDigest);
                throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                    $"Could not record staged validation evidence: {ex.Message}", runId, full);
            }
        }

        // ---- Replace: acquire the destination lock (waited, so concurrent publishers
        // serialize), then swap atomically.
        DestinationLock lk;
        try
        {
            lk = await DestinationLock.AcquireAsync(
                full, lockTimeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Abort(runId, full, stagedPath, stagedDigest, null,
                validateStagedArtifactAsync is null ? null : validatedDigest);
            throw;
        }

        using (lk)
        {
            try { authorizeBeforeReplace?.Invoke(); }
            catch
            {
                Abort(runId, full, stagedPath, stagedDigest, lk.Token, validatedDigest);
                throw;
            }
            try
            {
                _journal.Save(new JournalEntry(runId, full,
                    validateStagedArtifactAsync is null ? JournalState.Verified : JournalState.Validated,
                    stagedDigest, null, lk.Token, DateTimeOffset.UtcNow)
                { ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest });
            }
            catch (Exception ex)
            {
                Abort(runId, full, stagedPath, stagedDigest, lk.Token,
                    validateStagedArtifactAsync is null ? null : validatedDigest);
                throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                    $"Could not record the locked publication state: {ex.Message}", runId, full);
            }

            if (refuseDifferentExisting && lk.DestinationDigestAtAcquire is { } existingDigest)
            {
                if (!StringComparer.Ordinal.Equals(existingDigest, validatedDigest))
                {
                    Abort(runId, full, stagedPath, stagedDigest, lk.Token,
                        validateStagedArtifactAsync is null ? null : validatedDigest);
                    throw PublicationFailed(DiagnosticCodes.PubReceiptConflict,
                        "Mixed publication refuses to overwrite an existing file with different bytes.",
                        runId, full);
                }

                // Same-content retry: the destination already satisfies this intent.
                // Record the receipt without replacing or rewriting the existing file.
                TryDelete(stagedPath);
                var unchangedReceipt = new PublicationReceipt(runId, full, stagedDigest,
                    existingDigest, lk.Token, DateTimeOffset.UtcNow)
                {
                    ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest
                };
                _journal.Save(new JournalEntry(runId, full, JournalState.Finalized,
                    stagedDigest, existingDigest, lk.Token, unchangedReceipt.CompletedUtc)
                { ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest });
                return unchangedReceipt;
            }

            var backupPath = full + $".xbsl-prev-{runId}";
            try
            {
                beforeReplaceFault?.Invoke(stagedPath); // test seam: simulate host failure here
                var preReplaceDigest = TryDigest(stagedPath);
                if (!StringComparer.Ordinal.Equals(preReplaceDigest, validatedDigest))
                {
                    Abort(runId, full, stagedPath, stagedDigest, lk.Token,
                        validateStagedArtifactAsync is null ? null : validatedDigest);
                    throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                        "The staged artifact changed after validation and before replacement.", runId, full);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(full))
                    File.Replace(stagedPath, full, backupPath);
                else
                    File.Move(stagedPath, full);
            }
            catch (PublicationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                Abort(runId, full, stagedPath, stagedDigest, lk.Token,
                    validateStagedArtifactAsync is null ? null : validatedDigest);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Prior destination untouched by the failed swap; staging is cleaned up.
                TryDelete(stagedPath);
                TryDelete(backupPath);
                _journal.Save(new JournalEntry(runId, full, JournalState.Aborted,
                    stagedDigest, null, lk.Token, DateTimeOffset.UtcNow)
                { ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest });
                throw PublicationFailed(DiagnosticCodes.PubPartialFailure,
                    $"Atomic replace failed for run '{runId}'; prior destination preserved. {ex.Message}",
                    runId, full);
            }
            afterAtomicReplaceForTest?.Invoke(full);
            _journal.Save(new JournalEntry(runId, full, JournalState.Replaced,
                stagedDigest, null, lk.Token, DateTimeOffset.UtcNow)
            { ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest });

            // ---- Verify-after-replace + Receipt: final bytes must equal the staged ones.
            var finalDigest = DestinationLock.DigestFile(full);
            if (!StringComparer.Ordinal.Equals(finalDigest, validatedDigest))
            {
                throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                    $"Post-replace digest {finalDigest} diverges from validated {validatedDigest}.",
                    runId, full);
            }
            TryDelete(backupPath); // prior accepted copy no longer needed
            var receipt = new PublicationReceipt(runId, full, stagedDigest, finalDigest,
                lk.Token, DateTimeOffset.UtcNow)
            {
                ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest
            };
            _journal.Save(new JournalEntry(runId, full, JournalState.Finalized,
                stagedDigest, finalDigest, lk.Token, DateTimeOffset.UtcNow)
            { ValidatedDigest = validateStagedArtifactAsync is null ? null : validatedDigest });
            return receipt;
        }
    }

    private void RecheckApproval(MappingProject project, FilingProfile profile, bool allowSyntheticProfile,
        RunManifest manifest, string approvedGraphDigest, string destination)
    {
        var gate = PublicationGate.Evaluate(project, profile, allowSyntheticProfile, manifest.OperationId, _approvalTrustPolicy);
        if (gate.HasBlockingErrors || project.ComputeSemanticDigest() != approvedGraphDigest
            || project.SemanticRevision != manifest.SemanticRevision)
            throw PublicationFailed(DiagnosticCodes.XbrApprovalRequired,
                "Approval or executable project identity changed before publication replacement.", manifest.RunId, destination);
    }

    private void Abort(string runId, string full, string stagedPath, string? stagedDigest,
        string? lockToken, string? validatedDigest = null)
    {
        TryDelete(stagedPath);
        _journal.Save(new JournalEntry(runId, full, JournalState.Aborted,
            stagedDigest, null, lockToken, DateTimeOffset.UtcNow)
        { ValidatedDigest = validatedDigest });
    }

    private static void EnsureDestinationDoesNotReplaceInput(
        string destinationPath,
        string workspaceRoot,
        MappingProject project,
        RunManifest manifest)
    {
        var protectedPathDiagnostics = DestinationPlanValidator.ValidateFileDestinationPath(destinationPath);
        if (protectedPathDiagnostics.HasBlockingErrors)
            throw PublicationFailed(
                protectedPathDiagnostics.Items.First(diagnostic => diagnostic.IsBlocking).Code,
                string.Join(Environment.NewLine, protectedPathDiagnostics.Items.Select(diagnostic => diagnostic.Message)),
                manifest.RunId, destinationPath);

        var sourceId = DestinationPathGuard.FindSourceIdAtPath(
            destinationPath,
            manifest.Inputs.Select(input => (input.SourceId, input.OriginalPath))
                .Concat(project.Bindings.Select(binding => (binding.BindingId, binding.Locator))),
            workspaceRoot);
        if (sourceId is not null)
            throw PublicationFailed(DiagnosticCodes.IoUnsupportedOperation,
                $"Report destination cannot replace source input '{sourceId}'. Choose a different destination path.",
                manifest.RunId, destinationPath);
    }

    private static string? TryDigest(string path)
    {
        try { return File.Exists(path) ? DestinationLock.DigestFile(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static PublicationException PublicationFailed(string code, string message,
        string runId, string destination)
        => new(code, message, runId, destination);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>Publication failure carrying the stable diagnostic code for the surface layer.</summary>
public sealed class PublicationException : Exception
{
    public string Code { get; }
    public string RunId { get; }
    public string DestinationPath { get; }

    public PublicationException(string code, string message, string runId, string destinationPath)
        : base(message)
    {
        Code = code;
        RunId = runId;
        DestinationPath = destinationPath;
    }

    public Diagnostic ToDiagnostic() => new()
    {
        Code = Code,
        Subsystem = DiagnosticSubsystem.Publication,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Publication,
        IsBlocking = true,
        Message = Message,
        OperationId = RunId,
    };
}
