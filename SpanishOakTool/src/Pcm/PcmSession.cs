using System;
using System.IO;
using System.Threading;
using SpanishOakTool.J2534;
using SpanishOakTool.Uds;

namespace SpanishOakTool.Pcm;

/// <summary>
/// Original service layer for the BF Falcon "Spanish Oak" Barra PCM. Wraps
/// the generic UdsClient with the specific addressing, sizes, and sequencing
/// this ECU family uses over ISO15765. Written from the public SAE J2534-1 /
/// ISO 14229 / ISO 15765 standards plus network-observable facts about this
/// vehicle (CAN IDs, flash size, block size) — no third-party tool code.
/// </summary>
public sealed class PcmSession : IDisposable
{
    // Standard diagnostic CAN pair for this ECU family.
    public const uint PcmRequestId = 0x7E0;
    public const uint PcmResponseId = 0x7E8;

    public const uint FlashSize = 0x100000;      // 1,048,576 bytes / 1024KB
    public const uint ReadBlockSize = 0x400;      // largest evenly-divisible block for ReadMemoryByAddress on this ECU
    public const uint FlashWriteStart = 0x10000;  // start offset for programmed region (bootblock reserved below this)

    private readonly J2534Device _device;
    private readonly UdsClient _uds;
    private readonly ISecurityKeyAlgorithm _securityAlgorithm;
    public Action<string>? Log { get; set; }

    public PcmSession(string j2534DllPath, ISecurityKeyAlgorithm securityAlgorithm)
    {
        _device = new J2534Device(j2534DllPath);
        _securityAlgorithm = securityAlgorithm;
        _uds = new UdsClient(_device, PcmRequestId, PcmResponseId);
        _uds.Log = msg => Log?.Invoke(msg);
    }

    public void Connect()
    {
        Log?.Invoke("Opening J2534 device...");
        _device.Open();
        Log?.Invoke("Connecting ISO15765 @ 500000 bps...");
        _device.ConnectIso15765(500000);
        _device.SetFlowControlFilter(PcmRequestId, PcmResponseId);
        Log?.Invoke("Connected.");
    }

    /// <summary>
    /// Basic connectivity check: default session + extended session, no
    /// security access, no flash access. Safe to run on a live vehicle.
    /// </summary>
    public bool Probe()
    {
        if (!_uds.StartDiagnosticSession(DiagnosticSessionType.Default))
            return false;
        Thread.Sleep(50);
        if (!_uds.StartDiagnosticSession(DiagnosticSessionType.ExtendedDiagnostic))
            return false;
        _uds.TesterPresent();
        return true;
    }

    /// <summary>
    /// Enters programming session and performs SecurityAccess level 0x01
    /// (requestSeed) / 0x02 (sendKey) — the conventional odd/even subfunction
    /// pairing from ISO 14229. Adjust the level byte here if your captured
    /// bus traffic shows this ECU using a different access level.
    /// </summary>
    public bool EnterProgrammingSessionAndUnlock(byte requestSeedLevel = 0x01)
    {
        if (!_uds.StartDiagnosticSession(DiagnosticSessionType.Programming))
        {
            Log?.Invoke("Could not enter programming session.");
            return false;
        }
        Thread.Sleep(100);

        byte sendKeyLevel = (byte)(requestSeedLevel + 1);
        byte[] seed = _uds.RequestSecuritySeed(requestSeedLevel);
        if (seed.Length == 0)
        {
            Log?.Invoke("ECU returned an empty seed (may already be unlocked, or session rejected the request).");
            return true;
        }

        byte[] key = _securityAlgorithm.ComputeKey(seed);
        bool accepted = _uds.SendSecurityKey(sendKeyLevel, key);
        Log?.Invoke(accepted ? "Security access granted." : "Security access denied.");
        return accepted;
    }

    /// <summary>
    /// Reads the full flash image to <paramref name="outputPath"/> in
    /// ReadBlockSize chunks, reporting progress via <paramref name="onProgress"/>.
    /// </summary>
    public void ReadFlash(string outputPath, Action<uint, uint>? onProgress = null)
    {
        using var file = File.Create(outputPath);
        for (uint address = 0; address < FlashSize; address += ReadBlockSize)
        {
            ushort chunkLen = (ushort)Math.Min(ReadBlockSize, FlashSize - address);
            byte[] chunk = _uds.ReadMemoryByAddress(address, chunkLen);
            file.Write(chunk, 0, chunk.Length);
            onProgress?.Invoke(address + (uint)chunk.Length, FlashSize);
        }
        file.Flush();
        Log?.Invoke($"Flash read complete: {outputPath} ({FlashSize} bytes).");
    }

    /// <summary>
    /// Erases and writes <paramref name="image"/> to flash starting at
    /// FlashWriteStart. Caller is responsible for having already erased
    /// via an ECU-specific RoutineControl request if this ECU requires a
    /// separate erase step before RequestDownload accepts (capture your own
    /// bus trace to confirm whether this variant needs it).
    /// </summary>
    public void WriteFlash(byte[] image, Action<uint, uint>? onProgress = null)
    {
        if (image.Length == 0)
            throw new ArgumentException("Flash image is empty.");

        uint maxBlockLen = _uds.RequestDownload(FlashWriteStart, (uint)image.Length);
        uint payloadPerFrame = Math.Max(1, maxBlockLen) - 2; // minus TransferData SID + BSC byte, if ECU's figure includes them
        if (payloadPerFrame == 0 || payloadPerFrame > ReadBlockSize)
            payloadPerFrame = ReadBlockSize;

        byte blockSequenceCounter = 1; // ISO 14229: starts at 1, wraps 0x00-0xFF
        uint offset = 0;
        while (offset < image.Length)
        {
            int len = (int)Math.Min(payloadPerFrame, (uint)image.Length - offset);
            byte[] chunk = new byte[len];
            Array.Copy(image, offset, chunk, 0, len);

            _uds.TransferData(blockSequenceCounter, chunk);
            blockSequenceCounter = (byte)(blockSequenceCounter == 0xFF ? 0x00 : blockSequenceCounter + 1);

            offset += (uint)len;
            onProgress?.Invoke(offset, (uint)image.Length);
        }

        _uds.RequestTransferExit();
        Log?.Invoke("Flash write complete, transfer exit confirmed.");
    }

    public void ClearDtcs() => _uds.ClearDiagnosticInformation();

    public void Dispose() => _device.Dispose();
}
