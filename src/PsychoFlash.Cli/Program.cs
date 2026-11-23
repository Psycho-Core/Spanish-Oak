using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using PsychoFlash.Core.Flashing;
using PsychoFlash.Core.J2534;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.Cli
{
    internal static class Program
    {
        private static string _driver;
        private static string _deviceName;
        private static string _profileName = "falcon";
        private static bool _yes;
        private static string _keybagPath;
        private static int _fepsPin = -1;
        private static int _fepsVoltage = -1;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("PsychoFlash " + typeof(Program).Assembly.GetName().Version + " - Ford EEC-VI / Spanish Oak PCM tool");
            Console.WriteLine();

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            List<string> options = new List<string>();
            for (int i = 1; i < args.Length; i++) options.Add(args[i]);

            try
            {
                Dictionary<string, string> parsed = ParseOptions(options);
                if (parsed.ContainsKey("dll")) _driver = parsed["dll"];
                if (parsed.ContainsKey("device")) _deviceName = parsed["device"];
                if (parsed.ContainsKey("profile")) _profileName = parsed["profile"].ToLowerInvariant();
                _yes = parsed.ContainsKey("yes") || parsed.ContainsKey("y");
                if (parsed.ContainsKey("keybag")) _keybagPath = parsed["keybag"];
                if (parsed.ContainsKey("feps-pin")) _fepsPin = ParseNumber(parsed["feps-pin"], "--feps-pin");
                if (parsed.ContainsKey("feps-voltage")) _fepsVoltage = ParseNumber(parsed["feps-voltage"], "--feps-voltage");

                switch (command)
                {
                    case "interfaces":
                    case "list":
                        return ListInterfaces();
                    case "seed":
                        return Seed(parsed);
                    case "info":
                        return Info();
                    case "read":
                        return Read(parsed);
                    case "write":
                        return Write(parsed);
                    case "erase":
                        return Erase();
                    case "verify":
                        return Verify(parsed);
                    case "dtcs":
                    case "codes":
                        return ReadCodes(parsed);
                    case "vin":
                        return ReadVin();
                    case "keybag":
                        return Keybag(parsed);
                    default:
                        Console.Error.WriteLine("Unknown command '" + command + "'.");
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("ERROR: " + ex.Message);
                if (ex is J2534Exception) Console.Error.WriteLine("       " + ((J2534Exception)ex).Error.Describe());
                return 1;
            }
        }

        private static bool IsHelp(string s)
        {
            return s == "-h" || s == "--help" || s == "/?" || s == "help";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: psychoflash <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands");
            Console.WriteLine("  interfaces                 List installed J2534 interfaces");
            Console.WriteLine("  info                       Show interface, battery voltage and PCM identification");
            Console.WriteLine("  read  --out <file.bin>     Read the flash and save it");
            Console.WriteLine("  write --in  <file.bin>     Erase and program a flash image");
            Console.WriteLine("  verify --in <file.bin>     Compare the PCM contents against a file");
            Console.WriteLine("  erase                      Erase the flash (recovery)");
            Console.WriteLine("  dtcs [--clear]             Read stored trouble codes (0x19); --clear wipes them (0x14)");
            Console.WriteLine("  vin                        Read the VIN from the PCM (0x22 F190)");
            Console.WriteLine("  seed  --seed <AABBCC>      Compute the 0x27 key for a seed (no hardware needed)");
            Console.WriteLine("  keybag --file <bag.txt>    Parse and list a keybag of seed/key constant sets");
            Console.WriteLine();
            Console.WriteLine("Options");
            Console.WriteLine("  --dll <path>               J2534 driver DLL (default: first interface in the registry)");
            Console.WriteLine("  --device <name>            Device name passed to PassThruOpen");
            Console.WriteLine("  --profile <name>           falcon (default) | blackoak");
            Console.WriteLine("  --keybag <file>            Try each constant set in the file until the PCM unlocks");
            Console.WriteLine("  --feps-pin <n>             Programming voltage pin (default 0x80; use 13 for OBD pin 13)");
            Console.WriteLine("  --feps-voltage <volts>     Programming voltage (default 18)");
            Console.WriteLine("  --verify                   Verify after writing");
            Console.WriteLine("  --yes                      Do not ask for confirmation");
            Console.WriteLine();
            Console.WriteLine("Examples");
            Console.WriteLine("  psychoflash interfaces");
            Console.WriteLine("  psychoflash read  --out BF_falcon_stock.bin");
            Console.WriteLine("  psychoflash write --in BF_falcon_tuned.bin --verify");
            Console.WriteLine("  psychoflash seed  --seed 6A3F12");
            Console.WriteLine("  psychoflash dtcs");
            Console.WriteLine("  psychoflash dtcs --clear");
            Console.WriteLine("  psychoflash keybag --file keybag.txt");
            Console.WriteLine("  psychoflash read --out stock.bin --keybag keybag.txt");
        }

        private static Dictionary<string, string> ParseOptions(List<string> options)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < options.Count; i++)
            {
                string token = options[i];
                if (!token.StartsWith("-", StringComparison.Ordinal)) continue;
                string key = token.TrimStart('-');
                if (i + 1 < options.Count && !options[i + 1].StartsWith("-", StringComparison.Ordinal) && NeedsValue(key))
                {
                    map[key] = options[++i];
                }
                else
                {
                    map[key] = "true";
                }
            }
            return map;
        }

        private static bool NeedsValue(string key)
        {
            string[] withValue = { "dll", "device", "profile", "in", "out", "seed", "pin", "voltage",
                                   "keybag", "feps-pin", "feps-voltage", "file" };
            foreach (string s in withValue) if (string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static PcmProfile ResolveProfile()
        {
            PcmProfile profile = (_profileName == "blackoak" || _profileName == "aston" || _profileName == "dual")
                ? PcmProfile.BlackOakDual()
                : PcmProfile.SpanishOakFalcon();

            // The reference tool used the pseudo pin 0x80; several interfaces want the real OBD
            // pin (13 on Ford) instead, so both are selectable.
            if (_fepsPin >= 0) profile.ProgrammingPin = (uint)_fepsPin;
            if (_fepsVoltage > 0) profile.ProgrammingVoltage = (ushort)_fepsVoltage;
            return profile;
        }

        /// <summary>Accepts decimal or 0x-prefixed hex.</summary>
        private static int ParseNumber(string text, string option)
        {
            string value = (text ?? string.Empty).Trim();
            int result;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)) return result;
            }
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            throw new FormatException(option + " expects a number, got '" + text + "'.");
        }

        private static int ListInterfaces()
        {
            IList<J2534Library.Registration> found = J2534Library.FindInstalledInterfaces();
            if (found.Count == 0)
            {
                Console.WriteLine("No J2534 interface registered. Install the vendor driver first.");
                return 1;
            }
            foreach (J2534Library.Registration reg in found)
            {
                Console.WriteLine("  " + reg);
                Console.WriteLine("      DLL: " + reg.FunctionLibrary);
            }
            return 0;
        }

        private static int Keybag(Dictionary<string, string> parsed)
        {
            string path;
            if (!parsed.TryGetValue("file", out path) && !string.IsNullOrEmpty(_keybagPath)) path = _keybagPath;
            if (string.IsNullOrEmpty(path))
            {
                Console.Error.WriteLine("--file <bag.txt> is required.");
                return 2;
            }

            IList<ISeedKeyAlgorithm> bag = AlgorithmBag.Load(path);
            Console.WriteLine("Keybag " + path + ": " + bag.Count + " candidate(s)");
            for (int i = 0; i < bag.Count; i++)
            {
                FordSeedKeyMkI ford = bag[i] as FordSeedKeyMkI;
                Console.WriteLine("  " + (i + 1).ToString().PadLeft(2) + ". " + bag[i].Name.PadRight(24) +
                                  (ford != null ? ford.Constants : "(custom algorithm)"));
            }
            if (bag.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Keys for seed 0x123456:");
                foreach (ISeedKeyAlgorithm algorithm in bag)
                {
                    Console.WriteLine("  " + algorithm.Name.PadRight(24) + " -> 0x" + algorithm.ComputeKey(0x123456).ToString("X6"));
                }
                Console.WriteLine();
                Console.WriteLine("Each wrong candidate costs the PCM one security attempt - keep bags short.");
            }
            return 0;
        }

        private static int Seed(Dictionary<string, string> parsed)
        {
            string value;
            if (!parsed.TryGetValue("seed", out value))
            {
                Console.Error.WriteLine("--seed <AABBCC> is required.");
                return 2;
            }
            uint seed = FordSeedKeyMkI.ParseSeed(value);

            if (!string.IsNullOrEmpty(_keybagPath))
            {
                IList<ISeedKeyAlgorithm> bag = AlgorithmBag.Load(_keybagPath);
                Console.WriteLine("Seed      : 0x" + seed.ToString("X6"));
                foreach (ISeedKeyAlgorithm candidate in bag)
                {
                    uint candidateKey = candidate.ComputeKey(seed);
                    Console.WriteLine("  " + candidate.Name.PadRight(24) + " key 0x" + candidateKey.ToString("X6") +
                                      "   (0x27 0x02 " + FordSeedKeyMkI.FormatKey(candidateKey).Insert(2, " ").Insert(5, " ") + ")");
                }
                return 0;
            }

            ISeedKeyAlgorithm algorithm = SeedKeyAlgorithms.FordSpanishOak;
            uint key = algorithm.ComputeKey(seed);
            Console.WriteLine("Algorithm : " + algorithm.Name);
            Console.WriteLine("Seed      : 0x" + seed.ToString("X6"));
            Console.WriteLine("Key       : 0x" + key.ToString("X6") + "   (0x27 0x02 " + FordSeedKeyMkI.FormatKey(key).Insert(2, " ").Insert(5, " ") + ")");
            return 0;
        }

        private static bool Confirm(string question)
        {
            if (_yes) return true;
            Console.Write(question + " [y/N] ");
            string answer = Console.ReadLine();
            return answer != null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                      answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        private static FlashSession CreateSession(bool connect)
        {
            FlashSession session = new FlashSession();
            if (!string.IsNullOrEmpty(_keybagPath))
            {
                IList<ISeedKeyAlgorithm> bag = AlgorithmBag.Load(_keybagPath);
                if (bag.Count == 0) throw new InvalidOperationException("The keybag '" + _keybagPath + "' contains no candidates.");
                session.KeyAlgorithms = bag;
                Console.WriteLine("  Keybag: " + bag.Count + " candidate(s) from " + _keybagPath);
            }
            session.Log += delegate(string message) { Console.WriteLine("  " + message); };
            session.OperatorPrompt += delegate(string message)
            {
                Console.WriteLine();
                Console.WriteLine(">>> " + message);
                Console.WriteLine(">>> Press Enter when done ...");
                Console.ReadLine();
            };
            if (connect)
            {
                string dll = _driver;
                if (string.IsNullOrEmpty(dll))
                {
                    IList<J2534Library.Registration> found = J2534Library.FindInstalledInterfaces();
                    if (found.Count == 0) throw new InvalidOperationException("No J2534 interface found. Use --dll <path>.");
                    dll = found[0].FunctionLibrary;
                }
                session.Connect(dll, _deviceName, ResolveProfile());
            }
            return session;
        }

        private static int Info()
        {
            using (FlashSession session = CreateSession(true))
            {
                Console.WriteLine("Battery voltage: " + session.ReadBatteryVoltage().ToString("0.00") + " V");
                Console.WriteLine("PCM VIN        : " + (session.Client.ReadVehicleIdentification() ?? string.Empty));
                return 0;
            }
        }

        private static CancellationTokenSource CreateCancellation()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancelling ...");
            };
            return cts;
        }

        private static Progress<FlashProgress> CreateProgress()
        {
            int lastPercent = -1;
            return new Progress<FlashProgress>(delegate(FlashProgress p)
            {
                int percent = (int)p.Percent;
                if (percent == lastPercent) return;
                lastPercent = percent;
                Console.Write("\r  " + p.Status + ": " + percent.ToString().PadLeft(3) + "%  ");
                if (percent >= 100) Console.WriteLine();
            });
        }

        private static int Read(Dictionary<string, string> parsed)
        {
            string output;
            if (!parsed.TryGetValue("out", out output))
            {
                Console.Error.WriteLine("--out <file.bin> is required.");
                return 2;
            }
            if (File.Exists(output) && !Confirm("'" + output + "' exists. Overwrite?")) return 1;

            using (CancellationTokenSource cts = CreateCancellation())
            using (FlashSession session = CreateSession(true))
            {
                byte[] image = session.ReadToFile(output, CreateProgress(), cts.Token);
                Console.WriteLine("Read " + image.Length + " bytes. Blank image: " + (ImageTools.IsBlank(image) ? "yes" : "no"));
            }
            return 0;
        }

        private static int Write(Dictionary<string, string> parsed)
        {
            string input;
            if (!parsed.TryGetValue("in", out input))
            {
                Console.Error.WriteLine("--in <file.bin> is required.");
                return 2;
            }
            if (!File.Exists(input))
            {
                Console.Error.WriteLine("File not found: " + input);
                return 2;
            }
            byte[] image = File.ReadAllBytes(input);
            PcmProfile profile = ResolveProfile();
            if (image.Length != profile.ImageSize)
            {
                Console.Error.WriteLine("Image is " + image.Length + " bytes; " + profile.Name + " needs " + profile.ImageSize + " bytes.");
                return 2;
            }

            Console.WriteLine("Profile : " + profile.Name);
            Console.WriteLine("Image   : " + Path.GetFullPath(input));
            Console.WriteLine("SHA-256 : " + FlashService.Hash(image));
            if (!Confirm("Erase and reprogram the PCM now?")) return 1;

            bool verify = parsed.ContainsKey("verify");
            using (CancellationTokenSource cts = CreateCancellation())
            using (FlashSession session = CreateSession(true))
            {
                session.Flash(image, verify, CreateProgress(), cts.Token);
            }
            return 0;
        }

        private static int ReadCodes(Dictionary<string, string> parsed)
        {
            using (FlashSession session = CreateSession(true))
            {
                if (parsed.ContainsKey("clear"))
                {
                    if (!Confirm("Clear the stored trouble codes?")) return 1;
                    session.ClearCodes();
                    return 0;
                }

                IList<DiagnosticCode> codes = session.ReadCodes();
                Console.WriteLine();
                if (codes.Count == 0) Console.WriteLine("No stored trouble codes.");
                foreach (DiagnosticCode code in codes)
                {
                    Console.WriteLine("  " + code.Code + "  status 0x" + code.Status.ToString("X2") +
                                      (code.IsCurrent ? "  PRESENT" : code.IsConfirmed ? "  confirmed" : string.Empty) +
                                      (code.Description.Length > 0 ? "  " + code.Description : string.Empty));
                }
                return 0;
            }
        }

        private static int ReadVin()
        {
            using (FlashSession session = CreateSession(true))
            {
                string vin = session.ReadVin();
                return string.IsNullOrEmpty(vin) ? 1 : 0;
            }
        }

        private static int Verify(Dictionary<string, string> parsed)
        {
            string input;
            if (!parsed.TryGetValue("in", out input))
            {
                Console.Error.WriteLine("--in <file.bin> is required.");
                return 2;
            }
            byte[] image = File.ReadAllBytes(input);
            using (CancellationTokenSource cts = CreateCancellation())
            using (FlashSession session = CreateSession(true))
            {
                session.Service.EnterProgrammingMode(true);
                try
                {
                    if (!session.UnlockPcm())
                    {
                        Console.Error.WriteLine("Security access not granted.");
                        return 1;
                    }
                    int diff = session.Service.VerifyImage(image, CreateProgress(), cts.Token);
                    Console.WriteLine(diff == 0 ? "PCM matches the file." : diff + " bytes differ.");
                    return diff == 0 ? 0 : 1;
                }
                finally
                {
                    session.Service.LeaveProgrammingMode();
                }
            }
        }

        private static int Erase()
        {
            if (!Confirm("This erases the PCM flash. The engine will not start until a valid image is written. Continue?")) return 1;
            using (CancellationTokenSource cts = CreateCancellation())
            using (FlashSession session = CreateSession(true))
            {
                session.Erase(CreateProgress(), cts.Token);
            }
            Console.WriteLine("Erase complete.");
            return 0;
        }
    }
}
