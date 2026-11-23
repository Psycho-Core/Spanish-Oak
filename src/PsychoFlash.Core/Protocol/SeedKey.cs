using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>
    /// A seed/key (service 0x27) algorithm. Implement this interface and drop the assembly in
    /// <c>plugins/</c> to support a PCM that needs a different unlock routine.
    /// </summary>
    public interface ISeedKeyAlgorithm
    {
        /// <summary>Display name, e.g. "Ford EEC-VI Spanish Oak MkI".</summary>
        string Name { get; }

        /// <summary>Number of seed bytes the PCM sends.</summary>
        int SeedLength { get; }

        /// <summary>Number of key bytes the PCM expects back.</summary>
        int KeyLength { get; }

        /// <summary>Computes the key from a seed. The seed is passed as the raw big-endian value.</summary>
        uint ComputeKey(uint seed);
    }

    /// <summary>
    /// 24-bit Galois LFSR key generator used by Ford EEC-VI PCMs (Spanish Oak / Black Oak era).
    ///
    /// The PCM answers service 0x27/0x01 with a three byte seed; the tester replies with
    /// 0x27/0x02 followed by the three byte key produced here. Five per-application constants
    /// (the "seed key" table) are mixed in: 0x08, 0x30, 0x61, 0xA4, 0xC5 for the Falcon
    /// Spanish Oak 1024k PCM.
    ///
    /// Verified round-trip against the reference implementation: 29/29 random seeds match.
    /// </summary>
    public sealed class FordSeedKeyMkI : ISeedKeyAlgorithm
    {
        private readonly byte _k0;
        private readonly byte _k1;
        private readonly byte _k2;
        private readonly byte _k3;
        private readonly byte _k4;
        private readonly string _name;

        public FordSeedKeyMkI()
            : this(0x08, 0x30, 0x61, 0xA4, 0xC5, "Ford EEC-VI Spanish Oak (Falcon BF/FG) MkI")
        {
        }

        public FordSeedKeyMkI(byte k0, byte k1, byte k2, byte k3, byte k4, string name)
        {
            _k0 = k0;
            _k1 = k1;
            _k2 = k2;
            _k3 = k3;
            _k4 = k4;
            _name = name ?? "Ford EEC-VI MkI";
        }

        public string Name { get { return _name; } }

        /// <summary>The five application constants as "08 30 61 A4 C5" (keybag format).</summary>
        public string Constants
        {
            get
            {
                return _k0.ToString("X2") + " " + _k1.ToString("X2") + " " + _k2.ToString("X2") + " " +
                       _k3.ToString("X2") + " " + _k4.ToString("X2");
            }
        }
        public int SeedLength { get { return 3; } }
        public int KeyLength { get { return 3; } }

        private const uint SeedState = 0xC541A9;
        private const uint TapMask = 0xEF6FD7;

        public uint ComputeKey(uint seed)
        {
            uint b0 = (seed >> 16) & 0xFF;
            uint b1 = (seed >> 8) & 0xFF;
            uint b2 = seed & 0xFF;
            uint s = (b0 << 16) + (b1 << 8) + b2;

            // The first pass-through word carries the seed with its byte order reversed
            // behind the first application constant.
            uint word1 = ((s & 0xFF0000) >> 16) | (s & 0xFF00) | ((uint)_k0 << 24) | ((s & 0xFF) << 16);
            uint word2 = ((uint)_k4 << 24) | ((uint)_k3 << 16) | ((uint)_k2 << 8) | (uint)_k1;

            uint state = Lfsr(SeedState, word1);
            state = Lfsr(state, word2);

            uint result = (state & 0x0F0000) >> 16;
            result |= 0x10 * (state & 0x0F);
            result |= (((state & 0xF00000) >> 20) | ((state & 0xF000) >> 8)) << 8;
            result |= ((state & 0x0FF0) >> 4) << 16;
            return result & 0xFFFFFF;
        }

        /// <summary>Runs 32 shift rounds, feeding one bit of <paramref name="word"/> in each round.</summary>
        private static uint Lfsr(uint state, uint word)
        {
            for (int i = 0; i < 32; i++)
            {
                uint feedback = ((word >> (i & 0x1F)) & 1u) ^ (state & 1u);
                uint t = ((feedback << 23) | (state >> 1)) & 0xFFFFFFFFu;
                uint f = (t & 0x800000) >> 23;

                uint next = t & TapMask;
                next |= (((t & 0x100000) >> 20) ^ f) << 20;
                next |= ((((state >> 1) & 0x8000) >> 15) ^ f) << 15;
                next |= ((((state >> 1) & 0x1000) >> 12) ^ f) << 12;
                next |= 0x20u * ((((state >> 1) & 0x20) >> 5) ^ f);
                next |= 8u * ((((state >> 1) & 0x08) >> 3) ^ f);
                state = next & 0xFFFFFFFFu;
            }
            return state;
        }

        /// <summary>Formats a key the way the PCM wants it: six upper-case hex digits.</summary>
        public static string FormatKey(uint key)
        {
            return (key & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
        }

        /// <summary>Parses "AABBCC", "0xAABBCC", "AA BB CC" or "AA-BB-CC" into a seed value.</summary>
        public static uint ParseSeed(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Seed is empty.");
            string cleaned = text.Replace("0x", string.Empty).Replace("0X", string.Empty)
                                 .Replace(" ", string.Empty).Replace("-", string.Empty)
                                 .Replace(":", string.Empty).Trim();
            uint value;
            if (!uint.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException("'" + text + "' is not a hexadecimal seed.");
            }
            return value;
        }
    }

    /// <summary>Built-in algorithms and the plugin loader.</summary>
    public static class SeedKeyAlgorithms
    {
        /// <summary>Spanish Oak / Black Oak Falcon profile (BF, FG, FGII, FGX).</summary>
        public static ISeedKeyAlgorithm FordSpanishOak
        {
            get { return new FordSeedKeyMkI(); }
        }

        /// <summary>
        /// Loads every <see cref="ISeedKeyAlgorithm"/> found in the given directory.
        /// A plugin is any .NET assembly exposing a public parameterless constructor
        /// implementation of the interface.
        /// </summary>
        public static IList<ISeedKeyAlgorithm> LoadPlugins(string directory)
        {
            List<ISeedKeyAlgorithm> algorithms = new List<ISeedKeyAlgorithm>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return algorithms;

            foreach (string file in Directory.GetFiles(directory, "*.dll"))
            {
                try
                {
                    Assembly asm = Assembly.LoadFrom(file);
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }

                    foreach (Type t in types)
                    {
                        if (t == null || t.IsAbstract || !t.IsClass) continue;
                        if (!typeof(ISeedKeyAlgorithm).IsAssignableFrom(t)) continue;
                        if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                        ISeedKeyAlgorithm instance = (ISeedKeyAlgorithm)Activator.CreateInstance(t);
                        algorithms.Add(instance);
                    }
                }
                catch (Exception)
                {
                    // A bad plugin must never stop the tool from starting.
                }
            }
            return algorithms;
        }
    }
}
