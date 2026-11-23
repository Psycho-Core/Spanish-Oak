using System;
using System.Collections.Generic;
using PsychoFlash.Core.J2534;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.SelfTest
{
    /// <summary>
    /// A Spanish Oak 1024k PCM that exists only in memory, sitting behind the same
    /// <see cref="IPassThruDevice"/> seam the real J2534 driver uses.
    ///
    /// It speaks the recovered protocol at message level - CAN identifier plus payload - and
    /// enforces the behaviour that matters: FEPS before the programming session, security access
    /// with the real key algorithm, erase, download, block transfer, read-back, trouble codes and
    /// OBD-II parameters. It also misbehaves on request (wrong key, response pending, a first-frame
    /// hiccup, or the legacy <c>63 &lt;counter&gt; &lt;data&gt;</c> read layout), so the client's
    /// error handling gets exercised rather than assumed.
    /// </summary>
    internal sealed class SimulatedPcm : IPassThruDevice
    {
        public const int ImageSize = 0x100000;
        // static readonly, not const: (byte)0x7E8 is a compile-time error on a constant.
        private static readonly uint RequestId = 0x7E0;
        private static readonly uint ResponseId = 0x7E8;

        private readonly Queue<byte[]> _responses = new Queue<byte[]>();
        private readonly uint _seed = 0x123456;
        private int _downloadAddress;
        private bool _pendingSent;

        /// <summary>The emulated flash memory - this is what a dump must reproduce.</summary>
        public byte[] Flash { get; private set; }

        public bool ProgrammingVoltageApplied { get; private set; }
        public uint AppliedVoltage { get; private set; }
        public bool Unlocked { get; private set; }
        public int WrongKeys { get; private set; }
        public int LockoutAfter { get; set; }
        public int Requests { get; private set; }
        public int BlocksTransferred { get; private set; }

        /// <summary>Return <c>63 &lt;counter&gt; &lt;data&gt;</c> instead of <c>63 &lt;data&gt;</c>.</summary>
        public bool LegacyReadPrefix { get; set; }

        /// <summary>Answer the first 0x34 with NRC 0x78 ("response pending") once.</summary>
        public bool SendResponsePendingOnce { get; set; }

        /// <summary>Require FEPS before the programming session and security access.</summary>
        public bool RequireFeps { get; set; }

        public List<string> Trace { get; private set; }

        public SimulatedPcm()
        {
            LockoutAfter = 3;
            RequireFeps = true;
            SendResponsePendingOnce = true;
            Trace = new List<string>();

            // A pattern that changes every byte, so a one-byte shift in the read path cannot hide.
            Flash = new byte[ImageSize];
            for (int i = 0; i < ImageSize; i++) Flash[i] = (byte)((i ^ 0x5A) & 0xFF);

            // Plus something that looks like a calibration.
            WriteAscii("BF-FALCON-SIM", 0x001000);
            WriteAscii("5R29-12A650-CC", 0x002000);
            WriteAscii("ASU-152", 0x003000);

            Vin = System.Text.Encoding.ASCII.GetBytes("6FPAAAJGSW4A12345");
        }

        public byte[] Vin { get; set; }

        // ------------------------------------------------------------- IPassThruDevice ---

        public string FirmwareVersion { get { return "sim-1.0"; } }
        public string DllVersion { get { return "sim"; } }
        public string ApiVersion { get { return "04.04"; } }

        public uint ReadBatteryVoltage() { return 13200; }

        public void SetProgrammingVoltage(uint pinNumber, uint voltage)
        {
            AppliedVoltage = voltage;
            ProgrammingVoltageApplied = voltage != 0;
            Trace.Add("FEPS pin 0x" + pinNumber.ToString("X") + " = " + voltage.ToString() + " mV");
        }

        public void ClearBuffers() { _responses.Clear(); }

        public void WriteMessage(byte[] canIdAndPayload, TxFlags flags, uint timeoutMs)
        {
            if (canIdAndPayload == null || canIdAndPayload.Length < 5) throw new ArgumentException("short frame");
            uint id = ((uint)canIdAndPayload[0] << 24) | ((uint)canIdAndPayload[1] << 16) |
                      ((uint)canIdAndPayload[2] << 8) | canIdAndPayload[3];
            if (id != RequestId) throw new InvalidOperationException("wrong CAN id 0x" + id.ToString("X3"));

            byte[] payload = new byte[canIdAndPayload.Length - 4];
            Array.Copy(canIdAndPayload, 4, payload, 0, payload.Length);
            Requests++;
            Handle(payload);
        }

        public byte[] ReadMessage(uint timeoutMs)
        {
            if (_responses.Count == 0) throw new TimeoutException("simulated: nothing queued");
            return _responses.Dequeue();
        }

        public void Dispose() { }

        // ------------------------------------------------------------------ protocol ---

        private void Handle(byte[] p)
        {
            byte service = p[0];
            Trace.Add("RX " + Hex(p));

            switch (service)
            {
                case 0x10: // start diagnostic session
                    if (RequireFeps && !ProgrammingVoltageApplied) { Negative(service, 0x22); return; }
                    Positive(new byte[] { 0x50, p[1] });
                    return;

                case 0x85: // control DTC setting
                    Positive(new byte[] { 0xC5, p[1], p[2] });
                    return;

                case 0x3E: // tester present
                    Positive(new byte[] { 0x7E, p.Length > 1 ? p[1] : (byte)0x00 });
                    return;

                case 0x27: // security access
                    SecurityAccess(p);
                    return;

                case 0x23: // read memory by address
                    ReadMemory(p);
                    return;

                case 0xB2: // Ford erase flash
                    if (!Unlocked) { Negative(service, 0x33); return; }
                    for (int i = 0; i < Flash.Length; i++) Flash[i] = 0xFF;
                    BlocksTransferred = 0;
                    Positive(new byte[] { 0xF2 });
                    return;

                case 0x34: // request download
                    if (!Unlocked) { Negative(service, 0x33); return; }
                    if (SendResponsePendingOnce && !_pendingSent)
                    {
                        // A real PCM sends the pending answer and then the real one; both are queued.
                        _pendingSent = true;
                        Negative(service, 0x78);
                    }
                    _downloadAddress = 0x010000;
                    Positive(new byte[] { 0x74, 0x20, 0x00 });
                    return;

                case 0x36: // transfer data (no sequence counter, as the reference tool does)
                    if (!Unlocked) { Negative(service, 0x33); return; }
                    int length = p.Length - 1;
                    if (_downloadAddress + length > Flash.Length) { Negative(service, 0x31); return; }
                    Array.Copy(p, 1, Flash, _downloadAddress, length);
                    _downloadAddress += length;
                    BlocksTransferred++;
                    Positive(new byte[] { 0x76 });
                    return;

                case 0x37: // transfer exit
                    Positive(new byte[] { 0x77 });
                    return;

                case 0x14: // clear diagnostic information
                    Positive(new byte[] { 0x54 });
                    return;

                case 0x19: // read DTCs
                    {
                        // P0123 present, P1260 confirmed
                        byte[] answer = new byte[]
                        {
                            0x59, 0x02, 0xFF,
                            0x01, 0x23, 0x45, 0x07,
                            0x12, 0x60, 0x00, 0x08
                        };
                        Positive(answer);
                        return;
                    }

                case 0x22: // read data by identifier
                    {
                        if (p.Length < 3) { Negative(service, 0x13); return; }
                        ushort did = (ushort)((p[1] << 8) | p[2]);
                        if (did == 0xF190)
                        {
                            byte[] answer = new byte[Vin.Length + 3];
                            answer[0] = 0x62;
                            answer[1] = 0xF1;
                            answer[2] = 0x90;
                            Array.Copy(Vin, 0, answer, 3, Vin.Length);
                            Positive(answer);
                        }
                        else
                        {
                            Negative(service, 0x31);
                        }
                        return;
                    }

                case 0x01: // OBD-II current data
                    {
                        byte pid = p[1];
                        switch (pid)
                        {
                            case 0x0C: Positive(new byte[] { 0x41, 0x0C, 0x0F, 0xA0 }); return; // 1000 rpm
                            case 0x05: Positive(new byte[] { 0x41, 0x05, 0x50 }); return;       // 40 C
                            case 0x11: Positive(new byte[] { 0x41, 0x11, 0x00 }); return;       // idle
                            default: Negative(service, 0x12); return;
                        }
                    }

                default:
                    Negative(service, 0x11);
                    return;
            }
        }

        private void SecurityAccess(byte[] p)
        {
            if (RequireFeps && !ProgrammingVoltageApplied) { Negative(0x27, 0x22); return; }
            if (p.Length < 2) { Negative(0x27, 0x13); return; }

            if (p[1] == 0x01)
            {
                if (WrongKeys >= LockoutAfter) { Negative(0x27, 0x36); return; }
                Positive(new byte[] { 0x67, 0x01, (byte)(_seed >> 16), (byte)(_seed >> 8), (byte)_seed });
                return;
            }

            if (p[1] == 0x02 && p.Length >= 5)
            {
                uint key = ((uint)p[2] << 16) | ((uint)p[3] << 8) | p[4];
                uint expected = SeedKeyAlgorithms.FordSpanishOak.ComputeKey(_seed);
                if (key == expected)
                {
                    Unlocked = true;
                    Positive(new byte[] { 0x67, 0x02 });
                    return;
                }
                WrongKeys++;
                Negative(0x27, WrongKeys >= LockoutAfter ? (byte)0x36 : (byte)0x35);
                return;
            }

            Negative(0x27, 0x12);
        }

        private void ReadMemory(byte[] p)
        {
            if (!Unlocked) { Negative(0x23, 0x33); return; }
            if (p.Length < 7) { Negative(0x23, 0x13); return; }

            int address = (p[2] << 16) | (p[3] << 8) | p[4];
            int length = (p[5] << 8) | p[6];
            if (address < 0 || length < 0 || address + length > Flash.Length) { Negative(0x23, 0x31); return; }

            int extra = LegacyReadPrefix ? 1 : 0;
            byte[] answer = new byte[1 + extra + length];
            int at = 0;
            answer[at++] = 0x63;
            if (LegacyReadPrefix) answer[at++] = 0x00;      // counter byte before the data
            Array.Copy(Flash, address, answer, at, length);
            Positive(answer);
        }

        private void Positive(byte[] payload) { Queue(payload); }

        private void Negative(byte service, byte nrc) { Queue(new byte[] { 0x7F, service, nrc }); }

        private void Queue(byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 4];
            frame[0] = (byte)(ResponseId >> 24);
            frame[1] = (byte)(ResponseId >> 16);
            frame[2] = (byte)(ResponseId >> 8);
            frame[3] = (byte)ResponseId;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            _responses.Enqueue(frame);
        }

        private void WriteAscii(string text, int offset)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, Flash, offset, bytes.Length);
        }

        private static string Hex(byte[] data)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < data.Length && i < 8; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            if (data.Length > 8) sb.Append(" ...");
            return sb.ToString();
        }
    }
}
