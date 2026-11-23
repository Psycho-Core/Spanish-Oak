using System;
using System.Runtime.InteropServices;

namespace PsychoFlash.Core.J2534
{
    /// <summary>Return codes defined by SAE J2534-1 (v04.04) plus common vendor extensions.</summary>
    public enum J2534Err
    {
        STATUS_NOERROR = 0x00,
        ERR_NOT_SUPPORTED = 0x01,
        ERR_INVALID_CHANNEL_ID = 0x02,
        ERR_INVALID_PROTOCOL_ID = 0x03,
        ERR_NULL_PARAMETER = 0x04,
        ERR_INVALID_IOCTL_VALUE = 0x05,
        ERR_INVALID_FLAGS = 0x06,
        ERR_FAILED = 0x07,
        ERR_DEVICE_NOT_CONNECTED = 0x08,
        ERR_TIMEOUT = 0x09,
        ERR_INVALID_MSG = 0x0A,
        ERR_INVALID_TIME_INTERVAL = 0x0B,
        ERR_EXCEEDED_LIMIT = 0x0C,
        ERR_INVALID_MSG_ID = 0x0D,
        ERR_DEVICE_IN_USE = 0x0E,
        ERR_INVALID_IA_ID = 0x0F,
        ERR_INVALID_IA_IMPLEMENTATION = 0x10,
        ERR_INVALID_DATA_RATE = 0x11,
        ERR_INVALID_ADDRESS = 0x12,
        ERR_INVALID_PROTOCOL = 0x13,
        ERR_INVALID_TX_MSG = 0x14,
        ERR_INVALID_RX_MSG = 0x15,
        ERR_NO_PERIODIC_MESSAGES = 0x16,
        ERR_MSG_NOT_FOUND = 0x17,
        ERR_INVALID_FILTER_ID = 0x18,
        ERR_NO_FLOW_CONTROL = 0x19,
        ERR_NOT_UNIQUE = 0x1A,
        ERR_INVALID_BAUDRATE = 0x1B,
        ERR_INVALID_DEVICE_ID = 0x1C,
        // J2534-2 additions
        ERR_INVALID_DEVICE_STATE = 0x1D,
        ERR_NO_DEVICE_STATE = 0x1E,
        ERR_TIMEOUT_MS = 0x1F,
        ERR_TIMEOUT_S = 0x20,
        ERR_TIMEOUT_NS = 0x21,
        ERR_NO_ISO15765_FILTER_SET = 0x22,
        ERR_NO_ISO15765_FLOW_CONTROL = 0x23,
        ERR_ISO15765_TX_TIMEOUT = 0x24,
        ERR_ISO15765_RX_TIMEOUT = 0x25,
        ERR_ISO15765_TX_FC_TIMEOUT = 0x26,
        ERR_ISO15765_RX_CF_TIMEOUT = 0x27,
        // Buffer / transport level
        ERR_BUFFER_EMPTY = 0x28,
        ERR_BUFFER_FULL = 0x29,
        ERR_BUFFER_OVERFLOW = 0x2A,
        ERR_PIN_INVALID = 0x2B,
        ERR_CHANNEL_IN_USE = 0x2C,
        ERR_MSG_FILTERED_OUT = 0x2D,
        ERR_NO_ISO15765_CONNECTION = 0x30,
        ERR_NO_CONNECTION = 0x31,
        ERR_INVALID_FILTER_PARAMETER = 0x32
    }

    public enum ProtocolId : uint
    {
        J1850PWM = 0x01,
        J1850VPW = 0x02,
        ISO9141 = 0x03,
        ISO14230 = 0x04,
        CAN = 0x05,
        ISO15765 = 0x06,
        SCI_A_ENGINE = 0x07,
        SCI_A_TRANS = 0x08,
        SCI_B_ENGINE = 0x09,
        SCI_B_TRANS = 0x0A,
        J1850VPW_PS = 0x8000,
        J1850PWM_PS = 0x8001,
        ISO9141_PS = 0x8002,
        ISO14230_PS = 0x8003,
        CAN_PS = 0x8004,
        ISO15765_PS = 0x8005
    }

