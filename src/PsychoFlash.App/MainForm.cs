using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PsychoFlash.Core.Flashing;
using PsychoFlash.Core.J2534;
using PsychoFlash.Core.Protocol;

namespace PsychoFlash.App
{
    /// <summary>
    /// PsychoFlash main window. All hardware work happens on a background thread; the UI only
    /// touches controls through <see cref="BeginInvoke"/>.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly Color _background = Color.FromArgb(24, 24, 27);
        private readonly Color _panel = Color.FromArgb(32, 32, 36);
        private readonly Color _accent = Color.FromArgb(0, 200, 140);
        private readonly Color _warning = Color.FromArgb(240, 180, 60);
        private readonly Color _text = Color.FromArgb(228, 228, 231);

        private ComboBox _interfaceCombo;
        private ComboBox _profileCombo;
        private Button _refreshButton;
        private Button _connectButton;
        private Button _readButton;
        private Button _writeButton;
        private Button _eraseButton;
        private Button _verifyButton;
        private Button _unlockButton;
        private Button _batteryButton;
        private Button _codesButton;
        private CheckBox _verifyAfterWrite;
        private ProgressBar _progress;
        private Label _progressLabel;
        private Label _interfaceLabel;
        private Label _voltageLabel;
        private Label _vinLabel;
        private TextBox _logBox;

        private FlashSession _session;
        private CancellationTokenSource _cancellation;
        private bool _busy;

        public MainForm()
        {
            BuildUi();
            LoadInterfaces();
            SetConnected(false);
        }

        // ------------------------------------------------------------------- UI setup ---

