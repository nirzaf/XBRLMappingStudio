namespace XBRLMappingStudio.Worker;

/// <summary>Bounds process command construction before an operating-system launch.</summary>
internal static class WorkerProcessArgumentBudget
{
    private const int MaximumExecutableCharacters = 4_096;
    private const int MaximumArgumentCount = 512;
    private const int MaximumArgumentCharacters = 8_192;
    private const int MaximumCommandCharacters = 16_384;

    public static string[] ValidateAndCopy(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executable.Length > MaximumExecutableCharacters || executable.Contains('\0'))
            throw new ArgumentException(
                "Worker executable is invalid or exceeds the process-path budget.", nameof(executable));

        var validated = new List<string>();
        long commandCharacters = executable.Length + 1L;
        foreach (var argument in arguments)
        {
            if (validated.Count >= MaximumArgumentCount)
                throw new ArgumentException(
                    $"Worker command exceeds the {MaximumArgumentCount}-argument limit.", nameof(arguments));
            if (argument is null || argument.Contains('\0'))
                throw new ArgumentException(
                    "Worker arguments cannot be null or contain NUL characters.", nameof(arguments));
            if (argument.Length > MaximumArgumentCharacters)
                throw new ArgumentException(
                    $"A worker argument exceeds the {MaximumArgumentCharacters}-character limit.", nameof(arguments));

            commandCharacters += argument.Length + 3L;
            if (commandCharacters > MaximumCommandCharacters)
                throw new ArgumentException(
                    $"Worker command exceeds the {MaximumCommandCharacters}-character limit.", nameof(arguments));
            validated.Add(argument);
        }

        return validated.ToArray();
    }
}
