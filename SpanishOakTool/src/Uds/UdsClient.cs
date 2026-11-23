using System;
using System.Threading;
using SpanishOakTool.J2534;

namespace SpanishOakTool.Uds;

/// <summary>
/// Original UDS (ISO 14229) request/response client sitting on top of the
/// J2534 ISO15765 transport. Encapsulates the request/response transaction
/// pattern (send, wait for matching reply, raise on negative response,
/// transparently wait out 0x78 "response pending") — logic every UDS client
/// needs regardless of target ECU, written from the ISO 14229 spec.
/// </summary>
public sealed class UdsClient
{
    private readonly J2534Device _device;
    private readonly uint _txId;
    private readonly uint _rxId;
    public Action<string>? Log { get; set; }

    public UdsClient(J2534Device device, uint txId, uint rxId)
    {
        _device = device;
        _txId = txId;
        _rxId = rxId;
    }

    /// <summary>
    /// Sends a UDS request and returns the full response payload (service
    /// byte + data), handling 0x78 response-pending retries. Throws
    /// UdsNegativeResponseException on any other negative response.
    /// </summary>
    public byte[] Request(byte serviceId, byte[] payload, int timeoutMs = 2000, int maxPendingRetries = 10)
    {
        byte[] request = new byte[1 + payload.Length];
        request[0] = serviceId;
        Array.Copy(payload, 0, request, 1, payload.Length);

        _device.WriteMessage(_txId, request, (uint)timeoutMs);

        for (int attempt = 0; attempt <= maxPendingRetries; attempt++)
        {
            byte[]? response = _device.ReadMessage((uint)timeoutMs);
            if (response == null || response.Length == 0)
                throw new TimeoutException($"No response from ECU for service 0x{serviceId:X2}.");

            if (response[0] == UdsService.NegativeResponse)
            {
                byte requestedSid = response.Length > 1 ? response[1] : serviceId;
                byte nrc = response.Length > 2 ? response[2] : 0;
                if (nrc == 0x78)
                {
                    Log?.Invoke($"ECU busy (response pending) for service 0x{serviceId:X2}, waiting...");
                    continue; // wait for the real response
                }
                throw new UdsNegativeResponseException(requestedSid, nrc);
            }

            if (response[0] == (byte)(serviceId + UdsService.PositiveResponseOffset))
                return response;

            // Unrelated frame (e.g. stray tester-present echo) - keep waiting.
        }

        throw new TimeoutException($"ECU kept responding pending for service 0x{serviceId:X2} past retry limit.");
    }

