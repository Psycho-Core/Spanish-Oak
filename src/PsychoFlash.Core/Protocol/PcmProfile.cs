using System;
using System.Collections.Generic;

namespace PsychoFlash.Core.Protocol
{
    /// <summary>A contiguous range of PCM memory.</summary>
    public sealed class FlashSegment
    {
        public int Address { get; set; }
        public int Length { get; set; }

        public FlashSegment() { }

        public FlashSegment(int address, int length)
        {
            Address = address;
            Length = length;
        }

        public int End { get { return Address + Length; } }

        public override string ToString()
        {
            return "0x" + Address.ToString("X6") + " + 0x" + Length.ToString("X6");
        }
    }

    /// <summary>
    /// Everything that differs between EEC-VI PCM variants: CAN identifiers, memory map,
    /// block size, erase command and the request-download frame.
    /// </summary>
    public sealed class PcmProfile
    {
        public string Name { get; set; }
        public string Description { get; set; }

        public uint RequestId { get; set; }
        public uint ResponseId { get; set; }
        public uint BaudRate { get; set; }
        public bool Use29BitIdentifier { get; set; }

        /// <summary>Software programming (0x10) session used before unlocking.</summary>
        public byte ProgrammingSession { get; set; }

        /// <summary>Size of the binary image in bytes (1 MB for a Spanish Oak 1024k PCM).</summary>
        public int ImageSize { get; set; }

        public List<FlashSegment> ReadSegments { get; private set; }
        public List<FlashSegment> WriteSegments { get; private set; }

        /// <summary>Number of bytes requested per 0x23 readMemoryByAddress call.</summary>
        public int ReadBlockSize { get; set; }

        /// <summary>Number of bytes sent per 0x36 transferData call.</summary>
        public int WriteBlockSize { get; set; }

        /// <summary>Service payload sent to erase the flash (Spanish Oak uses 0xB2 0xAA).</summary>
        public byte[] ErasePayload { get; set; }

        /// <summary>Parameter bytes of the 0x34 requestDownload frame.</summary>
        public byte[] RequestDownloadPayload { get; set; }

        /// <summary>FEPS voltage and pin used to bring the PCM into programming mode.</summary>
        public ushort ProgrammingVoltage { get; set; }
        public uint ProgrammingPin { get; set; }

        /// <summary>Time the PCM needs after the erase command before it accepts a download (ms).</summary>
        public int EraseSettleMilliseconds { get; set; }

        public PcmProfile()
        {
            RequestId = 0x7E0;
            ResponseId = 0x7E8;
            BaudRate = 500000;
            ProgrammingSession = 0x85;
            ReadBlockSize = 0x400;
            WriteBlockSize = 0x400;
            ProgrammingVoltage = 18;
            ProgrammingPin = J2534.ProgrammingPin.Feps;
            EraseSettleMilliseconds = 5000;
            ReadSegments = new List<FlashSegment>();
            WriteSegments = new List<FlashSegment>();
        }

        public int TotalReadBytes
        {
            get
            {
                int total = 0;
                foreach (FlashSegment s in ReadSegments) total += s.Length;
                return total;
            }
        }

        public int TotalWriteBytes
        {
            get
            {
                int total = 0;
                foreach (FlashSegment s in WriteSegments) total += s.Length;
                return total;
            }
        }

        /// <summary>
        /// Ford Falcon Spanish Oak 1024k PCM (BF, FG, FGII, FGX).
        /// Read   : 0x000000 - 0x0FFFFF (full 1 MB image).
        /// Write  : 0x010000 - 0x0FFFFF (the calibration/strategy area; the boot block is left alone).
        /// Erase  : 0xB2 0xAA.
        /// </summary>
        public static PcmProfile SpanishOakFalcon()
        {
            PcmProfile p = new PcmProfile();
            p.Name = "Ford Falcon Spanish Oak 1024k (BF/FG/FGII/FGX)";
            p.Description = "EEC-VI PCM, 1 MB flash, CAN 0x7E0/0x7E8 at 500 kbit/s, FEPS 18 V.";
            p.ImageSize = 0x100000;
            p.ReadSegments.Add(new FlashSegment(0x000000, 0x100000));
            p.WriteSegments.Add(new FlashSegment(0x010000, 0x0F0000));
            p.ErasePayload = new byte[] { 0xB2, 0xAA };
            p.RequestDownloadPayload = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00 };
            p.EraseSettleMilliseconds = 5000;
            return p;
        }

        /// <summary>
        /// Black Oak / dual-PCM image layout used by the 1.5 MB Aston Martin applications.
        /// The two banks live at 0x000000 and 0x500000 inside the combined image.
        /// </summary>
        public static PcmProfile BlackOakDual()
        {
            PcmProfile p = new PcmProfile();
            p.Name = "Ford/Aston Black Oak dual bank (1.5 MB)";
            p.Description = "Two 1 MB banks combined into a 0x170000 image; second bank at 0x500000.";
            p.ImageSize = 0x170000;
            p.ReadSegments.Add(new FlashSegment(0x000000, 0x070000));
            p.ReadSegments.Add(new FlashSegment(0x500000, 0x100000));
            p.WriteSegments.Add(new FlashSegment(0x010000, 0x060000));
            p.WriteSegments.Add(new FlashSegment(0x500000, 0x100000));
            p.ErasePayload = new byte[] { 0xB2, 0xAA };
            p.RequestDownloadPayload = new byte[] { 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00 };
            p.EraseSettleMilliseconds = 60000;
            return p;
        }

        public static IList<PcmProfile> All()
        {
            List<PcmProfile> list = new List<PcmProfile>();
            list.Add(SpanishOakFalcon());
            list.Add(BlackOakDual());
            return list;
        }

        /// <summary>Maps a memory address to an offset inside the image file, or -1 if unmapped.</summary>
        public int MapAddressToImageOffset(int address)
        {
            int offset = 0;
            foreach (FlashSegment s in ReadSegments)
            {
                if (address >= s.Address && address < s.End)
                {
                    return offset + (address - s.Address);
                }
                offset += s.Length;
            }
            return -1;
        }
    }
}
