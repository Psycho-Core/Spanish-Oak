using System;
using System.Collections.Generic;
using PsychoFlash.Core.Flashing;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.SelfTest
{
    /// <summary>
    /// Dependency-free test runner (no NuGet needed): run `dotnet run --project tests/PsychoFlash.SelfTest`
    /// or execute the built psychoflash-selftest binary.
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        private static int Main()
        {
            Console.WriteLine("PsychoFlash self test");
            Console.WriteLine();

            TestSeedKeyVectors();
            TestSeedParsing();
            TestProfileMapping();
            TestImageTools();
            TestNegativeResponseCodes();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "All " + _checks + " checks passed."
                : _failures + " of " + _checks + " checks FAILED.");
            return _failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition, string description)
        {
            _checks++;
            if (!condition)
            {
                _failures++;
                Console.WriteLine("  FAIL  " + description);
            }
        }

        private static void CheckEqual(long expected, long actual, string description)
        {
            _checks++;
            if (expected != actual)
            {
                _failures++;
                Console.WriteLine("  FAIL  " + description + " (expected 0x" + expected.ToString("X") + ", got 0x" + actual.ToString("X") + ")");
            }
        }

        /// <summary>
        /// Seed/key pairs produced by the reference implementation that ships with the original
        /// Spanish Oak tool. These vectors were cross-checked against an emulation of the original
        /// IL, so a regression here means the unlock would stop working on a real PCM.
        /// </summary>
        private static void TestSeedKeyVectors()
        {
            Console.WriteLine("Seed/key (0x27) MkI vectors");
            ISeedKeyAlgorithm algorithm = SeedKeyAlgorithms.FordSpanishOak;
            uint[][] vectors = new uint[][]
            {
                new uint[] { 0x000000, 0xDAA9C2 },
                new uint[] { 0x000001, 0x9BC78F },
                new uint[] { 0x123456, 0x1DADBF },
                new uint[] { 0xFFFFFF, 0xB1541B },
                new uint[] { 0xA5C3F1, 0xBBA402 },
                new uint[] { 0x43DC13, 0xF7E84D },
                new uint[] { 0x0B522C, 0x8090D9 },
                new uint[] { 0xDE6616, 0xADEB39 },
                new uint[] { 0xC42ACD, 0x1EE582 },
                new uint[] { 0x7F4150, 0xFFC5B6 },
                new uint[] { 0x25BA23, 0x515B55 },
                new uint[] { 0x9E48D1, 0xB7CB43 }
            };
            foreach (uint[] v in vectors)
            {
                CheckEqual(v[1], algorithm.ComputeKey(v[0]), "key for seed 0x" + v[0].ToString("X6"));
            }
            CheckEqual(3, algorithm.KeyLength, "key length in bytes");
            CheckEqual(3, algorithm.SeedLength, "seed length in bytes");
        }

        private static void TestSeedParsing()
        {
            Console.WriteLine("Seed parsing");
            CheckEqual(0xAABBCC, FordSeedKeyMkI.ParseSeed("AABBCC"), "plain hex");
            CheckEqual(0xAABBCC, FordSeedKeyMkI.ParseSeed("0xAABBCC"), "0x prefix");
            CheckEqual(0xAABBCC, FordSeedKeyMkI.ParseSeed("AA BB CC"), "spaced");
            CheckEqual(0xAABBCC, FordSeedKeyMkI.ParseSeed("aa-bb-cc"), "lower case with dashes");
            Check(FordSeedKeyMkI.FormatKey(0x00FF00) == "00FF00", "key formatting pads to six digits");
        }

        private static void TestProfileMapping()
        {
            Console.WriteLine("PCM profile memory map");
            PcmProfile falcon = PcmProfile.SpanishOakFalcon();
            CheckEqual(0x100000, falcon.ImageSize, "Falcon image size");
            CheckEqual(0x100000, falcon.TotalReadBytes, "Falcon read bytes");
            CheckEqual(0x0F0000, falcon.TotalWriteBytes, "Falcon write bytes");
            CheckEqual(0x10000, falcon.MapAddressToImageOffset(0x10000), "Falcon address 0x10000 maps to itself");
            CheckEqual(0xFFFFF, falcon.MapAddressToImageOffset(0xFFFFF), "Falcon last byte maps to itself");
            CheckEqual(-1, falcon.MapAddressToImageOffset(0x100000), "Falcon address past the end is unmapped");
            CheckEqual(0xB2, falcon.ErasePayload[0], "Falcon erase command byte");
            CheckEqual(0xAA, falcon.ErasePayload[1], "Falcon erase parameter byte");

            PcmProfile blackOak = PcmProfile.BlackOakDual();
            CheckEqual(0x170000, blackOak.ImageSize, "Black Oak image size");
            CheckEqual(0x0, blackOak.MapAddressToImageOffset(0), "Black Oak bank 1 starts at 0");
            CheckEqual(0x70000, blackOak.MapAddressToImageOffset(0x500000), "Black Oak bank 2 starts at image 0x70000");
        }

        private static void TestImageTools()
        {
            Console.WriteLine("Image helpers");
            byte[] blank = new byte[64];
            Check(ImageTools.IsBlank(blank), "zero filled image is blank");
            blank[10] = 0x01;
            Check(!ImageTools.IsBlank(blank), "non zero image is not blank");
            CheckEqual(1, ImageTools.CountDifferences(blank, new byte[64]), "one differing byte detected");

            byte[] text = System.Text.Encoding.ASCII.GetBytes("....BF12345678901234....");
            IList<string> strings = ImageTools.FindPrintableStrings(text, 4, 10);
            Check(strings.Count == 1 && strings[0].Contains("BF12345678901234"), "printable string extraction");
        }

        private static void TestNegativeResponseCodes()
        {
            Console.WriteLine("Negative response descriptions");
            Check(NegativeResponseException.Describe(0x33).Contains("security"), "0x33 describes a security denial");
            Check(NegativeResponseException.Describe(0x35).Contains("key"), "0x35 describes an invalid key");
            Check(NegativeResponseException.Describe(0x78).Contains("pending"), "0x78 describes response pending");
        }
    }
}