    public bool StartDiagnosticSession(DiagnosticSessionType sessionType)
    {
        try
        {
            Request(UdsService.DiagnosticSessionControl, new[] { (byte)sessionType });
            Log?.Invoke($"Diagnostic session {sessionType} started.");
            return true;
        }
        catch (UdsNegativeResponseException ex)
        {
            Log?.Invoke($"Failed to start session {sessionType}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Requests a security-access seed (subfunction = requestSeed, odd byte,
    /// e.g. 0x01/0x03/0x05...). Returns the seed bytes from the response.
    /// </summary>
    public byte[] RequestSecuritySeed(byte requestSeedSubfunction)
    {
        var response = Request(UdsService.SecurityAccess, new[] { requestSeedSubfunction });
        // response: [0x67, subfunction, seed...]
        if (response.Length <= 2)
            return Array.Empty<byte>();
        byte[] seed = new byte[response.Length - 2];
        Array.Copy(response, 2, seed, 0, seed.Length);
        return seed;
    }

    /// <summary>
    /// Sends a computed key back (subfunction = sendKey, requestSeedSubfunction + 1).
    /// Returns true if the ECU accepted it.
    /// </summary>
    public bool SendSecurityKey(byte sendKeySubfunction, byte[] key)
    {
        byte[] payload = new byte[1 + key.Length];
        payload[0] = sendKeySubfunction;
        Array.Copy(key, 0, payload, 1, key.Length);
        try
        {
            Request(UdsService.SecurityAccess, payload);
            return true;
        }
        catch (UdsNegativeResponseException ex)
        {
            Log?.Invoke($"Security key rejected: {ex.Message}");
            return false;
        }
    }

    public void TesterPresent(bool suppressResponse = true)
    {
        byte[] payload = { suppressResponse ? (byte)0x80 : (byte)0x00 };
        if (suppressResponse)
        {
            // Fire and forget per ISO 14229 suppressPosRspMsgIndicationBit semantics.
            byte[] frame = { UdsService.TesterPresent, payload[0] };
            _device.WriteMessage(_txId, frame);
        }
        else
        {
            Request(UdsService.TesterPresent, payload);
        }
    }

    public void ClearDiagnosticInformation(uint groupOfDtc = 0xFFFFFF)
    {
        byte[] payload =
        {
            (byte)((groupOfDtc >> 16) & 0xFF),
            (byte)((groupOfDtc >> 8) & 0xFF),
            (byte)(groupOfDtc & 0xFF)
        };
        Request(UdsService.ClearDiagnosticInformation, payload);
    }

    /// <summary>
    /// ReadMemoryByAddress (0x23) with a 4-byte address and 2-byte length
    /// (addressAndLengthFormatIdentifier 0x24), matching the addressing width
    /// this ECU family uses over its 1MB flash range.
    /// </summary>
    public byte[] ReadMemoryByAddress(uint address, ushort length)
    {
        byte[] payload =
        {
            0x24, // ALFID: 2-byte length, 4-byte address
            (byte)((address >> 24) & 0xFF),
            (byte)((address >> 16) & 0xFF),
            (byte)((address >> 8) & 0xFF),
            (byte)(address & 0xFF),
            (byte)((length >> 8) & 0xFF),
            (byte)(length & 0xFF)
        };
        var response = Request(UdsService.ReadMemoryByAddress, payload, timeoutMs: 4000);
        byte[] data = new byte[response.Length - 1];
        Array.Copy(response, 1, data, 0, data.Length);
        return data;
    }

    /// <summary>
    /// RequestDownload (0x34): dataFormatIdentifier 0x00 (no compression/encryption),
    /// ALFID 0x44 (4-byte address, 4-byte size). Returns the maxNumberOfBlockLength
    /// the ECU reports it can accept per TransferData frame.
    /// </summary>
    public uint RequestDownload(uint address, uint size)
    {
        byte[] payload =
        {
            0x00, // dataFormatIdentifier
            0x44, // ALFID: 4-byte address, 4-byte size
            (byte)((address >> 24) & 0xFF),
            (byte)((address >> 16) & 0xFF),
            (byte)((address >> 8) & 0xFF),
            (byte)(address & 0xFF),
            (byte)((size >> 24) & 0xFF),
            (byte)((size >> 16) & 0xFF),
            (byte)((size >> 8) & 0xFF),
            (byte)(size & 0xFF)
        };
        var response = Request(UdsService.RequestDownload, payload, timeoutMs: 5000);
        // response: [0x74, lengthFormatIdentifier, maxBlockLength...]
        if (response.Length < 3) return 0x400;
        int lenSize = response[1] >> 4;
        uint maxBlockLength = 0;
        for (int i = 0; i < lenSize && 2 + i < response.Length; i++)
            maxBlockLength = (maxBlockLength << 8) | response[2 + i];
        return maxBlockLength == 0 ? 0x400 : maxBlockLength;
    }

    public void TransferData(byte blockSequenceCounter, byte[] chunk)
    {
        byte[] payload = new byte[1 + chunk.Length];
        payload[0] = blockSequenceCounter;
        Array.Copy(chunk, 0, payload, 1, chunk.Length);
        Request(UdsService.TransferData, payload, timeoutMs: 5000);
    }

    public void RequestTransferExit()
    {
        Request(UdsService.RequestTransferExit, Array.Empty<byte>(), timeoutMs: 5000);
    }
}
