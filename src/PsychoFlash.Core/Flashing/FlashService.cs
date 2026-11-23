using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using PsychoFlash.Core.J2534;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.Core.Flashing
{
    /// <summary>Progress snapshot for the UI / console.</summary>
    public sealed class FlashProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public string Status { get; set; }

        public double Percent
        {
            get
            {
                if (Total <= 0) return 0;
                double p = 100.0 * Completed / Total;
                if (p > 100) p = 100;
                if (p < 0) p = 0;
                return p;
            }
        }

        public override string ToString()
        {
            return (Status ?? string.Empty) + " " + Percent.ToString("0.0") + "%";
        }
    }

    /// <summary>
    /// High level operations against a Ford EEC-VI PCM: programming mode entry, security
    /// access, flash read, flash write (erase + download + transfer) and verification.
    /// </summary>
    public sealed class FlashService
    {
        private readonly FordPcmClient _client;
        private readonly Action<string> _log;
        private readonly PcmProfile _profile;

        public FlashService(FordPcmClient client, Action<string> log)
        {
            if (client == null) throw new ArgumentNullException("client");
            _client = client;
            _profile = client.Profile;
            _log = log ?? delegate { };
        }

        public PcmProfile Profile { get { return _profile; } }

        /// <summary>Number of security access attempts before giving up.</summary>
        public int SecurityAttempts { get; set; } = 3;

        /// <summary>Number of retries for a single flash block.</summary>
        public int BlockRetries { get; set; } = 3;

        /// <summary>
        /// Sends 0x3E tester present every N blocks to keep a long read or write inside the PCM's
        /// session timer. 0 disables it (the default, matching the reference tool).
        /// </summary>
        public int KeepAliveEveryBlocks { get; set; }

        /// <summary>Called when the tool needs the operator to cycle the ignition for FEPS.</summary>
        public Action<string> RequestOperatorAction { get; set; }

        private void Log(string message)
        {
            _log(message);
        }

        private void Notify(IProgress<FlashProgress> progress, int completed, int total, string status)
        {
            if (progress == null) return;
            FlashProgress p = new FlashProgress();
            p.Completed = completed;
            p.Total = total;
            p.Status = status;
            progress.Report(p);
        }

        /// <summary>
        /// Applies 18 V to the Ford FEPS pin, then asks for the programming session and disables
        /// DTC logging. On EEC-VI the FEPS edge is what actually opens the PCM, so a rejected
        /// session or DTC request is logged and ignored rather than aborting the flash - the
        /// reference tool behaves the same way.
        /// </summary>
        public void EnterProgrammingMode(bool promptForIgnitionCycle)
        {
            Log("Enabling FEPS (" + _profile.ProgrammingVoltage + " V) ...");
            _client.Device.SetProgrammingVoltage(_profile.ProgrammingPin, (uint)_profile.ProgrammingVoltage * 1000);

            if (promptForIgnitionCycle && RequestOperatorAction != null)
            {
                RequestOperatorAction("Turn the ignition OFF for 3 seconds, then back ON.");
            }
            Thread.Sleep(500);

            TryService("0x10 programming session", delegate { _client.StartDiagnosticSession(_profile.ProgrammingSession); });
            TryService("0x85 control DTC setting", delegate { _client.ControlDtcSetting(false); });
            TryService("0x3E tester present", delegate { _client.TesterPresent(); });
        }

        /// <summary>Runs an optional service, logging (but not propagating) a rejection.</summary>
        private void TryService(string name, Action action)
        {
            try
            {
                action();
            }
            catch (NegativeResponseException ex)
            {
                Log("  " + name + " rejected: " + ex.Message + " (continuing)");
            }
            catch (TimeoutException)
            {
                Log("  " + name + " got no answer (continuing)");
            }
        }

        /// <summary>Turns the programming voltage off again.</summary>
        public void LeaveProgrammingMode()
        {
            try
            {
                _client.Device.SetProgrammingVoltage(_profile.ProgrammingPin, ProgrammingPin.VoltageOff);
                Log("FEPS disabled.");
            }
            catch (Exception ex)
            {
                Log("Could not disable FEPS: " + ex.Message);
            }
        }

        /// <summary>Runs the 0x27 seed/key handshake, retrying the whole sequence on refusal.</summary>
        public bool Unlock(ISeedKeyAlgorithm algorithm)
        {
            for (int attempt = 1; attempt <= SecurityAttempts; attempt++)
            {
                Log("Security access attempt " + attempt + " of " + SecurityAttempts);
                try
                {
                    if (_client.SecurityAccess(algorithm)) return true;
                }
                catch (NegativeResponseException ex)
                {
                    Log("PCM refused the unlock: " + ex.Message);
                    // 0x36 = too many attempts -> the PCM wants the ignition cycled before retrying.
                    if (ex.Code == 0x36 || ex.Code == 0x37)
                    {
                        LeaveProgrammingMode();
                        if (RequestOperatorAction != null)
                        {
                            RequestOperatorAction("PCM locked out. Turn the ignition OFF, wait 10 seconds, then ON again.");
                        }
                        Thread.Sleep(1000);
                        EnterProgrammingMode(false);
                    }
                }
                Thread.Sleep(500);
            }
            return false;
        }

        /// <summary>
        /// Reads the seed and logs the key we <i>would</i> send, without sending it. Nothing is
        /// consumed: the PCM does not count a seed request against the security attempts. Use it
        /// to confirm communication, to collect the seed for offline analysis, or to compare
        /// against a pair from another tool.
        /// </summary>
        public uint ReadSeedOnly(ISeedKeyAlgorithm algorithm)
        {
            if (algorithm == null) throw new ArgumentNullException("algorithm");
            byte[] response = _client.RequestSeed(algorithm.SeedLength);
            uint seed = 0;
            for (int i = 0; i < algorithm.SeedLength && 2 + i < response.Length; i++)
            {
                seed = (seed << 8) | response[2 + i];
            }
            uint key = algorithm.ComputeKey(seed);
            Log("     seed = 0x" + seed.ToString("X" + (algorithm.SeedLength * 2).ToString(CultureInfo.InvariantCulture)) +
                "   key would be 0x" + key.ToString("X" + (algorithm.KeyLength * 2).ToString(CultureInfo.InvariantCulture)) +
                "   (no attempt used)");
            return seed;
        }

        /// <summary>
        /// Tries each algorithm in turn - the "keybag" approach, for a PCM whose application
        /// constants are not the built-in Falcon set. Returns the algorithm that unlocked the
        /// PCM, or null when none did.
        ///
        /// Each wrong candidate costs the PCM one security attempt (usually 10 before a lockout),
        /// so a bag is tried in file order and the PCM's own 0x36/0x37 answer stops the run.
        /// </summary>
        public ISeedKeyAlgorithm UnlockAny(IList<ISeedKeyAlgorithm> algorithms)
        {
            if (algorithms == null || algorithms.Count == 0) return null;
            int lockouts = 0;

            for (int i = 0; i < algorithms.Count; i++)
            {
                ISeedKeyAlgorithm algorithm = algorithms[i];
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    Log("Key candidate " + (i + 1) + " of " + algorithms.Count + ": " + algorithm.Name +
                        " [" + DescribeConstants(algorithm) + "]");
                    try
                    {
                        if (_client.SecurityAccess(algorithm))
                        {
                            Log("Security access granted with " + algorithm.Name + ".");
                            return algorithm;
                        }
                        break; // answered but not granted - next candidate
                    }
                    catch (NegativeResponseException ex)
                    {
                        if (ex.Code == 0x36 || ex.Code == 0x37)
                        {
                            lockouts++;
                            Log("  PCM locked out: " + ex.Message);
                            if (lockouts > 3)
                            {
                                Log("  giving up - the PCM keeps locking out.");
                                return null;
                            }
                            LeaveProgrammingMode();
                            if (RequestOperatorAction != null)
                            {
                                RequestOperatorAction("PCM locked out after too many keys. Turn the ignition OFF, wait 10 seconds, then ON again.");
                            }
                            Thread.Sleep(1000);
                            EnterProgrammingMode(false);
                            continue; // retry the same candidate
                        }
                        Log("  rejected: " + ex.Message);
                        break; // wrong key for this candidate
                    }
                    catch (TimeoutException ex)
                    {
                        Log("  no answer: " + ex.Message);
                        break;
                    }
                }
            }
            Log("No key candidate was accepted.");
            return null;
        }

        private static string DescribeConstants(ISeedKeyAlgorithm algorithm)
        {
            FordSeedKeyMkI ford = algorithm as FordSeedKeyMkI;
            return ford == null ? algorithm.GetType().Name : ford.Constants;
        }

        /// <summary>Erases the flash memory (0xB2 0xAA) and waits for the PCM to settle.</summary>
        public void Erase(IProgress<FlashProgress> progress, CancellationToken token)
        {
            Notify(progress, 0, 1, "Erasing flash");
            Log("Erasing flash memory - this takes about " + (_profile.EraseSettleMilliseconds / 1000) + " seconds ...");
            try
            {
                _client.EraseFlash();
            }
            catch (TimeoutException)
            {
                // Some PCMs simply stop answering while the erase runs; that is expected.
                Log("No answer to the erase command (expected on some PCMs).");
            }
            token.ThrowIfCancellationRequested();
            Thread.Sleep(_profile.EraseSettleMilliseconds);
            Notify(progress, 1, 1, "Erase complete");
        }

        /// <summary>Reads the complete flash image described by the profile.</summary>
        public byte[] ReadImage(IProgress<FlashProgress> progress, CancellationToken token)
        {
            int total = _profile.TotalReadBytes;
            byte[] image = new byte[total];
            int written = 0;

            foreach (FlashSegment segment in _profile.ReadSegments)
            {
                for (int address = segment.Address; address < segment.End; address += _profile.ReadBlockSize)
                {
                    token.ThrowIfCancellationRequested();

                    int block = Math.Min(_profile.ReadBlockSize, segment.End - address);
                    byte[] data = null;
                    for (int attempt = 1; attempt <= BlockRetries; attempt++)
                    {
                        try
                        {
                            data = _client.ReadMemory((uint)address, block);
                            if (data != null && data.Length >= block) break;
                            data = null;
                        }
                        catch (Exception ex)
                        {
                            Log("Read error at 0x" + address.ToString("X6") + " (attempt " + attempt + "): " + ex.Message);
                            data = null;
                        }
                    }
                    if (data == null)
                    {
                        throw new IOException("Flash read failed at address 0x" + address.ToString("X6") +
                                              ". Check battery voltage and the CAN connection, then try again.");
                    }

                    Array.Copy(data, 0, image, written, block);
                    written += block;
                    if (KeepAliveEveryBlocks > 0 && (written / _profile.ReadBlockSize) % KeepAliveEveryBlocks == 0)
                    {
                        TryService("0x3E tester present", delegate { _client.TesterPresent(); });
                    }
                    Notify(progress, written, total, "Reading flash");
                }
            }
            return image;
        }

        /// <summary>
        /// Writes an image: erase, request download, then every block of every write segment.
        /// The image must be exactly <see cref="PcmProfile.ImageSize"/> bytes.
        /// </summary>
        public void WriteImage(byte[] image, IProgress<FlashProgress> progress, CancellationToken token)
        {
            if (image == null) throw new ArgumentNullException("image");
            RequireImageSize(image);

            int total = _profile.TotalWriteBytes;
            int sent = 0;

            Erase(progress, token);
            token.ThrowIfCancellationRequested();

            if (!_client.RequestDownload())
            {
                throw new IOException("The PCM refused the download request (0x34).");
            }

            foreach (FlashSegment segment in _profile.WriteSegments)
            {
                for (int address = segment.Address; address < segment.End; address += _profile.WriteBlockSize)
                {
                    token.ThrowIfCancellationRequested();

                    int block = Math.Min(_profile.WriteBlockSize, segment.End - address);
                    int imageOffset = _profile.MapAddressToImageOffset(address);
                    if (imageOffset < 0 || imageOffset + block > image.Length)
                    {
                        throw new IOException("Address 0x" + address.ToString("X6") + " is outside the image.");
                    }
                    byte[] chunk = new byte[block];
                    Array.Copy(image, imageOffset, chunk, 0, block);

                    for (int attempt = 1; attempt <= BlockRetries; attempt++)
                    {
                        try
                        {
                            _client.TransferData(chunk);
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == BlockRetries)
                            {
                                throw new IOException("Block at 0x" + address.ToString("X6") + " failed after " +
                                                      BlockRetries + " attempts: " + ex.Message);
                            }
                            Log("Retrying block 0x" + address.ToString("X6") + " (" + ex.Message + ")");
                            Thread.Sleep(200);
                        }
                    }

                    sent += block;
                    if (KeepAliveEveryBlocks > 0 && (sent / _profile.WriteBlockSize) % KeepAliveEveryBlocks == 0)
                    {
                        TryService("0x3E tester present", delegate { _client.TesterPresent(); });
                    }
                    Notify(progress, sent, total, "Writing flash");
                }
            }

            _client.TransferExit();
            Log("Download finished, clearing diagnostic information ...");
            try { _client.ClearDiagnosticInformation(); } catch (Exception ex) { Log("Clear DTC: " + ex.Message); }
        }

        /// <summary>Reads the flash back and compares it with the image (only the written ranges).</summary>
        public int VerifyImage(byte[] expected, IProgress<FlashProgress> progress, CancellationToken token)
        {
            if (expected == null) throw new ArgumentNullException("expected");

            // A short file used to fail with IndexOutOfRangeException halfway through the compare,
            // i.e. after the PCM had already been unlocked. Say what is wrong before touching it.
            RequireImageSize(expected);
            int total = _profile.TotalWriteBytes;
            int checkedBytes = 0;
            int differences = 0;

            foreach (FlashSegment segment in _profile.WriteSegments)
            {
                for (int address = segment.Address; address < segment.End; address += _profile.ReadBlockSize)
                {
                    token.ThrowIfCancellationRequested();
                    int block = Math.Min(_profile.ReadBlockSize, segment.End - address);
                    byte[] actual = _client.ReadMemory((uint)address, block);
                    int imageOffset = _profile.MapAddressToImageOffset(address);
                    if (imageOffset < 0) continue;
                    for (int i = 0; i < block && i < actual.Length; i++)
                    {
                        if (actual[i] != expected[imageOffset + i]) differences++;
                    }
                    checkedBytes += block;
                    Notify(progress, checkedBytes, total, "Verifying flash");
                }
            }
            return differences;
        }

        /// <summary>Rejects an image that is not the size this PCM needs, before anything is touched.</summary>
        private void RequireImageSize(byte[] image)
        {
            if (image.Length != _profile.ImageSize)
            {
                throw new ArgumentException("Image is " + image.Length + " bytes but this PCM needs " +
                                            _profile.ImageSize + " bytes (" + (_profile.ImageSize / 1024) + " kB).");
            }
        }

        /// <summary>
        /// Why an image must not be written, or null when nothing obvious is wrong with it. A blank
        /// image is the dangerous case: an all-0x00 or all-0xFF megabyte is what a failed read, an
        /// empty file or a padded template produces, and writing one erases a working PCM for
        /// nothing. The GUI and the CLI both ask this before they offer to flash anything.
        /// </summary>
        public static string DescribeWriteProblem(byte[] image)
        {
            if (image == null) return "There is no image to write.";
            if (image.Length == 0) return "The file is empty.";
            if (ImageTools.IsBlank(image))
            {
                return "Every byte of the image is 0x" + image[0].ToString("X2") +
                       ", which means this is a blank image (a failed read or an empty template), " +
                       "not a calibration.";
            }
            return null;
        }

        /// <summary>SHA-256 of an image, handy for build logs and release notes.</summary>
        public static string Hash(byte[] image)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(image);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>A contiguous run of bytes that differs between two images.</summary>
    public sealed class ImageDifference
    {
        public int Offset { get; private set; }
        public int Length { get; private set; }

        public ImageDifference(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        public int End { get { return Offset + Length; } }

        public override string ToString()
        {
            return "0x" + Offset.ToString("X6") + " + 0x" + Length.ToString("X4");
        }
    }

    public static class ImageTools
    {
        /// <summary>
        /// Compares two images and returns the runs of differing bytes, coalescing differences
        /// that are closer together than <paramref name="gapTolerance"/> so a few scattered bytes
        /// become one readable region.
        /// </summary>
        public static IList<ImageDifference> Diff(byte[] a, byte[] b, int gapTolerance)
        {
            if (a == null || b == null) throw new ArgumentNullException(a == null ? "a" : "b");
            int n = Math.Min(a.Length, b.Length);
            List<ImageDifference> regions = new List<ImageDifference>();

            int start = -1;
            int last = -1;
            for (int i = 0; i < n; i++)
            {
                if (a[i] == b[i]) continue;
                if (start < 0)
                {
                    start = i;
                }
                else if (i - last > gapTolerance)
                {
                    regions.Add(new ImageDifference(start, last - start + 1));
                    start = i;
                }
                last = i;
            }
            if (start >= 0) regions.Add(new ImageDifference(start, last - start + 1));

            // Length mismatch shows up as one trailing region rather than being ignored.
            if (a.Length != b.Length)
            {
                int longer = Math.Max(a.Length, b.Length);
                regions.Add(new ImageDifference(n, longer - n));
            }
            return regions;
        }

        /// <summary>Sum of the bytes, modulo 2^32 - the simplest calibration checksum scheme.</summary>
        public static uint Sum32(byte[] data, int offset, int length)
        {
            if (data == null) return 0;
            int end = Math.Min(data.Length, offset + length);
            uint sum = 0;
            for (int i = Math.Max(0, offset); i < end; i++) sum += data[i];
            return sum;
        }

        /// <summary>Sum of the bytes, modulo 2^16.</summary>
        public static ushort Sum16(byte[] data, int offset, int length)
        {
            return (ushort)(Sum32(data, offset, length) & 0xFFFF);
        }

        /// <summary>CRC-32 (IEEE 802.3), the usual non-Ford choice, printed for comparison.</summary>
        public static uint Crc32(byte[] data, int offset, int length)
        {
            if (data == null) return 0;
            uint crc = 0xFFFFFFFFu;
            int end = Math.Min(data.Length, offset + length);
            for (int i = Math.Max(0, offset); i < end; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return ~crc;
        }

        /// <summary>True when the buffer looks like an all-0x00 or all-0xFF (blank) flash.</summary>
        public static bool IsBlank(byte[] image)
        {
            if (image == null || image.Length == 0) return true;
            byte first = image[0];
            if (first != 0x00 && first != 0xFF) return false;
            for (int i = 1; i < image.Length; i++)
            {
                if (image[i] != first) return false;
            }
            return true;
        }

        /// <summary>Finds printable ASCII runs of at least <paramref name="minimumLength"/> characters.</summary>
        public static IList<string> FindPrintableStrings(byte[] image, int minimumLength, int limit)
        {
            List<string> found = new List<string>();
            if (image == null) return found;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < image.Length && found.Count < limit; i++)
            {
                byte b = image[i];
                if (b >= 0x20 && b <= 0x7E)
                {
                    sb.Append((char)b);
                }
                else
                {
                    if (sb.Length >= minimumLength) found.Add("0x" + (i - sb.Length).ToString("X6") + ": " + sb);
                    sb.Length = 0;
                }
            }

            // A run that reaches the end of the image is never terminated by a non printable byte,
            // so flush it here. Without this the last string in a calibration is always lost.
            if (found.Count < limit && sb.Length >= minimumLength)
            {
                found.Add("0x" + (image.Length - sb.Length).ToString("X6") + ": " + sb);
            }
            return found;
        }

        /// <summary>Compares two images and returns the number of differing bytes.</summary>
        public static int CountDifferences(byte[] a, byte[] b)
        {
            if (a == null || b == null) throw new ArgumentNullException(a == null ? "a" : "b");
            int n = Math.Min(a.Length, b.Length);
            int diff = Math.Abs(a.Length - b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) diff++;
            return diff;
        }
    }
}
