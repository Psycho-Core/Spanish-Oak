using System;
using System.Runtime.InteropServices;

namespace SpanishOakTool.J2534;

/// <summary>
/// Raw P/Invoke bindings for the SAE J2534-1 pass-thru API. This is the public,
/// standardized interface every J2534 vendor DLL (Tactrix Openport, Ford VCM2,
/// OBDxPro FT, etc.) exposes. Nothing here is specific to any particular tool
/// or vendor; it's the same signature set published in the J2534-1 spec and
/// used by every pass-thru application.
/// </summary>
internal static class J2534Native
{
    public const int MAX_MSG_SIZE = 4128;

    [StructLayout(LayoutKind.Sequential)]
    public struct PASSTHRU_MSG
    {
        public uint ProtocolID;
        public uint RxStatus;
        public uint TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_MSG_SIZE)]
        public byte[] Data;

        public static PASSTHRU_MSG Create(uint protocolId, uint txFlags, byte[] data)
        {
            var msg = new PASSTHRU_MSG
            {
                ProtocolID = protocolId,
                TxFlags = txFlags,
                DataSize = (uint)data.Length,
                Data = new byte[MAX_MSG_SIZE]
            };
            Array.Copy(data, msg.Data, data.Length);
            return msg;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG
    {
        public uint Parameter;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCONFIG_LIST
    {
        public uint NumOfParams;
        public IntPtr ConfigPtr;
    }

    // Protocol IDs
    public const uint ISO15765 = 6;

    // Connect flags
    public const uint CAN_29BIT_ID = 0x0100;

    // Config parameters
    public const uint DATA_RATE = 0x01;
    public const uint LOOPBACK = 0x03;
    public const uint ISO15765_BS = 0x0F;
    public const uint ISO15765_STMIN = 0x10;

    // Filter types
    public const uint FLOW_CONTROL_FILTER = 3;

    /// <summary>
    /// Delegate set loaded dynamically via LoadLibrary/GetProcAddress against
    /// whatever vendor DLL the user points us at, so this tool isn't hard-wired
    /// to one adapter brand. The exported names/signatures below are the
    /// standard J2534-1 entry points.
    /// </summary>
    public delegate int PassThruOpenDelegate(IntPtr name, out uint deviceId);
    public delegate int PassThruCloseDelegate(uint deviceId);
    public delegate int PassThruConnectDelegate(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint channelId);
    public delegate int PassThruDisconnectDelegate(uint channelId);
    public delegate int PassThruReadMsgsDelegate(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);
    public delegate int PassThruWriteMsgsDelegate(uint channelId, IntPtr msgs, ref uint numMsgs, uint timeout);
    public delegate int PassThruStartPeriodicMsgDelegate(uint channelId, IntPtr msg, out uint msgId, uint timeInterval);
    public delegate int PassThruStopPeriodicMsgDelegate(uint channelId, uint msgId);
    public delegate int PassThruStartMsgFilterDelegate(uint channelId, uint filterType, IntPtr maskMsg, IntPtr patternMsg, IntPtr flowControlMsg, out uint filterId);
    public delegate int PassThruStopMsgFilterDelegate(uint channelId, uint filterId);
    public delegate int PassThruSetProgrammingVoltageDelegate(uint deviceId, uint pinNumber, uint voltage);
    public delegate int PassThruReadVersionDelegate(uint deviceId, IntPtr firmwareVersion, IntPtr dllVersion, IntPtr apiVersion);
    public delegate int PassThruGetLastErrorDelegate(IntPtr errorDescription);
    public delegate int PassThruIoctlDelegate(uint channelId, uint ioctlId, IntPtr input, IntPtr output);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);
}
