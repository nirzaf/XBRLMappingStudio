using System.Globalization;
using System.Text;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Xbrl;

/// <summary>Shared byte-bounded reader for xBRL report adapters and CLI validation.</summary>
public static class XbrlReportInputReader
{
    public static long ResolveMaximumInputBytes(string path, string? configuredMaximumBytes = null)
    {
        var isXml = string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);
        var defaultLimit = isXml ? XmlAdapter.DefaultMaximumInputBytes : JsonAdapter.DefaultMaximumInputBytes;
        if (configuredMaximumBytes is null) return defaultLimit;

        var absoluteLimit = JsonAdapter.Options.AbsoluteMaximumInputBytes;
        if (!long.TryParse(configuredMaximumBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > absoluteLimit)
            throw new AdapterException(DiagnosticCodes.IoInputBudgetExceeded,
                $"Configured XBRL report input limit must be between 1 and {absoluteLimit} bytes.",
                isInputBudgetExceeded: true);
        return value;
    }

    public static string ReadText(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        var absoluteLimit = JsonAdapter.Options.AbsoluteMaximumInputBytes;
        if (maximumBytes is < 1 || maximumBytes > absoluteLimit)
            throw new AdapterException(DiagnosticCodes.IoInputBudgetExceeded,
                $"XBRL report input limit must be between 1 and {absoluteLimit} bytes.",
                isInputBudgetExceeded: true);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw InputBudgetExceeded(maximumBytes);

        using var content = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > maximumBytes)
                throw InputBudgetExceeded(maximumBytes);
            content.Write(buffer, 0, read);
        }

        content.Position = 0;
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        return reader.ReadToEnd();
    }

    public static string ReadText(string path, CancellationToken cancellationToken = default)
        => ReadText(path, ResolveMaximumInputBytes(path), cancellationToken);

    private static AdapterException InputBudgetExceeded(long maximumBytes)
        => new(DiagnosticCodes.IoInputBudgetExceeded,
            $"XBRL report input exceeds the maximum size of {maximumBytes} bytes.",
            isInputBudgetExceeded: true);
}
