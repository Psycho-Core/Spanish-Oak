using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PsychoFlash.Core.J2534
{
    /// <summary>
    /// Friendly wrapper around a J2534 interface: opens the device, brings up an ISO15765
    /// (CAN) channel, programs the message filter and exposes blocking read/write helpers.
    /// </summary>
    public sealed class J2534Device : IPassThruDevice
    {
        private readonly J2534Library _lib;
        private uint _deviceId;
        private uint _channelId;
        private bool _deviceOpen;
        private bool _channelOpen;
        private uint _filterId;

        public J2534Library Library { get { return _lib; } }
        public uint DeviceId { get { return _deviceId; } }
        public uint ChannelId { get { return _channelId; } }
        public string FirmwareVersion { get; private set; }
        public string DllVersion { get; private set; }
        public string ApiVersion { get; private set; }
        public uint BaudRate { get; private set; }
        public uint RequestId { get; private set; }
        public uint ResponseId { get; private set; }

        private J2534Device(J2534Library lib)
        {
            _lib = lib;
            FirmwareVersion = DllVersion = ApiVersion = string.Empty;
        }

        /// <summary>Opens the first interface registered in the Windows registry.</summary>
        public static J2534Device OpenFirstInstalled()
        {
            IList<J2534Library.Registration> found = J2534Library.FindInstalledInterfaces();
            if (found.Count == 0)
            {
                throw new InvalidOperationException(
                    "No SAE J2534 interface found in HKLM\\SOFTWARE\\PassThruSupport.04.04. " +
                    "Install the vendor driver (OBDX Pro FT, Tactrix Openport, Ford VCM II, ...) first.");
            }
            return Open(found[0].FunctionLibrary);
        }

        public static J2534Device Open(string dllPath)
        {
            J2534Library lib = J2534Library.Load(dllPath);
            J2534Device dev = new J2534Device(lib);
            dev.OpenDevice(null);
            return dev;
        }

        public static J2534Device Open(string dllPath, string deviceName)
        {
            J2534Library lib = J2534Library.Load(dllPath);
            J2534Device dev = new J2534Device(lib);
            dev.OpenDevice(deviceName);
            return dev;
        }

        private void OpenDevice(string deviceName)
        {
            IntPtr namePtr = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(deviceName))
                {
                    namePtr = Marshal.StringToHGlobalAnsi(deviceName);
                }
                J2534Err err = _lib.Open(namePtr, out _deviceId);
                if (err != J2534Err.STATUS_NOERROR) throw new J2534Exception("PassThruOpen", err);
                _deviceOpen = true;
            }
            finally
            {
                if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
            }
            ReadVersions();
        }

        private void ReadVersions()
        {
            IntPtr fw = Marshal.AllocHGlobal(80);
            IntPtr dll = Marshal.AllocHGlobal(80);
            IntPtr api = Marshal.AllocHGlobal(80);
            try
            {
                for (int i = 0; i < 80; i++)
                {
                    Marshal.WriteByte(fw, i, 0);
                    Marshal.WriteByte(dll, i, 0);
                    Marshal.WriteByte(api, i, 0);
                }
                J2534Err err = _lib.ReadVersion(_deviceId, fw, dll, api);
                if (err == J2534Err.STATUS_NOERROR)
                {
                    FirmwareVersion = Marshal.PtrToStringAnsi(fw) ?? string.Empty;
                    DllVersion = Marshal.PtrToStringAnsi(dll) ?? string.Empty;
                    ApiVersion = Marshal.PtrToStringAnsi(api) ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // Version information is optional.
            }
            finally
            {
                Marshal.FreeHGlobal(fw);
                Marshal.FreeHGlobal(dll);
                Marshal.FreeHGlobal(api);
            }
        }

        /// <summary>Fetches the last J2534 error description from the driver, if it supports it.</summary>
        public string GetLastErrorDescription()
        {
            IntPtr ptr = Marshal.AllocHGlobal(256);
            try
            {
                for (int i = 0; i < 256; i++) Marshal.WriteByte(ptr, i, 0);
                J2534Err err = _lib.GetLastError(ptr);
                if (err != J2534Err.STATUS_NOERROR) return err.Describe();
                return Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Connects an ISO15765 channel and installs a pass filter so only responses from
        /// <paramref name="responseId"/> are queued. Ford EEC-VI uses 0x7E0 / 0x7E8 at 500 kbit/s.
        /// </summary>
        public void ConnectIso15765(uint baudRate, uint requestId, uint responseId, bool use29BitId)
        {
            BaudRate = baudRate;
            RequestId = requestId;
            ResponseId = responseId;

            uint flags = use29BitId ? (uint)TxFlags.CAN_29BIT_ID : 0;
            J2534Err err = _lib.Connect(_deviceId, ProtocolId.ISO15765, flags, baudRate, out _channelId);
            if (err != J2534Err.STATUS_NOERROR) throw new J2534Exception("PassThruConnect(ISO15765, " + baudRate + ")", err);
            _channelOpen = true;

            List<SConfig> config = new List<SConfig>();
            config.Add(MakeConfig(ConfigParam.DATA_RATE, baudRate));
            config.Add(MakeConfig(ConfigParam.LOOPBACK, 0));
            config.Add(MakeConfig(ConfigParam.ISO15765_BS, 0));
            config.Add(MakeConfig(ConfigParam.ISO15765_STMIN, 0));
            config.Add(MakeConfig(ConfigParam.ISO15765_WFT_MAX, 0));
            TrySetConfig(config);

            ClearBuffers();
            StartPassFilter(requestId, responseId, use29BitId);
        }

        private static SConfig MakeConfig(ConfigParam p, uint v)
        {
            SConfig c = new SConfig();
            c.Parameter = p;
            c.Value = v;
            return c;
        }

        /// <summary>Applies configuration parameters, ignoring ones the driver does not know.</summary>
        public void TrySetConfig(IList<SConfig> items)
        {
            if (items == null || items.Count == 0) return;
            int size = Marshal.SizeOf(typeof(SConfig));
            IntPtr listPtr = Marshal.AllocHGlobal(size * items.Count);
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Marshal.StructureToPtr(items[i], new IntPtr(listPtr.ToInt64() + i * size), false);
                }
                SConfigList list = new SConfigList();
                list.Count = (uint)items.Count;
                list.ConfigPtr = listPtr;

                IntPtr inPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SConfigList)));
                try
                {
                    Marshal.StructureToPtr(list, inPtr, false);
                    // A driver may apply only part of the list; the return code is deliberately ignored.
                    _lib.Ioctl(_channelId, IoctlId.SET_CONFIG, inPtr, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(inPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(listPtr);
            }
        }

        private void StartPassFilter(uint requestId, uint responseId, bool use29BitId)
        {
            PassThruMsg mask = MakeCanIdMessage(0xFFFFFFFF, use29BitId);
            PassThruMsg pattern = MakeCanIdMessage(responseId, use29BitId);
            PassThruMsg flowControl = MakeCanIdMessage(requestId, use29BitId);

            IntPtr maskPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PassThruMsg)));
            IntPtr patPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PassThruMsg)));
            IntPtr fcPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PassThruMsg)));
            try
            {
                Marshal.StructureToPtr(mask, maskPtr, false);
                Marshal.StructureToPtr(pattern, patPtr, false);
                Marshal.StructureToPtr(flowControl, fcPtr, false);

                uint filterId = 0;
                J2534Err err = _lib.StartMsgFilter(_channelId, FilterType.FLOW_CONTROL_FILTER, maskPtr, patPtr, fcPtr, ref filterId);
                if (err != J2534Err.STATUS_NOERROR)
                {
                    // Some drivers only accept a plain pass filter on ISO15765 channels.
                    err = _lib.StartMsgFilter(_channelId, FilterType.PASS_FILTER, maskPtr, patPtr, IntPtr.Zero, ref filterId);
                }
                if (err != J2534Err.STATUS_NOERROR)
                {
                    throw new J2534Exception("PassThruStartMsgFilter(0x" + responseId.ToString("X3") + ")", err);
                }
                _filterId = filterId;
            }
            finally
            {
                Marshal.FreeHGlobal(maskPtr);
                Marshal.FreeHGlobal(patPtr);
                Marshal.FreeHGlobal(fcPtr);
            }
        }

        private PassThruMsg MakeCanIdMessage(uint canId, bool use29BitId)
        {
            byte[] data;
            if (use29BitId)
            {
                data = new byte[] { (byte)(canId >> 24), (byte)(canId >> 16), (byte)(canId >> 8), (byte)canId };
            }
            else
            {
                data = new byte[] { 0x00, 0x00, (byte)((canId >> 8) & 0xFF), (byte)(canId & 0xFF) };
            }
            TxFlags flags = use29BitId ? TxFlags.CAN_29BIT_ID : TxFlags.NONE;
            return PassThruMsg.Create(ProtocolId.ISO15765, flags, data);
        }

        public void ClearBuffers()
        {
            if (!_channelOpen) return;
            _lib.Ioctl(_channelId, IoctlId.CLEAR_RX_BUFFER, IntPtr.Zero, IntPtr.Zero);
            _lib.Ioctl(_channelId, IoctlId.CLEAR_TX_BUFFER, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Reads the battery voltage measured on the interface's pin 16 (millivolts).</summary>
        public uint ReadBatteryVoltage()
        {
            IntPtr outPtr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(outPtr, 0);
                // READ_VBATT is issued against the device, not the channel, on most drivers;
                // the device id works for those that accept either.
                J2534Err err = _lib.Ioctl(_deviceId, IoctlId.READ_VBATT, IntPtr.Zero, outPtr);
                if (err != J2534Err.STATUS_NOERROR)
                {
                    err = _lib.Ioctl(_channelId, IoctlId.READ_VBATT, IntPtr.Zero, outPtr);
                }
                if (err != J2534Err.STATUS_NOERROR) throw new J2534Exception("READ_VBATT", err);
                return (uint)Marshal.ReadInt32(outPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(outPtr);
            }
        }

        /// <summary>Drives the Ford FEPS line (18 V) used to put the PCM into programming mode.</summary>
        public void SetProgrammingVoltage(uint pinNumber, uint voltage)
        {
            // Millivolts, as the J2534 API defines it - the caller converts the profile's volts.
            // Passing volts straight through asked interfaces for 18 mV (no FEPS at all) until the
            // simulated PCM in the self test caught it.
            J2534Err err = _lib.SetProgrammingVoltage(_deviceId, pinNumber, voltage);
            if (err != J2534Err.STATUS_NOERROR)
            {
                throw new J2534Exception("PassThruSetProgrammingVoltage(pin " + pinNumber + ", " + voltage + " mV)", err);
            }
        }

        /// <summary>Writes one message; the interface performs ISO-TP segmentation automatically.</summary>
        public void WriteMessage(byte[] canIdAndPayload, TxFlags flags, uint timeoutMs)
        {
            PassThruMsg msg = PassThruMsg.Create(ProtocolId.ISO15765, flags | (RequestId > 0x7FF ? TxFlags.CAN_29BIT_ID : TxFlags.NONE), canIdAndPayload);
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PassThruMsg)));
            try
            {
                Marshal.StructureToPtr(msg, ptr, false);
                uint count = 1;
                J2534Err err = _lib.WriteMsgs(_channelId, ptr, ref count, timeoutMs);
                if (err != J2534Err.STATUS_NOERROR) throw new J2534Exception("PassThruWriteMsgs", err);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>Reads one message and returns its bytes (CAN id + ISO-TP assembled payload).</summary>
        public byte[] ReadMessage(uint timeoutMs)
        {
            int size = Marshal.SizeOf(typeof(PassThruMsg));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);
                uint count = 1;
                J2534Err err = _lib.ReadMsgs(_channelId, ptr, ref count, timeoutMs);

                // "Nothing arrived" is not an error, it is the normal way for a read to end. The
                // interface contract promises a TimeoutException for it, so drivers that report
                // ERR_TIMEOUT / ERR_BUFFER_EMPTY instead of returning zero messages are translated
                // here rather than leaking a J2534Exception into the protocol code.
                if (err == J2534Err.ERR_TIMEOUT || err == J2534Err.ERR_BUFFER_EMPTY ||
                    err == J2534Err.ERR_ISO15765_RX_TIMEOUT || err == J2534Err.ERR_ISO15765_TX_FC_TIMEOUT)
                {
                    throw new TimeoutException("No message from the interface within " + timeoutMs +
                                               " ms (" + err + ").");
                }
                if (err != J2534Err.STATUS_NOERROR) throw new J2534Exception("PassThruReadMsgs", err);
                if (count == 0) return new byte[0];
                PassThruMsg msg = (PassThruMsg)Marshal.PtrToStructure(ptr, typeof(PassThruMsg));
                return msg.GetPayload();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Disconnect()
        {
            if (!_channelOpen) return;
            if (_filterId != 0)
            {
                _lib.StopMsgFilter(_channelId, _filterId);
                _filterId = 0;
            }
            _lib.Disconnect(_channelId);
            _channelOpen = false;
        }

        public void Dispose()
        {
            try { Disconnect(); } catch (Exception) { }
            if (_deviceOpen)
            {
                try { _lib.Close(_deviceId); } catch (Exception) { }
                _deviceOpen = false;
            }
            try { _lib.Dispose(); } catch (Exception) { }
        }

        /// <summary>Hex dump helper used by the log (e.g. "00 00 07 E8 67 01 ...").</summary>
        public static string ToHex(byte[] data, int maxBytes)
        {
            if (data == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            int n = maxBytes <= 0 || maxBytes > data.Length ? data.Length : maxBytes;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            if (n < data.Length) sb.Append(" ...");
            return sb.ToString();
        }
    }
}