    [Flags]
    public enum TxFlags : uint
    {
        NONE = 0x00000000,
        ISO15765_FRAME_PAD = 0x00000040,
        ISO15765_ADDR_TYPE = 0x00000080,
        CAN_29BIT_ID = 0x00000100,
        WAIT_P3_MIN_ONLY = 0x00000200,
        SCI_MESSAGE_TYPE = 0x00000400
    }

    [Flags]
    public enum RxStatus : uint
    {
        NONE = 0x00000000,
        TX_MSG_TYPE = 0x00000001,
        START_OF_MESSAGE = 0x00000002,
        ISO15765_FIRST_FRAME = 0x00000002,
        TX_INDICATION_SUCCESS = 0x00000004,
        TX_INDICATION_LOOPBACK = 0x00000008,
        ISO15765_PADDING_ERROR = 0x00000010,
        ISO15765_ADDR_TYPE = 0x00000080,
        CAN_29BIT_ID = 0x00000100
    }

    public enum IoctlId : uint
    {
        GET_CONFIG = 0x01,
        SET_CONFIG = 0x02,
        READ_VBATT = 0x03,
        FIVE_BAUD_INIT = 0x04,
        FAST_INIT = 0x05,
        CLEAR_TX_BUFFER = 0x07,
        CLEAR_RX_BUFFER = 0x08,
        CLEAR_PERIODIC_MSGS = 0x09,
        CLEAR_MSG_FILTERS = 0x0A,
        CLEAR_FUNCT_MSG_LOOKUP_TABLE = 0x0B,
        ADD_TO_FUNCT_MSG_LOOKUP_TABLE = 0x0C,
        DELETE_FROM_FUNCT_MSG_LOOKUP_TABLE = 0x0D,
        READ_PROG_VOLTAGE = 0x0E
    }

    /// <summary>Configuration parameter identifiers used with GET_CONFIG / SET_CONFIG.</summary>
    public enum ConfigParam : uint
    {
        DATA_RATE = 0x01,
        LOOPBACK = 0x03,
        NODE_ADDRESS = 0x04,
        NETWORK_LINE = 0x05,
        P1_MIN = 0x06,
        P1_MAX = 0x07,
        P2_MIN = 0x08,
        P2_MAX = 0x09,
        P3_MIN = 0x0A,
        P3_MAX = 0x0B,
        P4_MIN = 0x0C,
        P4_MAX = 0x0D,
        W1 = 0x0E,
        W2 = 0x0F,
        W3 = 0x10,
        W4 = 0x11,
        W5 = 0x12,
        TIDLE = 0x13,
        TINIL = 0x14,
        TWUP = 0x15,
        PARITY = 0x16,
        BIT_SAMPLE_POINT = 0x17,
        SYNC_JUMP_WIDTH = 0x18,
        W0 = 0x19,
        T1_MAX = 0x1A,
        T2_MAX = 0x1B,
        T4_MAX = 0x1C,
        T5_MAX = 0x1D,
        ISO15765_BS = 0x1E,
        ISO15765_STMIN = 0x1F,
        DATA_BITS = 0x20,
        FIVE_BAUD_MOD = 0x21,
        BS_TX = 0x22,
        STMIN_TX = 0x23,
        T3_MAX = 0x24,
        ISO15765_WFT_MAX = 0x25,
        // J2534-2
        CAN_MIXED_FORMAT = 0x8000,
        J1962_PINS = 0x8001,
        SW_CAN_HS_DATA_RATE = 0x8010,
        SW_CAN_SPEEDCHANGE_ENABLE = 0x8011,
        SW_CAN_RES_SWITCH = 0x8012,
        SW_CAN_HS = 0x8013,
        SW_CAN_PS_BAUD = 0x8014,
        ISO15765_PAD_VALUE = 0x8020,
        ISO15765_EXT_ADDR = 0x8030
    }

    /// <summary>J2534 filter types.</summary>
    public enum FilterType : uint
    {
        PASS_FILTER = 0x01,
        BLOCK_FILTER = 0x02,
        FLOW_CONTROL_FILTER = 0x03
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PassThruMsg
    {
        public const int MaxDataSize = 4128;

        public ProtocolId ProtocolId;
        public RxStatus RxStatus;
        public TxFlags TxFlags;
        public uint Timestamp;
        public uint DataSize;
        public uint ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxDataSize)]
        public byte[] Data;

