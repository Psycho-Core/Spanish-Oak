using System;
using System.Collections.Generic;
using PsychoFlash.Core.Flashing;
using PsychoFlash.Core.Protocol;
using PsychoFlash.Core.J2534;

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
            TestAlgorithmBag();
            TestFrameConstruction();
            TestReadResponseFraming();
            TestSingleByteAnswers();
            TestImageTools();
            TestImageDiffAndChecksums();
            TestChecksumDiscovery();
            TestEndToEnd();
            TestObdPids();
            TestDiagnosticCodes();
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

        /// <summary>
        /// A keybag is how an unfamiliar PCM gets unlocked without rebuilding the tool: a list of
        /// candidate constant sets, tried in order. Parsing it is pure text handling, so it is
        /// pinned here - including that the built-in Falcon line reproduces the known vectors.
        /// </summary>
        private static void TestAlgorithmBag()
        {
            Console.WriteLine("Keybag of seed/key constant sets");

            IList<ISeedKeyAlgorithm> bag = AlgorithmBag.Parse(AlgorithmBag.DefaultKeybagText());
            CheckEqual(1, bag.Count, "default keybag has one entry");
            Check(bag.Count == 1 && bag[0].Name == "falcon-mki", "entry keeps its name");
            Check(bag.Count == 1 && ((FordSeedKeyMkI)bag[0]).Constants == "08 30 61 A4 C5",
                  "five constants are readable back as hex");
            CheckEqual(0x1DADBF, bag[0].ComputeKey(0x123456), "bag entry reproduces the built-in key");

            // Compact form, comments, blank lines and trailing comments.
            string text = "# name  k0 k1 k2 k3 k4\n" +
                          "\n" +
                          "falcon      083061A4C5\n" +
                          "second      08 30 61 A4 C5   # same constants, different name\n" +
                          "third       0x00 0xFF 0x12 0x34 0x56\n";
            IList<ISeedKeyAlgorithm> many = AlgorithmBag.Parse(text);
            CheckEqual(3, many.Count, "three entries parsed, comments skipped");
            Check(many.Count == 3 && many[0].Name == "falcon", "compact form keeps order");
            CheckEqual(0x1DADBF, many[0].ComputeKey(0x123456), "compact form computes the same key");
            CheckEqual(0x1DADBF, many[1].ComputeKey(0x123456), "spaced form with trailing comment");
            Check(many.Count == 3 && ((FordSeedKeyMkI)many[2]).Constants == "00 FF 12 34 56",
                  "0x prefixed constants are accepted");

            // Malformed input must fail loudly - a silently ignored line could skip the one
            // candidate that would have unlocked the PCM.
            Check(Throws<FormatException>(delegate { AlgorithmBag.Parse("onlytwo 08 30\n"); }),
                  "wrong number of fields is rejected");
            Check(Throws<FormatException>(delegate { AlgorithmBag.Parse("odd 083061A4C\n"); }),
                  "odd number of hex digits is rejected");
            Check(Throws<FormatException>(delegate { AlgorithmBag.Parse("bad 08 30 61 A4 ZZ\n"); }),
                  "non hex constant is rejected");

            // Round trip.
            IList<ISeedKeyAlgorithm> round = AlgorithmBag.Parse(AlgorithmBag.Format(many));
            Check(round.Count == many.Count, "Format output parses back to the same count");
            CheckEqual(many[2].ComputeKey(0xABCDEF), round[2].ComputeKey(0xABCDEF), "round trip keeps the algorithm");
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
            catch
            {
                return false;
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

        /// <summary>
        /// The 0x23 answer layout is the one thing that can silently corrupt a dump: too many
        /// skipped prefix bytes shift the whole image, too few leave the 0x63 in it.
        /// </summary>
        private static void TestReadResponseFraming()
        {
            Console.WriteLine("0x23 read response framing");

            byte[] plain = new byte[] { 0x63, 0x01, 0x02, 0x03, 0x04 };
            byte[] a = FordPcmClient.ExtractReadData(plain, 4);
            Check(a.Length == 4 && a[0] == 0x01 && a[3] == 0x04, "0x63 then data (standard UDS layout)");

            byte[] withCounter = new byte[] { 0x63, 0x00, 0x01, 0x02, 0x03, 0x04 };
            byte[] b = FordPcmClient.ExtractReadData(withCounter, 4);
            Check(b.Length == 4 && b[0] == 0x01 && b[3] == 0x04, "0x63 then a counter byte then data");

            byte[] stripped = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            byte[] c = FordPcmClient.ExtractReadData(stripped, 4);
            Check(c.Length == 4 && c[0] == 0x01, "interface already stripped the response byte");

            byte[] shortAnswer = new byte[] { 0x63, 0x01, 0x02 };
            byte[] d = FordPcmClient.ExtractReadData(shortAnswer, 4);
            Check(d.Length == 2 && d[0] == 0x01, "short answer is reported to the caller, not padded");

            // A full size block - the case the read loop actually lives in.
            byte[] block = new byte[0x401];
            block[0] = 0x63;
            for (int i = 1; i < block.Length; i++) block[i] = (byte)(i & 0xFF);
            byte[] full = FordPcmClient.ExtractReadData(block, 0x400);
            Check(full.Length == 0x400 && full[0] == 0x01 && full[0x3FF] == (byte)(0x400 & 0xFF),
                  "0x400 byte block loses exactly one prefix byte");
        }

        /// <summary>
        /// Spanish Oak answers 0x36, 0x37 and 0xB2 with a single byte (0x76, 0x77, 0xF2). A client
        /// that throws away one byte payloads as padding reads a perfectly good block
        /// acknowledgement as silence, and the flash dies on the first block - which is exactly what
        /// the simulated PCM caught. This pins the smallest possible answer down.
        /// </summary>
        private static void TestSingleByteAnswers()
        {
            Console.WriteLine("Single byte positive answers");
            using (SingleByteDevice device = new SingleByteDevice())
            {
                FordPcmClient client = new FordPcmClient(device, PcmProfile.SpanishOakFalcon(), null);
                Check(client.EraseFlash(), "0xF2 alone is a complete erase answer");
                Check(client.TransferData(new byte[4]), "0x76 alone acknowledges a data block");
                Check(client.TransferExit(), "0x77 alone is a complete transfer exit answer");
            }
        }

        /// <summary>
        /// An interface that answers every request with the positive response byte and nothing else -
        /// the shortest legal answer on this protocol.
        /// </summary>
        private sealed class SingleByteDevice : IPassThruDevice
        {
            private readonly Queue<byte[]> _answers = new Queue<byte[]>();

            public string FirmwareVersion { get { return "one-byte"; } }
            public string DllVersion { get { return "one-byte"; } }
            public string ApiVersion { get { return "04.04"; } }

            public uint ReadBatteryVoltage() { return 12000; }
            public void SetProgrammingVoltage(uint pinNumber, uint voltage) { }
            public void ClearBuffers() { _answers.Clear(); }

            public void WriteMessage(byte[] canIdAndPayload, TxFlags flags, uint timeoutMs)
            {
                byte service = canIdAndPayload[4];
                _answers.Enqueue(new byte[] { 0x00, 0x00, 0x07, 0xE8, (byte)(service + 0x40) });
            }

            public byte[] ReadMessage(uint timeoutMs)
            {
                if (_answers.Count == 0) throw new TimeoutException("nothing queued");
                return _answers.Dequeue();
            }

            public void Dispose() { }
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

        /// <summary>
        /// The 0x19 answer is decoded without hardware, so the parser is pinned here. A dump of
        /// the codes is the first thing to look at when a swapped engine cranks but will not run.
        /// </summary>
        /// <summary>
        /// Live data is decoded from the standard J1979 scaling, so a wrong formula would show up
        /// as a wrong number on the driver's screen. Pinned here.
        /// </summary>
        private static void TestObdPids()
        {
            Console.WriteLine("OBD-II live data decoding");

            CheckEqual(0x01, ObdPids.ServiceCurrentData, "mode 01 is the current-data service");
            Check(ObdPids.Describe(0x0C).Contains("rpm"), "0x0C is engine speed");

            Check(ObdPids.Format(0x0C, new byte[] { 0x0F, 0xA0 }) == "1000 rpm", "0x0FA0 decodes to 1000 rpm");
            Check(ObdPids.Format(0x05, new byte[] { 0x50 }) == "40 C", "0x50 decodes to 40 C coolant");
            Check(ObdPids.Format(0x0F, new byte[] { 0x00 }) == "-40 C", "0x00 decodes to -40 C");
            Check(ObdPids.Format(0x11, new byte[] { 0xFF }) == "100.0 %", "throttle wide open is 100 %");
            Check(ObdPids.Format(0x10, new byte[] { 0x01, 0xF4 }) == "5.00 g/s", "0x01F4 decodes to 5 g/s");
            Check(ObdPids.Format(0x42, new byte[] { 0x2E, 0xE0 }) == "12.00 V", "0x2EE0 decodes to 12 V");
            Check(ObdPids.Format(0x0D, new byte[] { 0x64 }) == "100 km/h", "speed is one byte");
            Check(ObdPids.Format(0x7A, new byte[] { 0xDE, 0xAD }) == "DE AD", "unknown PID is shown as hex");

            byte[] answer = new byte[] { 0x41, 0x0C, 0x0F, 0xA0 };
            byte[] payload = ObdPids.ExtractPayload(0x0C, answer);
            Check(payload != null && payload.Length == 2 && payload[0] == 0x0F, "payload is taken after the echoed PID");
            Check(ObdPids.ExtractPayload(0x05, answer) == null, "answer for another PID is rejected");
            Check(ObdPids.ExtractPayload(0x0C, new byte[] { 0x41 }) == null, "truncated answer is rejected");

            byte[] pids = ObdPids.ParseList("0C,05,11");
            CheckEqual(3, pids.Length, "PID list parses");
            Check(pids.Length == 3 && pids[0] == 0x0C && pids[2] == 0x11, "PID list keeps order");
            CheckEqual(2, ObdPids.ParseList("0x0C 05").Length, "0x prefix and space separator accepted");
            Check(Throws<FormatException>(delegate { ObdPids.ParseList("ZZ"); }), "bad PID is rejected");
        }

        private static void TestDiagnosticCodes()
        {
            Console.WriteLine("Diagnostic trouble codes (0x19)");

            Check(FordPcmClient.ServiceReadDtcInformation == 0x19, "0x19 is the read DTC service");

            Check(DiagnosticCodes.Format(0x01, 0x23) == "P0123", "P0123 decoding");
            Check(DiagnosticCodes.Format(0x03, 0x45) == "P0345", "P0345 decoding");
            Check(DiagnosticCodes.Format(0x41, 0x00) == "C0100", "C0100 decoding");
            Check(DiagnosticCodes.Format(0x81, 0x00) == "B0100", "B0100 decoding");
            Check(DiagnosticCodes.Format(0xC1, 0x00) == "U0100", "U0100 decoding");

            // 59 02 FF | P0123 ftb 45 status 0x07 (test failed, not confirmed)
            //           | P1260 ftb 00 status 0x08 (confirmed, not current)
            byte[] answer = new byte[]
            {
                0x59, 0x02, 0xFF,
                0x01, 0x23, 0x45, 0x07,
                0x12, 0x60, 0x00, 0x08
            };
            IList<DiagnosticCode> codes = DiagnosticCodes.Parse(answer);
            CheckEqual(2, codes.Count, "two codes decoded");
            Check(codes.Count == 2 && codes[0].Code == "P0123", "first code is P0123");
            Check(codes.Count == 2 && codes[0].IsCurrent, "P0123 flagged as present");
            Check(codes.Count == 2 && !codes[0].IsConfirmed, "P0123 not flagged as confirmed");
            Check(codes.Count == 2 && codes[1].Code == "P1260", "second code is P1260");
            Check(codes.Count == 2 && codes[1].Description.Contains("theft"), "P1260 describes the PATS lockout");
            Check(codes.Count == 2 && codes[1].IsConfirmed && !codes[1].IsCurrent, "P1260 confirmed but not current");

            // No codes: the answer is just the header.
            IList<DiagnosticCode> none = DiagnosticCodes.Parse(new byte[] { 0x59, 0x02, 0xFF });
            CheckEqual(0, none.Count, "header only means no codes");

            // Junk in, nothing out (no exception).
            CheckEqual(0, DiagnosticCodes.Parse(new byte[0]).Count, "empty answer yields no codes");
            CheckEqual(0, DiagnosticCodes.Parse(null).Count, "null answer yields no codes");
            CheckEqual(0, DiagnosticCodes.Parse(new byte[] { 0x58, 0x02, 0xFF, 0x01, 0x23, 0x45, 0x2F }).Count,
                       "wrong service byte is rejected");

            // A trailing partial record is ignored, not thrown.
            byte[] padded = new byte[] { 0x59, 0x02, 0xFF, 0x01, 0x23, 0x45, 0x2F, 0x00, 0x00 };
            CheckEqual(1, DiagnosticCodes.Parse(padded).Count, "trailing partial record is ignored");

            Check(DiagnosticCodes.Describe("P0335").Contains("crankshaft"), "P0335 describes the crank sensor");
            Check(DiagnosticCodes.Describe("P2101").Contains("throttle"), "P2101 describes the throttle actuator");
            Check(DiagnosticCodes.Describe("Z9999").Length == 0, "unknown code has no description");
        }

        /// <summary>
        /// Diff and checksum helpers: the tool does not correct Ford checksums, so what it can do
        /// is report them accurately and show exactly which bytes a tune changed.
        /// </summary>
        private static void TestImageDiffAndChecksums()
        {
            Console.WriteLine("Image diff and checksum candidates");

            byte[] a = new byte[32];
            byte[] b = new byte[32];
            b[3] = 0x11;
            b[4] = 0x22;
            b[20] = 0x33;

            // Tolerance 8: bytes 3 and 4 collapse into one region, byte 20 is a second one.
            IList<ImageDifference> regions = ImageTools.Diff(a, b, 8);
            CheckEqual(2, regions.Count, "close differences collapse into one region");
            Check(regions.Count == 2 && regions[0].Offset == 3 && regions[0].Length == 2, "first region is 0x3 + 0x2");
            Check(regions.Count == 2 && regions[1].Offset == 20 && regions[1].Length == 1, "second region is 0x14 + 0x1");

            CheckEqual(0, ImageTools.Diff(a, a, 8).Count, "identical images have no regions");

            IList<ImageDifference> longer = ImageTools.Diff(new byte[8], new byte[12], 16);
            Check(longer.Count == 1 && longer[0].Offset == 8 && longer[0].Length == 4,
                  "a length mismatch is reported, not ignored");

            byte[] little = new byte[] { 0x01, 0x02, 0x03 };
            CheckEqual(6, ImageTools.Sum32(little, 0, 3), "sum32");
            CheckEqual(6, ImageTools.Sum16(little, 0, 3), "sum16");
            CheckEqual(2, ImageTools.Sum32(little, 1, 1), "sum over a window (second byte = 0x02)");

            // CRC-32 of "123456789" is the standard check value.
            byte[] check = System.Text.Encoding.ASCII.GetBytes("123456789");
            CheckEqual(0xCBF43926, ImageTools.Crc32(check, 0, check.Length), "crc32 check value");
        }

        /// <summary>
        /// Checksum discovery, proven on a synthetic image pair: a calibration with a sum32 stored
        /// big endian at a known offset must be found, and an unrelated pair must produce nothing.
        /// This is the mechanism that lets the checksum scheme be derived from a stock image plus a
        /// known-good flashed one, instead of guessed.
        /// </summary>
        private static void TestChecksumDiscovery()
        {
            Console.WriteLine("Checksum discovery from an image pair");

            const int size = 0x100000;
            const int fieldOffset = 0x100;          // inside the boot block, outside the calibration
            byte[] stock = new byte[size];
            for (int i = 0; i < size; i++) stock[i] = (byte)(i * 7);   // non trivial data
            uint stockSum = ImageTools.Sum32(stock, 0x010000, size - 0x10000);
            WriteBigEndian(stock, fieldOffset, stockSum);

            byte[] modified = new byte[size];
            Array.Copy(stock, modified, size);
            modified[0x0A0000] = (byte)(stock[0x0A0000] ^ 0xFF);       // one calibration byte changes
            modified[0x0A0001] = (byte)(stock[0x0A0001] ^ 0x5A);
            uint modifiedSum = ImageTools.Sum32(modified, 0x010000, size - 0x10000);
            WriteBigEndian(modified, fieldOffset, modifiedSum);

            IList<ChecksumFinding> findings = ChecksumScanner.Scan(stock, modified, null);
            Check(findings.Count >= 1, "the stored checksum is found (" + findings.Count + " finding(s))");

            bool exact = false;
            foreach (ChecksumFinding finding in findings)
            {
                if (finding.Offset == fieldOffset && finding.Size == 4 && finding.BigEndian &&
                    finding.Scheme == "sum32" && finding.Region.Name == "calibration" &&
                    finding.StoredInStock == stockSum && finding.StoredInModified == modifiedSum)
                {
                    exact = true;
                }
            }
            Check(exact, "offset, size, endianness, scheme and region all identified");

            CheckEqual(0, ChecksumScanner.Scan(stock, stock, null).Count, "identical images produce no findings");

            byte[] unrelated = new byte[size];
            for (int i = 0; i < size; i++) unrelated[i] = (byte)(i * 13);
            CheckEqual(0, ChecksumScanner.Scan(stock, unrelated, null).Count, "unrelated images produce no false finding");

            IList<ChecksumRegion> regions = ChecksumScanner.DefaultRegions(size);
            CheckEqual(3, regions.Count, "whole image, boot block and calibration are scanned by default");
        }

        private static void WriteBigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        /// <summary>
        /// The whole tool against a simulated PCM: unlock, dump, flash, verify, codes, live data.
        ///
        /// This is what "tested without a car" means here. The real client, service and framing code
        /// run unchanged against an in-memory PCM that speaks the recovered protocol and enforces
        /// FEPS, security access and block transfer. It does not prove the PCM on your bench behaves
        /// the same way - it proves the software does what it says when something answers correctly.
        /// </summary>
        private static void TestEndToEnd()
        {
            Console.WriteLine("End to end against a simulated PCM");

            PcmProfile profile = PcmProfile.SpanishOakFalcon();
            profile.EraseSettleMilliseconds = 5;          // keep the self test quick

            SimulatedPcm pcm = new SimulatedPcm();
            List<string> log = new List<string>();
            FordPcmClient client = new FordPcmClient(pcm, profile, delegate(string m) { log.Add(m); });
            FlashService service = new FlashService(client, delegate(string m) { log.Add(m); });

            // --- programming mode needs FEPS, and says so when it is missing ---
            Check(!pcm.ProgrammingVoltageApplied, "no FEPS before programming mode");
            service.EnterProgrammingMode(false);
            Check(pcm.ProgrammingVoltageApplied, "FEPS applied on entering programming mode");
            Check(pcm.AppliedVoltage == profile.ProgrammingVoltage * 1000, "FEPS applied at the profile voltage");

            // --- unlock, with the real key algorithm against the real seed ---
            Check(service.Unlock(SeedKeyAlgorithms.FordSpanishOak), "unlock accepted by the simulated PCM");
            Check(pcm.Unlocked, "PCM reports itself unlocked");

            // --- dump, byte for byte ---
            byte[] stock = service.ReadImage(null, System.Threading.CancellationToken.None);
            CheckEqual(SimulatedPcm.ImageSize, stock.Length, "dump is exactly 1 MB");
            Check(SameBytes(stock, pcm.Flash), "dump matches the simulated flash byte for byte");
            Check(SameBytes(stock, pcm.Flash), "and no byte is shifted (pattern is position dependent)");

            // --- the legacy read layout must produce the same image ---
            pcm.LegacyReadPrefix = true;
            byte[] stock2 = service.ReadImage(null, System.Threading.CancellationToken.None);
            Check(SameBytes(stock, stock2), "both 0x23 response layouts produce an identical dump");
            pcm.LegacyReadPrefix = false;

            // --- write a modified image, the way a tune would ---
            byte[] tuned = new byte[stock.Length];
            Array.Copy(stock, tuned, stock.Length);
            tuned[0x020000] = 0x12;
            tuned[0x080000] = 0x34;
            tuned[0x0FFFFF] = 0x56;

            try
            {
                service.WriteImage(tuned, null, System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                Check(false, "write failed: " + ex.Message + "  | last PCM trace: " + Tail(pcm, 10));
            }
            Check(pcm.BlocksTransferred == (0x0F0000 / profile.WriteBlockSize),
                  "every block of the write window was transferred (" + pcm.BlocksTransferred + ")");
            CheckEqual(0, FirstDifference(pcm.Flash, tuned, 0x010000, 0x0F0000), "flashed bytes match the file");
            CheckEqual(0xFF, pcm.Flash[0x000000], "boot block was erased but never written");

            // --- verify catches a match and a mismatch ---
            CheckEqual(0, service.VerifyImage(tuned, null, System.Threading.CancellationToken.None), "verify passes on the file just flashed");
            byte[] tampered = new byte[tuned.Length];
            Array.Copy(tuned, tampered, tuned.Length);
            tampered[0x050000] ^= 0xFF;
            Check(service.VerifyImage(tampered, null, System.Threading.CancellationToken.None) > 0, "verify reports differing bytes");

            // --- diagnostics through the same transport ---
            IList<DiagnosticCode> codes = DiagnosticCodes.Parse(client.ReadTroubleCodes());
            CheckEqual(2, codes.Count, "trouble codes read from the PCM");
            Check(codes.Count == 2 && codes[0].Code == "P0123" && codes[1].Code == "P1260", "codes decoded end to end");

            byte[] rpm = client.ReadPid(0x0C);
            Check(rpm != null && ObdPids.Format(0x0C, rpm) == "1000 rpm", "live data decoded end to end");

            string vin = client.ReadVehicleIdentification();
            Check(vin == "6FPAAAJGSW4A12345", "VIN read end to end");

            // --- and it refuses politely when the key is wrong ---
            SimulatedPcm strict = new SimulatedPcm();
            strict.SetProgrammingVoltage(profile.ProgrammingPin, profile.ProgrammingVoltage);
            FordPcmClient wrong = new FordPcmClient(strict, profile, delegate { });
            bool refused = false;
            try
            {
                wrong.SecurityAccess(new FordSeedKeyMkI(0x00, 0x00, 0x00, 0x00, 0x00, "deliberately wrong"));
            }
            catch (NegativeResponseException ex)
            {
                refused = ex.Code == 0x35;
            }
            Check(refused, "a wrong key raises NRC 0x35 rather than being reported as success");

            service.LeaveProgrammingMode();
            Check(!pcm.ProgrammingVoltageApplied, "FEPS removed on leaving programming mode");
            Check(log.Count > 10, "the log recorded the session (" + log.Count + " lines)");
        }

        private static string Tail(SimulatedPcm pcm, int count)
        {
            int start = Math.Max(0, pcm.Trace.Count - count);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = start; i < pcm.Trace.Count; i++)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(pcm.Trace[i]);
            }
            return sb.ToString();
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int FirstDifference(byte[] a, byte[] b, int offset, int length)
        {
            for (int i = offset; i < offset + length && i < a.Length && i < b.Length; i++)
            {
                if (a[i] != b[i]) return i;
            }
            return 0;
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