        private void BuildUi()
        {
            Text = "PsychoFlash - Ford EEC-VI / Spanish Oak PCM Flasher";
            Width = 980;
            Height = 720;
            MinimumSize = new Size(900, 640);
            BackColor = _background;
            ForeColor = _text;
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;

            Label title = new Label();
            title.Text = "PsychoFlash";
            title.Font = new Font("Segoe UI Semibold", 22f);
            title.ForeColor = _accent;
            title.AutoSize = true;
            title.Location = new Point(18, 12);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Spanish Oak 1024k PCM flashing for BF / FG / FG II / FG-X Falcon over SAE J2534";
            subtitle.ForeColor = Color.FromArgb(150, 150, 158);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(24, 52);
            Controls.Add(subtitle);

            GroupBox device = MakeGroup("Interface", new Point(18, 78), new Size(600, 130));

            _interfaceCombo = new ComboBox();
            _interfaceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _interfaceCombo.SetBounds(14, 28, 430, 24);
            device.Controls.Add(_interfaceCombo);

            _refreshButton = MakeButton("Scan", new Point(452, 26), new Size(64, 26), Color.FromArgb(60, 60, 66));
            _refreshButton.Click += delegate { LoadInterfaces(); };
            device.Controls.Add(_refreshButton);

            _connectButton = MakeButton("Connect", new Point(522, 26), new Size(64, 26), _accent);
            _connectButton.ForeColor = Color.Black;
            _connectButton.Click += delegate { ToggleConnect(); };
            device.Controls.Add(_connectButton);

            _interfaceLabel = new Label();
            _interfaceLabel.Text = "Not connected";
            _interfaceLabel.ForeColor = Color.FromArgb(150, 150, 158);
            _interfaceLabel.SetBounds(14, 60, 570, 20);
            device.Controls.Add(_interfaceLabel);

            _voltageLabel = new Label();
            _voltageLabel.Text = "Battery: --.-- V";
            _voltageLabel.SetBounds(14, 84, 200, 20);
            device.Controls.Add(_voltageLabel);

            _batteryButton = MakeButton("Measure", new Point(220, 82), new Size(76, 24), Color.FromArgb(60, 60, 66));
            _batteryButton.Click += delegate { MeasureBattery(); };
            device.Controls.Add(_batteryButton);

            // Diagnostic trouble codes: key on is enough, no FEPS and no programming session, so
            // this is the button to press when the engine will not run.
            _codesButton = MakeButton("Read codes", new Point(306, 82), new Size(110, 24), Color.FromArgb(60, 60, 66));
            _codesButton.Click += delegate { ReadCodes(); };
            device.Controls.Add(_codesButton);

            Controls.Add(device);

            GroupBox pcm = MakeGroup("PCM", new Point(634, 78), new Size(320, 130));

            Label profileLabel = new Label();
            profileLabel.Text = "PCM profile";
            profileLabel.SetBounds(14, 24, 100, 20);
            pcm.Controls.Add(profileLabel);

            _profileCombo = new ComboBox();
            _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _profileCombo.SetBounds(14, 46, 290, 24);
            IList<PcmProfile> profiles = PcmProfile.All();
            foreach (PcmProfile p in profiles) _profileCombo.Items.Add(p.Name);
            _profileCombo.SelectedIndex = 0;
            pcm.Controls.Add(_profileCombo);

            _vinLabel = new Label();
            _vinLabel.Text = "VIN: -";
            _vinLabel.SetBounds(14, 78, 290, 20);
            pcm.Controls.Add(_vinLabel);

            _verifyAfterWrite = new CheckBox();
            _verifyAfterWrite.Text = "Verify after writing";
            _verifyAfterWrite.Checked = true;
            _verifyAfterWrite.SetBounds(14, 100, 200, 22);
            pcm.Controls.Add(_verifyAfterWrite);
            Controls.Add(pcm);

            GroupBox actions = MakeGroup("Operations", new Point(18, 216), new Size(936, 96));
            _readButton = MakeButton("Read flash to file", new Point(14, 30), new Size(160, 40), Color.FromArgb(60, 60, 66));
            _readButton.Click += delegate { ReadFlash(); };
            actions.Controls.Add(_readButton);

            _writeButton = MakeButton("Write flash from file", new Point(184, 30), new Size(170, 40), _accent);
            _writeButton.ForeColor = Color.Black;
            _writeButton.Click += delegate { WriteFlash(); };
            actions.Controls.Add(_writeButton);

            _verifyButton = MakeButton("Verify PCM", new Point(364, 30), new Size(130, 40), Color.FromArgb(60, 60, 66));
            _verifyButton.Click += delegate { VerifyFlash(); };
            actions.Controls.Add(_verifyButton);

            _unlockButton = MakeButton("Test unlock (0x27)", new Point(504, 30), new Size(150, 40), Color.FromArgb(60, 60, 66));
            _unlockButton.Click += delegate { TestUnlock(); };
            actions.Controls.Add(_unlockButton);

            _eraseButton = MakeButton("Erase flash", new Point(664, 30), new Size(120, 40), Color.FromArgb(150, 60, 60));
            _eraseButton.Click += delegate { EraseFlash(); };
            actions.Controls.Add(_eraseButton);

            Button saveLog = MakeButton("Save log", new Point(794, 30), new Size(120, 40), Color.FromArgb(60, 60, 66));
            saveLog.Click += delegate { SaveLog(); };
            actions.Controls.Add(saveLog);
            Controls.Add(actions);

            _progress = new ProgressBar();
            _progress.SetBounds(18, 324, 936, 18);
            _progress.Minimum = 0;
            _progress.Maximum = 1000;
            Controls.Add(_progress);

            _progressLabel = new Label();
            _progressLabel.Text = "Idle";
            _progressLabel.SetBounds(18, 346, 936, 20);
            _progressLabel.ForeColor = Color.FromArgb(150, 150, 158);
            Controls.Add(_progressLabel);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(16, 16, 18);
            _logBox.ForeColor = Color.FromArgb(200, 200, 205);
            _logBox.Font = new Font("Consolas", 9f);
            _logBox.SetBounds(18, 372, 936, 262);
            Controls.Add(_logBox);

            Label footer = new Label();
            footer.Text = "Flashing rewrites the PCM. Keep a battery charger connected and never switch the ignition off mid-write.";
            footer.ForeColor = _warning;
            footer.SetBounds(18, 640, 936, 20);
            Controls.Add(footer);
        }

        private GroupBox MakeGroup(string text, Point location, Size size)
        {
            GroupBox box = new GroupBox();
            box.Text = text;
            box.ForeColor = _text;
            box.BackColor = _panel;
            box.SetBounds(location.X, location.Y, size.Width, size.Height);
            return box;
        }

        private Button MakeButton(string text, Point location, Size size, Color backColor)
        {
            Button button = new Button();
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = backColor;
            button.ForeColor = _text;
            button.SetBounds(location.X, location.Y, size.Width, size.Height);
            return button;
        }

