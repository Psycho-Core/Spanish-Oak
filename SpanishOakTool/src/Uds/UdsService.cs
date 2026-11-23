namespace SpanishOakTool.Uds;

/// <summary>
/// ISO 14229 (UDS) service identifiers. These are the public standard's
/// service IDs — the same values in every UDS reference, not anything
/// specific to a particular tool.
/// </summary>
public static class UdsService
{
    public const byte DiagnosticSessionControl = 0x10;
    public const byte EcuReset = 0x11;
    public const byte ClearDiagnosticInformation = 0x14;
    public const byte ReadDtcInformation = 0x19;
    public const byte ReadDataByIdentifier = 0x22;
    public const byte ReadMemoryByAddress = 0x23;
    public const byte SecurityAccess = 0x27;
    public const byte WriteDataByIdentifier = 0x2E;
    public const byte RoutineControl = 0x31;
    public const byte RequestDownload = 0x34;
    public const byte RequestUpload = 0x35;
    public const byte TransferData = 0x36;
    public const byte RequestTransferExit = 0x37;
    public const byte TesterPresent = 0x3E;

    public const byte NegativeResponse = 0x7F;
    public const byte PositiveResponseOffset = 0x40;
}

public enum DiagnosticSessionType : byte
{
    Default = 0x01,
    Programming = 0x02,
    ExtendedDiagnostic = 0x03,
    SafetySystemDiagnostic = 0x04
}

/// <summary>
/// Thrown when an ECU returns a 0x7F negative response. Carries the NRC
/// (negative response code) so callers can decide whether to retry
/// (e.g. 0x78 RequestCorrectlyReceived-ResponsePending) or fail.
/// </summary>
public sealed class UdsNegativeResponseException : System.Exception
{
    public byte RequestedServiceId { get; }
    public byte NegativeResponseCode { get; }

    public UdsNegativeResponseException(byte requestedServiceId, byte nrc)
        : base($"ECU returned negative response for service 0x{requestedServiceId:X2}: NRC 0x{nrc:X2} ({DescribeNrc(nrc)})")
    {
        RequestedServiceId = requestedServiceId;
        NegativeResponseCode = nrc;
    }

    public static string DescribeNrc(byte nrc) => nrc switch
    {
        0x10 => "generalReject",
        0x11 => "serviceNotSupported",
        0x12 => "subFunctionNotSupported",
        0x13 => "incorrectMessageLengthOrInvalidFormat",
        0x22 => "conditionsNotCorrect",
        0x24 => "requestSequenceError",
        0x31 => "requestOutOfRange",
        0x33 => "securityAccessDenied",
        0x35 => "invalidKey",
        0x36 => "exceedNumberOfAttempts",
        0x37 => "requiredTimeDelayNotExpired",
        0x70 => "uploadDownloadNotAccepted",
        0x71 => "transferDataSuspended",
        0x72 => "generalProgrammingFailure",
        0x73 => "wrongBlockSequenceCounter",
        0x78 => "requestCorrectlyReceived-ResponsePending",
        0x7E => "subFunctionNotSupportedInActiveSession",
        0x7F => "serviceNotSupportedInActiveSession",
        _ => "unknown"
    };
}
