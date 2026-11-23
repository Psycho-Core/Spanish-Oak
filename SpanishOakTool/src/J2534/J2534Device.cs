using System;
using System.Runtime.InteropServices;
using static SpanishOakTool.J2534.J2534Native;

namespace SpanishOakTool.J2534;

public sealed class J2534Exception : Exception
{
    public J2534Exception(string message) : base(message) { }
}

/// <summary>
/// Thin, original wrapper around a dynamically-loaded J2534 vendor DLL. Loads
/// the DLL by path (user supplies it, or we fall back to scanning the standard
/// registry location) and binds the standard J2534-1 exports. Owns device +
/// channel lifetime.
/// </summary>
public sealed class J2534Device : IDisposable
{
    private readonly IntPtr _dll;
    private uint _deviceId;
    private uint _channelId;
    private bool _connected;

    private readonly PassThruOpenDelegate _open;
    private readonly PassThruCloseDelegate _close;
    private readonly PassThruConnectDelegate _connect;
    private readonly PassThruDisconnectDelegate _disconnect;
    private readonly PassThruReadMsgsDelegate _readMsgs;
    private readonly PassThruWriteMsgsDelegate _writeMsgs;
    private readonly PassThruStartMsgFilterDelegate _startFilter;
    private readonly PassThruStopMsgFilterDelegate _stopFilter;
    private readonly PassThruSetProgrammingVoltageDelegate _setProgVoltage;
    private readonly PassThruIoctlDelegate _ioctl;
    private readonly PassThruGetLastErrorDelegate _getLastError;

    public uint ChannelId => _channelId;

    public J2534Device(string dllPath)
    {
        _dll = LoadLibrary(dllPath);
        if (_dll == IntPtr.Zero)
            throw new J2534Exception($"Could not load J2534 DLL at '{dllPath}' (Win32 error {Marshal.GetLastWin32Error()}).");

        _open = Bind<PassThruOpenDelegate>("PassThruOpen");
        _close = Bind<PassThruCloseDelegate>("PassThruClose");
        _connect = Bind<PassThruConnectDelegate>("PassThruConnect");
        _disconnect = Bind<PassThruDisconnectDelegate>("PassThruDisconnect");
        _readMsgs = Bind<PassThruReadMsgsDelegate>("PassThruReadMsgs");
        _writeMsgs = Bind<PassThruWriteMsgsDelegate>("PassThruWriteMsgs");
        _startFilter = Bind<PassThruStartMsgFilterDelegate>("PassThruStartMsgFilter");
        _stopFilter = Bind<PassThruStopMsgFilterDelegate>("PassThruStopMsgFilter");
        _setProgVoltage = Bind<PassThruSetProgrammingVoltageDelegate>("PassThruSetProgrammingVoltage");
        _ioctl = Bind<PassThruIoctlDelegate>("PassThruIoctl");
        _getLastError = Bind<PassThruGetLastErrorDelegate>("PassThruGetLastError");
    }

    private T Bind<T>(string export) where T : Delegate
    {
        var ptr = GetProcAddress(_dll, export);
        if (ptr == IntPtr.Zero)
            throw new J2534Exception($"J2534 DLL is missing required export '{export}'.");
        return (T)Marshal.GetDelegateForFunctionPointer(ptr, typeof(T));
    }

    public void Open()
    {
        int rc = _open(IntPtr.Zero, out _deviceId);
        ThrowIfError(rc, "PassThruOpen");
    }

    public void ConnectIso15765(uint baudRate = 500000)
    {
        int rc = _connect(_deviceId, ISO15765, 0, baudRate, out _channelId);
        ThrowIfError(rc, "PassThruConnect");
        _connected = true;
    }

