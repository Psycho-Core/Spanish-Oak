using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>
    /// A text file of candidate seed/key constant sets - a "keybag". One line per candidate:
    /// <code>
    ///   # name        k0 k1 k2 k3 k4
    ///   falcon-mki    08 30 61 A4 C5
    ///   falcon-fg     083061A4C5
    /// </code>
    ///
    /// The five constants are the per-application table mixed into the 24-bit LFSR
    /// (<see cref="FordSeedKeyMkI"/>). A PCM that refuses the built-in Falcon set is either an
    /// operating system with different constants, or a different key algorithm altogether; a bag
    /// lets you try the former without rebuilding the tool.
    ///
    /// Every wrong candidate costs one of the PCM's security attempts (usually 10, then a delay
    /// or an ignition cycle), so keep bags short and put the likely entry first.
    /// </summary>
    public static class AlgorithmBag
    {
        /// <summary>Parses keybag text into ready-to-use algorithms, in file order.</summary>
        public static IList<ISeedKeyAlgorithm> Parse(string text)
        {
            List<ISeedKeyAlgorithm> algorithms = new List<ISeedKeyAlgorithm>();
            if (string.IsNullOrEmpty(text)) return algorithms;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                string[] parts = line.Split(new char[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    // name 083061A4C5
                    byte[] packed = ParseHexBytes(parts[1], i);
                    if (packed.Length != 5)
                    {
                        throw new FormatException("Keybag line " + (i + 1) + ": expected five constants, got " + packed.Length + ".");
                    }
                    algorithms.Add(new FordSeedKeyMkI(packed[0], packed[1], packed[2], packed[3], packed[4], parts[0]));
                }
                else if (parts.Length == 6)
                {
                    // name k0 k1 k2 k3 k4
                    byte[] values = new byte[5];
                    for (int c = 0; c < 5; c++)
                    {
                        values[c] = (byte)ParseHexByte(parts[c + 1], i);
                    }
                    algorithms.Add(new FordSeedKeyMkI(values[0], values[1], values[2], values[3], values[4], parts[0]));
                }
                else
                {
                    throw new FormatException("Keybag line " + (i + 1) +
                        ": expected '<name> <five constants>' or '<name> <ten hex digits>', got " + parts.Length + " field(s).");
                }
            }
            return algorithms;
        }

        /// <summary>Reads a keybag file from disk.</summary>
        public static IList<ISeedKeyAlgorithm> Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path");
            if (!File.Exists(path)) throw new FileNotFoundException("Keybag file not found: " + path, path);
            return Parse(File.ReadAllText(path));
        }

        /// <summary>The built-in Falcon set as a one-entry bag, for tests and for --keybag defaults.</summary>
        public static string DefaultKeybagText()
        {
            return "# name         k0 k1 k2 k3 k4" + Environment.NewLine +
                   "falcon-mki     08 30 61 A4 C5" + Environment.NewLine;
        }

        /// <summary>Writes the bag back out in canonical form (handy after editing by hand).</summary>
        public static string Format(IEnumerable<ISeedKeyAlgorithm> algorithms)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# name         k0 k1 k2 k3 k4");
            if (algorithms != null)
            {
                foreach (ISeedKeyAlgorithm algorithm in algorithms)
                {
                    sb.AppendLine(algorithm.Name.Replace(' ', '-') + "    " + Describe(algorithm));
                }
            }
            return sb.ToString();
        }

        private static string Describe(ISeedKeyAlgorithm algorithm)
        {
            FordSeedKeyMkI ford = algorithm as FordSeedKeyMkI;
            if (ford == null) return "(custom algorithm: " + algorithm.GetType().Name + ")";
            return ford.Constants;
        }

        private static int ParseHexByte(string text, int line)
        {
            int value;
            string cleaned = text.Trim();
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
            if (cleaned.Length == 0 || cleaned.Length > 2 ||
                !int.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException("Keybag line " + (line + 1) + ": '" + text + "' is not a hex byte.");
            }
            return value;
        }

        private static byte[] ParseHexBytes(string text, int line)
        {
            string cleaned = text.Trim();
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
            if (cleaned.Length % 2 != 0)
            {
                throw new FormatException("Keybag line " + (line + 1) + ": '" + text + "' has an odd number of hex digits.");
            }
            byte[] bytes = new byte[cleaned.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)ParseHexByte(cleaned.Substring(i * 2, 2), line);
            }
            return bytes;
        }
    }
}
