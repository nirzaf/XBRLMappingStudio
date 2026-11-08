using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.CLI;

/// <summary>
/// Stable CLI exit codes (section 9 "CLI contract"). Chosen to be scheduler-friendly:
/// success and succeeded-with-warnings are distinguished; neither requires retry.
/// Failure codes alone do not prove that no destination committed.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int SuccessWithWarnings = 1;
    public const int InvalidMapping = 2;
    public const int DataError = 3;
    public const int IncompleteProfile = 4;
    public const int AuthorizationFailure = 5;
    public const int Cancelled = 6;
    public const int OperationalFailure = 7;

    public static int FromOutcome(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Success => Success,
        RunOutcome.SuccessWithWarnings => SuccessWithWarnings,
        RunOutcome.InvalidMapping => InvalidMapping,
        RunOutcome.DataError => DataError,
        RunOutcome.IncompleteConfiguration => IncompleteProfile,
        RunOutcome.AuthorizationFailure => AuthorizationFailure,
        RunOutcome.Cancelled => Cancelled,
        RunOutcome.OperationalFailure => OperationalFailure,
        RunOutcome.Unknown => OperationalFailure,
        _ => OperationalFailure
    };
}
