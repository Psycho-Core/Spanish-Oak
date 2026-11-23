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
            Console.WriteLine("  seed  --seed <AABBCC>      Compute the 0x27 key for a seed (no hardware needed)");
            Console.WriteLine();
            Console.WriteLine("Options");
            Console.WriteLine("  --dll <path>               J2534 driver DLL (default: first interface in the registry)");
            Console.WriteLine("  --device <name>            Device name passed to PassThruOpen");
            Console.WriteLine("  --profile <name>           falcon (default) | blackoak");
            Console.WriteLine("  --verify                   Verify after writing");
            Console.WriteLine("  --yes                      Do not ask for confirmation");
            Console.WriteLine();
            Console.WriteLine("Examples");
            Console.WriteLine("  psychoflash interfaces");
            Console.WriteLine("  psychoflash read  --out BF_falcon_stock.bin");
            Console.WriteLine("  psychoflash write --in BF_falcon_tuned.bin --verify");
            Console.WriteLine("  psychoflash seed  --seed 6A3F12");
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
            string[] withValue = { "dll", "device", "profile", "in", "out", "seed", "pin", "voltage" };
            foreach (string s in withValue) if (string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static PcmProfile ResolveProfile()
        {
            if (_profileName == "blackoak" || _profileName == "aston" || _profileName == "dual")
            {
                return PcmProfile.BlackOakDual();
            }
            return PcmProfile.SpanishOakFalcon();
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

        private static int Seed(Dictionary<string, string> parsed)
        {
            string value;
            if (!parsed.TryGetValue("seed", out value))
            {
                Console.Error.WriteLine("--seed <AABBCC> is required.");
                return 2;
            }
            uint seed = FordSeedKeyMkI.ParseSeed(value);
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
                    if (!session.Service.Unlock(session.KeyAlgorithm))
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