        // ------------------------------------------------------------------ logging ---

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), message);
                return;
            }
            _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }

        private void LogThreadSafe(string message)
        {
            Log(message);
        }

        // ------------------------------------------------------------------ devices ---

        private void LoadInterfaces()
        {
            _interfaceCombo.Items.Clear();
            IList<J2534Library.Registration> found = J2534Library.FindInstalledInterfaces();
            foreach (J2534Library.Registration reg in found)
            {
                _interfaceCombo.Items.Add(reg);
            }
            if (_interfaceCombo.Items.Count > 0)
            {
                _interfaceCombo.SelectedIndex = 0;
                Log("Found " + _interfaceCombo.Items.Count + " J2534 interface(s).");
            }
            else
            {
                Log("No J2534 interface found in the registry. Install the vendor driver (OBDX Pro FT, Tactrix Openport, VCM II ...).");
            }
        }

        private PcmProfile SelectedProfile()
        {
            if (_profileCombo.SelectedIndex == 1) return PcmProfile.BlackOakDual();
            return PcmProfile.SpanishOakFalcon();
        }

        private void ToggleConnect()
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
                SetConnected(false);
                return;
            }

            J2534Library.Registration reg = _interfaceCombo.SelectedItem as J2534Library.Registration;
            if (reg == null)
            {
                MessageBox.Show(this, "No interface selected.", "PsychoFlash", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _session = new FlashSession();
            _session.Log += LogThreadSafe;
            _session.OperatorPrompt += delegate(string message)
            {
                BeginInvoke(new Action(delegate
                {
                    MessageBox.Show(this, message, "PsychoFlash - action required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            };

            try
            {
                SetBusy(true, "Connecting");
                _session.Connect(reg.FunctionLibrary, null, SelectedProfile());
                _interfaceLabel.Text = reg.Name + "  |  firmware " + _session.Device.FirmwareVersion + "  |  DLL " + _session.Device.DllVersion;
                SetConnected(true);
                MeasureBattery();
                ReadVin();
            }
            catch (Exception ex)
            {
                Log("Connection failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "PsychoFlash - connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (_session != null) _session.Dispose();
                _session = null;
                SetConnected(false);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void SetConnected(bool connected)
        {
            bool enabled = connected && !_busy;
            _readButton.Enabled = enabled;
            _writeButton.Enabled = enabled;
            _eraseButton.Enabled = enabled;
            _verifyButton.Enabled = enabled;
            _unlockButton.Enabled = enabled;
            _batteryButton.Enabled = enabled;
            _codesButton.Enabled = enabled;
            _profileCombo.Enabled = !connected;
            _interfaceCombo.Enabled = !connected;
            _refreshButton.Enabled = !connected;
            _connectButton.Text = connected ? "Disconnect" : "Connect";
            if (!connected)
            {
                _interfaceLabel.Text = "Not connected";
                _voltageLabel.Text = "Battery: --.-- V";
                _vinLabel.Text = "VIN: -";
            }
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (status != null) _progressLabel.Text = status;
            bool enabled = !busy;
            _connectButton.Enabled = enabled;
            _readButton.Enabled = enabled && _session != null;
            _writeButton.Enabled = enabled && _session != null;
            _eraseButton.Enabled = enabled && _session != null;
            _verifyButton.Enabled = enabled && _session != null;
            _unlockButton.Enabled = enabled && _session != null;
            _batteryButton.Enabled = enabled && _session != null;
            _codesButton.Enabled = enabled && _session != null;
            _readButton.Enabled = enabled && _session != null;
            if (!busy)
            {
                _progress.Value = 0;
            }
        }

        // ---------------------------------------------------------------- operations ---

        private Progress<FlashProgress> CreateProgress()
        {
            return new Progress<FlashProgress>(delegate(FlashProgress p)
            {
                int value = (int)(p.Percent * 10);
                if (value < 0) value = 0;
                if (value > 1000) value = 1000;
                _progress.Value = value;
                _progressLabel.Text = p.Status + " - " + p.Completed.ToString("N0") + " / " + p.Total.ToString("N0") + " bytes (" + p.Percent.ToString("0.0") + "%)";
            });
        }

        private void MeasureBattery()
        {
            if (_session == null) return;
            try
            {
                double volts = _session.ReadBatteryVoltage();
                _voltageLabel.Text = "Battery: " + volts.ToString("0.00") + " V";
                _voltageLabel.ForeColor = volts < 12.0 ? _warning : _text;
                Log("Battery voltage: " + volts.ToString("0.00") + " V");
            }
            catch (Exception ex)
            {
                Log("Could not read battery voltage: " + ex.Message);
            }
        }

        private void ReadCodes()
        {
            if (_session == null) return;
            RunBackground("Reading trouble codes", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                _session.ReadCodes();
            });
        }

        private void ReadVin()
        {
            if (_session == null) return;
            Task.Run(delegate
            {
                try
                {
                    string vin = _session.Client.ReadVehicleIdentification();
                    BeginInvoke(new Action(delegate
                    {
                        _vinLabel.Text = "VIN: " + (string.IsNullOrEmpty(vin) ? "-" : vin);
                    }));
                }
                catch (Exception)
                {
                    // A locked PCM refuses identifiers; nothing to report.
                }
            });
        }

        private void ReadFlash()
        {
            if (_session == null) return;
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Filter = "Binary flash image (*.bin)|*.bin|All files (*.*)|*.*";
            dialog.FileName = "spanish-oak-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".bin";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string path = dialog.FileName;

            RunBackground("Reading flash", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                if (!ConfirmBattery()) return;
                _session.ReadToFile(path, progress, token);
            });
        }

        private void WriteFlash()
        {
            if (_session == null) return;
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Binary flash image (*.bin)|*.bin|All files (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            byte[] image = File.ReadAllBytes(dialog.FileName);
            PcmProfile profile = SelectedProfile();
            if (image.Length != profile.ImageSize)
            {
                MessageBox.Show(this,
                    "This file is " + image.Length + " bytes, but " + profile.Name + " needs " + profile.ImageSize + " bytes.",
                    "PsychoFlash - wrong image size", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string message = "About to erase and reprogram this PCM with:\r\n\r\n" + Path.GetFileName(dialog.FileName) +
                             "\r\n" + image.Length + " bytes\r\nSHA-256: " + FlashService.Hash(image) +
                             "\r\n\r\nDo not interrupt the process.";
            if (MessageBox.Show(this, message, "PsychoFlash - confirm write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            bool verify = _verifyAfterWrite.Checked;
            RunBackground("Writing flash", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                if (!ConfirmBattery()) return;
                _session.Flash(image, verify, progress, token);
            });
        }

        private void VerifyFlash()
        {
            if (_session == null) return;
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Binary flash image (*.bin)|*.bin|All files (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            byte[] image = File.ReadAllBytes(dialog.FileName);

            RunBackground("Verifying flash", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                _session.Service.EnterProgrammingMode(true);
                try
                {
                    if (!_session.UnlockPcm())
                    {
                        Log("Security access not granted - cannot verify.");
                        return;
                    }
                    int diff = _session.Service.VerifyImage(image, progress, token);
                    Log(diff == 0 ? "Verification OK: PCM matches the file." : "Verification FAILED: " + diff + " bytes differ.");
                    if (diff == 0)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            MessageBox.Show(this, "PCM matches the file.", "PsychoFlash - verify", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                    }
                }
                finally
                {
                    _session.Service.LeaveProgrammingMode();
                }
            });
        }

        private void EraseFlash()
        {
            if (_session == null) return;
            if (MessageBox.Show(this,
                    "This erases the PCM flash. The vehicle will not start until a valid image is written back.\r\n\r\nContinue?",
                    "PsychoFlash - erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            RunBackground("Erasing flash", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                _session.Erase(progress, token);
            });
        }

        private void TestUnlock()
        {
            if (_session == null) return;
            RunBackground("Testing security access", delegate(CancellationToken token, Progress<FlashProgress> progress)
            {
                _session.Service.EnterProgrammingMode(true);
                try
                {
                    bool granted = _session.UnlockPcm();
                    Log(granted ? "Security access granted (PCM unlocked)." : "Security access refused.");
                }
                finally
                {
                    _session.Service.LeaveProgrammingMode();
                }
            });
        }

        /// <summary>Asks the operator to confirm when the battery is too low to flash safely.</summary>
        private bool ConfirmBattery()
        {
            try
            {
                double volts = _session.ReadBatteryVoltage();
                Log("Battery voltage: " + volts.ToString("0.00") + " V");
                if (volts >= 11.8) return true;

                bool answer = false;
                Invoke(new Action(delegate
                {
                    answer = MessageBox.Show(this,
                        "Battery voltage is only " + volts.ToString("0.00") + " V.\r\n" +
                        "Flashing on a low battery can brick the PCM - connect a charger first.\r\n\r\nContinue anyway?",
                        "PsychoFlash - low battery", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                }));
                return answer;
            }
            catch (Exception ex)
            {
                Log("Battery check skipped: " + ex.Message);
                return true;
            }
        }

        private void RunBackground(string status, Action<CancellationToken, Progress<FlashProgress>> work)
        {
            if (_session == null) return;
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            SetBusy(true, status);
            Log("--- " + status + " ---");

            Progress<FlashProgress> progress = CreateProgress();

            Task.Run(delegate
            {
                try
                {
                    work(token, progress);
                    Log(status + " finished.");
                }
                catch (OperationCanceledException)
                {
                    Log(status + " cancelled.");
                }
                catch (Exception ex)
                {
                    Log("ERROR: " + ex.Message);
                    BeginInvoke(new Action(delegate
                    {
                        MessageBox.Show(this, ex.Message, "PsychoFlash - " + status, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    BeginInvoke(new Action(delegate { SetBusy(false, "Idle"); }));
                }
            });
        }

        private void SaveLog()
        {
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.FileName = "psychoflash-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, _logBox.Text, Encoding.UTF8);
            Log("Log saved to " + dialog.FileName);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                DialogResult result = MessageBox.Show(this, "An operation is still running. Close anyway?",
                    "PsychoFlash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                if (_cancellation != null) _cancellation.Cancel();
            }
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
            base.OnFormClosing(e);
        }
    }
}
