using System;
using System.Collections.Generic;
using System.Globalization;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>
    /// Standard SAE J1979 (OBD-II "mode 01") parameters, which every EEC-VI PCM answers on the
    /// same CAN identifiers the flasher already uses. This is the live-data path that does not
    /// depend on Ford's proprietary data identifiers: request <c>01 &lt;pid&gt;</c>, get
    /// <c>41 &lt;pid&gt; &lt;data...&gt;</c>.
    /// </summary>
    public static class ObdPids
    {
        /// <summary>Service 0x01 - "show current data".</summary>
        public const byte ServiceCurrentData = 0x01;

        /// <summary>Positive response to service 0x01.</summary>
        public const byte ServiceCurrentDataResponse = 0x41;

        /// <summary>The parameters worth watching on a conversion that will not run.</summary>
        public static byte[] DefaultSet()
        {
            return new byte[] { 0x0C, 0x05, 0x11, 0x0D, 0x10, 0x0F, 0x42 };
        }

        /// <summary>Human name for a PID, e.g. 0x0C -> "engine speed (rpm)".</summary>
        public static string Describe(byte pid)
        {
            switch (pid)
            {
                case 0x04: return "engine load (%)";
                case 0x05: return "coolant temperature (C)";
                case 0x06: return "short term fuel trim bank 1 (%)";
                case 0x07: return "long term fuel trim bank 1 (%)";
                case 0x0B: return "intake manifold pressure (kPa)";
                case 0x0C: return "engine speed (rpm)";
                case 0x0D: return "vehicle speed (km/h)";
                case 0x0E: return "timing advance (deg)";
                case 0x0F: return "intake air temperature (C)";
                case 0x10: return "mass air flow (g/s)";
                case 0x11: return "throttle position (%)";
                case 0x1F: return "run time since start (s)";
                case 0x21: return "distance with MIL on (km)";
                case 0x2F: return "fuel level (%)";
                case 0x31: return "distance since codes cleared (km)";
                case 0x33: return "barometric pressure (kPa)";
                case 0x42: return "control module voltage (V)";
                case 0x46: return "ambient air temperature (C)";
                case 0x51: return "fuel type";
                case 0x5C: return "engine oil temperature (C)";
                default: return "PID 0x" + pid.ToString("X2");
            }
        }

        /// <summary>The number of data bytes this PID returns, or 0 when unknown.</summary>
        public static int DataLength(byte pid)
        {
            switch (pid)
            {
                case 0x04: case 0x05: case 0x0B: case 0x0D: case 0x0F:
                case 0x11: case 0x2F: case 0x33: case 0x46: case 0x51: case 0x5C:
                    return 1;
                case 0x06: case 0x07: case 0x0E: case 0x10: case 0x42:
                    return 2;
                case 0x0C: return 2;
                case 0x1F: case 0x21: case 0x31: return 2;
                default: return 0;
            }
        }

        /// <summary>
        /// Decodes a PID answer (the bytes after the echoed PID) into something readable. Unknown
        /// PIDs are shown as hex rather than guessed at - a wrong unit is worse than no unit.
        /// </summary>
        public static string Format(byte pid, byte[] data)
        {
            if (data == null || data.Length == 0) return "(no data)";
            int a = data[0];
            int b = data.Length > 1 ? data[1] : 0;
            int c = data.Length > 2 ? data[2] : 0;
            int d = data.Length > 3 ? data[3] : 0;

            switch (pid)
            {
                case 0x04: return Percent(a);
                case 0x05: return (a - 40).ToString(CultureInfo.InvariantCulture) + " C";
                case 0x06:
                case 0x07: return Trim(a);
                case 0x0B: return a.ToString(CultureInfo.InvariantCulture) + " kPa";
                case 0x0C: return ((a * 256 + b) / 4).ToString(CultureInfo.InvariantCulture) + " rpm";
                case 0x0D: return a.ToString(CultureInfo.InvariantCulture) + " km/h";
                case 0x0E: return ((a / 2.0) - 64).ToString("0.0", CultureInfo.InvariantCulture) + " deg";
                case 0x0F: return (a - 40).ToString(CultureInfo.InvariantCulture) + " C";
                case 0x10: return ((a * 256 + b) / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " g/s";
                case 0x11: return Percent(a);
                case 0x1F: return (a * 256 + b).ToString(CultureInfo.InvariantCulture) + " s";
                case 0x21: return (a * 256 + b).ToString(CultureInfo.InvariantCulture) + " km";
                case 0x2F: return Percent(a);
                case 0x31: return (a * 256 + b).ToString(CultureInfo.InvariantCulture) + " km";
                case 0x33: return a.ToString(CultureInfo.InvariantCulture) + " kPa";
                case 0x42: return ((a * 256 + b) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
                case 0x46: return (a - 40).ToString(CultureInfo.InvariantCulture) + " C";
                case 0x5C: return (a - 40).ToString(CultureInfo.InvariantCulture) + " C";
                default:
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        for (int i = 0; i < data.Length; i++)
                        {
                            if (i > 0) sb.Append(' ');
                            sb.Append(data[i].ToString("X2"));
                        }
                        return sb.ToString();
                    }
            }
        }

        private static string Percent(int raw)
        {
            return (raw * 100.0 / 255.0).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        }

        private static string Trim(int raw)
        {
            return ((raw * 100.0 / 128.0) - 100.0).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        }

        /// <summary>
        /// Pulls the data bytes out of a <c>41 &lt;pid&gt; ...</c> answer, or returns null when the
        /// answer is not for this PID.
        /// </summary>
        public static byte[] ExtractPayload(byte pid, byte[] response)
        {
            if (response == null || response.Length < 3) return null;
            if (response[0] != ServiceCurrentDataResponse) return null;
            if (response[1] != pid) return null;
            byte[] data = new byte[response.Length - 2];
            Array.Copy(response, 2, data, 0, data.Length);
            return data;
        }

        /// <summary>Parses "0C,05,11" or "0C 05 11" into PID numbers.</summary>
        public static byte[] ParseList(string text)
        {
            List<byte> pids = new List<byte>();
            if (string.IsNullOrWhiteSpace(text)) return pids.ToArray();
            string[] parts = text.Split(new char[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string cleaned = part.Trim();
                if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
                int value;
                if (cleaned.Length == 0 || cleaned.Length > 2 ||
                    !int.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                {
                    throw new FormatException("'" + part + "' is not a hex PID.");
                }
                pids.Add((byte)value);
            }
            return pids.ToArray();
        }
    }
}
