using System;
using System.Collections.Generic;
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
            _client.Device.SetProgrammingVoltage(_profile.ProgrammingPin, _profile.ProgrammingVoltage);

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
            if (image.Length != _profile.ImageSize)
            {
                throw new ArgumentException("Image is " + image.Length + " bytes but this PCM needs " +
                                            _profile.ImageSize + " bytes (" + (_profile.ImageSize / 1024) + " kB).");
            }

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

    /// <summary>Helpers for the binary files themselves.</summary>
    public static class ImageTools
    {
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
