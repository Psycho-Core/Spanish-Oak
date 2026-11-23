using System;
using System.Collections.Generic;
using System.IO;
using PsychoFlash.Core.J2534;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.Core.Flashing
{
    /// <summary>
    /// Owns the whole hardware stack (J2534 driver, ISO15765 channel, Ford PCM client and the
    /// flashing service) so the console and the GUI share exactly the same code path.
    /// </summary>
    public sealed class FlashSession : IDisposable
    {
        private J2534Device _device;
        private FordPcmClient _client;
        private FlashService _service;

        public event Action<string> Log;

        public J2534Device Device { get { return _device; } }
        public FordPcmClient Client { get { return _client; } }
        public FlashService Service { get { return _service; } }
        public PcmProfile Profile { get; private set; }
        public ISeedKeyAlgorithm KeyAlgorithm { get; set; }

        /// <summary>
        /// Optional list of key candidates to try, in order, when the PCM refuses the built-in
        /// algorithm (see <see cref="AlgorithmBag"/>). Empty means "use <see cref="KeyAlgorithm"/>".
        /// </summary>
        public IList<ISeedKeyAlgorithm> KeyAlgorithms { get; set; }

        /// <summary>
        /// Unlocks the PCM with <see cref="KeyAlgorithms"/> when one is configured, otherwise with
        /// the single <see cref="KeyAlgorithm"/>. On success the algorithm that worked is kept in
        /// <see cref="KeyAlgorithm"/> so the log and the next operation stay consistent.
        /// </summary>
        public bool UnlockPcm()
        {
            if (_service == null) throw new InvalidOperationException("Not connected.");
            if (KeyAlgorithms != null && KeyAlgorithms.Count > 0)
            {
                ISeedKeyAlgorithm used = _service.UnlockAny(KeyAlgorithms);
                if (used == null) return false;
                KeyAlgorithm = used;
                return true;
            }
            return _service.Unlock(KeyAlgorithm);
        }

        public Action<string> OperatorPrompt { get; set; }

        public FlashSession()
        {
            KeyAlgorithm = SeedKeyAlgorithms.FordSpanishOak;
        }

        private void Write(string message)
        {
            Action<string> handler = Log;
            if (handler != null) handler(message);
        }

        /// <summary>Opens the interface and brings up the ISO15765 channel for the profile.</summary>
        public void Connect(string driverDll, string deviceName, PcmProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            Disconnect();

            Profile = profile;
            Write("Opening J2534 driver: " + driverDll);
            _device = string.IsNullOrEmpty(deviceName)
                ? J2534Device.Open(driverDll)
                : J2534Device.Open(driverDll, deviceName);

            Write("Interface: firmware " + _device.FirmwareVersion + ", DLL " + _device.DllVersion + ", API " + _device.ApiVersion);
            Write("Connecting ISO15765 at " + profile.BaudRate + " bit/s (request 0x" + profile.RequestId.ToString("X3") +
                  ", response 0x" + profile.ResponseId.ToString("X3") + ")");
            _device.ConnectIso15765(profile.BaudRate, profile.RequestId, profile.ResponseId, profile.Use29BitIdentifier);

            _client = new FordPcmClient(_device, profile, Write);
            _service = new FlashService(_client, Write);
            if (OperatorPrompt != null) _service.RequestOperatorAction = OperatorPrompt;
            Write("Ready.");
        }

        /// <summary>Battery voltage reported by the interface, in volts.</summary>
        public double ReadBatteryVoltage()
        {
            if (_device == null) throw new InvalidOperationException("Not connected.");
            return _device.ReadBatteryVoltage() / 1000.0;
        }

        /// <summary>Full flashing sequence: programming mode, unlock, erase, write, verify.</summary>
        public void Flash(byte[] image, bool verify, IProgress<FlashProgress> progress, System.Threading.CancellationToken token)
        {
            if (_service == null) throw new InvalidOperationException("Not connected.");
            Write("=== Flash write started ===");
            _service.EnterProgrammingMode(true);
            try
            {
                if (!UnlockPcm())
                {
                    throw new InvalidOperationException("The PCM did not grant security access. Nothing was written.");
                }
                _service.WriteImage(image, progress, token);
                if (verify)
                {
                    Write("Verifying ...");
                    int diff = _service.VerifyImage(image, progress, token);
                    Write(diff == 0 ? "Verification OK - flash matches the file." : "Verification found " + diff + " differing bytes.");
                    if (diff != 0) throw new IOException("Verification failed: " + diff + " bytes differ.");
                }
                Write("=== Flash write complete ===");
            }
            finally
            {
                _service.LeaveProgrammingMode();
            }
        }

        /// <summary>
        /// Reads the stored trouble codes. Deliberately does not touch FEPS, the programming
        /// session or security access, so it can be used on a running car to find out why it
        /// will not start.
        /// </summary>
        public IList<DiagnosticCode> ReadCodes()
        {
            if (_client == null) throw new InvalidOperationException("Not connected.");
            Write("=== Reading diagnostic trouble codes ===");
            byte[] response = _client.ReadTroubleCodes();
            IList<DiagnosticCode> codes = DiagnosticCodes.Parse(response);
            if (codes.Count == 0)
            {
                Write("No stored trouble codes (raw answer: " + FordPcmClient.BytesToHex(response) + ")");
            }
            else
            {
                foreach (DiagnosticCode code in codes) Write("  " + code.ToString());
                Write(codes.Count + " code(s) stored.");
            }
            return codes;
        }

        /// <summary>0x14 - clears the stored trouble codes.</summary>
        public bool ClearCodes()
        {
            if (_client == null) throw new InvalidOperationException("Not connected.");
            Write("=== Clearing diagnostic trouble codes ===");
            bool ok = _client.ClearDiagnosticInformation();
            Write(ok ? "Trouble codes cleared." : "The PCM did not confirm the clear.");
            return ok;
        }

        /// <summary>The VIN reported by the PCM, or an empty string when it is not readable.</summary>
        public string ReadVin()
        {
            if (_client == null) throw new InvalidOperationException("Not connected.");
            string vin = _client.ReadVehicleIdentification();
            Write("VIN: " + (string.IsNullOrEmpty(vin) ? "(not readable)" : vin));
            return vin;
        }

        /// <summary>Reads the flash and saves it to <paramref name="path"/>.</summary>
        public byte[] ReadToFile(string path, IProgress<FlashProgress> progress, System.Threading.CancellationToken token)
        {
            if (_service == null) throw new InvalidOperationException("Not connected.");
            Write("=== Flash read started ===");
            _service.EnterProgrammingMode(true);
            try
            {
                if (!UnlockPcm())
                {
                    throw new InvalidOperationException("The PCM did not grant security access. The read was aborted.");
                }
                byte[] image = _service.ReadImage(progress, token);
                File.WriteAllBytes(path, image);
                Write("Saved " + image.Length + " bytes to " + path);
                Write("SHA-256: " + FlashService.Hash(image));
                return image;
            }
            finally
            {
                _service.LeaveProgrammingMode();
            }
        }

        /// <summary>Erases the flash without writing anything (recovery when a write was interrupted).</summary>
        public void Erase(IProgress<FlashProgress> progress, System.Threading.CancellationToken token)
        {
            if (_service == null) throw new InvalidOperationException("Not connected.");
            _service.EnterProgrammingMode(true);
            try
            {
                if (!_service.Unlock(KeyAlgorithm)) throw new InvalidOperationException("Security access was not granted.");
                _service.Erase(progress, token);
            }
            finally
            {
                _service.LeaveProgrammingMode();
            }
        }

        public void Disconnect()
        {
            if (_device != null)
            {
                try { _device.Dispose(); } catch (Exception) { }
                _device = null;
                _client = null;
                _service = null;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
