using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Worker;

/// <summary>
/// Private stdin/stdout protocol used to run PdfPig in a killable child process. The
/// process receives bounded bytes only; it has no path, network, or publication API.
/// </summary>
public static class PdfExtractionWorkerProtocol
{
    public const string InternalCommand = "__xms-pdf-worker";
    public const string OutputMarker = "XMS-PDF-RESULT:";
    public const int ProtocolVersion = 1;
    public const int MaxRequestBytes = 35 * 1024 * 1024;
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public const int MaxCapturedLogBytes = MaxResponseBytes + 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsInternalCommand(string[] args)
        => args.Length == 1 && StringComparer.Ordinal.Equals(args[0], InternalCommand);

    public static async Task<int> RunAsync()
    {
        try
        {
            var requestBytes = await ReadBoundedAsync(Console.OpenStandardInput(), MaxRequestBytes);
            PdfWorkerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<PdfWorkerRequest>(requestBytes, JsonOptions);
            }
            catch (JsonException)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The PDF worker request is invalid.", DiagnosticStage.Validation);
            }

            if (request is null || request.PdfBase64 is null || request.Options is null)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The PDF worker request is incomplete.", DiagnosticStage.Validation);
            }

            if (request.ProtocolVersion != ProtocolVersion)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The PDF worker request uses an unsupported protocol version.", DiagnosticStage.Validation);
            }

            byte[] sourceBytes;
            try
            {
                sourceBytes = Convert.FromBase64String(request.PdfBase64);
            }
            catch (FormatException)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfInvalid,
                    "The PDF worker request does not contain valid document bytes.", DiagnosticStage.Validation);
            }

            var result = PdfDigitalDocumentExtractor.Extract(sourceBytes, request.Options);
            var response = new PdfWorkerResponse(true, result, null, null);
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            if (responseBytes.Length > MaxResponseBytes)
            {
                responseBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new PdfWorkerResponse(false, null, DiagnosticCodes.IoPdfBudgetExceeded,
                        "The extracted PDF evidence exceeds the response size limit."), JsonOptions);
            }

            await WriteResponseAsync(responseBytes);
            return 0;
        }
        catch (AdapterException ex)
        {
            await WriteResponseAsync(JsonSerializer.SerializeToUtf8Bytes(
                new PdfWorkerResponse(false, null, ex.Code, ex.Message), JsonOptions));
            return 0;
        }
        catch (Exception)
        {
            await WriteResponseAsync(JsonSerializer.SerializeToUtf8Bytes(
                new PdfWorkerResponse(false, null, DiagnosticCodes.WkrCrash,
                    "The isolated PDF parser failed."), JsonOptions));
            return 1;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maxBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
            {
                throw new AdapterException(DiagnosticCodes.IoPdfBudgetExceeded,
                    "The PDF worker request exceeds its byte limit.", DiagnosticStage.Validation);
            }
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return output.ToArray();
    }

    private static async Task WriteResponseAsync(byte[] responseBytes)
    {
        var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(Encoding.UTF8.GetBytes(OutputMarker));
        await stdout.WriteAsync(responseBytes);
        await stdout.WriteAsync(new byte[] { (byte)'\n' });
        await stdout.FlushAsync();
    }

    internal sealed record PdfWorkerRequest(
        int ProtocolVersion,
        string PdfBase64,
        PdfDigitalExtractionOptions Options);

    internal sealed record PdfWorkerResponse(
        bool Success,
        PdfDigitalDocument? Document,
        string? ErrorCode,
        string? ErrorMessage);
}