        /// <summary>Creates an unmanaged-friendly message with a fixed 4128 byte data buffer.</summary>
        public static PassThruMsg Create(ProtocolId protocol, TxFlags flags, byte[] data)
        {
            PassThruMsg msg = new PassThruMsg();
            msg.ProtocolId = protocol;
            msg.RxStatus = RxStatus.NONE;
            msg.TxFlags = flags;
            msg.Timestamp = 0;
            msg.ExtraDataIndex = 0;
            msg.Data = new byte[MaxDataSize];
            msg.DataSize = (uint)data.Length;
            Array.Copy(data, msg.Data, data.Length);
            return msg;
        }

        /// <summary>Returns a copy of the meaningful bytes of <see cref="Data"/>.</summary>
        public byte[] GetPayload()
        {
            int len = (int)DataSize;
            if (Data == null) return new byte[0];
            if (len > Data.Length) len = Data.Length;
            byte[] copy = new byte[len];
            Array.Copy(Data, copy, len);
            return copy;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SConfig
    {
        public ConfigParam Parameter;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SConfigList
    {
        public uint Count;
        public IntPtr ConfigPtr;
    }

    /// <summary>Pin numbers accepted by PassThruSetProgrammingVoltage.</summary>
    public static class ProgrammingPin
    {
        public const uint ShortToGround = 0xFFFFFFFF;
        public const uint VoltageOff = 0x00000000;
        /// <summary>Ford FEPS pin number used by the OBDX Pro / VCM II family (as observed in the reference tool).</summary>
        public const uint Feps = 0x80;
    }

    /// <summary>Thin wrapper that turns a J2534Err into an exception with a readable message.</summary>
    public class J2534Exception : Exception
    {
        public J2534Err Error { get; private set; }

        public J2534Exception(string operation, J2534Err error)
            : base(operation + " failed: " + error + " (" + error.Describe() + ")")
        {
            Error = error;
        }
    }

    public static class J2534Errors
    {
        /// <summary>Human readable explanation of a J2534 status code.</summary>
        public static string Describe(this J2534Err err)
        {
            switch (err)
            {
                case J2534Err.STATUS_NOERROR: return "Success";
                case J2534Err.ERR_NOT_SUPPORTED: return "Device does not support this request";
                case J2534Err.ERR_INVALID_CHANNEL_ID: return "Invalid channel id";
                case J2534Err.ERR_INVALID_PROTOCOL_ID: return "Invalid protocol id";
                case J2534Err.ERR_NULL_PARAMETER: return "Null parameter";
                case J2534Err.ERR_INVALID_IOCTL_VALUE: return "Invalid ioctl value";
                case J2534Err.ERR_INVALID_FLAGS: return "Invalid flags";
                case J2534Err.ERR_FAILED: return "General failure";
                case J2534Err.ERR_DEVICE_NOT_CONNECTED: return "Interface not connected";
                case J2534Err.ERR_TIMEOUT: return "Timed out";
                case J2534Err.ERR_INVALID_MSG: return "Invalid message";
                case J2534Err.ERR_DEVICE_IN_USE: return "Interface is in use by another application";
                case J2534Err.ERR_INVALID_DEVICE_ID: return "Invalid device id";
                case J2534Err.ERR_BUFFER_EMPTY: return "No message available";
                case J2534Err.ERR_BUFFER_FULL: return "Buffer full";
                case J2534Err.ERR_BUFFER_OVERFLOW: return "Buffer overflow";
                case J2534Err.ERR_NO_FLOW_CONTROL: return "No flow control filter matching the message found";
                case J2534Err.ERR_NO_ISO15765_FILTER_SET: return "No ISO15765 filter set";
                case J2534Err.ERR_ISO15765_TX_TIMEOUT: return "ISO15765 transmit timeout";
                case J2534Err.ERR_ISO15765_RX_TIMEOUT: return "ISO15765 receive timeout";
                case J2534Err.ERR_MSG_FILTERED_OUT: return "Message filtered out";
                case J2534Err.ERR_PIN_INVALID: return "Invalid pin number";
                default: return "Unknown J2534 status";
            }
        }
    }
}
