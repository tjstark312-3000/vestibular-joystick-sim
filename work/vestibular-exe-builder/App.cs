using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VestibularJoystickSim
{
    internal static class App
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return PacketBuilder.SelfTest() && MainForm.SelfTest() ? 0 : 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }

    internal sealed class MainForm : Form
    {
        private const int SerialBaud = 9600;
        private const double Deadzone = 0.01;
        // These constants mirror VMocion-main\legacy_demo-main\gvs.py.
        private const double GvsMaxRate = 3.14 / 4.0;
        private const double GvsMaxRamp = 0.03;
        private const double PacketValueToVolts = 0.15;
        private const double GvsMaxCurrentMilliamp = 2.0;
        private const double DefaultGain = 10.0;
        private const double MaxGain = 100000.0;
        private const double SerialSendIntervalSeconds = 0.025;
        private const int MsfsSimConnectMessage = 0x0402;

        private readonly PhysicalStickView stickView;
        private readonly PhysicalJoystickInput physicalInput;
        private readonly MsfsSimConnectInput msfsInput;
        private readonly ForzaUdpTelemetryInput forzaInput;
        private readonly HeadView headView;
        private readonly Timer timer;
        private readonly ComboBox portCombo;
        private readonly ComboBox protocolCombo;
        private readonly CheckBox msfsPhysicsCheck;
        private readonly CheckBox forzaPhysicsCheck;
        private readonly Button connectButton;
        private readonly Button armButton;
        private readonly Button pauseButton;
        private readonly Button resetButton;
        private readonly Button refreshButton;
        private readonly Button calLeftButton;
        private readonly Button calRightButton;
        private readonly TrackBar gainSlider;
        private readonly Label gainValueLabel;
        private readonly Label statusLabel;
        private readonly Label yawLabel;
        private readonly Label pitchLabel;
        private readonly Label rollLabel;
        private readonly Label pRateLabel;
        private readonly Label qRateLabel;
        private readonly Label rRateLabel;
        private readonly Label commandLabel;
        private readonly Label dac1Label;
        private readonly Label dac2Label;
        private readonly Label dac3Label;
        private readonly Label dac4Label;
        private readonly Label packetLabel;
        private readonly Label lastPacketLabel;
        private readonly Label inputLabel;

        private SerialPort serialPort;
        private DateTime lastTick;
        private DateTime lastSerialSend = DateTime.MinValue;
        private double yaw;
        private double pitch;
        private double roll;
        private double pRate;
        private double qRate;
        private double rRate;
        private double inputX;
        private double inputY;
        private bool physicalInputConnected;
        private int physicalInputIndex = -1;
        private string physicalInputSource = "No left stick";
        private MotionSnapshot msfsMotion = MotionSnapshot.Empty("MSFS physics off");
        private string msfsInputStatus = "MSFS physics off";
        private MotionSnapshot forzaMotion = MotionSnapshot.Empty("Forza physics off");
        private string forzaInputStatus = "Forza physics off";
        private double commandedP;
        private double commandedQ;
        private double commandedR;
        private readonly double[] packetValues = new double[] { 0.0, 0.0, 0.0, 0.0 };
        private readonly int[] currentBytes = new int[] { 128, 128, 128, 128 };
        private readonly double[] dacValues = new double[] { 0.0, 0.0, 0.0, 0.0 };
        private string lastPacketText = "-";
        private double gain = DefaultGain;
        private bool paused;
        private bool armed;
        private int packetCount;
        private int calibrationDirection;
        private int calibrationStep;
        private DateTime calibrationStart;

        public MainForm()
        {
            Text = "Vestibular Joystick Simulation";
            Width = 1180;
            Height = 760;
            MinimumSize = new Size(980, 640);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.0f);
            StartPosition = FormStartPosition.CenterScreen;
            physicalInput = new PhysicalJoystickInput();
            msfsInput = new MsfsSimConnectInput(MsfsSimConnectMessage);
            forzaInput = new ForzaUdpTelemetryInput();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.RowCount = 2;
            root.ColumnCount = 1;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Padding = new Padding(18);
            root.BackColor = Theme.Background;
            Controls.Add(root);

            Panel top = new Panel();
            top.Dock = DockStyle.Fill;
            top.Padding = new Padding(16, 12, 16, 12);
            top.BackColor = Theme.Surface;
            top.Paint += PaintPanelBorder;
            root.Controls.Add(top, 0, 0);

            Label title = new Label();
            title.Text = "Vestibular Joystick Simulation";
            title.AutoSize = true;
            title.Font = new Font(Font.FontFamily, 15.0f, FontStyle.Bold);
            title.Location = new Point(18, 16);
            title.ForeColor = Theme.Text;
            top.Controls.Add(title);

            Label safety = new Label();
            safety.Text = "Native VMocion controller. Disarmed on launch. Stop if discomfort, dizziness, or nausea occurs.";
            safety.AutoSize = false;
            safety.Width = 520;
            safety.Height = 36;
            safety.Location = new Point(19, 49);
            safety.ForeColor = Theme.Muted;
            top.Controls.Add(safety);

            FlowLayoutPanel topControls = new FlowLayoutPanel();
            topControls.Dock = DockStyle.Right;
            topControls.Width = 690;
            topControls.FlowDirection = FlowDirection.LeftToRight;
            topControls.WrapContents = true;
            topControls.Padding = new Padding(0);
            top.Controls.Add(topControls);

            topControls.Controls.Add(MakeSmallLabel("Gain"));
            gainSlider = new TrackBar();
            gainSlider.Minimum = 50;
            gainSlider.Maximum = (int)(MaxGain * 100.0);
            gainSlider.Value = (int)(DefaultGain * 100.0);
            gainSlider.TickFrequency = 1000000;
            gainSlider.Width = 130;
            gainSlider.BackColor = Theme.Surface;
            gainSlider.Scroll += GainSliderScroll;
            topControls.Controls.Add(gainSlider);

            gainValueLabel = MakeSmallLabel(DefaultGain.ToString("0.00"));
            gainValueLabel.Width = 92;
            topControls.Controls.Add(gainValueLabel);

            msfsPhysicsCheck = MakeCheckBox("MSFS physics");
            msfsPhysicsCheck.CheckedChanged += delegate
            {
                if (msfsPhysicsCheck.Checked && forzaPhysicsCheck != null && forzaPhysicsCheck.Checked)
                {
                    forzaPhysicsCheck.Checked = false;
                }

                msfsInput.ResetStatus();
                SendNeutralOutput();
                UpdateUi();
            };
            topControls.Controls.Add(msfsPhysicsCheck);

            forzaPhysicsCheck = MakeCheckBox("Forza physics");
            forzaPhysicsCheck.CheckedChanged += delegate
            {
                if (forzaPhysicsCheck.Checked && msfsPhysicsCheck != null && msfsPhysicsCheck.Checked)
                {
                    msfsPhysicsCheck.Checked = false;
                }

                forzaInput.ResetStatus();
                SendNeutralOutput();
                UpdateUi();
            };
            topControls.Controls.Add(forzaPhysicsCheck);

            protocolCombo = MakeCombo();
            protocolCombo.Items.Add("Legacy gvs.py UART 9600");
            protocolCombo.SelectedIndex = 0;
            protocolCombo.Width = 205;
            topControls.Controls.Add(protocolCombo);

            portCombo = MakeCombo();
            portCombo.Width = 92;
            topControls.Controls.Add(portCombo);

            refreshButton = MakeButton("Refresh");
            refreshButton.Click += delegate { RefreshPorts(); };
            topControls.Controls.Add(refreshButton);

            connectButton = MakeButton("Connect");
            connectButton.Click += ConnectButtonClick;
            topControls.Controls.Add(connectButton);

            armButton = MakeButton("Arm");
            armButton.Enabled = false;
            armButton.Click += ArmButtonClick;
            topControls.Controls.Add(armButton);

            calLeftButton = MakeButton("Cal Left");
            calLeftButton.Click += delegate { StartCalibration(1); };
            topControls.Controls.Add(calLeftButton);

            calRightButton = MakeButton("Cal Right");
            calRightButton.Click += delegate { StartCalibration(-1); };
            topControls.Controls.Add(calRightButton);

            pauseButton = MakeButton("Pause");
            pauseButton.Click += PauseButtonClick;
            topControls.Controls.Add(pauseButton);

            resetButton = MakeButton("Reset");
            resetButton.Click += ResetButtonClick;
            topControls.Controls.Add(resetButton);

            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.ColumnCount = 3;
            workspace.RowCount = 1;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            workspace.Padding = new Padding(0, 16, 0, 0);
            root.Controls.Add(workspace, 0, 1);

            Panel leftPanel = MakePanel();
            leftPanel.Padding = new Padding(16);
            workspace.Controls.Add(leftPanel, 0, 0);

            Label leftTitle = MakeSectionTitle("PHYSICAL LEFT STICK");
            leftTitle.Dock = DockStyle.Top;
            leftPanel.Controls.Add(leftTitle);

            inputLabel = MakeMetric("Input", "Waiting for XInput left stick");
            inputLabel.Dock = DockStyle.Bottom;
            leftPanel.Controls.Add(inputLabel);

            stickView = new PhysicalStickView();
            stickView.Dock = DockStyle.Fill;
            stickView.Margin = new Padding(0, 18, 0, 18);
            leftPanel.Controls.Add(stickView);
            stickView.BringToFront();

            headView = new HeadView();
            headView.Dock = DockStyle.Fill;
            headView.Margin = new Padding(16, 0, 16, 0);
            workspace.Controls.Add(headView, 1, 0);

            Panel rightPanel = MakePanel();
            rightPanel.Padding = new Padding(16);
            workspace.Controls.Add(rightPanel, 2, 0);

            TableLayoutPanel metrics = new TableLayoutPanel();
            metrics.Dock = DockStyle.Fill;
            metrics.RowCount = 16;
            metrics.ColumnCount = 1;
            for (int i = 0; i < 16; i++)
            {
                metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 40 : 42));
            }
            rightPanel.Controls.Add(metrics);

            Label telemetry = MakeSectionTitle("TELEMETRY");
            statusLabel = MakeSmallLabel("No serial");
            Panel telemetryHeader = new Panel();
            telemetryHeader.Dock = DockStyle.Fill;
            telemetry.Dock = DockStyle.Left;
            statusLabel.Dock = DockStyle.Right;
            telemetryHeader.Controls.Add(telemetry);
            telemetryHeader.Controls.Add(statusLabel);
            metrics.Controls.Add(telemetryHeader);

            yawLabel = AddMetric(metrics, "Yaw", "0.0 deg");
            pitchLabel = AddMetric(metrics, "Pitch", "0.0 deg");
            rollLabel = AddMetric(metrics, "Roll", "0.0 deg");
            pRateLabel = AddMetric(metrics, "p roll rate", "0.000 rad/s");
            qRateLabel = AddMetric(metrics, "q pitch rate", "0.000 rad/s");
            rRateLabel = AddMetric(metrics, "r yaw rate", "0.000 rad/s");
            commandLabel = AddMetric(metrics, "Command", "P 0.000 Q 0.000 R 0.000");
            dac1Label = AddMetric(metrics, "DAC 1", "byte 128");
            dac2Label = AddMetric(metrics, "DAC 2", "byte 128");
            dac3Label = AddMetric(metrics, "DAC 3", "byte 128");
            dac4Label = AddMetric(metrics, "DAC 4", "byte 128");
            packetLabel = AddMetric(metrics, "Packets", "0");
            lastPacketLabel = AddMetric(metrics, "Last TX", "-");

            Label note = new Label();
            note.Dock = DockStyle.Fill;
            note.Text = "Arm sends live VMocion output to the selected serial device.";
            note.ForeColor = Theme.Coral;
            note.Padding = new Padding(0, 8, 0, 0);
            metrics.Controls.Add(note);

            lastTick = DateTime.UtcNow;
            timer = new Timer();
            timer.Interval = 25;
            timer.Tick += TimerTick;
            timer.Start();

            RefreshPorts();
            UpdateUi();
        }

        public static bool SelfTest()
        {
            return IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, 0.0)), new int[] { 128, 128, 128, 128 }) &&
                   PacketValuesEqual(CurrentBytesToDacVolts(new int[] { 128, 128, 128, 128 }), new double[] { 0.0, 0.0, 0.0, 0.0 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(GvsMaxRate, 0.0, 0.0)), new int[] { 28, 28, 228, 228 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, GvsMaxRate, 0.0)), new int[] { 228, 28, 28, 228 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, GvsMaxRate)), new int[] { 28, 228, 28, 228 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(-GvsMaxRate, 0.0, 0.0)), new int[] { 228, 228, 28, 28 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, -GvsMaxRate, 0.0)), new int[] { 28, 228, 228, 28 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, -GvsMaxRate)), new int[] { 228, 28, 228, 28 }) &&
                   PacketValuesEqual(CurrentBytesToPacketValues(new int[] { 28, 28, 228, 228 }), new double[] { -100.0, -100.0, 100.0, 100.0 }) &&
                   PacketValuesEqual(CurrentBytesToDacVolts(new int[] { 28, 28, 228, 228 }), new double[] { -15.0, -15.0, 15.0, 15.0 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(1.0, 0.0, 1.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 1.0, 1.0, 0, 0.0), new double[] { 0.0, GvsMaxRate, 0.0 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.5, 0.0, 1.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate * 0.5 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.001, 0.0, 100000.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate }) &&
                   PacketValuesEqual(JoystickToCommandedRates(1.0, 1.0, 100000.0, 0, 0.0), new double[] { 0.0, GvsMaxRate, GvsMaxRate }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 0.0, 1.0, 1, 1.0), new double[] { GvsMaxRate, 0.0, 0.0 }) &&
                   PacketValuesEqual(MsfsMotionToCommandedRates(MotionSnapshot.Empty("MSFS off"), 100000.0), new double[] { 0.0, 0.0, 0.0 }) &&
                   PacketValuesEqual(MsfsMotionToCommandedRates(MotionSnapshot.Fresh("MSFS", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0), 1.0), new double[] { 0.1, 0.2, 0.3 }) &&
                   PacketValuesEqual(MsfsMotionToCommandedRates(MotionSnapshot.Fresh("MSFS", 2.0, -2.0, 2.0, 0.0, 0.0, 0.0), 1.0), new double[] { GvsMaxRate, -GvsMaxRate, GvsMaxRate }) &&
                   PacketValuesEqual(MsfsControlToCommandedRates(MotionSnapshot.Fresh("MSFS", 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.5, -0.25), 1.0), new double[] { 0.0, -GvsMaxRate * 0.25, GvsMaxRate * 0.5 }) &&
                   PacketValuesEqual(MsfsControlToCommandedRates(MotionSnapshot.Fresh("MSFS", 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, -2.0), 1.0), new double[] { 0.0, -GvsMaxRate, GvsMaxRate }) &&
                   Math.Abs(NormalizeMsfsControlPosition(16384.0) - 1.0) < 0.0001 &&
                   Math.Abs(NormalizeMsfsControlPosition(-16384.0) + 1.0) < 0.0001 &&
                   Math.Abs(NormalizeMsfsControlPosition(0.5) - 0.5) < 0.0001 &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0), 1.0), new double[] { 0.1, 0.2, 0.3 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 2.0, -2.0, 2.0, 0.0, 0.0, 0.0), 1.0), new double[] { GvsMaxRate, -GvsMaxRate, GvsMaxRate }) &&
                   ForzaUdpTelemetryInput.SelfTest();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SafeDisarmAndClose();
            forzaInput.Dispose();
            msfsInput.Dispose();
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (msfsInput != null)
            {
                msfsInput.HandleWindowMessage(m.Msg);
            }

            base.WndProc(ref m);
        }

        private void TimerTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double dt = Math.Min((now - lastTick).TotalSeconds, 0.05);
            lastTick = now;
            UpdatePhysicalStickInput();
            UpdateMsfsInput();
            UpdateForzaInput();

            if (!paused)
            {
                double visualGain = Math.Min(gain, 4.0);
                double targetYaw;
                double targetPitch;
                double targetRoll;
                if (UseMsfsPhysics() && msfsMotion.IsFresh)
                {
                    double controlX = ActiveControlX();
                    double controlY = ActiveControlY();
                    targetYaw = Clamp((msfsMotion.R * 42.0) + (controlX * 20.0 * visualGain), -65.0, 65.0);
                    targetPitch = Clamp(msfsMotion.PitchDeg + (controlY * 14.0 * visualGain), -55.0, 55.0);
                    targetRoll = Clamp(msfsMotion.RollDeg + (controlX * 8.0 * visualGain), -28.0, 28.0);
                }
                else if (UseForzaPhysics() && forzaMotion.IsFresh)
                {
                    double controlX = ApplyDeadzone(inputX);
                    double controlY = ApplyDeadzone(inputY);
                    targetYaw = Clamp((forzaMotion.R * 42.0) + (controlX * 20.0 * visualGain), -65.0, 65.0);
                    targetPitch = Clamp(forzaMotion.PitchDeg + (controlY * 14.0 * visualGain), -55.0, 55.0);
                    targetRoll = Clamp(forzaMotion.RollDeg + (controlX * 8.0 * visualGain), -28.0, 28.0);
                }
                else
                {
                    targetYaw = Clamp(ApplyDeadzone(inputX) * 48.0 * visualGain, -65.0, 65.0);
                    targetPitch = Clamp(ApplyDeadzone(inputY) * 38.0 * visualGain, -55.0, 55.0);
                    targetRoll = Clamp(ApplyDeadzone(inputX) * 16.0 * visualGain, -28.0, 28.0);
                }
                double response = 1.0 - Math.Pow(0.001, dt);

                yaw = Lerp(yaw, targetYaw, response);
                pitch = Lerp(pitch, targetPitch, response);
                roll = Lerp(roll, targetRoll, response);
            }

            UpdateVestibularOutput();
            SendVestibularOutput();
            UpdateUi();
        }

        private void UpdateVestibularOutput()
        {
            double activeX = paused ? 0.0 : ApplyDeadzone(inputX);
            double activeY = paused ? 0.0 : ApplyDeadzone(inputY);
            double calibrationIntensity = GetCalibrationIntensity();

            double[] commanded = BuildCommandedRates(activeX, activeY, calibrationIntensity);
            commandedP = commanded[0];
            commandedQ = commanded[1];
            commandedR = commanded[2];
            double[] headPqr = RotateRates(commandedP, commandedQ, commandedR, roll, pitch, yaw);

            pRate += Clamp(headPqr[0] - pRate, -GvsMaxRamp, GvsMaxRamp);
            qRate += Clamp(headPqr[1] - qRate, -GvsMaxRamp, GvsMaxRamp);
            rRate += Clamp(headPqr[2] - rRate, -GvsMaxRamp, GvsMaxRamp);

            double[] currents = PqrToCodeMatrixCurrents(pRate, qRate, rRate);
            int[] bytes = CurrentsToUartBytes(currents);
            double[] values = CurrentBytesToPacketValues(bytes);
            double[] volts = CurrentBytesToDacVolts(bytes);
            for (int i = 0; i < 4; i++)
            {
                packetValues[i] = values[i];
                currentBytes[i] = bytes[i];
                dacValues[i] = volts[i];
            }
        }

        private void SendVestibularOutput()
        {
            if (!armed || serialPort == null || !serialPort.IsOpen)
            {
                return;
            }

            try
            {
                DateTime now = DateTime.UtcNow;
                if ((now - lastSerialSend).TotalSeconds < SerialSendIntervalSeconds)
                {
                    return;
                }

                WritePacket(PacketBuilder.UartSignal(new int[] { 10, currentBytes[0], currentBytes[1], currentBytes[2], currentBytes[3] }));
                lastSerialSend = now;
            }
            catch (Exception ex)
            {
                armed = false;
                SetStatus("Serial error - disarmed");
                MessageBox.Show("Serial write failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void WritePacket(byte[] packet)
        {
            serialPort.Write(packet, 0, packet.Length);
            packetCount++;
            lastPacketText = PacketToPreview(packet);
        }

        private void SendNeutralOutput()
        {
            SendNeutralOutput(true);
        }

        private void SendNeutralOutput(bool ignoreWriteErrors)
        {
            pRate = 0.0;
            qRate = 0.0;
            rRate = 0.0;
            for (int i = 0; i < 4; i++)
            {
                packetValues[i] = 0.0;
                currentBytes[i] = 128;
                dacValues[i] = 0.0;
            }
            commandedP = 0.0;
            commandedQ = 0.0;
            commandedR = 0.0;

            if (serialPort == null || !serialPort.IsOpen)
            {
                return;
            }

            try
            {
                WritePacket(PacketBuilder.UartSignal(new int[] { 10, 128, 128, 128, 128 }));
                lastSerialSend = DateTime.UtcNow;
            }
            catch (Exception)
            {
                if (!ignoreWriteErrors)
                {
                    throw;
                }

                lastPacketText = "Neutral TX failed";
                SetStatus("Neutral TX failed");
                // Closing should continue even if the device disappears.
            }
        }

        private void SetHardwareMode(bool enabled, bool ignoreWriteErrors)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                return;
            }

            try
            {
                WritePacket(PacketBuilder.UartSignal(new int[] { enabled ? 2 : 1 }));
                lastSerialSend = DateTime.MinValue;
            }
            catch (Exception)
            {
                if (!ignoreWriteErrors)
                {
                    throw;
                }

                lastPacketText = "Mode TX failed";
                SetStatus("Mode TX failed");
            }
        }

        private void ConnectButtonClick(object sender, EventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                SafeDisarmAndClose();
                return;
            }

            if (portCombo.SelectedItem == null)
            {
                MessageBox.Show("Select a COM port first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                serialPort = new SerialPort(portCombo.SelectedItem.ToString(), SerialBaud);
                serialPort.DataBits = 8;
                serialPort.Parity = Parity.None;
                serialPort.StopBits = StopBits.One;
                serialPort.Handshake = Handshake.None;
                serialPort.DtrEnable = false;
                serialPort.RtsEnable = false;
                serialPort.WriteBufferSize = 4096;
                serialPort.WriteTimeout = 1000;
                serialPort.ReadTimeout = 250;
                serialPort.Open();
                packetCount = 0;
                lastSerialSend = DateTime.MinValue;
                connectButton.Text = "Disconnect";
                protocolCombo.Enabled = false;
                portCombo.Enabled = false;
                armButton.Enabled = true;
                SetStatus("Connected");
            }
            catch (Exception ex)
            {
                serialPort = null;
                SetStatus("Connect failed");
                MessageBox.Show("Could not open serial port: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            UpdateUi();
        }

        private void UpdatePhysicalStickInput()
        {
            double x;
            double y;
            int controllerIndex;
            string source;
            bool connected = physicalInput.TryReadLeftStick(out x, out y, out controllerIndex, out source);
            physicalInputConnected = connected;
            physicalInputIndex = connected ? controllerIndex : -1;
            physicalInputSource = connected ? source : "No left stick";
            inputX = connected ? x : 0.0;
            inputY = connected ? y : 0.0;
        }

        private void UpdateMsfsInput()
        {
            if (!UseMsfsPhysics())
            {
                msfsMotion = MotionSnapshot.Empty("MSFS physics off");
                msfsInputStatus = "MSFS physics off";
                return;
            }

            msfsMotion = msfsInput.Poll(Handle);
            msfsInputStatus = msfsMotion.Source;
        }

        private void UpdateForzaInput()
        {
            if (!UseForzaPhysics())
            {
                forzaMotion = MotionSnapshot.Empty("Forza physics off");
                forzaInputStatus = "Forza physics off";
                return;
            }

            forzaMotion = forzaInput.Poll();
            forzaInputStatus = forzaMotion.Source;
        }

        private double[] BuildCommandedRates(double activeX, double activeY, double calibrationIntensity)
        {
            if (calibrationDirection != 0)
            {
                return JoystickToCommandedRates(activeX, activeY, gain, calibrationDirection, calibrationIntensity);
            }

            double[] stickCommand = JoystickToCommandedRates(activeX, activeY, gain, 0, 0.0);
            if (UseMsfsPhysics() && msfsMotion.IsFresh)
            {
                double[] controlCommand = msfsMotion.HasControls ? MsfsControlToCommandedRates(msfsMotion, gain) : stickCommand;
                double[] msfsCommand = MsfsMotionToCommandedRates(msfsMotion, gain);
                return new double[]
                {
                    Clamp(controlCommand[0] + msfsCommand[0], -GvsMaxRate, GvsMaxRate),
                    Clamp(controlCommand[1] + msfsCommand[1], -GvsMaxRate, GvsMaxRate),
                    Clamp(controlCommand[2] + msfsCommand[2], -GvsMaxRate, GvsMaxRate)
                };
            }

            if (UseForzaPhysics() && forzaMotion.IsFresh)
            {
                double[] forzaCommand = PhysicsMotionToCommandedRates(forzaMotion, gain);
                return new double[]
                {
                    Clamp(stickCommand[0] + forzaCommand[0], -GvsMaxRate, GvsMaxRate),
                    Clamp(stickCommand[1] + forzaCommand[1], -GvsMaxRate, GvsMaxRate),
                    Clamp(stickCommand[2] + forzaCommand[2], -GvsMaxRate, GvsMaxRate)
                };
            }

            return stickCommand;
        }

        private bool UseMsfsPhysics()
        {
            return msfsPhysicsCheck != null && msfsPhysicsCheck.Checked;
        }

        private bool UseForzaPhysics()
        {
            return forzaPhysicsCheck != null && forzaPhysicsCheck.Checked;
        }

        private double ActiveControlX()
        {
            if (UseMsfsPhysics() && msfsMotion.IsFresh && msfsMotion.HasControls)
            {
                return ApplyDeadzone(msfsMotion.ControlX);
            }

            return ApplyDeadzone(inputX);
        }

        private double ActiveControlY()
        {
            if (UseMsfsPhysics() && msfsMotion.IsFresh && msfsMotion.HasControls)
            {
                return ApplyDeadzone(msfsMotion.ControlY);
            }

            return ApplyDeadzone(inputY);
        }

        private void ArmButtonClick(object sender, EventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                return;
            }

            if (armed)
            {
                try
                {
                    armed = false;
                    SendNeutralOutput(false);
                    SetHardwareMode(false, false);
                    SetStatus("Connected");
                }
                catch (Exception ex)
                {
                    SetStatus("Disarm error");
                    MessageBox.Show("Could not disarm output: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                try
                {
                    SetHardwareMode(true, false);
                    SendNeutralOutput(false);

                    armed = true;
                    SetStatus("Output armed - legacy gvs.py");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not arm output: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            UpdateUi();
        }

        private void PauseButtonClick(object sender, EventArgs e)
        {
            paused = !paused;
            if (paused)
            {
                StopCalibration();
            }
            pauseButton.Text = paused ? "Resume" : "Pause";
            UpdateUi();
        }

        private void ResetButtonClick(object sender, EventArgs e)
        {
            yaw = 0.0;
            pitch = 0.0;
            roll = 0.0;
            StopCalibration();
            SendNeutralOutput();
            UpdateUi();
        }

        private void GainSliderScroll(object sender, EventArgs e)
        {
            gain = gainSlider.Value / 100.0;
            gainValueLabel.Text = gain.ToString("0.00");
        }

        private void StartCalibration(int direction)
        {
            calibrationDirection = direction < 0 ? -1 : 1;
            calibrationStep = 0;
            calibrationStart = DateTime.UtcNow;
            SetStatus(direction > 0 ? "Cal left" : "Cal right");
        }

        private void StopCalibration()
        {
            calibrationDirection = 0;
            calibrationStep = 0;
        }

        private double GetCalibrationIntensity()
        {
            if (calibrationDirection == 0)
            {
                return 0.0;
            }

            double elapsed = (DateTime.UtcNow - calibrationStart).TotalSeconds;
            int step = calibrationStep++;
            if (step < 34)
            {
                return step * 0.03;
            }

            if (step < 68)
            {
                return 1.0 - ((step - 34) * 0.03);
            }

            if (elapsed >= 5.0)
            {
                StopCalibration();
                SetStatus(armed ? "Output armed - legacy gvs.py" : "Ready");
            }

            return 0.0;
        }

        private void RefreshPorts()
        {
            string previous = portCombo.SelectedItem == null ? null : portCombo.SelectedItem.ToString();
            portCombo.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, ComparePortNames);
            foreach (string port in ports)
            {
                portCombo.Items.Add(port);
            }

            string preferred = ChoosePreferredPort(ports, previous);
            if (preferred != null && portCombo.Items.Contains(preferred))
            {
                portCombo.SelectedItem = preferred;
            }

            if (serialPort == null || !serialPort.IsOpen)
            {
                SetStatus(portCombo.Items.Count == 0 ? "No serial" : "Ready");
            }
        }

        private void SafeDisarmAndClose()
        {
            armed = false;
            SendNeutralOutput();
            SetHardwareMode(false, true);

            if (serialPort != null)
            {
                try
                {
                    serialPort.Close();
                }
                catch
                {
                }
                serialPort.Dispose();
                serialPort = null;
            }

            connectButton.Text = "Connect";
            armButton.Enabled = false;
            protocolCombo.Enabled = true;
            portCombo.Enabled = true;
            SetStatus("Ready");
            UpdateUi();
        }

        private void UpdateUi()
        {
            double displayX = ActiveControlX();
            double displayY = ActiveControlY();
            double intensity = Math.Min(1.0, Math.Sqrt(displayX * displayX + displayY * displayY));
            string controlSource = UseMsfsPhysics() && msfsMotion.IsFresh && msfsMotion.HasControls ? "MSFS controls" : physicalInputSource;
            string activeInput = controlSource;
            if (UseMsfsPhysics())
            {
                activeInput = msfsInputStatus + " + " + controlSource;
            }
            else if (UseForzaPhysics())
            {
                activeInput = forzaInputStatus + " + " + physicalInputSource;
            }
            inputLabel.Text = "Input: " + activeInput + "   Yaw " + Math.Round(Math.Abs(displayX) * 100.0) +
                "%   Pitch " + Math.Round(Math.Abs(displayY) * 100.0) +
                "%   Signal " + Math.Round(intensity * 100.0) + "%";

            yawLabel.Text = "Yaw: " + yaw.ToString("0.0") + " deg";
            pitchLabel.Text = "Pitch: " + pitch.ToString("0.0") + " deg";
            rollLabel.Text = "Roll: " + roll.ToString("0.0") + " deg";
            pRateLabel.Text = "p roll rate: " + pRate.ToString("0.000") + " rad/s";
            qRateLabel.Text = "q pitch rate: " + qRate.ToString("0.000") + " rad/s";
            rRateLabel.Text = "r yaw rate: " + rRate.ToString("0.000") + " rad/s";
            commandLabel.Text = "Command: P " + commandedP.ToString("0.000") + " Q " + commandedQ.ToString("0.000") + " R " + commandedR.ToString("0.000");
            dac1Label.Text = "Ch 1: byte " + currentBytes[0].ToString() + " (" + packetValues[0].ToString("0") + ")";
            dac2Label.Text = "Ch 2: byte " + currentBytes[1].ToString() + " (" + packetValues[1].ToString("0") + ")";
            dac3Label.Text = "Ch 3: byte " + currentBytes[2].ToString() + " (" + packetValues[2].ToString("0") + ")";
            dac4Label.Text = "Ch 4: byte " + currentBytes[3].ToString() + " (" + packetValues[3].ToString("0") + ")";
            packetLabel.Text = "Packets: " + packetCount.ToString();
            lastPacketLabel.Text = "Last TX: " + lastPacketText;
            armButton.Text = armed ? "Disarm" : "Arm";
            armButton.BackColor = armed ? Theme.Coral : Theme.Control;
            armButton.ForeColor = armed ? Color.Black : Theme.Text;
            calLeftButton.BackColor = calibrationDirection > 0 ? Theme.Gold : Theme.Control;
            calLeftButton.ForeColor = calibrationDirection > 0 ? Color.Black : Theme.Text;
            calRightButton.BackColor = calibrationDirection < 0 ? Theme.Gold : Theme.Control;
            calRightButton.ForeColor = calibrationDirection < 0 ? Color.Black : Theme.Text;

            headView.Yaw = yaw;
            headView.Pitch = pitch;
            headView.Roll = roll;
            headView.VectorX = inputX;
            headView.VectorY = inputY;
            headView.PRate = pRate;
            headView.QRate = qRate;
            headView.RRate = rRate;
            headView.Invalidate();
            bool gamePhysicsFresh = (UseMsfsPhysics() && msfsMotion.IsFresh) || (UseForzaPhysics() && forzaMotion.IsFresh);
            stickView.VectorX = gamePhysicsFresh ? Clamp(commandedR / GvsMaxRate, -1.0, 1.0) : displayX;
            stickView.VectorY = gamePhysicsFresh ? Clamp(commandedQ / GvsMaxRate, -1.0, 1.0) : displayY;
            stickView.Connected = physicalInputConnected || gamePhysicsFresh;
            stickView.ControllerIndex = physicalInputIndex;
            stickView.SourceName = activeInput;
            stickView.Invalidate();
        }

        private void SetStatus(string text)
        {
            statusLabel.Text = text;
        }

        private static Label AddMetric(TableLayoutPanel parent, string name, string value)
        {
            Label label = MakeMetric(name, value);
            label.Dock = DockStyle.Fill;
            parent.Controls.Add(label);
            return label;
        }

        private static Label MakeMetric(string name, string value)
        {
            Label label = new Label();
            label.Text = name + ": " + value;
            label.ForeColor = Theme.Text;
            label.BackColor = Theme.Control;
            label.Padding = new Padding(10, 9, 10, 0);
            label.Margin = new Padding(0, 0, 0, 8);
            label.Height = 34;
            label.AutoEllipsis = true;
            return label;
        }

        private static Label MakeSmallLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.ForeColor = Theme.Muted;
            label.AutoSize = false;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Width = 54;
            label.Height = 32;
            label.Margin = new Padding(4, 6, 4, 4);
            return label;
        }

        private static Label MakeSectionTitle(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Height = 26;
            label.ForeColor = Theme.Muted;
            label.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static ComboBox MakeCombo()
        {
            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.BackColor = Theme.Control;
            combo.ForeColor = Theme.Text;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Height = 32;
            combo.Margin = new Padding(4, 6, 4, 4);
            return combo;
        }

        private static CheckBox MakeCheckBox(string text)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.Text = text;
            checkBox.AutoSize = false;
            checkBox.Width = 112;
            checkBox.Height = 32;
            checkBox.TextAlign = ContentAlignment.MiddleLeft;
            checkBox.FlatStyle = FlatStyle.Flat;
            checkBox.BackColor = Theme.Surface;
            checkBox.ForeColor = Theme.Text;
            checkBox.Margin = new Padding(4, 6, 4, 4);
            return checkBox;
        }

        private static Button MakeButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 88;
            button.Height = 34;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Theme.Line;
            button.BackColor = Theme.Control;
            button.ForeColor = Theme.Text;
            button.Margin = new Padding(4, 5, 4, 4);
            return button;
        }

        private static Panel MakePanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Theme.Surface;
            panel.Paint += PaintPanelBorder;
            return panel;
        }

        private static void PaintPanelBorder(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender;
            using (Pen pen = new Pen(Theme.Line))
            {
                Rectangle rect = new Rectangle(0, 0, control.Width - 1, control.Height - 1);
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        private static double[] RotateRates(double p, double q, double r, double phiDeg, double thetaDeg, double psiDeg)
        {
            double[] vector = new double[] { p, q, r };
            vector = MatMulVec(RotZ(-psiDeg * Math.PI / 180.0), vector);
            vector = MatMulVec(RotY(-thetaDeg * Math.PI / 180.0), vector);
            vector = MatMulVec(RotX(-phiDeg * Math.PI / 180.0), vector);
            return vector;
        }

        private static string ChoosePreferredPort(string[] ports, string previous)
        {
            if (ports == null || ports.Length == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(previous) && PortExists(ports, previous))
            {
                return previous;
            }

            string vmocionPort = FindPortByHardwareText(ports, new string[] { "A403QKIL", "VMOCION", "DIGITUS" });
            if (vmocionPort != null)
            {
                return vmocionPort;
            }

            string usbPort = FindPortByHardwareText(ports, new string[] { "USB SERIAL", "USB-SERIAL", "USB UART", "USB", "UART" });
            if (usbPort != null)
            {
                return usbPort;
            }

            if (PortExists(ports, "COM4"))
            {
                return "COM4";
            }

            return ports[0];
        }

        private static string FindPortByHardwareText(string[] ports, string[] needles)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (ManagementObjectCollection devices = searcher.Get())
                {
                    foreach (ManagementObject device in devices)
                    {
                        string text = ((device["Name"] as string) ?? "") + " " + ((device["PNPDeviceID"] as string) ?? "");
                        if (!ContainsAny(text, needles))
                        {
                            continue;
                        }

                        string port = ExtractPortName(text, ports);
                        if (port != null)
                        {
                            return port;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string ExtractPortName(string text, string[] ports)
        {
            for (int i = 0; i < ports.Length; i++)
            {
                string port = ports[i];
                if (text.IndexOf("(" + port + ")", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf(port, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return port;
                }
            }

            return null;
        }

        private static bool ContainsAny(string text, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PortExists(string[] ports, string port)
        {
            for (int i = 0; i < ports.Length; i++)
            {
                if (string.Equals(ports[i], port, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ComparePortNames(string left, string right)
        {
            int leftNumber;
            int rightNumber;
            bool leftCom = TryGetComNumber(left, out leftNumber);
            bool rightCom = TryGetComNumber(right, out rightNumber);
            if (leftCom && rightCom)
            {
                return leftNumber.CompareTo(rightNumber);
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetComNumber(string port, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(port) || port.Length <= 3 || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(port.Substring(3), out number);
        }

        private static double[] JoystickToCommandedRates(double activeX, double activeY, double outputGain, int calibratingDirection, double calibrationIntensity)
        {
            if (calibratingDirection != 0)
            {
                return new double[]
                {
                    calibratingDirection * GvsMaxRate * calibrationIntensity,
                    0.0,
                    0.0
                };
            }

            double yaw = Clamp(activeX * GvsMaxRate * outputGain, -GvsMaxRate, GvsMaxRate);
            double pitch = Clamp(activeY * GvsMaxRate * outputGain, -GvsMaxRate, GvsMaxRate);
            return new double[] { 0.0, pitch, yaw };
        }

        private static double[] MsfsMotionToCommandedRates(MotionSnapshot motion, double outputGain)
        {
            return PhysicsMotionToCommandedRates(motion, outputGain);
        }

        private static double[] PhysicsMotionToCommandedRates(MotionSnapshot motion, double outputGain)
        {
            if (!motion.IsFresh)
            {
                return new double[] { 0.0, 0.0, 0.0 };
            }

            double safeGain = Clamp(outputGain, 0.0, MaxGain);
            return new double[]
            {
                Clamp(motion.P * safeGain, -GvsMaxRate, GvsMaxRate),
                Clamp(motion.Q * safeGain, -GvsMaxRate, GvsMaxRate),
                Clamp(motion.R * safeGain, -GvsMaxRate, GvsMaxRate)
            };
        }

        private static double[] MsfsControlToCommandedRates(MotionSnapshot motion, double outputGain)
        {
            if (!motion.IsFresh || !motion.HasControls)
            {
                return new double[] { 0.0, 0.0, 0.0 };
            }

            double safeGain = Clamp(outputGain, 0.0, MaxGain);
            double yaw = Clamp(motion.ControlX * GvsMaxRate * safeGain, -GvsMaxRate, GvsMaxRate);
            double pitch = Clamp(motion.ControlY * GvsMaxRate * safeGain, -GvsMaxRate, GvsMaxRate);
            return new double[] { 0.0, pitch, yaw };
        }

        internal static double NormalizeMsfsControlPosition(double value)
        {
            double sanitized = Clamp(value, -16384.0, 16384.0);
            if (Math.Abs(sanitized) <= 1.0)
            {
                return sanitized;
            }

            return Clamp(sanitized / 16384.0, -1.0, 1.0);
        }

        private static double[] PqrToCodeMatrixCurrents(double p, double q, double r)
        {
            double weightedPitch = 1.5 * q;
            double[] raw = new double[]
            {
                -p + weightedPitch - r,
                -p - weightedPitch + r,
                p - weightedPitch - r,
                p + weightedPitch + r
            };

            double[] currents = new double[4];
            for (int i = 0; i < 4; i++)
            {
                currents[i] = Clamp(raw[i] * GvsMaxCurrentMilliamp / GvsMaxRate, -GvsMaxCurrentMilliamp, GvsMaxCurrentMilliamp);
            }
            return currents;
        }

        private static double[] CurrentBytesToPacketValues(int[] bytes)
        {
            double[] values = new double[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                values[i] = bytes[i] - 128;
            }
            return values;
        }

        private static double[] CurrentBytesToDacVolts(int[] bytes)
        {
            double[] volts = new double[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                volts[i] = (bytes[i] - 128) * PacketValueToVolts;
            }
            return volts;
        }

        private static int[] CurrentsToUartBytes(double[] currents)
        {
            int[] bytes = new int[currents.Length];
            for (int i = 0; i < currents.Length; i++)
            {
                bytes[i] = CurrentToUartByte(currents[i]);
            }
            return bytes;
        }

        private static int CurrentToUartByte(double current)
        {
            return (int)Clamp(Math.Truncate((current + 2.56) / 0.02), 0, 255);
        }

        private static string PacketToPreview(byte[] packet)
        {
            int visibleBytes = Math.Min(packet.Length, 8);
            string[] parts = new string[visibleBytes];
            for (int i = 0; i < visibleBytes; i++)
            {
                parts[i] = packet[i].ToString("X2");
            }

            string text = string.Join(" ", parts);
            if (packet.Length > visibleBytes)
            {
                text += " ...";
            }

            return text;
        }

        private static bool PacketValuesEqual(double[] actual, double[] expected)
        {
            if (actual.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (Math.Abs(actual[i] - expected[i]) > 0.0001)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IntValuesEqual(int[] actual, int[] expected)
        {
            if (actual.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static double ApplyDeadzone(double value)
        {
            if (Math.Abs(value) < Deadzone)
            {
                return 0.0;
            }

            double scaled = (Math.Abs(value) - Deadzone) / (1.0 - Deadzone);
            return value > 0.0 ? scaled : -scaled;
        }

        private static double[][] RotX(double phi)
        {
            return new double[][]
            {
                new double[] { 1, 0, 0 },
                new double[] { 0, Math.Cos(phi), -Math.Sin(phi) },
                new double[] { 0, Math.Sin(phi), Math.Cos(phi) }
            };
        }

        private static double[][] RotY(double theta)
        {
            return new double[][]
            {
                new double[] { Math.Cos(theta), 0, Math.Sin(theta) },
                new double[] { 0, 1, 0 },
                new double[] { -Math.Sin(theta), 0, Math.Cos(theta) }
            };
        }

        private static double[][] RotZ(double psi)
        {
            return new double[][]
            {
                new double[] { Math.Cos(psi), -Math.Sin(psi), 0 },
                new double[] { Math.Sin(psi), Math.Cos(psi), 0 },
                new double[] { 0, 0, 1 }
            };
        }

        private static double[] MatMulVec(double[][] matrix, double[] vector)
        {
            return new double[]
            {
                matrix[0][0] * vector[0] + matrix[0][1] * vector[1] + matrix[0][2] * vector[2],
                matrix[1][0] * vector[0] + matrix[1][1] * vector[1] + matrix[1][2] * vector[2],
                matrix[2][0] * vector[0] + matrix[2][1] * vector[1] + matrix[2][2] * vector[2]
            };
        }

        private static double Lerp(double from, double to, double amount)
        {
            return from + (to - from) * amount;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal struct MotionSnapshot
    {
        private static readonly TimeSpan FreshWindow = TimeSpan.FromMilliseconds(900);

        public bool Connected;
        public DateTime TimestampUtc;
        public string Source;
        public double P;
        public double Q;
        public double R;
        public double RollDeg;
        public double PitchDeg;
        public double YawDeg;
        public bool HasControls;
        public double ControlX;
        public double ControlY;

        public bool IsFresh
        {
            get { return Connected && (DateTime.UtcNow - TimestampUtc) <= FreshWindow; }
        }

        public static MotionSnapshot Fresh(string source, double p, double q, double r, double rollDeg, double pitchDeg, double yawDeg)
        {
            MotionSnapshot snapshot = new MotionSnapshot();
            snapshot.Connected = true;
            snapshot.TimestampUtc = DateTime.UtcNow;
            snapshot.Source = source;
            snapshot.P = Sanitize(p);
            snapshot.Q = Sanitize(q);
            snapshot.R = Sanitize(r);
            snapshot.RollDeg = Sanitize(rollDeg);
            snapshot.PitchDeg = Sanitize(pitchDeg);
            snapshot.YawDeg = Sanitize(yawDeg);
            return snapshot;
        }

        public static MotionSnapshot Fresh(string source, double p, double q, double r, double rollDeg, double pitchDeg, double yawDeg, double controlX, double controlY)
        {
            MotionSnapshot snapshot = Fresh(source, p, q, r, rollDeg, pitchDeg, yawDeg);
            snapshot.HasControls = true;
            snapshot.ControlX = Sanitize(controlX);
            snapshot.ControlY = Sanitize(controlY);
            return snapshot;
        }

        public static MotionSnapshot Empty(string source)
        {
            MotionSnapshot snapshot = new MotionSnapshot();
            snapshot.Connected = false;
            snapshot.TimestampUtc = DateTime.MinValue;
            snapshot.Source = source;
            return snapshot;
        }

        private static double Sanitize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }

            return value;
        }
    }

    internal sealed class ForzaUdpTelemetryInput : IDisposable
    {
        private const int PrimaryPort = 5300;
        private const int SecondaryPort = 5607;
        private readonly List<UdpClient> clients = new List<UdpClient>();
        private MotionSnapshot latest = MotionSnapshot.Empty("Forza waiting on UDP 5300/5607");
        private bool started;
        private string status = "Forza waiting on UDP 5300/5607";

        public MotionSnapshot Poll()
        {
            EnsureStarted();

            for (int i = 0; i < clients.Count; i++)
            {
                UdpClient client = clients[i];
                while (client.Available > 0)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref remote);
                    MotionSnapshot parsed;
                    if (TryParse(data, out parsed))
                    {
                        latest = parsed;
                        status = parsed.Source;
                    }
                }
            }

            return latest.IsFresh ? latest : MotionSnapshot.Empty(status);
        }

        public static bool SelfTest()
        {
            byte[] packet = new byte[68];
            WriteSingle(packet, 44, 0.2f);
            WriteSingle(packet, 48, 0.3f);
            WriteSingle(packet, 52, 0.1f);
            WriteSingle(packet, 56, 0.4f);
            WriteSingle(packet, 60, -0.2f);
            WriteSingle(packet, 64, 0.1f);

            MotionSnapshot motion;
            return TryParse(packet, out motion) &&
                   Math.Abs(motion.P - 0.1) < 0.0001 &&
                   Math.Abs(motion.Q - 0.2) < 0.0001 &&
                   Math.Abs(motion.R - 0.3) < 0.0001 &&
                   Math.Abs(motion.RollDeg - (0.1 * 180.0 / Math.PI)) < 0.0001 &&
                   Math.Abs(motion.PitchDeg - (-0.2 * 180.0 / Math.PI)) < 0.0001;
        }

        public void ResetStatus()
        {
            status = started ? "Forza waiting on UDP 5300/5607" : "Forza UDP not started";
            latest = MotionSnapshot.Empty(status);
        }

        private void EnsureStarted()
        {
            if (started)
            {
                return;
            }

            started = true;
            TryBind(PrimaryPort);
            TryBind(SecondaryPort);

            if (clients.Count == 0)
            {
                status = "Forza UDP bind failed";
            }
        }

        private void TryBind(int port)
        {
            try
            {
                UdpClient client = new UdpClient();
                client.ExclusiveAddressUse = false;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                clients.Add(client);
                status = "Forza waiting on UDP " + port.ToString();
            }
            catch
            {
                // Another telemetry app may own the port; keep any port that did bind.
            }
        }

        private static bool TryParse(byte[] data, out MotionSnapshot motion)
        {
            motion = MotionSnapshot.Empty("Forza packet too short");
            if (data == null || data.Length < 68)
            {
                return false;
            }

            float angularX = ReadSingle(data, 44);
            float angularY = ReadSingle(data, 48);
            float angularZ = ReadSingle(data, 52);
            float yaw = ReadSingle(data, 56);
            float pitch = ReadSingle(data, 60);
            float roll = ReadSingle(data, 64);

            motion = MotionSnapshot.Fresh(
                "Forza UDP physics",
                angularZ,
                angularX,
                angularY,
                roll * 180.0 / Math.PI,
                pitch * 180.0 / Math.PI,
                yaw * 180.0 / Math.PI);
            return true;
        }

        private static float ReadSingle(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
            {
                return 0.0f;
            }

            return BitConverter.ToSingle(data, offset);
        }

        private static void WriteSingle(byte[] data, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, offset, bytes.Length);
        }

        public void Dispose()
        {
            for (int i = 0; i < clients.Count; i++)
            {
                clients[i].Close();
            }

            clients.Clear();
        }
    }

    internal sealed class MsfsSimConnectInput : IDisposable
    {
        private const uint SimConnectObjectIdUser = 0;
        private const uint SimConnectUnused = 0xffffffff;
        private const uint DefinitionMotion = 1;
        private const uint RequestMotion = 1;
        private const uint SimConnectDatatypeFloat64 = 4;
        private const uint SimConnectPeriodSimFrame = 3;
        private const uint SimConnectDataRequestFlagDefault = 0;
        private const uint SimConnectRecvIdSimobjectData = 8;
        private const int SimobjectDataOffset = 40;
        private const int MotionDataBytes = 40;

        private readonly int windowMessage;
        private readonly DispatchProc dispatchProc;
        private IntPtr handle = IntPtr.Zero;
        private DateTime lastConnectAttempt = DateTime.MinValue;
        private MotionSnapshot latest = MotionSnapshot.Empty("MSFS waiting for SimConnect");
        private string status = "MSFS waiting for SimConnect";
        private bool definitionsRegistered;

        public MsfsSimConnectInput(int msfsWindowMessage)
        {
            windowMessage = msfsWindowMessage;
            dispatchProc = Dispatch;
        }

        public MotionSnapshot Poll(IntPtr windowHandle)
        {
            EnsureConnected(windowHandle);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    SimConnect_CallDispatch(handle, dispatchProc, IntPtr.Zero);
                }
                catch
                {
                    Close();
                    status = "MSFS SimConnect disconnected";
                }
            }

            return latest.IsFresh ? latest : MotionSnapshot.Empty(status);
        }

        public void HandleWindowMessage(int message)
        {
            if (message != windowMessage || handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                SimConnect_CallDispatch(handle, dispatchProc, IntPtr.Zero);
            }
            catch
            {
                Close();
                status = "MSFS SimConnect disconnected";
            }
        }

        public void ResetStatus()
        {
            status = handle == IntPtr.Zero ? "MSFS waiting for SimConnect" : "MSFS SimConnect connected";
            latest = MotionSnapshot.Empty(status);
        }

        private void EnsureConnected(IntPtr windowHandle)
        {
            if (handle != IntPtr.Zero)
            {
                return;
            }

            if ((DateTime.UtcNow - lastConnectAttempt).TotalSeconds < 2.0)
            {
                return;
            }

            lastConnectAttempt = DateTime.UtcNow;
            IntPtr simConnect;
            try
            {
                int hr = SimConnect_Open(out simConnect, "Vestibular Joystick Simulation", windowHandle, (uint)windowMessage, IntPtr.Zero, 0);
                if (hr != 0 || simConnect == IntPtr.Zero)
                {
                    status = "MSFS waiting for simulator";
                    return;
                }

                handle = simConnect;
                RegisterDefinitions();
                status = "MSFS SimConnect connected";
            }
            catch (DllNotFoundException)
            {
                status = "MSFS SimConnect.dll missing";
                Close();
            }
            catch
            {
                status = "MSFS SimConnect failed";
                Close();
            }
        }

        private void RegisterDefinitions()
        {
            if (definitionsRegistered || handle == IntPtr.Zero)
            {
                return;
            }

            AddDouble("ROTATION VELOCITY BODY X", "Feet per second");
            AddDouble("ROTATION VELOCITY BODY Y", "Feet per second");
            AddDouble("ROTATION VELOCITY BODY Z", "Feet per second");
            AddDouble("AILERON POSITION", "Position");
            AddDouble("ELEVATOR POSITION", "Position");

            int hr = SimConnect_RequestDataOnSimObject(
                handle,
                RequestMotion,
                DefinitionMotion,
                SimConnectObjectIdUser,
                SimConnectPeriodSimFrame,
                SimConnectDataRequestFlagDefault,
                0,
                0,
                0);
            if (hr != 0)
            {
                throw new InvalidOperationException("SimConnect request failed.");
            }

            definitionsRegistered = true;
        }

        private void AddDouble(string name, string units)
        {
            int hr = SimConnect_AddToDataDefinition(handle, DefinitionMotion, name, units, SimConnectDatatypeFloat64, 0.0f, SimConnectUnused);
            if (hr != 0)
            {
                throw new InvalidOperationException("SimConnect definition failed: " + name);
            }
        }

        private void Dispatch(IntPtr data, uint cbData, IntPtr context)
        {
            if (data == IntPtr.Zero || cbData < SimobjectDataOffset + MotionDataBytes)
            {
                return;
            }

            uint receiveId = (uint)Marshal.ReadInt32(data, 0);
            if (receiveId != SimConnectRecvIdSimobjectData)
            {
                return;
            }

            double bodyX = ReadDouble(data, SimobjectDataOffset);
            double bodyY = ReadDouble(data, SimobjectDataOffset + 8);
            double bodyZ = ReadDouble(data, SimobjectDataOffset + 16);
            double aileron = MainForm.NormalizeMsfsControlPosition(ReadDouble(data, SimobjectDataOffset + 24));
            double elevator = MainForm.NormalizeMsfsControlPosition(ReadDouble(data, SimobjectDataOffset + 32));
            latest = MotionSnapshot.Fresh("MSFS controls + physics", bodyY, bodyX, bodyZ, 0.0, 0.0, 0.0, aileron, elevator);
            status = latest.Source;
        }

        private static double ReadDouble(IntPtr pointer, int offset)
        {
            byte[] bytes = new byte[8];
            Marshal.Copy(IntPtr.Add(pointer, offset), bytes, 0, bytes.Length);
            return BitConverter.ToDouble(bytes, 0);
        }

        private void Close()
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    SimConnect_Close(handle);
                }
                catch
                {
                }
            }

            handle = IntPtr.Zero;
            definitionsRegistered = false;
        }

        public void Dispose()
        {
            Close();
        }

        private delegate void DispatchProc(IntPtr data, uint cbData, IntPtr context);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_Open(out IntPtr phSimConnect, string name, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_Close(IntPtr hSimConnect);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint defineId, string datumName, string unitsName, uint datumType, float epsilon, uint datumId);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_RequestDataOnSimObject(IntPtr hSimConnect, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);

        [DllImport("SimConnect.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int SimConnect_CallDispatch(IntPtr hSimConnect, DispatchProc dispatchProc, IntPtr context);
    }

    internal sealed class PhysicalJoystickInput
    {
        private const int ErrorSuccess = 0;
        private const int JoyNoError = 0;
        private const int JoyReturnAll = 0x000000ff;
        private const double LeftThumbDeadzone = 7849.0 / 32767.0;
        private const int KeyPressedMask = unchecked((int)0x8000);
        private bool xinput14Unavailable;
        private bool xinput910Unavailable;
        private bool hasLastCursor;
        private NativePoint lastCursor;

        public bool TryReadLeftStick(out double x, out double y, out int controllerIndex, out string source)
        {
            x = 0.0;
            y = 0.0;
            controllerIndex = -1;
            source = "No left stick";

            bool found = false;
            double bestMagnitude = -1.0;

            for (int i = 0; i < 4; i++)
            {
                XInputState state;
                if (!TryGetState(i, out state))
                {
                    continue;
                }

                double candidateX;
                double candidateY;
                NormalizeLeftStick(state.Gamepad.ThumbLX, state.Gamepad.ThumbLY, out candidateX, out candidateY);
                double magnitude = Magnitude(candidateX, candidateY);
                if (!found || magnitude > bestMagnitude)
                {
                    found = true;
                    bestMagnitude = magnitude;
                    x = candidateX;
                    y = candidateY;
                    controllerIndex = i;
                    source = "XInput " + (i + 1).ToString();
                }
            }

            uint count = JoyGetNumDevsSafe();
            for (uint i = 0; i < count && i < 16; i++)
            {
                double candidateX;
                double candidateY;
                string candidateSource;
                if (!TryReadWinMmJoystick(i, out candidateX, out candidateY, out candidateSource))
                {
                    continue;
                }

                double magnitude = Magnitude(candidateX, candidateY);
                if (!found || magnitude > bestMagnitude || (bestMagnitude <= 0.001 && candidateSource.IndexOf("Windows joystick", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    found = true;
                    bestMagnitude = magnitude;
                    x = candidateX;
                    y = candidateY;
                    controllerIndex = (int)i;
                    source = candidateSource;
                }
            }

            double keyboardX;
            double keyboardY;
            if (TryReadKeyboardStick(out keyboardX, out keyboardY))
            {
                double magnitude = Magnitude(keyboardX, keyboardY);
                if (!found || magnitude >= bestMagnitude)
                {
                    found = true;
                    bestMagnitude = magnitude;
                    x = keyboardX;
                    y = keyboardY;
                    controllerIndex = -1;
                    source = "Keyboard stick";
                }
            }

            double pointerX;
            double pointerY;
            if (TryReadPointerStick(out pointerX, out pointerY))
            {
                double magnitude = Magnitude(pointerX, pointerY);
                if (!found || magnitude > bestMagnitude)
                {
                    found = true;
                    bestMagnitude = magnitude;
                    x = pointerX;
                    y = pointerY;
                    controllerIndex = -1;
                    source = "Desktop pointer stick";
                }
            }

            return found;
        }

        private bool TryGetState(int controllerIndex, out XInputState state)
        {
            state = new XInputState();

            if (!xinput14Unavailable)
            {
                try
                {
                    return XInputGetState14(controllerIndex, out state) == ErrorSuccess;
                }
                catch (DllNotFoundException)
                {
                    xinput14Unavailable = true;
                }
                catch (EntryPointNotFoundException)
                {
                    xinput14Unavailable = true;
                }
            }

            if (!xinput910Unavailable)
            {
                try
                {
                    return XInputGetState910(controllerIndex, out state) == ErrorSuccess;
                }
                catch (DllNotFoundException)
                {
                    xinput910Unavailable = true;
                }
                catch (EntryPointNotFoundException)
                {
                    xinput910Unavailable = true;
                }
            }

            return false;
        }

        private static bool TryReadKeyboardStick(out double x, out double y)
        {
            x = 0.0;
            y = 0.0;

            if (IsKeyDown(Keys.Left) || IsKeyDown(Keys.A) || IsKeyDown(Keys.J)) x -= 1.0;
            if (IsKeyDown(Keys.Right) || IsKeyDown(Keys.D) || IsKeyDown(Keys.L)) x += 1.0;
            if (IsKeyDown(Keys.Up) || IsKeyDown(Keys.W) || IsKeyDown(Keys.I)) y += 1.0;
            if (IsKeyDown(Keys.Down) || IsKeyDown(Keys.S) || IsKeyDown(Keys.K)) y -= 1.0;

            double magnitude = Magnitude(x, y);
            if (magnitude <= 0.0)
            {
                return false;
            }

            if (magnitude > 1.0)
            {
                x /= magnitude;
                y /= magnitude;
            }

            return true;
        }

        private bool TryReadPointerStick(out double x, out double y)
        {
            x = 0.0;
            y = 0.0;

            NativePoint point;
            if (!GetCursorPos(out point))
            {
                return false;
            }

            if (!hasLastCursor)
            {
                lastCursor = point;
                hasLastCursor = true;
                return false;
            }

            int dx = point.X - lastCursor.X;
            int dy = point.Y - lastCursor.Y;
            lastCursor = point;

            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2)
            {
                return false;
            }

            x = Clamp(dx / 24.0, -1.0, 1.0);
            y = Clamp(-dy / 24.0, -1.0, 1.0);
            return Magnitude(x, y) > 0.03;
        }

        private static void NormalizeLeftStick(short rawX, short rawY, out double x, out double y)
        {
            double nx = NormalizeAxis(rawX);
            double ny = NormalizeAxis(rawY);
            double magnitude = Math.Sqrt(nx * nx + ny * ny);

            if (magnitude <= LeftThumbDeadzone)
            {
                x = 0.0;
                y = 0.0;
                return;
            }

            double scaled = Math.Min(1.0, (magnitude - LeftThumbDeadzone) / (1.0 - LeftThumbDeadzone));
            x = nx / magnitude * scaled;
            y = ny / magnitude * scaled;
        }

        private static double NormalizeAxis(short value)
        {
            return value >= 0 ? value / 32767.0 : value / 32768.0;
        }

        private bool TryReadWinMmJoystick(uint joystickIndex, out double x, out double y, out string source)
        {
            x = 0.0;
            y = 0.0;
            source = "No left stick";

            try
            {
                JoyCaps caps;
                uint capResult = joyGetDevCaps(joystickIndex, out caps, (uint)Marshal.SizeOf(typeof(JoyCaps)));
                if (capResult != JoyNoError || caps.NumAxes < 2)
                {
                    return false;
                }

                JoyInfoEx info = new JoyInfoEx();
                info.Size = (uint)Marshal.SizeOf(typeof(JoyInfoEx));
                info.Flags = JoyReturnAll;
                uint posResult = joyGetPosEx(joystickIndex, ref info);
                if (posResult != JoyNoError)
                {
                    return false;
                }

                x = NormalizeWinMmAxis(info.X, caps.XMin, caps.XMax, false);
                y = NormalizeWinMmAxis(info.Y, caps.YMin, caps.YMax, true);
                source = "Windows joystick " + (joystickIndex + 1).ToString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static uint JoyGetNumDevsSafe()
        {
            try
            {
                return joyGetNumDevs();
            }
            catch
            {
                return 0;
            }
        }

        private static double NormalizeWinMmAxis(uint value, uint min, uint max, bool invert)
        {
            if (max <= min)
            {
                min = 0;
                max = 65535;
            }

            double center = (min + max) / 2.0;
            double halfRange = (max - min) / 2.0;
            if (halfRange <= 0.0)
            {
                return 0.0;
            }

            double normalized = ((double)value - center) / halfRange;
            if (invert)
            {
                normalized = -normalized;
            }

            return Clamp(normalized, -1.0, 1.0);
        }

        private static double Magnitude(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        private static bool IsKeyDown(Keys key)
        {
            return (GetAsyncKeyState((int)key) & KeyPressedMask) != 0;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState14(int dwUserIndex, out XInputState pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState910(int dwUserIndex, out XInputState pState);

        [DllImport("winmm.dll")]
        private static extern uint joyGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern uint joyGetDevCaps(uint uJoyID, out JoyCaps pjc, uint cbjc);

        [DllImport("winmm.dll")]
        private static extern uint joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct JoyCaps
        {
            public ushort Mid;
            public ushort Pid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ProductName;
            public uint XMin;
            public uint XMax;
            public uint YMin;
            public uint YMax;
            public uint ZMin;
            public uint ZMax;
            public uint NumButtons;
            public uint PeriodMin;
            public uint PeriodMax;
            public uint RMin;
            public uint RMax;
            public uint UMin;
            public uint UMax;
            public uint VMin;
            public uint VMax;
            public uint Caps;
            public uint MaxAxes;
            public uint NumAxes;
            public uint MaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string RegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string OemVxD;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public uint Size;
            public uint Flags;
            public uint X;
            public uint Y;
            public uint Z;
            public uint R;
            public uint U;
            public uint V;
            public uint Buttons;
            public uint ButtonNumber;
            public uint Pov;
            public uint Reserved1;
            public uint Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }
    }

    internal sealed class PhysicalStickView : Control
    {
        public double VectorX;
        public double VectorY;
        public bool Connected;
        public int ControllerIndex;
        public string SourceName = "No left stick";

        public PhysicalStickView()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Theme.Surface);

            Rectangle bounds = ClientRectangle;
            using (Brush text = new SolidBrush(Theme.Text))
            using (Brush muted = new SolidBrush(Theme.Muted))
            using (Font title = new Font("Segoe UI", 12.0f, FontStyle.Bold))
            using (Font regular = new Font("Segoe UI", 9.0f))
            {
                e.Graphics.DrawString("ROG Ally Physical Input", title, text, 18, 22);
                string source = Connected ? SourceName + " left stick" : "No physical stick detected";
                e.Graphics.DrawString(source, regular, muted, 19, 52);
            }

            int left = 22;
            int right = bounds.Width - 22;
            int barWidth = Math.Max(80, right - left);
            DrawAxisBar(e.Graphics, "Yaw X", VectorX, left, 104, barWidth);
            DrawAxisBar(e.Graphics, "Pitch Y", VectorY, left, 164, barWidth);

            RectangleF plot = new RectangleF(left, 226, barWidth, Math.Max(90, bounds.Height - 264));
            DrawVectorPlot(e.Graphics, plot);
        }

        private void DrawAxisBar(Graphics graphics, string label, double value, int x, int y, int width)
        {
            Rectangle track = new Rectangle(x, y + 22, width, 12);
            int center = track.Left + track.Width / 2;
            int fill = (int)Math.Round((track.Width / 2.0) * Math.Max(-1.0, Math.Min(1.0, value)));

            using (Brush muted = new SolidBrush(Theme.Muted))
            using (Brush text = new SolidBrush(Theme.Text))
            using (Brush trackBrush = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            using (Brush fillBrush = new SolidBrush(Connected ? Theme.Cyan : Theme.Muted))
            using (Pen line = new Pen(Theme.Line))
            using (Font small = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (Font valueFont = new Font("Segoe UI", 9.0f, FontStyle.Bold))
            {
                graphics.DrawString(label, small, muted, x, y);
                graphics.DrawString((value * 100.0).ToString("0") + "%", valueFont, text, x + width - 58, y);
                graphics.FillRectangle(trackBrush, track);
                graphics.DrawRectangle(line, track);
                graphics.DrawLine(line, center, track.Top - 5, center, track.Bottom + 5);

                if (fill != 0)
                {
                    Rectangle active = fill > 0
                        ? new Rectangle(center, track.Top, fill, track.Height)
                        : new Rectangle(center + fill, track.Top, -fill, track.Height);
                    graphics.FillRectangle(fillBrush, active);
                }
            }
        }

        private void DrawVectorPlot(Graphics graphics, RectangleF rect)
        {
            float size = Math.Min(rect.Width, rect.Height);
            RectangleF square = new RectangleF(rect.Left + (rect.Width - size) / 2.0f, rect.Top, size, size);
            PointF center = new PointF(square.Left + square.Width / 2.0f, square.Top + square.Height / 2.0f);

            using (Pen line = new Pen(Theme.Line))
            using (Pen accent = new Pen(Connected ? Theme.Gold : Theme.Muted, 2.5f))
            using (Brush dot = new SolidBrush(Connected ? Theme.Gold : Theme.Muted))
            {
                graphics.DrawRectangle(line, Rectangle.Round(square));
                graphics.DrawLine(line, center.X, square.Top + 8, center.X, square.Bottom - 8);
                graphics.DrawLine(line, square.Left + 8, center.Y, square.Right - 8, center.Y);

                float px = center.X + (float)(VectorX * (square.Width / 2.0f - 12.0f));
                float py = center.Y - (float)(VectorY * (square.Height / 2.0f - 12.0f));
                graphics.DrawLine(accent, center, new PointF(px, py));
                graphics.FillEllipse(dot, px - 5.0f, py - 5.0f, 10.0f, 10.0f);
            }
        }
    }

    internal sealed class JoystickControl : Control
    {
        private bool dragging;
        private double vectorX;
        private double vectorY;

        public double VectorX { get { return vectorX; } }
        public double VectorY { get { return vectorY; } }

        public JoystickControl()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            TabStop = true;
        }

        public void ResetVector()
        {
            vectorX = 0.0;
            vectorY = 0.0;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            dragging = true;
            Capture = true;
            SetFromPoint(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                SetFromPoint(e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
            ResetVector();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double x = vectorX;
            double y = vectorY;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.A) x = -1.0;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.D) x = 1.0;
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.W) y = 1.0;
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.S) y = -1.0;
            double length = Math.Sqrt(x * x + y * y);
            if (length > 1.0)
            {
                x /= length;
                y /= length;
            }
            vectorX = x;
            vectorY = y;
            Invalidate();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            ResetVector();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            int size = Math.Min(Width, Height) - 36;
            if (size < 80) size = Math.Min(Width, Height) - 8;
            Rectangle rect = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            PointF center = new PointF(rect.Left + rect.Width / 2.0f, rect.Top + rect.Height / 2.0f);
            float radius = rect.Width / 2.0f;
            float knobRadius = Math.Max(24.0f, radius * 0.28f);
            float max = radius - knobRadius - 8.0f;

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(18, 18, 18)))
            {
                e.Graphics.FillEllipse(brush, rect);
            }
            using (Pen pen = new Pen(Theme.Line))
            {
                e.Graphics.DrawEllipse(pen, rect);
                e.Graphics.DrawLine(pen, center.X, rect.Top + 12, center.X, rect.Bottom - 12);
                e.Graphics.DrawLine(pen, rect.Left + 12, center.Y, rect.Right - 12, center.Y);
            }
            using (Pen dashed = new Pen(Color.FromArgb(70, Theme.Text)))
            {
                dashed.DashStyle = DashStyle.Dash;
                int inset = (int)(radius * 0.32f);
                e.Graphics.DrawEllipse(dashed, Rectangle.Inflate(rect, -inset, -inset));
            }

            DrawCenteredText(e.Graphics, "PITCH", new PointF(center.X, rect.Top + 24), Theme.Muted);
            DrawCenteredText(e.Graphics, "PITCH", new PointF(center.X, rect.Bottom - 24), Theme.Muted);
            DrawCenteredText(e.Graphics, "YAW", new PointF(rect.Left + 30, center.Y), Theme.Muted);
            DrawCenteredText(e.Graphics, "YAW", new PointF(rect.Right - 30, center.Y), Theme.Muted);

            float knobX = center.X + (float)(vectorX * max);
            float knobY = center.Y - (float)(vectorY * max);
            RectangleF knob = new RectangleF(knobX - knobRadius, knobY - knobRadius, knobRadius * 2, knobRadius * 2);
            using (LinearGradientBrush knobBrush = new LinearGradientBrush(knob, Color.FromArgb(70, 230, 220), Color.FromArgb(12, 65, 65), 45.0f))
            {
                e.Graphics.FillEllipse(knobBrush, knob);
            }
            using (Pen pen = new Pen(Color.FromArgb(160, 255, 255, 255)))
            {
                e.Graphics.DrawEllipse(pen, knob);
            }
        }

        private void SetFromPoint(Point point)
        {
            float size = Math.Min(Width, Height) - 36;
            RectangleF rect = new RectangleF((Width - size) / 2.0f, (Height - size) / 2.0f, size, size);
            PointF center = new PointF(rect.Left + rect.Width / 2.0f, rect.Top + rect.Height / 2.0f);
            float knobRadius = Math.Max(24.0f, rect.Width * 0.14f);
            double max = rect.Width / 2.0 - knobRadius - 8.0;
            double rawX = point.X - center.X;
            double rawY = point.Y - center.Y;
            double distance = Math.Sqrt(rawX * rawX + rawY * rawY);
            if (distance > max && distance > 0.0)
            {
                rawX = rawX * max / distance;
                rawY = rawY * max / distance;
            }
            vectorX = Clamp(rawX / max, -1.0, 1.0);
            vectorY = Clamp(-rawY / max, -1.0, 1.0);
            Invalidate();
        }

        private static void DrawCenteredText(Graphics graphics, string text, PointF point, Color color)
        {
            using (Font font = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (Brush brush = new SolidBrush(color))
            {
                SizeF size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, brush, point.X - size.Width / 2.0f, point.Y - size.Height / 2.0f);
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal sealed class HeadView : Control
    {
        public double Yaw;
        public double Pitch;
        public double Roll;
        public double VectorX;
        public double VectorY;
        public double PRate;
        public double QRate;
        public double RRate;

        public HeadView()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = ClientRectangle;
            e.Graphics.Clear(Theme.Surface);

            using (Pen gridPen = new Pen(Color.FromArgb(28, 255, 255, 255)))
            {
                for (int x = 0; x < bounds.Width; x += 44) e.Graphics.DrawLine(gridPen, x, 0, x, bounds.Height);
                for (int y = 0; y < bounds.Height; y += 44) e.Graphics.DrawLine(gridPen, 0, y, bounds.Width, y);
            }

            using (Brush brush = new SolidBrush(Theme.Text))
            using (Font font = new Font("Segoe UI", 10.0f, FontStyle.Bold))
            {
                e.Graphics.DrawString("HEAD ORIENTATION MODEL", font, brush, 18, 18);
            }

            DrawReadout(e.Graphics, "Yaw", Yaw.ToString("0.0") + " deg", 20, 48);
            DrawReadout(e.Graphics, "Pitch", Pitch.ToString("0.0") + " deg", 108, 48);
            DrawReadout(e.Graphics, "Roll", Roll.ToString("0.0") + " deg", 210, 48);

            PointF center = new PointF(bounds.Width / 2.0f, bounds.Height / 2.0f + 30);
            float headWidth = Math.Min(bounds.Width * 0.28f, 190.0f);
            float headHeight = headWidth * 1.42f;
            float yawShift = (float)(Yaw * 1.2);
            float pitchShift = (float)(-Pitch * 0.7);
            float rollAngle = (float)Roll;

            Matrix old = e.Graphics.Transform;
            e.Graphics.TranslateTransform(center.X + yawShift, center.Y + pitchShift);
            e.Graphics.RotateTransform(rollAngle);

            using (Pen ring1 = new Pen(Theme.Gold, 3.0f))
            using (Pen ring2 = new Pen(Theme.Cyan, 2.0f))
            using (Pen ring3 = new Pen(Theme.Violet, 2.0f))
            {
                e.Graphics.DrawEllipse(ring1, -headWidth * 0.95f, -headHeight * 0.20f, headWidth * 1.9f, headHeight * 0.42f);
                e.Graphics.DrawEllipse(ring2, -headWidth * 0.20f, -headHeight * 0.55f, headWidth * 0.40f, headHeight * 1.1f);
                e.Graphics.RotateTransform(-36.0f);
                e.Graphics.DrawEllipse(ring3, -headWidth * 0.18f, -headHeight * 0.62f, headWidth * 0.36f, headHeight * 1.24f);
                e.Graphics.RotateTransform(36.0f);
            }

            RectangleF head = new RectangleF(-headWidth / 2.0f, -headHeight / 2.0f, headWidth, headHeight);
            using (LinearGradientBrush headBrush = new LinearGradientBrush(head, Color.FromArgb(237, 216, 194), Color.FromArgb(103, 74, 65), 90.0f))
            {
                e.Graphics.FillEllipse(headBrush, head);
            }
            using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255)))
            {
                e.Graphics.DrawEllipse(pen, head);
            }
            using (Brush dark = new SolidBrush(Color.FromArgb(95, 65, 58)))
            {
                e.Graphics.FillEllipse(dark, -headWidth * 0.23f, -headHeight * 0.06f, 18, 18);
                e.Graphics.FillEllipse(dark, headWidth * 0.13f, -headHeight * 0.06f, 18, 18);
                e.Graphics.FillRoundedRectangle(dark, new RectangleF(-6, -headHeight * 0.03f, 14, 58), 7);
                e.Graphics.FillEllipse(new SolidBrush(Color.FromArgb(90, 64, 56)), -headWidth * 0.22f, headHeight * 0.28f, headWidth * 0.44f, 14);
            }

            e.Graphics.Transform = old;

            DrawVector(e.Graphics, center);
            DrawAxis(e.Graphics, center, 0.0f, Theme.Cyan, "Yaw");
            DrawAxis(e.Graphics, center, -90.0f, Theme.Gold, "Pitch");
            DrawAxis(e.Graphics, center, 42.0f, Theme.Coral, "Roll");
        }

        private void DrawVector(Graphics graphics, PointF center)
        {
            double intensity = Math.Min(1.0, Math.Sqrt(VectorX * VectorX + VectorY * VectorY));
            if (intensity < 0.02)
            {
                return;
            }
            float length = (float)(Math.Min(Width, Height) * 0.24 * intensity);
            float angle = (float)Math.Atan2(-VectorY, VectorX);
            PointF end = new PointF(center.X + (float)Math.Cos(angle) * length, center.Y + (float)Math.Sin(angle) * length);
            using (Pen pen = new Pen(Theme.Coral, 4.0f))
            {
                pen.EndCap = LineCap.ArrowAnchor;
                graphics.DrawLine(pen, center, end);
            }
        }

        private static void DrawAxis(Graphics graphics, PointF center, float degrees, Color color, string label)
        {
            float length = 170.0f;
            float radians = degrees * (float)Math.PI / 180.0f;
            PointF end = new PointF(center.X + (float)Math.Cos(radians) * length, center.Y + (float)Math.Sin(radians) * length);
            using (Pen pen = new Pen(Color.FromArgb(160, color), 2.0f))
            {
                pen.EndCap = LineCap.RoundAnchor;
                graphics.DrawLine(pen, center, end);
            }
            using (Brush brush = new SolidBrush(color))
            using (Font font = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            {
                graphics.DrawString(label, font, brush, end.X + 6, end.Y - 8);
            }
        }

        private static void DrawReadout(Graphics graphics, string name, string value, int x, int y)
        {
            Rectangle rect = new Rectangle(x, y, 82, 42);
            using (Brush brush = new SolidBrush(Theme.Control))
            using (Pen pen = new Pen(Theme.Line))
            using (Brush muted = new SolidBrush(Theme.Muted))
            using (Brush text = new SolidBrush(Theme.Text))
            using (Font small = new Font("Segoe UI", 7.0f, FontStyle.Bold))
            using (Font bold = new Font("Segoe UI", 9.0f, FontStyle.Bold))
            {
                graphics.FillRectangle(brush, rect);
                graphics.DrawRectangle(pen, rect);
                graphics.DrawString(name, small, muted, x + 8, y + 5);
                graphics.DrawString(value, bold, text, x + 8, y + 20);
            }
        }
    }

    internal static class PacketBuilder
    {
        public static bool SelfTest()
        {
            byte[] actual = DacSetValue(1, 9.37f);
            List<byte> expected = new List<byte>();
            expected.Add(0x00);
            expected.Add(0x00);
            expected.Add(0x00);
            expected.Add(0x01);
            expected.Add(0x01);
            expected.AddRange(FloatBytesLittleEndian(9.37f));
            expected.Add(0xff);
            expected[1] = (byte)(expected.Count & 0xff);
            expected[2] = Checksum(expected);
            expected[1] = (byte)((expected.Count & 0xff) - 1);

            return BytesEqual(UartSignal(new int[] { 2 }), new byte[] { 0xaa, 0x01, 0x02, 0x02, 0x55 }) &&
                   BytesEqual(UartSignal(new int[] { 1 }), new byte[] { 0xaa, 0x01, 0x01, 0x01, 0x55 }) &&
                   BytesEqual(UartSignal(new int[] { 10, 128, 128, 128, 128 }), new byte[] { 0xaa, 0x05, 0x0a, 0x80, 0x80, 0x80, 0x80, 0x0a, 0x55 }) &&
                   BytesEqual(UartSignal(new int[] { 10, 28, 228, 28, 228 }), new byte[] { 0xaa, 0x05, 0x0a, 0x1c, 0xe4, 0x1c, 0xe4, 0x0a, 0x55 }) &&
                   BytesEqual(actual, expected.ToArray()) &&
                   BytesEqual(DacClearAll(), new byte[] { 0x00, 0x04, 0x04, 0x00, 0xff }) &&
                   BytesEqual(actual, new byte[] { 0x00, 0x09, 0xd1, 0x01, 0x01, 0x85, 0xeb, 0x15, 0x41, 0xff }) &&
                   BytesEqual(DacSetValue(1, 0.0f), new byte[] { 0x00, 0x09, 0x0b, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0xff }) &&
                   BytesEqual(DacSetValue(1, -15.0f), new byte[] { 0x00, 0x09, 0x3c, 0x01, 0x01, 0x00, 0x00, 0x70, 0xc1, 0xff }) &&
                   BytesEqual(DacSetValue(1, 15.0f), new byte[] { 0x00, 0x09, 0xbc, 0x01, 0x01, 0x00, 0x00, 0x70, 0x41, 0xff });
        }

        public static byte[] UartSignal(int[] signal)
        {
            List<byte> packet = new List<byte>();
            packet.Add(0xaa);
            packet.Add((byte)(signal.Length & 0xff));
            for (int i = 0; i < signal.Length; i++)
            {
                packet.Add((byte)(signal[i] & 0xff));
            }
            packet.Add(UartChecksum(signal));
            packet.Add(0x55);
            return packet.ToArray();
        }

        public static byte[] DacClearAll()
        {
            return CommandPacket(new object[] { 0 });
        }

        public static byte[] DacSetValue(int dacId, float volts)
        {
            return CommandPacket(new object[] { 1, dacId, volts });
        }

        private static byte[] CommandPacket(object[] data)
        {
            List<byte> packet = new List<byte>();
            packet.Add(0x00);
            packet.Add(0x00);
            packet.Add(0x00);

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] is int)
                {
                    packet.Add((byte)(((int)data[i]) & 0xff));
                }
                else if (data[i] is float)
                {
                    packet.AddRange(FloatBytesLittleEndian((float)data[i]));
                }
                else
                {
                    throw new ArgumentException("Unsupported packet item type.");
                }
            }

            packet.Add(0xff);
            packet[1] = (byte)(packet.Count & 0xff);
            packet[2] = Checksum(packet);
            packet[1] = (byte)((packet.Count & 0xff) - 1);
            return packet.ToArray();
        }

        private static byte[] FloatBytesLittleEndian(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return bytes;
        }

        private static byte UartChecksum(int[] signal)
        {
            int sum = 0;
            for (int i = 0; i < signal.Length; i++)
            {
                sum += signal[i];
            }
            return (byte)(sum & 0xff);
        }

        private static byte Checksum(List<byte> data)
        {
            int sum = 0;
            for (int i = 0; i < data.Count; i++)
            {
                sum += data[i];
            }
            return (byte)(sum & 0xff);
        }

        private static bool BytesEqual(byte[] actual, byte[] expected)
        {
            if (actual.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(17, 17, 17);
        public static readonly Color Surface = Color.FromArgb(25, 25, 25);
        public static readonly Color Control = Color.FromArgb(32, 32, 32);
        public static readonly Color Line = Color.FromArgb(58, 58, 58);
        public static readonly Color Text = Color.FromArgb(246, 243, 237);
        public static readonly Color Muted = Color.FromArgb(188, 181, 170);
        public static readonly Color Cyan = Color.FromArgb(54, 211, 198);
        public static readonly Color Gold = Color.FromArgb(243, 198, 77);
        public static readonly Color Coral = Color.FromArgb(255, 118, 95);
        public static readonly Color Violet = Color.FromArgb(188, 167, 255);
    }

    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rect, float radius)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                float diameter = radius * 2.0f;
                path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }
}
