using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using PsychoFlash.Core.J2534;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>Raised when the PCM answers a request with a negative response code.</summary>
    public sealed class NegativeResponseException : Exception
    {
        public byte Service { get; private set; }
        public byte Code { get; private set; }

        public NegativeResponseException(byte service, byte code)
            : base("Service 0x" + service.ToString("X2") + " rejected with NRC 0x" + code.ToString("X2") + " (" + Describe(code) + ")")
        {
            Service = service;
            Code = code;
        }

        /// <summary>Plain-English meaning of a UDS/Ford negative response code.</summary>
        public static string Describe(byte code)
        {
            switch (code)
            {
                case 0x10: return "general reject";
                case 0x11: return "service not supported";
                case 0x12: return "sub-function not supported";
                case 0x13: return "incorrect message length";
                case 0x21: return "busy, repeat request";
                case 0x22: return "conditions not correct";
                case 0x24: return "request sequence error";
                case 0x31: return "request out of range";
                case 0x33: return "security access denied";
                case 0x35: return "invalid key";
                case 0x36: return "exceeded number of attempts";
                case 0x37: return "required time delay not expired";
                case 0x50: return "security access not granted";
                case 0x70: return "upload/download not accepted";
                case 0x71: return "transfer data suspended";
                case 0x72: return "general programming failure";
                case 0x73: return "wrong block sequence counter";
                case 0x78: return "response pending";
                case 0x7E: return "sub-function not supported in active session";
                case 0x7F: return "service not supported in active session";
                case 0x92: return "voltage too high";
                case 0x93: return "voltage too low";
                default: return "unknown code";
            }
        }
    }

    /// <summary>
    /// Ford EEC-VI (Spanish Oak) transport: builds SAE J2534 messages (four byte CAN identifier
    /// followed by the service payload), waits for the assembled response and understands
    /// the negative-response / pending conventions of the PCM.
    /// </summary>
    public sealed class FordPcmClient : IDisposable
    {
        // Service identifiers
        public const byte ServiceDiagnosticSessionControl = 0x10;
        public const byte ServiceEcuReset = 0x11;
        public const byte ServiceClearDiagnosticInformation = 0x14;
        public const byte ServiceReadDtcInformation = 0x19;
        public const byte ServiceReadDataByIdentifier = 0x22;
        public const byte ServiceReadMemoryByAddress = 0x23;
        public const byte ServiceSecurityAccess = 0x27;
        public const byte ServiceTestPresent = 0x3E;
        public const byte ServiceRequestDownload = 0x34;
        public const byte ServiceTransferData = 0x36;
        public const byte ServiceTransferExit = 0x37;
        public const byte ServiceRoutineControl = 0x31;
        public const byte ServiceControlDtcSetting = 0x85;
        public const byte ServiceEraseFlash = 0xB2;

        private readonly J2534Device _device;
        private readonly PcmProfile _profile;
        private readonly Action<string> _log;
        private readonly object _sync = new object();

        public FordPcmClient(J2534Device device, PcmProfile profile, Action<string> log)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (profile == null) throw new ArgumentNullException("profile");
            _device = device;
            _profile = profile;
            _log = log ?? delegate { };
        }

        public PcmProfile Profile { get { return _profile; } }
        public J2534Device Device { get { return _device; } }

        /// <summary>Default per-request timeout in milliseconds (response pending restarts it).</summary>
        public uint ResponseTimeout { get; set; } = 2000;

        /// <summary>
        /// Timeout for handing a request to the interface. Multi-frame transfers (0x36) need more
        /// than the response timeout because the driver has to clock every ISO-TP frame out.
        /// </summary>
        public uint WriteTimeout { get; set; } = 5000;

        private void Log(string message)
        {
            _log(message);
        }

        /// <summary>
        /// Prefixes a service payload with the four byte CAN identifier, which is what a J2534
        /// ISO15765 message expects. Kept static so the framing can be unit tested without hardware.
        /// </summary>
        public static byte[] BuildFrame(PcmProfile profile, byte[] payload)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            if (payload == null) throw new ArgumentNullException("payload");

            uint id = profile.RequestId;
            byte[] frame = new byte[payload.Length + 4];
            if (profile.Use29BitIdentifier)
            {
                frame[0] = (byte)(id >> 24);
                frame[1] = (byte)(id >> 16);
                frame[2] = (byte)(id >> 8);
                frame[3] = (byte)id;
            }
            else
            {
                frame[0] = 0x00;
                frame[1] = 0x00;
                frame[2] = (byte)((id >> 8) & 0xFF);
                frame[3] = (byte)(id & 0xFF);
            }
            Array.Copy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        /// <summary>Wraps a service payload in the J2534 CAN identifier header and sends it.</summary>
        public void SendRaw(byte[] payload)
        {
            byte[] frame = BuildFrame(_profile, payload);
            _device.WriteMessage(frame, TxFlags.ISO15765_FRAME_PAD, WriteTimeout);
        }

        /// <summary>
        /// Sends a request and returns the service payload of the positive response.
        /// Throws <see cref="NegativeResponseException"/> when the PCM answers 0x7F.
        /// </summary>
        public byte[] Request(byte[] payload, int expectedResponseLength)
        {
            if (payload == null || payload.Length == 0) throw new ArgumentException("payload");
            lock (_sync)
            {
                _device.ClearBuffers();
                SendRaw(payload);
                return WaitForResponse(payload[0], expectedResponseLength);
            }
        }

        private byte[] WaitForResponse(byte requestService, int minimumResponseLength)
        {
            int minimum = minimumResponseLength > 0 ? minimumResponseLength : 1;
            byte[] best = null;
            int silentReads = 0;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                byte[] raw = null;
                try
                {
                    raw = _device.ReadMessage(ResponseTimeout);
                }
                catch (TimeoutException)
                {
                    raw = null;
                }
                catch (J2534Exception ex)
                {
                    if (ex.Error == J2534Err.ERR_BUFFER_EMPTY || ex.Error == J2534Err.ERR_TIMEOUT)
                    {
                        raw = null;
                    }
                    else
                    {
                        throw;
                    }
                }

                if (raw == null || raw.Length < 5)
                {
                    silentReads++;
                    // Give up quickly when the PCM never answers, but hand back whatever we have.
                    if (best != null) break;
                    if (silentReads >= 3) break;
                    continue;
                }
                silentReads = 0;

                byte[] data = StripCanId(raw);
                if (data.Length < 2) continue;

                if (data[0] == 0x7F)
                {
                    byte service = data.Length > 1 ? data[1] : (byte)0;
                    byte code = data.Length > 2 ? data[2] : (byte)0;
                    if (code == 0x78)
                    {
                        Log("  PCM: response pending (0x78)");
                        continue;
                    }
                    throw new NegativeResponseException(service, code);
                }

                if (data[0] != (byte)(requestService + 0x40)) continue;

                // Some drivers hand back a multi-frame answer as several messages. Keep the
                // longest payload seen instead of concatenating (which would duplicate bytes).
                if (best == null || data.Length > best.Length) best = data;
                if (best.Length >= minimum) return best;
            }

            if (best != null) return best;
            throw new TimeoutException("No response from the PCM for service 0x" + requestService.ToString("X2") +
                                       ". Check ignition, FEPS supply and the CAN wiring.");
        }

        /// <summary>Removes the four byte CAN identifier that J2534 prepends to every message.</summary>
        public static byte[] StripCanId(byte[] raw)
        {
            if (raw == null) return new byte[0];
            if (raw.Length <= 4) return new byte[0];
            byte[] data = new byte[raw.Length - 4];
            Array.Copy(raw, 4, data, 0, data.Length);
            return data;
        }

        // ------------------------------------------------------------------ services ---

        /// <summary>0x10 - start a diagnostic session, e.g. 0x85 for Ford programming mode.</summary>
        public bool StartDiagnosticSession(byte session)
        {
            Log("0x10 -> start session 0x" + session.ToString("X2"));
            byte[] p = new byte[] { ServiceDiagnosticSessionControl, session };
            byte[] r = Request(p, 1);
            return r.Length >= 2 && r[0] == 0x50;
        }

        /// <summary>0x3E - keeps the programming session alive.</summary>
        public void TesterPresent()
        {
            byte[] p = new byte[] { ServiceTestPresent, 0x00 };
            try { Request(p, 1); }
            catch (Exception) { /* tester present is best effort */ }
        }

        /// <summary>0x85 - stops the PCM from logging DTCs while flashing.</summary>
        public bool ControlDtcSetting(bool enable)
        {
            Log("0x85 -> control DTC setting " + (enable ? "on" : "off"));
            byte[] p = new byte[] { ServiceControlDtcSetting, (byte)(enable ? 0x01 : 0x02), 0xFF };
            byte[] r = Request(p, 1);
            return r.Length >= 1 && r[0] == 0xC5;
        }

        /// <summary>0x14 - clears stored diagnostic trouble codes.</summary>
        public bool ClearDiagnosticInformation()
        {
            Log("0x14 -> clear diagnostic information");
            byte[] p = new byte[] { ServiceClearDiagnosticInformation, 0xFF, 0x00 };
            byte[] r = Request(p, 1);
            return r.Length >= 1 && r[0] == 0x54;
        }

        /// <summary>0x11 - resets the PCM (used before/after a flash).</summary>
        public bool EcuReset(byte resetType)
        {
            Log("0x11 -> ECU reset 0x" + resetType.ToString("X2"));
            byte[] p = new byte[] { ServiceEcuReset, resetType };
            byte[] r = Request(p, 1);
            return r.Length >= 2 && r[0] == 0x51;
        }

        /// <summary>
        /// 0x27 - reads the seed and returns the computed key. Throws when the unlock is
        /// refused so callers can retry (the PCM limits attempts per ignition cycle).
        /// </summary>
        public bool SecurityAccess(ISeedKeyAlgorithm algorithm)
        {
            if (algorithm == null) throw new ArgumentNullException("algorithm");
            Log("0x27 -> request seed (algorithm: " + algorithm.Name + ")");
            byte[] requestSeed = new byte[] { ServiceSecurityAccess, 0x01 };
            byte[] seedResponse = Request(requestSeed, 2 + algorithm.SeedLength);
            if (seedResponse[0] != 0x67) return false;

            uint seed = 0;
            for (int i = 0; i < algorithm.SeedLength; i++)
            {
                seed = (seed << 8) | seedResponse[2 + i];
            }
            Log("     seed = 0x" + seed.ToString("X" + (algorithm.SeedLength * 2).ToString(CultureInfo.InvariantCulture)));

            uint key = algorithm.ComputeKey(seed);
            byte[] sendKey = new byte[2 + algorithm.KeyLength];
            sendKey[0] = ServiceSecurityAccess;
            sendKey[1] = 0x02;
            for (int i = 0; i < algorithm.KeyLength; i++)
            {
                sendKey[2 + i] = (byte)(key >> (8 * (algorithm.KeyLength - 1 - i)));
            }
            Log("     key  = 0x" + key.ToString("X" + (algorithm.KeyLength * 2).ToString(CultureInfo.InvariantCulture)));

            byte[] keyResponse = Request(sendKey, 1);
            bool granted = keyResponse.Length >= 2 && keyResponse[0] == 0x67 && keyResponse[1] == 0x02;
            Log(granted ? "     security access granted" : "     security access refused");
            return granted;
        }

        /// <summary>0x23 - reads <paramref name="length"/> bytes starting at <paramref name="address"/>.</summary>
        public byte[] ReadMemory(uint address, int length)
        {
            byte[] request = new byte[7];
            request[0] = ServiceReadMemoryByAddress;
            request[1] = 0x00;                                  // address/length format, Ford EEC-VI style
            request[2] = (byte)((address >> 16) & 0xFF);
            request[3] = (byte)((address >> 8) & 0xFF);
            request[4] = (byte)(address & 0xFF);
            request[5] = (byte)((length >> 8) & 0xFF);
            request[6] = (byte)(length & 0xFF);

            byte[] response = Request(request, length + 1);

            // Positive answer: 0x63 followed by the data.
            if (response.Length < 2 || response[0] != 0x63) throw new InvalidOperationException("Unexpected response to 0x23 read.");
            byte[] data = ExtractReadData(response, length);
            if (data.Length != length)
            {
                Log("     0x23 at 0x" + address.ToString("X6") + " asked for " + length + " bytes, answer carried " +
                    data.Length + " (raw message " + response.Length + " bytes)");
            }
            return data;
        }

        /// <summary>
        /// Takes the data out of a 0x23 answer. The reference tool reads a whole image and then
        /// throws away one byte from the start of the file, because the answer begins with the
        /// 0x63 response byte; some EEC-VI operating systems also insert a counter byte between
        /// 0x63 and the data. Instead of guessing, this looks at the length that was asked for:
        /// the data is the tail of the message, so a longer answer simply means a longer prefix.
        /// </summary>
        public static byte[] ExtractReadData(byte[] response, int expectedLength)
        {
            if (response == null || response.Length == 0) return new byte[0];

            int start = response[0] == 0x63 ? 1 : 0;
            int available = response.Length - start;

            // A short answer is handed back as is so the caller can report or retry it; a long
            // answer can only mean extra prefix bytes, so keep the tail.
            if (expectedLength > 0 && available > expectedLength)
            {
                start = response.Length - expectedLength;
                available = expectedLength;
            }

            byte[] data = new byte[available];
            Array.Copy(response, start, data, 0, available);
            return data;
        }

        /// <summary>
        /// 0x27/0x01 - reads the seed <b>without</b> sending a key. Costs no security attempts, so
        /// it is the safe way to find out whether the PCM is talking and what it seeds with.
        /// </summary>
        public byte[] RequestSeed(int seedLength)
        {
            Log("0x27 -> request seed (no key will be sent)");
            byte[] r = Request(new byte[] { ServiceSecurityAccess, 0x01 }, 2 + seedLength);
            if (r.Length >= 2 && r[0] == 0x67) return r;
            throw new InvalidOperationException("Unexpected response to 0x27/0x01 read.");
        }

        /// <summary>
        /// 0x19/0x02 - reads every stored trouble code, filtered by the status mask 0xFF.
        /// Needs no programming session, FEPS or security access: key on is enough.
        /// </summary>
        public byte[] ReadTroubleCodes()
        {
            Log("0x19 -> read trouble codes (status mask 0xFF)");
            byte[] r = Request(new byte[] { ServiceReadDtcInformation, 0x02, 0xFF }, 1);
            if (r.Length < 1 || r[0] != 0x59)
            {
                throw new InvalidOperationException("Unexpected response to 0x19 read.");
            }
            return r;
        }

        /// <summary>
        /// 0x01 - reads one OBD-II parameter (SAE J1979 mode 01). Returns the data bytes after the
        /// echoed PID, or null when the PCM does not support that PID.
        /// </summary>
        public byte[] ReadPid(byte pid)
        {
            try
            {
                byte[] r = Request(new byte[] { ObdPids.ServiceCurrentData, pid }, 3);
                return ObdPids.ExtractPayload(pid, r);
            }
            catch (NegativeResponseException)
            {
                return null;
            }
        }

        /// <summary>0x22 - reads a data identifier, e.g. 0xF190 for the VIN.</summary>
        public byte[] ReadDataByIdentifier(ushort did)
        {
            Log("0x22 -> read DID 0x" + did.ToString("X4"));
            byte[] p = new byte[] { ServiceReadDataByIdentifier, (byte)(did >> 8), (byte)(did & 0xFF) };
            byte[] r = Request(p, 0);
            if (r.Length < 4 || r[0] != 0x62) return new byte[0];
            byte[] data = new byte[r.Length - 3];
            Array.Copy(r, 3, data, 0, data.Length);
            return data;
        }

        /// <summary>0x34 - announces the download; the PCM erases and reports the block size.</summary>
        public bool RequestDownload()
        {
            byte[] payload = new byte[_profile.RequestDownloadPayload.Length + 1];
            payload[0] = ServiceRequestDownload;
            Array.Copy(_profile.RequestDownloadPayload, 0, payload, 1, _profile.RequestDownloadPayload.Length);
            Log("0x34 -> request download " + BytesToHex(_profile.RequestDownloadPayload));
            byte[] r = Request(payload, 1);
            return r.Length >= 1 && r[0] == 0x74;
        }

        /// <summary>0x36 - sends one block of flash data.</summary>
        public void TransferData(byte[] block)
        {
            byte[] payload = new byte[block.Length + 1];
            payload[0] = ServiceTransferData;
            Array.Copy(block, 0, payload, 1, block.Length);
            byte[] r = Request(payload, 1);
            if (r.Length < 1 || r[0] != 0x76) throw new InvalidOperationException("Unexpected response to 0x36 transfer data.");
        }

        /// <summary>0x37 - closes the download.</summary>
        public bool TransferExit()
        {
            Log("0x37 -> transfer exit");
            byte[] r = Request(new byte[] { ServiceTransferExit }, 1);
            return r.Length >= 1 && r[0] == 0x77;
        }

        /// <summary>0xB2 - Ford specific flash erase (Spanish Oak: 0xB2 0xAA).</summary>
        public bool EraseFlash()
        {
            Log("0xB2 -> erase flash " + BytesToHex(_profile.ErasePayload));
            byte[] r = Request(_profile.ErasePayload, 1);
            // 0xF2 is the positive answer to 0xB2; some PCMs simply stop responding while erasing.
            if (r.Length >= 1 && r[0] == 0xF2) return true;
            return r.Length >= 1 && r[0] == 0xB2 + 0x40;
        }

        /// <summary>Best effort identification string for the log and the UI.</summary>
        public string ReadVehicleIdentification()
        {
            try
            {
                byte[] vin = ReadDataByIdentifier(0xF190);
                if (vin != null && vin.Length > 0)
                {
                    string text = System.Text.Encoding.ASCII.GetString(vin).Trim('\0', ' ');
                    if (text.Length >= 11) return text;
                }
            }
            catch (Exception)
            {
                // Identification is optional - a locked PCM refuses data identifiers.
            }
            return string.Empty;
        }

        public static string BytesToHex(byte[] data)
        {
            if (data == null) return string.Empty;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            // The device is owned by the caller; nothing to release here.
        }
    }
}
