using System;
using System.Collections.Generic;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>
    /// One stored diagnostic trouble code (service 0x19). Ford EEC-VI answers with a three byte
    /// record per code - two code bytes plus a failure type byte - followed by the one byte UDS
    /// status mask.
    /// </summary>
    public sealed class DiagnosticCode
    {
        /// <summary>The usual "P1233" style code.</summary>
        public string Code { get; private set; }

        /// <summary>Third record byte (Ford failure type byte / UDS failure type).</summary>
        public byte FailureType { get; private set; }

        /// <summary>UDS status mask: bit0 test failed, bit2 pending, bit3 confirmed, ...</summary>
        public byte Status { get; private set; }

        /// <summary>Plain-English meaning, empty when the code is not in the built-in table.</summary>
        public string Description { get; private set; }

        /// <summary>Bit 0 - the fault is present right now.</summary>
        public bool IsCurrent { get { return (Status & 0x01) != 0; } }

        /// <summary>Bit 2 - stored as pending.</summary>
        public bool IsPending { get { return (Status & 0x04) != 0; } }

        /// <summary>Bit 3 - confirmed / matured.</summary>
        public bool IsConfirmed { get { return (Status & 0x08) != 0; } }

        public DiagnosticCode(string code, byte failureType, byte status, string description)
        {
            Code = code;
            FailureType = failureType;
            Status = status;
            Description = description ?? string.Empty;
        }

        public override string ToString()
        {
            string text = Code + "  status 0x" + Status.ToString("X2");
            if (IsCurrent) text += " present";
            else if (IsConfirmed) text += " confirmed";
            if (!string.IsNullOrEmpty(Description)) text += "  " + Description;
            return text;
        }
    }

    /// <summary>Decoding and description of the Ford EEC-VI diagnostic trouble codes.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>
        /// Turns the two code bytes into the familiar five character code, e.g. 0x01,0x23 -> P0123.
        /// Letters: 0=P (powertrain), 1=C (chassis), 2=B (body), 3=U (network).
        /// </summary>
        public static string Format(byte first, byte second)
        {
            char[] letters = new char[] { 'P', 'C', 'B', 'U' };
            char letter = letters[(first >> 6) & 0x03];
            int digit = (first >> 4) & 0x03;
            return new string(new char[]
            {
                letter,
                (char)('0' + digit),
                Nibble(first & 0x0F),
                Nibble((second >> 4) & 0x0F),
                Nibble(second & 0x0F)
            });
        }

        private static char Nibble(int value)
        {
            return value < 10 ? (char)('0' + value) : (char)('A' + (value - 10));
        }

        /// <summary>
        /// Parses a 0x19/0x02 answer: <c>59 02 &lt;availability mask&gt;</c> followed by four byte
        /// records (three DTC bytes + status). A trailing partial record is ignored rather than
        /// thrown away silently, so a firmware that pads the answer still works.
        /// </summary>
        public static IList<DiagnosticCode> Parse(byte[] response)
        {
            List<DiagnosticCode> codes = new List<DiagnosticCode>();
            if (response == null || response.Length < 3) return codes;
            if (response[0] != 0x59) return codes;

            for (int i = 3; i + 3 < response.Length; i += 4)
            {
                string code = Format(response[i], response[i + 1]);
                codes.Add(new DiagnosticCode(code, response[i + 2], response[i + 3], Describe(code)));
            }
            return codes;
        }

        /// <summary>
        /// The codes that matter for an engine that cranks but will not run, plus the usual
        /// sensor faults. Not exhaustive - an unknown code is reported with no description.
        /// </summary>
        public static string Describe(string code)
        {
            switch (code)
            {
                // Power / programming / theft
                case "P0600": return "serial communication link";
                case "P0601": return "control module memory checksum error";
                case "P0602": return "control module programming error";
                case "P0605": return "internal ROM error";
                case "P0606": return "PCM processor fault";
                case "P1000": return "OBD readiness tests not complete";
                case "P1233": return "fuel pump driver module off line";
                case "P1235": return "fuel pump control out of range";
                case "P1260": return "theft detected - engine disabled (PATS)";
                case "P1633": return "keep alive memory voltage low";
                case "P1261": return "invalid key - theft module";

                // Air, fuel, spark
                case "P0100": return "mass air flow circuit";
                case "P0101": return "mass air flow range/performance";
                case "P0102": return "mass air flow low input";
                case "P0103": return "mass air flow high input";
                case "P0110": return "intake air temperature circuit";
                case "P0115": return "engine coolant temperature circuit";
                case "P0117": return "engine coolant temperature low";
                case "P0118": return "engine coolant temperature high";
                case "P0120": return "throttle position sensor A circuit";
                case "P0122": return "throttle position sensor A low";
                case "P0123": return "throttle position sensor A high";
                case "P0190": return "fuel rail pressure sensor circuit";
                case "P0191": return "fuel rail pressure range/performance";
                case "P0200": return "injector circuit";
                case "P0219": return "engine overspeed condition";
                case "P0230": return "fuel pump relay circuit";
                case "P0234": return "turbo overboost";
                case "P0325": return "knock sensor circuit";
                case "P0335": return "crankshaft position sensor A circuit";
                case "P0340": return "camshaft position sensor A circuit";
                case "P0345": return "camshaft position sensor A circuit (bank 2)";
                case "P0350": return "ignition coil primary circuit";
                case "P1001": return "key on engine running test not complete";

                // Drive by wire (a Barra will not rev without a working throttle)
                case "P2100": return "throttle actuator control motor circuit open";
                case "P2101": return "throttle actuator control motor range/performance";
                case "P2105": return "throttle actuator control forced engine shutdown";
                case "P2110": return "throttle actuator control forced limited rpm";
                case "P2111": return "throttle actuator control stuck open";
                case "P2112": return "throttle actuator control stuck closed";
                case "P2135": return "throttle position sensor 1/2 correlation";

                // Vehicle speed / network
                case "P0500": return "vehicle speed sensor";
                case "P0560": return "system voltage";
                case "P0562": return "system voltage low";
                case "P0563": return "system voltage high";
                case "U0100": return "lost communication with ECM/PCM";
                case "U0155": return "lost communication with instrument cluster";
                default: return string.Empty;
            }
        }
    }
}
