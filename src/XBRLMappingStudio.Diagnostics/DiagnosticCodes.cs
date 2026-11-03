namespace XBRLMappingStudio.Diagnostics;

/// <summary>
/// Registry of stable diagnostic codes used across the product.
/// Codes follow pattern: 3-letter subsystem prefix + 3-digit sequence.
/// A code, once published, must never be reused for a different meaning.
/// </summary>
public static class DiagnosticCodes
{
    // MAP - Mapping graph and execution
    public const string MapUnknownNodeType = "MAP001";
    public const string MapDanglingPort = "MAP002";
    public const string MapCycleDetected = "MAP003";
    public const string MapTypeIncompatible = "MAP004";
    public const string MapCardinalityMismatch = "MAP005";
    public const string MapUnboundRequiredInput = "MAP006";
    public const string MapInvalidConstant = "MAP007";
    public const string MapNumericOverflow = "MAP008";
    public const string MapDivideByZero = "MAP009";
    public const string MapCultureMissing = "MAP010";
    public const string MapExecutionCancelled = "MAP011";
    public const string MapMissingPeriodStart = "MAP012";
    public const string MapMissingPeriodEnd = "MAP013";

    // PRJ - Project schema and lifecycle
    public const string PrjSchemaVersionUnsupported = "PRJ001";
    public const string PrjRevisionConflict = "PRJ002";
    public const string PrjLockHeld = "PRJ003";
    public const string PrjAtomicSaveFailed = "PRJ004";
    public const string PrjCorruptFile = "PRJ005";

    // IO - Source and target adapters
    public const string IoSchemaMismatch = "IO001";
    public const string IoHeaderShifted = "IO002";
    public const string IoSheetMissing = "IO003";
    public const string IoFormulaCacheMissing = "IO004";
    public const string IoUnsupportedEncoding = "IO005";
    public const string IoRowBudgetExceeded = "IO006";
    public const string IoColumnMissing = "IO007";

    // XBR - XBRL and taxonomy
    public const string XbrConceptUnknown = "XBR001";
    public const string XbrUnitMissing = "XBR002";
    public const string XbrPeriodInvalid = "XBR003";
    public const string XbrPeriodTypeMismatch = "XBR004";
    public const string XbrEntityMissing = "XBR005";
    public const string XbrTaxonomyLoadFailed = "XBR006";
    public const string XbrWorkerUnavailable = "XBR007";
    public const string XbrDecimalsOutOfRange = "XBR008";
    public const string XbrValidationFailed = "XBR009";
    public const string XbrValidationIncomplete = "XBR010";

    // WKR - Worker process protocol
    public const string WkrHandshakeFailed = "WKR001";
    public const string WkrProtocolVersionMismatch = "WKR002";
    public const string WkrCrash = "WKR003";
    public const string WkrTimeout = "WKR004";

    // PUB - Publication and job lifecycle
    public const string PubLockFailed = "PUB001";
    public const string PubPartialFailure = "PUB002";
    public const string PubDigestMismatch = "PUB003";
}
