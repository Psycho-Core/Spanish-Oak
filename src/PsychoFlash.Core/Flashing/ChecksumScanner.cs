using System;
using System.Collections.Generic;

namespace PsychoFlash.Core.Flashing
{
    /// <summary>A range of an image over which a checksum might be calculated.</summary>
    public sealed class ChecksumRegion
    {
        public string Name { get; private set; }
        public int Start { get; private set; }
        public int Length { get; private set; }

        public ChecksumRegion(string name, int start, int length)
        {
            Name = name;
            Start = start;
            Length = length;
        }

        public int End { get { return Start + Length; } }

        public override string ToString()
        {
            return Name + " 0x" + Start.ToString("X6") + " + 0x" + Length.ToString("X6");
        }
    }

    /// <summary>A stored value that matches a checksum scheme over a region.</summary>
    public sealed class ChecksumFinding
    {
        public ChecksumRegion Region { get; private set; }
        public string Scheme { get; private set; }
        public int Offset { get; private set; }
        public int Size { get; private set; }
        public bool BigEndian { get; private set; }
        public uint StoredInStock { get; private set; }
        public uint ComputedForStock { get; private set; }
        public uint StoredInModified { get; private set; }
        public uint ComputedForModified { get; private set; }

        public ChecksumFinding(ChecksumRegion region, string scheme, int offset, int size, bool bigEndian,
                               uint storedStock, uint computedStock, uint storedModified, uint computedModified)
        {
            Region = region;
            Scheme = scheme;
            Offset = offset;
            Size = size;
            BigEndian = bigEndian;
            StoredInStock = storedStock;
            ComputedForStock = computedStock;
            StoredInModified = storedModified;
            ComputedForModified = computedModified;
        }

        public override string ToString()
        {
            return "0x" + Offset.ToString("X6") + "  " + Size * 8 + "-bit " + (BigEndian ? "big" : "little") +
                   " endian  =  " + Scheme + " over " + Region.Name +
                   "   (0x" + StoredInStock.ToString("X8") + " -> 0x" + StoredInModified.ToString("X8") + ")";
        }
    }

    /// <summary>
    /// Finds the checksum scheme of an operating system from evidence instead of guessing.
    ///
    /// Give it a stock image and a version of the same image that the PCM has accepted (a tune from
    /// a known-good tool). A checksum field is a value that (a) changed between the two, and (b)
    /// equals a candidate scheme computed over a candidate region in *both* images. Requiring the
    /// match twice is what makes a finding credible.
    ///
    /// This is a measurement tool. It never writes to an image.
    /// </summary>
    public static class ChecksumScanner
    {
        /// <summary>Whole image, boot block and calibration area - the ranges Ford uses.</summary>
        public static IList<ChecksumRegion> DefaultRegions(int imageLength)
        {
            List<ChecksumRegion> regions = new List<ChecksumRegion>();
            regions.Add(new ChecksumRegion("whole image", 0, imageLength));
            if (imageLength > 0x10000)
            {
                regions.Add(new ChecksumRegion("boot block", 0x000000, 0x10000));
                regions.Add(new ChecksumRegion("calibration", 0x010000, imageLength - 0x10000));
            }
            return regions;
        }

        /// <summary>
        /// Scans for checksum fields. Returns an empty list when nothing matches, which is the
        /// honest answer when the pair is not related or the scheme is not one of the candidates.
        /// </summary>
        public static IList<ChecksumFinding> Scan(byte[] stock, byte[] modified, IList<ChecksumRegion> regions)
        {
            List<ChecksumFinding> findings = new List<ChecksumFinding>();
            if (stock == null || modified == null) return findings;

            int length = Math.Min(stock.Length, modified.Length);
            if (length == 0) return findings;
            if (regions == null || regions.Count == 0) regions = DefaultRegions(length);

            // A checksum field has to be somewhere the two images disagree.
            List<int> changed = new List<int>();
            for (int i = 0; i < length; i++)
            {
                if (stock[i] != modified[i]) changed.Add(i);
            }
            if (changed.Count == 0) return findings;

            // With millions of candidate offsets a 16-bit scheme matches by chance every few
            // thousand tries, so unrelated images would produce pure noise. A tune pair differs in
            // a fraction of a percent; anything changing more than 1% of the image is not a pair
            // this can say anything useful about.
            int maxChanged = Math.Max(64, length / 100);
            if (changed.Count > maxChanged) return findings;

            foreach (ChecksumRegion region in regions)
            {
                int start = Math.Max(0, region.Start);
                int regionLength = Math.Min(region.Length, length - start);
                if (regionLength <= 0) continue;

                uint[] stockValues = new uint[]
                {
                    ImageTools.Sum16(stock, start, regionLength),
                    ImageTools.Crc32(stock, start, regionLength),
                    ImageTools.Sum32(stock, start, regionLength)
                };
                uint[] modifiedValues = new uint[]
                {
                    ImageTools.Sum16(modified, start, regionLength),
                    ImageTools.Crc32(modified, start, regionLength),
                    ImageTools.Sum32(modified, start, regionLength)
                };

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int size = attempt == 0 ? 2 : 4;

                    for (int scheme = 0; scheme < 3; scheme++)
                    {
                        uint expectStock = stockValues[scheme] & (size == 2 ? 0xFFFFu : 0xFFFFFFFFu);
                        uint expectModified = modifiedValues[scheme] & (size == 2 ? 0xFFFFu : 0xFFFFFFFFu);

                        for (int c = 0; c < changed.Count; c++)
                        {
                            int offset = changed[c];
                            if (offset + size > length) continue;

                            for (int order = 0; order < 2; order++)
                            {
                                bool bigEndian = order == 0;
                                uint storedStock = Read(stock, offset, size, bigEndian);
                                uint storedModified = Read(modified, offset, size, bigEndian);

                                if (storedStock != expectStock || storedModified != expectModified) continue;
                                if (storedStock == storedModified) continue;  // a checksum that did not move is unlikely

                                findings.Add(new ChecksumFinding(region, SchemeName(scheme), offset, size, bigEndian,
                                                                 storedStock, expectStock, storedModified, expectModified));
                            }
                        }
                    }
                }
            }
            return findings;
        }

        private static string SchemeName(int index)
        {
            switch (index)
            {
                case 0: return "sum16";
                case 1: return "crc32";
                default: return "sum32";
            }
        }

        private static uint Read(byte[] data, int offset, int size, bool bigEndian)
        {
            if (size == 2)
            {
                return bigEndian
                    ? (uint)((data[offset] << 8) | data[offset + 1])
                    : (uint)((data[offset + 1] << 8) | data[offset]);
            }
            return bigEndian
                ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
                : ((uint)data[offset + 3] << 24) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset];
        }
    }
}
