using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Worker;

/// <summary>How to invoke the product's internal, non-user-facing PDF worker mode.</summary>
public sealed record PdfWorkerInvocation(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    bool AppendInternalCommand = true)
{
    public static PdfWorkerInvocation ForCurrentApplication()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process executable could not be resolved.");
        var executableName = Path.GetFileNameWithoutExtension(executable);
        if (StringComparer.OrdinalIgnoreCase.Equals(executableName, "dotnet"))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("The current .NET entry assembly could not be resolved.");
            }
            return new PdfWorkerInvocation(executable, new[] { assemblyPath });
        }

        return new PdfWorkerInvocation(executable, Array.Empty<string>());
    }
}

/// <summary>
/// Reads a bounded source file and sends only its bytes to the isolated PDF parser.
/// Both source reading and parser execution share one caller-visible deadline.
/// </summary>
public sealed class PdfExtractionProcessClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkerSupervisor _supervisor;

    public PdfExtractionProcessClient()
        : this(new WorkerSupervisor(PdfExtractionWorkerProtocol.MaxCapturedLogBytes))
    {
    }

    internal PdfExtractionProcessClient(WorkerSupervisor supervisor)
        => _supervisor = supervisor;

    public async Task<PdfDigitalDocument> ExtractFileAsync(
        string path,
        PdfDigitalExtractionOptions? options = null,
        TimeSpan? deadline = null,
        PdfWorkerInvocation? invocation = null,
        CancellationToken cancellationToken = default)
    {
        var limits = (options ?? new PdfDigitalExtractionOptions()).Validate();
        var allowedTime = deadline ?? TimeSpan.FromSeconds(30);
        if (allowedTime <= TimeSpan.Zero || allowedTime > TimeSpan.FromMinutes(5))
        {
            throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                "The PDF extraction deadline must be between zero and five minutes.",
                DiagnosticStage.Validation);
        }

        using var deadlineCancellation = new CancellationTokenSource(allowedTime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCancellation.Token);
        var timer = Stopwatch.StartNew();

        try
        {
            var bytes = await ReadBoundedFileAsync(path, limits.MaxFileBytes, linked.Token);
            var request = new PdfExtractionWorkerProtocol.PdfWorkerRequest(
                PdfExtractionWorkerProtocol.ProtocolVersion,
                Convert.ToBase64String(bytes),
                limits);
            var payload = JsonSerializer.Serialize(request, JsonOptions);
            var launch = invocation ?? PdfWorkerInvocation.ForCurrentApplication();
            var arguments = launch.PrefixArguments.ToList();
            if (launch.AppendInternalCommand)
                arguments.Add(PdfExtractionWorkerProtocol.InternalCommand);

            var remaining = allowedTime - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException(linked.Token);

            var supervised = await _supervisor.RunAsync(
                launch.ExecutablePath,
                arguments,
                remaining,
                payload,
                linked.Token);

            if (supervised.Outcome == SupervisedOutcome.TimedOut)
            {
                throw new AdapterException(DiagnosticCodes.WkrTimeout,
                    "The isolated PDF parser exceeded its deadline and was terminated.",
                    DiagnosticStage.Worker);
            }
            if (supervised.Outcome != SupervisedOutcome.Completed || supervised.OrphanDetected)
            {
                throw new AdapterException(DiagnosticCodes.WkrCrash,
                    "The isolated PDF parser terminated without a complete result.",
                    DiagnosticStage.Worker);
            }
            if (supervised.LogTruncated)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                    "The isolated PDF parser response exceeds its byte limit.",
                    DiagnosticStage.Validation);
            }

            var resultLines = supervised.CapturedLog
                .Where(line => line.StartsWith(PdfExtractionWorkerProtocol.OutputMarker, StringComparison.Ordinal))
                .ToArray();
            if (resultLines.Length != 1)
            {
                throw new AdapterException(DiagnosticCodes.WkrCrash,
                    "The isolated PDF parser returned an incomplete or ambiguous response.",
                    DiagnosticStage.Worker);
            }

            var json = resultLines[0][PdfExtractionWorkerProtocol.OutputMarker.Length..];
            PdfExtractionWorkerProtocol.PdfWorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<PdfExtractionWorkerProtocol.PdfWorkerResponse>(json, JsonOptions);
            }
            catch (JsonException)
            {
                throw WorkerFailure("The isolated PDF parser response is invalid.");
            }
            if (response is null)
                throw WorkerFailure("The isolated PDF parser response is invalid.");
            if (!response.Success)
            {
                var code = IsKnownFailureCode(response.ErrorCode) ? response.ErrorCode! : DiagnosticCodes.WkrCrash;
                throw new AdapterException(code,
                    response.ErrorMessage ?? "The PDF could not be extracted.",
                    FailureStage(code));
            }

            var document = response.Document
                ?? throw WorkerFailure("The isolated PDF parser returned no document.");
            VerifyResponse(document, bytes, limits);
            return document;
        }
        catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new AdapterException(DiagnosticCodes.WkrTimeout,
                "PDF extraction exceeded its deadline and was terminated.",
                DiagnosticStage.Worker);
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                "A PDF source path is required.", DiagnosticStage.Introspection);

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maxBytes)
            throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                "The PDF exceeds the configured byte limit.", DiagnosticStage.Validation);

        using var output = new MemoryStream((int)Math.Min(input.Length, maxBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
                throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                    "The PDF exceeds the configured byte limit.", DiagnosticStage.Validation);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static void VerifyResponse(
        PdfDigitalDocument document,
        byte[] sourceBytes,
        PdfDigitalExtractionOptions limits)
    {
        var expectedDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        if (document.Pages is null || document.Pages.Count is 0 || document.Pages.Count > limits.MaxPages)
            throw WorkerFailure("The isolated PDF parser response has an invalid page list.");

        long wordCount = 0;
        long textLength = 0;
        for (var index = 0; index < document.Pages.Count; index++)
        {
            var page = document.Pages[index];
            if (page is null || page.Text is null || page.Words is null
                || page.PageNumber != index + 1
                || page.RotationDegrees is not (0 or 90 or 180 or 270)
                || !double.IsFinite(page.Width) || page.Width <= 0
                || !double.IsFinite(page.Height) || page.Height <= 0)
            {
                throw WorkerFailure("The isolated PDF parser response contains invalid page evidence.");
            }

            textLength += page.Text.Length;
            wordCount += page.Words.Count;
            foreach (var word in page.Words)
            {
                if (word is null || string.IsNullOrEmpty(word.Text)
                    || !double.IsFinite(word.Left) || !double.IsFinite(word.Bottom)
                    || !double.IsFinite(word.Right) || !double.IsFinite(word.Top)
                    || word.Right < word.Left || word.Top < word.Bottom)
                {
                    throw WorkerFailure("The isolated PDF parser response contains invalid word evidence.");
                }
            }
        }

        if (document.SourceByteLength != sourceBytes.Length
            || !StringComparer.Ordinal.Equals(document.SourceSha256, expectedDigest)
            || wordCount > limits.MaxWords || textLength > limits.MaxTextCharacters)
        {
            throw WorkerFailure("The isolated PDF parser response does not match the bounded source request.");
        }
    }

    private static string FailureStage(string code)
        => code switch
        {
            DiagnosticCodes.IoPdfInvalid or DiagnosticCodes.IoPdfEncrypted => DiagnosticStage.Introspection,
            DiagnosticCodes.IoPdfBudgetExceeded => DiagnosticStage.Validation,
            _ => DiagnosticStage.Worker,
        };

    private static bool IsKnownFailureCode(string? code)
        => code is DiagnosticCodes.IoPdfInvalid
            or DiagnosticCodes.IoPdfBudgetExceeded
            or DiagnosticCodes.IoPdfEncrypted
            or DiagnosticCodes.WkrCrash;

    private static AdapterException WorkerFailure(string message)
        => new(DiagnosticCodes.WkrCrash, message, DiagnosticStage.Worker);
}
