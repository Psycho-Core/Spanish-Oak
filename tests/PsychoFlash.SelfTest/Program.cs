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
            TestFalconProfileContract();
            TestFrameConstruction();
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

        /// <summary>
        /// The exact contract the BF Barra Spanish Oak 1024k PCM needs. If any of these change,
        /// a BF Falcon will stop talking to the tool.
        /// </summary>
        private static void TestFalconProfileContract()
        {
            Console.WriteLine("BF Barra / Spanish Oak 1024k profile contract");
            PcmProfile falcon = PcmProfile.SpanishOakFalcon();

            CheckEqual(0x7E0, falcon.RequestId, "tester CAN id");
            CheckEqual(0x7E8, falcon.ResponseId, "PCM CAN id");
            CheckEqual(500000, falcon.BaudRate, "CAN bit rate");
            CheckEqual(0x85, falcon.ProgrammingSession, "programming session (0x10)");
            CheckEqual(18, falcon.ProgrammingVoltage, "FEPS voltage");
            CheckEqual(0x80, falcon.ProgrammingPin, "FEPS pin");
            CheckEqual(0x400, falcon.ReadBlockSize, "read block size (0x23)");
            CheckEqual(0x400, falcon.WriteBlockSize, "transfer block size (0x36)");
            CheckEqual(0x100000, falcon.ImageSize, "image size (1 MB)");
            CheckEqual(1, falcon.ReadSegments.Count, "single read window");
            CheckEqual(0x000000, falcon.ReadSegments[0].Address, "read window start");
            CheckEqual(0x100000, falcon.ReadSegments[0].Length, "read window length");
            CheckEqual(1, falcon.WriteSegments.Count, "single write window");
            CheckEqual(0x010000, falcon.WriteSegments[0].Address, "write window starts after the boot block");
            CheckEqual(0x0F0000, falcon.WriteSegments[0].Length, "write window length");
            CheckEqual(0xB2, falcon.ErasePayload[0], "erase service");
            CheckEqual(0xAA, falcon.ErasePayload[1], "erase parameter");

            byte[] download = falcon.RequestDownloadPayload;
            byte[] expected = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 };
            CheckEqual(expected.Length, download.Length, "request download parameter length");
            for (int i = 0; i < expected.Length; i++)
            {
                CheckEqual(expected[i], download[i], "request download byte " + i);
            }
        }

        private static void TestFrameConstruction()
        {
            Console.WriteLine("J2534 message framing");
            PcmProfile falcon = PcmProfile.SpanishOakFalcon();
            byte[] frame = FordPcmClient.BuildFrame(falcon, new byte[] { 0x27, 0x01 });

            CheckEqual(6, frame.Length, "frame length (4 byte id + payload)");
            CheckEqual(0x00, frame[0], "id byte 0");
            CheckEqual(0x07, frame[2], "id byte 2 (0x07)");
            CheckEqual(0xE0, frame[3], "id byte 3 (0xE0)");
            CheckEqual(0x27, frame[4], "service byte");
            CheckEqual(0x01, frame[5], "sub-function byte");

            byte[] response = new byte[] { 0x00, 0x00, 0x07, 0xE8, 0x67, 0x01, 0x12, 0x34, 0x56 };
            byte[] payload = FordPcmClient.StripCanId(response);
            CheckEqual(5, payload.Length, "response payload length");
            CheckEqual(0x67, payload[0], "response service byte");
            CheckEqual(0x12, payload[2], "first seed byte");

            PcmProfile blackOak = PcmProfile.BlackOakDual();
            byte[] big = FordPcmClient.BuildFrame(blackOak, new byte[0x401]);
            CheckEqual(0x405, big.Length, "multi-frame transfer length");
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
            Check(strings.Count == 1 && strings[0].Contains("BF12345678901234"), "printable string extraction (run at end of image)");

            // A run that ends on a non printable byte, plus a second run that runs to the very end.
            byte[] mixed = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x00, 0x42, 0x42, 0x42, 0x42, 0x42 };
            IList<string> runs = ImageTools.FindPrintableStrings(mixed, 4, 10);
            Check(runs.Count == 2, "both printable runs are found (" + runs.Count + ")");
            Check(runs.Count == 2 && runs[0].Contains("AAAA") && runs[1].Contains("BBBBB"), "run contents and order");

            IList<string> limited = ImageTools.FindPrintableStrings(mixed, 4, 1);
            Check(limited.Count == 1, "string limit is honoured");
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