    /// <summary>
    /// Installs a flow-control filter so the DLL's ISO-TP layer handles
    /// multi-frame CAN reassembly for us (standard J2534 ISO15765 behaviour).
    /// </summary>
    public uint SetFlowControlFilter(uint txId, uint rxId)
    {
        byte[] mask = { 0xFF, 0xFF, 0xFF, 0xFF };
        byte[] patternRx = IdToBytes(rxId);
        byte[] patternTx = IdToBytes(txId);

        var maskMsg = PASSTHRU_MSG.Create(ISO15765, 0, mask);
        var patternMsg = PASSTHRU_MSG.Create(ISO15765, 0, patternRx);
        var flowMsg = PASSTHRU_MSG.Create(ISO15765, 0, patternTx);

        IntPtr pMask = StructToPtr(maskMsg);
        IntPtr pPattern = StructToPtr(patternMsg);
        IntPtr pFlow = StructToPtr(flowMsg);
        try
        {
            int rc = _startFilter(_channelId, FLOW_CONTROL_FILTER, pMask, pPattern, pFlow, out uint filterId);
            ThrowIfError(rc, "PassThruStartMsgFilter");
            return filterId;
        }
        finally
        {
            Marshal.FreeHGlobal(pMask);
            Marshal.FreeHGlobal(pPattern);
            Marshal.FreeHGlobal(pFlow);
        }
    }

    private static byte[] IdToBytes(uint id) => new byte[]
    {
        (byte)((id >> 24) & 0xFF),
        (byte)((id >> 16) & 0xFF),
        (byte)((id >> 8) & 0xFF),
        (byte)(id & 0xFF)
    };

    public void WriteMessage(uint canId, byte[] payload, uint timeoutMs = 1000)
    {
        byte[] data = new byte[4 + payload.Length];
        Array.Copy(IdToBytes(canId), data, 4);
        Array.Copy(payload, 0, data, 4, payload.Length);

        var msg = PASSTHRU_MSG.Create(ISO15765, 0, data);
        IntPtr pMsg = StructToPtr(msg);
        try
        {
            uint numMsgs = 1;
            int rc = _writeMsgs(_channelId, pMsg, ref numMsgs, timeoutMs);
            ThrowIfError(rc, "PassThruWriteMsgs");
        }
        finally
        {
            Marshal.FreeHGlobal(pMsg);
        }
    }

    /// <summary>
    /// Reads one reassembled ISO-TP message, stripping the 4-byte CAN ID
    /// prefix the J2534 API prepends for ISO15765. Returns null on timeout.
    /// </summary>
    public byte[]? ReadMessage(uint timeoutMs = 2000)
    {
        int size = Marshal.SizeOf<PASSTHRU_MSG>();
        IntPtr pMsg = Marshal.AllocHGlobal(size);
        try
        {
            uint numMsgs = 1;
            int rc = _readMsgs(_channelId, pMsg, ref numMsgs, timeoutMs);
            if (rc == 0x10) // ERR_BUFFER_EMPTY / ERR_TIMEOUT depending on vendor mapping
                return null;
            ThrowIfError(rc, "PassThruReadMsgs");
            if (numMsgs == 0)
                return null;

            var msg = Marshal.PtrToStructure<PASSTHRU_MSG>(pMsg);
            if (msg.DataSize < 4)
                return Array.Empty<byte>();

            int payloadLen = (int)msg.DataSize - 4;
            var result = new byte[payloadLen];
            Array.Copy(msg.Data, 4, result, 0, payloadLen);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pMsg);
        }
    }

    public void SetProgrammingVoltage(uint pin, uint millivolts)
    {
        int rc = _setProgVoltage(_deviceId, pin, millivolts);
        ThrowIfError(rc, "PassThruSetProgrammingVoltage");
    }

    private static IntPtr StructToPtr<T>(T value) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, ptr, false);
        return ptr;
    }

    private void ThrowIfError(int rc, string call)
    {
        if (rc == 0) return;
        string detail = TryGetLastError();
        throw new J2534Exception($"{call} failed with J2534 status {rc}. {detail}");
    }

    private string TryGetLastError()
    {
        try
        {
            IntPtr buf = Marshal.AllocHGlobal(80);
            try
            {
                _getLastError(buf);
                return Marshal.PtrToStringAnsi(buf) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_connected)
            {
                _disconnect(_channelId);
                _connected = false;
            }
            if (_deviceId != 0)
            {
                _close(_deviceId);
            }
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            if (_dll != IntPtr.Zero)
                FreeLibrary(_dll);
        }
    }
}
