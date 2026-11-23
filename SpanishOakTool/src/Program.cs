using System;
using System.IO;
using SpanishOakTool.Pcm;

namespace SpanishOakTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string command = args[0].ToLowerInvariant();
        var options = ParseOptions(args);

        if (!options.TryGetValue("dll", out string? dllPath) || string.IsNullOrWhiteSpace(dllPath))
        {
            Console.Error.WriteLine("Missing required --dll <path-to-vendor-J2534.dll>");
            return 1;
        }

        try
        {
            switch (command)
            {
                case "probe":
                    return RunProbe(dllPath);
                case "info":
                    return RunInfo(dllPath);
                case "read":
                    if (!options.TryGetValue("out", out string? outPath) || outPath is null)
                    {
                        Console.Error.WriteLine("read requires --out <file.bin>");
                        return 1;
                    }
                    return RunRead(dllPath, outPath);
                case "write":
                    if (!options.TryGetValue("in", out string? inPath) || inPath is null)
                    {
                        Console.Error.WriteLine("write requires --in <file.bin>");
                        return 1;
                    }
                    return RunWrite(dllPath, inPath, confirmed: options.ContainsKey("yes"));
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 2;
        }
    }

    private static int RunProbe(string dllPath)
    {
        using var session = NewSession(dllPath);
        session.Connect();
        bool ok = session.Probe();
        Console.WriteLine(ok ? "Probe OK: PCM responded to session control." : "Probe FAILED: no/negative response from PCM.");
        return ok ? 0 : 1;
    }

    private static int RunInfo(string dllPath)
    {
        using var session = NewSession(dllPath);
        session.Connect();
        if (!session.Probe())
        {
            Console.WriteLine("Could not establish a diagnostic session with the PCM.");
            return 1;
        }
        Console.WriteLine("Session established. (ReadDataByIdentifier lookups for VIN/part number/" +
                           "calibration ID are ECU-variant-specific DIDs this project doesn't hardcode yet " +
                           "- capture your own bus trace to add the DIDs this PCM responds to.)");
        return 0;
    }

    private static int RunRead(string dllPath, string outPath)
    {
        using var session = NewSession(dllPath);
        session.Connect();

        Console.WriteLine("Entering programming session and requesting security access...");
        if (!session.EnterProgrammingSessionAndUnlock())
        {
            Console.WriteLine("Security access was not granted - aborting read.");
            return 1;
        }

        Console.WriteLine($"Reading {PcmSession.FlashSize} bytes to '{outPath}'...");
        session.ReadFlash(outPath, (done, total) =>
        {
            Console.Write($"\r  {done,8}/{total} bytes ({(done * 100.0 / total):F1}%)");
        });
        Console.WriteLine();

        var fi = new FileInfo(outPath);
        if (fi.Length != PcmSession.FlashSize)
        {
            Console.WriteLine($"WARNING: output file is {fi.Length} bytes, expected {PcmSession.FlashSize}. Read may be incomplete.");
            return 1;
        }

        Console.WriteLine("Read complete and size-verified.");
        return 0;
    }

    private static int RunWrite(string dllPath, string inPath, bool confirmed)
    {
        if (!File.Exists(inPath))
        {
            Console.Error.WriteLine($"Input file not found: {inPath}");
            return 1;
        }

        byte[] image = File.ReadAllBytes(inPath);
        if (image.Length != PcmSession.FlashSize)
        {
            Console.Error.WriteLine(
                $"Refusing to write: image is {image.Length} bytes, expected exactly {PcmSession.FlashSize} " +
                "(1024KB). Writing a mis-sized image can brick the PCM.");
            return 1;
        }

        if (!confirmed)
        {
            Console.WriteLine("This will erase and reprogram the PCM flash. A failed/interrupted write can");
            Console.WriteLine("brick the module. Ensure a battery support/charger is connected.");
            Console.Write("Type YES to continue: ");
            string? answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "YES", StringComparison.Ordinal))
            {
                Console.WriteLine("Aborted.");
                return 1;
            }
        }

        using var session = NewSession(dllPath);
        session.Connect();

        Console.WriteLine("Entering programming session and requesting security access...");
        if (!session.EnterProgrammingSessionAndUnlock())
        {
            Console.WriteLine("Security access was not granted - aborting write.");
            return 1;
        }

        Console.WriteLine("Writing flash...");
        session.WriteFlash(image, (done, total) =>
        {
            Console.Write($"\r  {done,8}/{total} bytes ({(done * 100.0 / total):F1}%)");
        });
        Console.WriteLine();
        session.ClearDtcs();
        Console.WriteLine("Write complete, DTCs cleared.");
        return 0;
    }

    private static PcmSession NewSession(string dllPath)
    {
        var session = new PcmSession(dllPath, new NotImplementedSecurityKeyAlgorithm());
        session.Log = msg => Console.WriteLine(msg);
        return session;
    }

    private static System.Collections.Generic.Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new System.Collections.Generic.Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            string? value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : null;
            result[key] = value;
        }
        return result;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SpanishOakTool - original J2534/UDS utility for the BF Barra PCM");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  SpanishOakTool probe --dll <path>");
        Console.WriteLine("  SpanishOakTool info  --dll <path>");
        Console.WriteLine("  SpanishOakTool read  --dll <path> --out <file.bin>");
        Console.WriteLine("  SpanishOakTool write --dll <path> --in <file.bin> [--yes]");
        Console.WriteLine();
        Console.WriteLine("NOTE: read/write require a SecurityAccess key algorithm, which this project");
        Console.WriteLine("intentionally does not ship (see src/Pcm/ISecurityKeyAlgorithm.cs). 'probe' and");
        Console.WriteLine("'info' work without it.");
    }
}
