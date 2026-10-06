using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("VFORCE Vestibular Simulation Console")]
[assembly: AssemblyProduct("VFORCE Vestibular Simulation Console")]
[assembly: AssemblyDescription("Joystick and simulator telemetry bridge for VMocion-compatible vestibular hardware.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace VestibularJoystickSim
{
    internal static class App
    {
        private const string SingleInstanceName = @"Local\VMocion.VForce.VestibularJoystickSimulation";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return PacketBuilder.SelfTest() && MainForm.SelfTest() && VmocionUsbProtocol.SelfTest() ? 0 : 2;
            }

            if (args.Length == 2 && args[0] == "--render-preview")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (MainForm form = new MainForm())
                {
                    form.Show(); Application.DoEvents();
                    using (Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }

            if (args.Length == 3 && args[0] == "--usb-status")
            {
                try
                {
                    using (VmocionUsbMonitor monitor = new VmocionUsbMonitor(args[1]))
                    {
                        Stopwatch timeout = Stopwatch.StartNew();
                        while (!monitor.Verified && monitor.Error == null && timeout.ElapsedMilliseconds < 8000) Thread.Sleep(50);
                        VmocionUsbStatus status = monitor.Latest;
                        bool verified = monitor.Verified;
                        string report = "scope=FIRMWARE_STATUS_ONLY_NO_ANALOG_MEASUREMENT\r\n" +
                            "output_control_available=false\r\nactive_commands_sent=0\r\n" +
                            "usb_verified=" + verified + "\r\n" +
                            (status == null ? "firmware=unverified" : status.Summary + "\r\n" + status.Raw) +
                            "\r\nerror=" + (monitor.Error ?? "") + "\r\n";
                        System.IO.File.WriteAllText(args[2], report);
                        return verified ? 0 : 3;
                    }
                }
                catch (Exception ex) { System.IO.File.WriteAllText(args[2], "usb_verified=false\r\nerror=" + ex.Message); return 3; }
            }

            bool createdNew;
            using (Mutex instanceMutex = new Mutex(true, SingleInstanceName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("VFORCE is already running.", "VFORCE", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(instanceMutex);
            }
            return 0;
        }
    }

    internal sealed class MainForm : Form
    {
        private enum GameKind
        {
            Msfs,
            Forza
        }

        // USB firmware status uses 115200; transport runs outside the UI timers.
        private const double Deadzone = 0.01;
        // These constants mirror VMocion-main\legacy_demo-main\gvs.py.
        private const double GvsMaxRate = 3.14 / 4.0;
        private const double GvsMaxRamp = 0.03;
        private const double PacketValueToVolts = 0.15;
        private const double MaxCurrentPeakMilliamp = 2.5;
        private const double DefaultCurrentPeakMilliamp = 0.5;
        private const int CurrentPeakSliderScale = 100;

        private const int MsfsSimConnectMessage = 0x0402;

        private readonly PhysicalStickView stickView;
        private readonly PhysicalJoystickInput physicalInput;
        private readonly MsfsSimConnectInput msfsInput;
        private readonly ForzaUdpTelemetryInput forzaInput;
        private readonly HeadView headView;
        private readonly System.Windows.Forms.Timer timer;
        private readonly System.Windows.Forms.Timer visualTimer;
        private readonly VForceComboBox portCombo;
        private readonly Button connectButton;
        private readonly Button armButton;
        private readonly Button pauseButton;
        private readonly Button refreshButton;
        private readonly Button msfsLaunchButton;
        private readonly Button forzaLaunchButton;
        private readonly VForceSlider gainSlider;
        private readonly Label gainValueLabel;
        private readonly Label statusLabel;
        private readonly StatusBadge controllerBadge;
        private readonly StatusBadge deviceBadge;
        private readonly StatusBadge outputBadge;
        private readonly ToolTip toolTip;
        private readonly Image logoImage;
        private readonly Image msfsGameImage;
        private readonly Image forzaGameImage;
        private readonly Label inputLabel;

        private VmocionUsbMonitor serialPort;
        private DateTime lastTick;

        private DateTime lastSerialScan = DateTime.MinValue;
        private DateTime lastAutoConnectAttempt = DateTime.MinValue;
        private DateTime lastUiTextUpdate = DateTime.MinValue;
        private int serialDiscoveryRunning;
        private volatile bool closing;
        private bool useMsfsPhysics;
        private bool useForzaPhysics;
        private double lastRenderedInputX = double.NaN;
        private double lastRenderedInputY = double.NaN;
        private double lastRenderedYaw = double.NaN;
        private double lastRenderedPitch = double.NaN;
        private double lastRenderedRoll = double.NaN;
        private bool lastRenderedConnected;
        private bool hasRenderedConnection;
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
        private readonly double[] currentMilliampValues = new double[] { 0.0, 0.0, 0.0, 0.0 };
        private readonly double[] dacValues = new double[] { 0.0, 0.0, 0.0, 0.0 };
        private string lastPacketText = "-";
        private double currentPeakMilliamp = DefaultCurrentPeakMilliamp;
        private bool paused;
        private bool armed;
        private int packetCount;
        private int calibrationDirection;
        private int calibrationStep;
        private DateTime calibrationStart;

        public MainForm()
        {
            SuspendLayout();
            Text = "VFORCE | Vestibular Simulation Console";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1180, 650);
            MinimumSize = new Size(1000, 620);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9.0f);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            physicalInput = new PhysicalJoystickInput();
            msfsInput = new MsfsSimConnectInput(MsfsSimConnectMessage);
            forzaInput = new ForzaUdpTelemetryInput();
            toolTip = new ToolTip();
            toolTip.AutoPopDelay = 10000;
            toolTip.InitialDelay = 350;
            toolTip.ReshowDelay = 100;
            logoImage = LoadEmbeddedImage("VestibularJoystickSim.VForceLogo.png");
            msfsGameImage = LoadEmbeddedImageScaled("VestibularJoystickSim.MsfsGameIcon.png", 44, 44);
            forzaGameImage = LoadEmbeddedImageScaled("VestibularJoystickSim.ForzaGameIcon.png", 44, 44);

            VForceBackdrop backdrop = new VForceBackdrop();
            backdrop.Dock = DockStyle.Fill;
            Controls.Add(backdrop);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.RowCount = 4;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.Padding = new Padding(10);
            root.BackColor = Color.Transparent;
            backdrop.Controls.Add(root);

            TableLayoutPanel brandBar = new TableLayoutPanel();
            brandBar.Dock = DockStyle.Fill;
            brandBar.ColumnCount = 2;
            brandBar.RowCount = 1;
            brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            brandBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brandBar.Margin = new Padding(0, 0, 0, 14);
            brandBar.BackColor = Color.Transparent;
            root.Controls.Add(brandBar, 0, 0);

            if (logoImage != null)
            {
                PictureBox logo = new PictureBox();
                logo.Dock = DockStyle.None;
                logo.Size = new Size(270, 76);
                logo.Margin = new Padding(0);
                logo.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                logo.Image = logoImage;
                logo.SizeMode = PictureBoxSizeMode.Zoom;
                logo.Padding = new Padding(8, 6, 18, 8);
                logo.TabStop = false;
                logo.AccessibleName = "VFORCE";
                brandBar.Controls.Add(logo, 0, 0);
            }
            else
            {
                Label fallbackLogo = new Label();
                fallbackLogo.Text = "VFORCE";
                fallbackLogo.Dock = DockStyle.Fill;
                fallbackLogo.Font = new Font("Segoe UI", 24.0f, FontStyle.Bold);
                fallbackLogo.ForeColor = Theme.Text;
                fallbackLogo.TextAlign = ContentAlignment.MiddleLeft;
                brandBar.Controls.Add(fallbackLogo, 0, 0);
            }

            FlowLayoutPanel badgeFlow = new FlowLayoutPanel();
            badgeFlow.Dock = DockStyle.Fill;
            badgeFlow.FlowDirection = FlowDirection.RightToLeft;
            badgeFlow.WrapContents = false;
            badgeFlow.Padding = new Padding(0, 8, 0, 0);
            badgeFlow.BackColor = Color.Transparent;
            brandBar.Controls.Add(badgeFlow, 1, 0);

            outputBadge = new StatusBadge("OUTPUT");
            deviceBadge = new StatusBadge("VMOCION");
            controllerBadge = new StatusBadge("LEFT STICK");
            msfsLaunchButton = MakeGameButton("FLIGHT SIM", msfsGameImage);
            forzaLaunchButton = MakeGameButton("FORZA 5", forzaGameImage);
            msfsLaunchButton.Click += delegate { LaunchGame(GameKind.Msfs); };
            forzaLaunchButton.Click += delegate { LaunchGame(GameKind.Forza); };
            badgeFlow.Controls.Add(outputBadge);
            badgeFlow.Controls.Add(deviceBadge);
            badgeFlow.Controls.Add(controllerBadge);
            badgeFlow.Controls.Add(forzaLaunchButton);
            badgeFlow.Controls.Add(msfsLaunchButton);

            TableLayoutPanel controlDeck = new TableLayoutPanel();
            controlDeck.Dock = DockStyle.Fill;
            controlDeck.ColumnCount = 3;
            controlDeck.RowCount = 1;
            controlDeck.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            controlDeck.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            controlDeck.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            controlDeck.Margin = new Padding(0, 0, 0, 10);
            controlDeck.BackColor = Color.Transparent;
            root.Controls.Add(controlDeck, 0, 1);

            Panel gainCard = MakePanel();
            gainCard.Padding = new Padding(14, 9, 14, 10);
            gainCard.Margin = new Padding(0, 0, 8, 0);
            controlDeck.Controls.Add(gainCard, 0, 0);

            TableLayoutPanel gainLayout = MakeInnerLayout(2);
            gainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            gainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gainCard.Controls.Add(gainLayout);
            Label gainTitle = MakeSectionTitle("SOFTWARE PREVIEW   /   SCALE");
            gainTitle.Dock = DockStyle.Fill;
            gainLayout.Controls.Add(gainTitle, 0, 0);

            TableLayoutPanel gainControl = new TableLayoutPanel();
            gainControl.Dock = DockStyle.Fill;
            gainControl.ColumnCount = 3;
            gainControl.RowCount = 1;
            gainControl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            gainControl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            gainControl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            gainControl.BackColor = Color.Transparent;
            gainLayout.Controls.Add(gainControl, 0, 1);

            gainSlider = new VForceSlider();
            gainSlider.Minimum = 0;
            gainSlider.Maximum = (int)(MaxCurrentPeakMilliamp * CurrentPeakSliderScale);
            gainSlider.Value = (int)(DefaultCurrentPeakMilliamp * CurrentPeakSliderScale);
            gainSlider.SmallChange = 1;
            gainSlider.LargeChange = 25;
            gainSlider.Dock = DockStyle.Fill;
            gainSlider.Margin = new Padding(14, 2, 26, 0);
            gainSlider.AccessibleName = "Software preview current scale";
            gainSlider.AccessibleDescription = "Scales the software preview only; it does not set PCB output current.";
            gainSlider.Scroll += GainSliderScroll;
            gainControl.Controls.Add(gainSlider, 0, 0);

            gainValueLabel = MakeSmallLabel(FormatCurrentPeakLabel(DefaultCurrentPeakMilliamp));
            gainValueLabel.Dock = DockStyle.Fill;
            gainValueLabel.Font = new Font("Segoe UI", 12.0f, FontStyle.Bold);
            gainValueLabel.ForeColor = Theme.PurpleBright;
            gainValueLabel.TextAlign = ContentAlignment.MiddleCenter;
            gainValueLabel.Margin = new Padding(0);
            gainControl.Controls.Add(gainValueLabel, 2, 0);

            Panel deviceCard = MakePanel();
            deviceCard.Padding = new Padding(12, 9, 12, 9);
            deviceCard.Margin = new Padding(0, 0, 8, 0);
            controlDeck.Controls.Add(deviceCard, 1, 0);

            TableLayoutPanel deviceLayout = MakeInnerLayout(2);
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            deviceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            deviceCard.Controls.Add(deviceLayout);
            Label deviceTitle = MakeSectionTitle("DEVICE LINK   /   AUTO-DETECT");
            deviceTitle.Dock = DockStyle.Fill;
            deviceLayout.Controls.Add(deviceTitle, 0, 0);

            portCombo = MakeCombo();
            portCombo.Dock = DockStyle.None;
            portCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            portCombo.Margin = new Padding(0, 0, 4, 0);
            portCombo.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
            portCombo.AccessibleName = "VMocion COM port";

            refreshButton = MakeButton("Scan");
            refreshButton.Click += delegate { RefreshPorts(); TryAutoConnectVestibularDevice(); };

            connectButton = MakeButton("Connect");
            connectButton.Click += ConnectButtonClick;

            TableLayoutPanel deviceButtons = new TableLayoutPanel();
            deviceButtons.Dock = DockStyle.Fill;
            deviceButtons.ColumnCount = 3;
            deviceButtons.RowCount = 1;
            deviceButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            deviceButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            deviceButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            deviceButtons.BackColor = Color.Transparent;
            deviceLayout.Controls.Add(deviceButtons, 0, 1);
            SetFillButton(refreshButton);
            SetFillButton(connectButton);
            deviceButtons.Controls.Add(portCombo, 0, 0);
            deviceButtons.Controls.Add(refreshButton, 1, 0);
            deviceButtons.Controls.Add(connectButton, 2, 0);

            armButton = MakeButton("Arm");
            armButton.Enabled = false;
            armButton.Click += ArmButtonClick;

            pauseButton = MakeButton("Pause");
            pauseButton.Click += PauseButtonClick;

            Button diagnosticsButton = MakeButton("Advanced");
            diagnosticsButton.Width = 108;
            diagnosticsButton.Click += ShowDiagnostics;

            Panel simulationCard = MakePanel();
            simulationCard.Padding = new Padding(12, 9, 12, 9);
            simulationCard.Margin = new Padding(0);
            controlDeck.Controls.Add(simulationCard, 2, 0);

            TableLayoutPanel simulationLayout = MakeInnerLayout(2);
            simulationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            simulationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            simulationCard.Controls.Add(simulationLayout);
            Label simulationTitle = MakeSectionTitle("OUTPUT CONTROL");
            simulationTitle.Dock = DockStyle.Fill;
            simulationLayout.Controls.Add(simulationTitle, 0, 0);

            TableLayoutPanel actionFlow = new TableLayoutPanel();
            actionFlow.Dock = DockStyle.Fill;
            actionFlow.ColumnCount = 3;
            actionFlow.RowCount = 1;
            actionFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            actionFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            actionFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
            actionFlow.Margin = new Padding(0);
            actionFlow.Padding = new Padding(0, 1, 0, 0);
            actionFlow.BackColor = Color.Transparent;
            SetFillButton(armButton);
            SetFillButton(pauseButton);
            SetFillButton(diagnosticsButton);
            actionFlow.Controls.Add(armButton, 0, 0);
            actionFlow.Controls.Add(pauseButton, 1, 0);
            actionFlow.Controls.Add(diagnosticsButton, 2, 0);
            simulationLayout.Controls.Add(actionFlow, 0, 1);

            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.ColumnCount = 2;
            workspace.RowCount = 1;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            workspace.Margin = new Padding(0, 0, 0, 10);
            workspace.BackColor = Color.Transparent;
            root.Controls.Add(workspace, 0, 2);

            Panel leftPanel = MakePanel();
            leftPanel.Padding = new Padding(14);
            leftPanel.Margin = new Padding(0, 0, 8, 0);
            workspace.Controls.Add(leftPanel, 0, 0);

            Label leftTitle = MakeSectionTitle("LEFT STICK");
            leftTitle.Dock = DockStyle.Top;
            leftPanel.Controls.Add(leftTitle);

            inputLabel = MakeMetric("Input", "Waiting for XInput left stick");
            inputLabel.Dock = DockStyle.Bottom;
            inputLabel.Height = 44;
            inputLabel.Padding = new Padding(10, 12, 10, 6);
            inputLabel.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            leftPanel.Controls.Add(inputLabel);

            stickView = new PhysicalStickView();
            stickView.Dock = DockStyle.Fill;
            stickView.Margin = new Padding(0, 18, 0, 18);
            leftPanel.Controls.Add(stickView);
            stickView.BringToFront();

            Panel headCard = MakePanel();
            headCard.Padding = new Padding(1);
            headCard.Margin = new Padding(2, 0, 0, 0);
            workspace.Controls.Add(headCard, 1, 0);

            headView = new HeadView();
            headView.Dock = DockStyle.Fill;
            headView.Margin = new Padding(0);
            headCard.Controls.Add(headView);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.Margin = new Padding(0);
            footer.BackColor = Theme.SurfaceDeep;
            footer.Paint += PaintFooterBorder;
            root.Controls.Add(footer, 0, 3);

            TableLayoutPanel footerLayout = new TableLayoutPanel();
            footerLayout.Dock = DockStyle.Fill;
            footerLayout.ColumnCount = 2;
            footerLayout.RowCount = 1;
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            footerLayout.Padding = new Padding(10, 0, 10, 0);
            footerLayout.BackColor = Color.Transparent;
            footer.Controls.Add(footerLayout);

            statusLabel = new Label();
            statusLabel.Text = "SYSTEM  /  STARTING";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.ForeColor = Theme.PurpleBright;
            statusLabel.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.AutoEllipsis = true;
            footerLayout.Controls.Add(statusLabel, 0, 0);

            Label safetyFooter = new Label();
            safetyFooter.Text = "ENGINEERING PREVIEW   |   PHYSICAL OUTPUT CONTROL UNAVAILABLE";
            safetyFooter.Dock = DockStyle.Fill;
            safetyFooter.ForeColor = Theme.Warning;
            safetyFooter.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            safetyFooter.TextAlign = ContentAlignment.MiddleRight;
            safetyFooter.AutoEllipsis = true;
            footerLayout.Controls.Add(safetyFooter, 1, 0);

            toolTip.SetToolTip(gainSlider, "Software preview scale only; not a physical current setting or biological safety limit.");
            toolTip.SetToolTip(portCombo, "Detects a XIAO application USB port, then verifies firmware status. Legacy FTDI devices are unsupported in this build.");
            toolTip.SetToolTip(refreshButton, "Rescan Windows serial devices.");
            toolTip.SetToolTip(connectButton, "Connect or disconnect the XIAO USB status monitor. This does not authorize electrical output.");
            toolTip.SetToolTip(armButton, "Physical output control is unavailable in this engineering USB compatibility build.");
            toolTip.SetToolTip(msfsLaunchButton, "Open Microsoft Flight Simulator and select its motion feed.");
            toolTip.SetToolTip(forzaLaunchButton, "Open Forza Horizon 5 and select its motion feed.");
            toolTip.SetToolTip(safetyFooter, safetyFooter.Text);

            portCombo.TabIndex = 0;
            refreshButton.TabIndex = 1;
            connectButton.TabIndex = 2;
            armButton.TabIndex = 3;
            pauseButton.TabIndex = 4;
            diagnosticsButton.TabIndex = 5;
            gainSlider.TabIndex = 6;

            lastTick = DateTime.UtcNow;
            useMsfsPhysics = false;
            useForzaPhysics = false;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 25;
            timer.Tick += TimerTick;
            timer.Start();

            visualTimer = new System.Windows.Forms.Timer();
            visualTimer.Interval = 16;
            visualTimer.Tick += VisualTimerTick;
            visualTimer.Start();

            RefreshPorts();
            TryAutoConnectVestibularDevice();
            UpdateUi();
            Shown += delegate
            {
                if (Screen.FromControl(this).WorkingArea.Height <= 800)
                {
                    WindowState = FormWindowState.Maximized;
                }
                connectButton.Select();
            };
            ResumeLayout(true);
        }

        public static bool SelfTest()
        {
            return IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, 0.0, MaxCurrentPeakMilliamp)), new int[] { 128, 128, 128, 128 }) &&
                   PacketValuesEqual(CurrentBytesToDacVolts(new int[] { 128, 128, 128, 128 }), new double[] { 0.0, 0.0, 0.0, 0.0 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(GvsMaxRate, 0.0, 0.0, MaxCurrentPeakMilliamp)), new int[] { 3, 3, 253, 253 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, GvsMaxRate, 0.0, MaxCurrentPeakMilliamp)), new int[] { 253, 3, 3, 253 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, GvsMaxRate, MaxCurrentPeakMilliamp)), new int[] { 3, 253, 3, 253 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(-GvsMaxRate, 0.0, 0.0, MaxCurrentPeakMilliamp)), new int[] { 253, 253, 3, 3 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, -GvsMaxRate, 0.0, MaxCurrentPeakMilliamp)), new int[] { 3, 253, 253, 3 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(0.0, 0.0, -GvsMaxRate, MaxCurrentPeakMilliamp)), new int[] { 253, 3, 253, 3 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(GvsMaxRate, 0.0, 0.0, 0.0)), new int[] { 128, 128, 128, 128 }) &&
                   IntValuesEqual(CurrentsToUartBytes(PqrToCodeMatrixCurrents(GvsMaxRate, 0.0, 0.0, 1.25)), new int[] { 65, 65, 190, 190 }) &&
                   PacketValuesEqual(CurrentBytesToPacketValues(new int[] { 3, 3, 253, 253 }), new double[] { -125.0, -125.0, 125.0, 125.0 }) &&
                   PacketValuesEqual(CurrentBytesToDacVolts(new int[] { 3, 3, 253, 253 }), new double[] { -18.75, -18.75, 18.75, 18.75 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(1.0, 0.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 1.0, 0, 0.0), new double[] { 0.0, GvsMaxRate, 0.0 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.5, 0.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate * 0.5 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 0.0, 1, 1.0), new double[] { GvsMaxRate, 0.0, 0.0 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0)), new double[] { 0.1, 0.2, 0.3 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 2.0, -2.0, 2.0, 0.0, 0.0, 0.0)), new double[] { GvsMaxRate, -GvsMaxRate, GvsMaxRate }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Empty("MSFS off")), new double[] { 0.0, 0.0, 0.0 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("MSFS", -0.1, 0.2, -0.3, 0.0, 0.0, 0.0)), new double[] { -0.1, 0.2, -0.3 }) &&
                   SelectGameMotion(true, MotionSnapshot.Fresh("MSFS", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0), true, MotionSnapshot.Fresh("Forza", -0.1, -0.2, -0.3, 0.0, 0.0, 0.0)).Source == "MSFS" &&
                   SelectGameMotion(false, MotionSnapshot.Empty("MSFS off"), true, MotionSnapshot.Fresh("Forza", -0.1, -0.2, -0.3, 0.0, 0.0, 0.0)).Source == "Forza" &&
                   ForzaUdpTelemetryInput.SelfTest() &&
                   MsfsSimConnectInput.SelfTest();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            timer.Stop();
            visualTimer.Stop();
            SafeDisarmAndClose();
            msfsInput.Dispose();
            forzaInput.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (logoImage != null)
            {
                logoImage.Dispose();
            }
            if (msfsGameImage != null)
            {
                msfsGameImage.Dispose();
            }
            if (forzaGameImage != null)
            {
                forzaGameImage.Dispose();
            }
            toolTip.Dispose();
            base.OnFormClosed(e);
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
            try
            {
                TimerTickCore();
            }
            catch (Exception ex)
            {
                timer.Stop();
                armed = false;
                paused = true;
                try
                {
                    SafeDisarmAndClose();
                }
                catch
                {
                    // The output path may itself be the source of the failure.
                }
                SetStatus("Preview stopped; USB closed");
                MessageBox.Show("VFORCE stopped the preview and closed USB after an unexpected error.\r\n\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TimerTickCore()
        {
            DateTime now = DateTime.UtcNow;
            if ((serialPort == null || !serialPort.IsOpen) && (now - lastSerialScan).TotalSeconds >= 5.0)
            {
                lastSerialScan = now;
                TryAutoConnectVestibularDevice();
            }

            double dt = Math.Min((now - lastTick).TotalSeconds, 0.05);
            lastTick = now;
            UpdateMsfsInput();
            UpdateForzaInput();

            if (!paused)
            {
                double targetYaw;
                double targetPitch;
                double targetRoll;
                GetVisualTargets(out targetYaw, out targetPitch, out targetRoll);
                double response = 1.0 - Math.Pow(0.001, dt);

                yaw = Lerp(yaw, targetYaw, response);
                pitch = Lerp(pitch, targetPitch, response);
                roll = Lerp(roll, targetRoll, response);
            }

            UpdateVestibularOutput();
            SendVestibularOutput();
        }

        private void VisualTimerTick(object sender, EventArgs e)
        {
            try
            {
                UpdatePhysicalStickInput();
                UpdateUi(false);
            }
            catch
            {
                visualTimer.Stop();
                SetStatus("Display paused - output safe");
            }
        }

        private void GetVisualTargets(out double targetYaw, out double targetPitch, out double targetRoll)
        {
            double visualGain = Clamp(currentPeakMilliamp / MaxCurrentPeakMilliamp, 0.0, 1.0);
            MotionSnapshot visualMotion = GetFreshGameMotion();
            if (visualMotion.IsFresh)
            {
                targetYaw = Clamp((visualMotion.R * 42.0) + (ApplyDeadzone(inputX) * 20.0 * visualGain), -65.0, 65.0);
                targetPitch = Clamp(visualMotion.PitchDeg + (ApplyDeadzone(inputY) * 14.0 * visualGain), -55.0, 55.0);
                targetRoll = Clamp(visualMotion.RollDeg + (ApplyDeadzone(inputX) * 8.0 * visualGain), -28.0, 28.0);
            }
            else
            {
                targetYaw = Clamp(ApplyDeadzone(inputX) * 48.0 * visualGain, -65.0, 65.0);
                targetPitch = Clamp(ApplyDeadzone(inputY) * 38.0 * visualGain, -55.0, 55.0);
                targetRoll = Clamp(ApplyDeadzone(inputX) * 16.0 * visualGain, -28.0, 28.0);
            }
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

            double[] currents = PqrToCodeMatrixCurrents(pRate, qRate, rRate, currentPeakMilliamp);
            int[] bytes = CurrentsToUartBytes(currents);
            double[] values = CurrentBytesToPacketValues(bytes);
            double[] volts = CurrentBytesToDacVolts(bytes);
            for (int i = 0; i < 4; i++)
            {
                packetValues[i] = values[i];
                currentBytes[i] = bytes[i];
                currentMilliampValues[i] = currents[i];
                dacValues[i] = volts[i];
            }
        }

        private void SendVestibularOutput()
        {
            // This engineering build never sends an output or activation packet.
            // Joystick/game telemetry remains a software preview.
            armed = false;
            if (serialPort != null && serialPort.Error != null)
            {
                string reason = serialPort.Error;
                SafeDisarmAndClose();
                SetStatus("USB check failed: " + reason);
            }
        }

        private void SendNeutralOutput()
        {
            SendNeutralOutput(true);
        }

        private void SendNeutralOutput(bool ignoreWriteErrors)
        {
            pRate = qRate = rRate = commandedP = commandedQ = commandedR = 0.0;
            for (int i = 0; i < 4; ++i)
            {
                packetValues[i] = currentMilliampValues[i] = dacValues[i] = 0.0;
                currentBytes[i] = 128;
            }
            armed = false;
        }

        private void ConnectButtonClick(object sender, EventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                SafeDisarmAndClose();
                return;
            }

            TryConnectSelectedPort(true);
        }

        private bool TryConnectSelectedPort(bool interactive)
        {
            if (portCombo.SelectedItem == null)
            {
                SetStatus("Connect XIAO USB, then refresh ports");
                return false;
            }
            try
            {
                serialPort = new VmocionUsbMonitor(portCombo.SelectedItem.ToString());
                packetCount = 0;
                lastPacketText = "Status requests only; no output packets";
                connectButton.Text = "Disconnect";
                portCombo.Enabled = false;
                armButton.Enabled = false;
                SetStatus("Checking firmware - output control unavailable");
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                if (serialPort != null) serialPort.Dispose();
                serialPort = null;
                SetStatus("Connect failed: " + ex.Message);
                if (interactive) MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateUi();
                return false;
            }
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
            if (paused)
            {
                return new double[] { 0.0, 0.0, 0.0 };
            }

            if (calibrationDirection != 0)
            {
                return JoystickToCommandedRates(activeX, activeY, calibrationDirection, calibrationIntensity);
            }

            double[] stickCommand = JoystickToCommandedRates(activeX, activeY, 0, 0.0);
            double[] output = new double[] { stickCommand[0], stickCommand[1], stickCommand[2] };

            MotionSnapshot gameMotion = GetFreshGameMotion();
            if (gameMotion.IsFresh)
            {
                AddCommandedRates(output, PhysicsMotionToCommandedRates(gameMotion));
            }

            return output;
        }

        private static void AddCommandedRates(double[] target, double[] addition)
        {
            target[0] = Clamp(target[0] + addition[0], -GvsMaxRate, GvsMaxRate);
            target[1] = Clamp(target[1] + addition[1], -GvsMaxRate, GvsMaxRate);
            target[2] = Clamp(target[2] + addition[2], -GvsMaxRate, GvsMaxRate);
        }

        private MotionSnapshot GetFreshGameMotion()
        {
            return SelectGameMotion(UseMsfsPhysics(), msfsMotion, UseForzaPhysics(), forzaMotion);
        }

        private static MotionSnapshot SelectGameMotion(bool useMsfs, MotionSnapshot msfs, bool useForza, MotionSnapshot forza)
        {
            // MSFS has deterministic priority when both sources are live. This keeps
            // the visualized motion identical to the motion sent to the hardware.
            if (useMsfs && msfs.IsFresh)
            {
                return msfs;
            }

            if (useForza && forza.IsFresh)
            {
                return forza;
            }

            return MotionSnapshot.Empty("No game physics");
        }

        private bool UseMsfsPhysics()
        {
            return useMsfsPhysics;
        }

        private bool UseForzaPhysics()
        {
            return useForzaPhysics;
        }

        private void ArmButtonClick(object sender, EventArgs e)
        {
            armed = false;
            SetStatus("Physical output control unavailable in this engineering build");
            UpdateUi();
        }

        private void PauseButtonClick(object sender, EventArgs e)
        {
            paused = !paused;
            if (paused)
            {
                StopCalibration();
                SendNeutralOutput();
                SetStatus("Preview paused; no output commands sent");
            }
            else if (armed)
            {
                SetStatus("Physical output control unavailable");
            }
            else if (serialPort != null && serialPort.IsOpen)
            {
                SetStatus("Connected");
            }
            else
            {
                SetStatus("Ready");
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
            currentPeakMilliamp = Clamp(gainSlider.Value / (double)CurrentPeakSliderScale, 0.0, MaxCurrentPeakMilliamp);
            gainValueLabel.Text = FormatCurrentPeakLabel(currentPeakMilliamp);
        }

        private static string FormatCurrentPeakLabel(double currentPeakMilliamp)
        {
            return currentPeakMilliamp.ToString("0.00") + " mA";
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
                SetStatus("Preview ready");
            }

            return 0.0;
        }

        private void RefreshPorts()
        {
            try
            {
                string[] ports = SerialPort.GetPortNames();
                Array.Sort(ports, ComparePortNames);
                ApplyPortSnapshot(ports, null, false);
            }
            catch
            {
                SetStatus("USB scan unavailable");
            }
        }

        private void TryAutoConnectVestibularDevice()
        {
            if (closing || (serialPort != null && serialPort.IsOpen))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if ((now - lastAutoConnectAttempt).TotalSeconds < 4.0 ||
                Interlocked.CompareExchange(ref serialDiscoveryRunning, 1, 0) != 0)
            {
                return;
            }

            lastAutoConnectAttempt = now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string[] ports = new string[0];
                string vestibularPort = null;
                try
                {
                    ports = SerialPort.GetPortNames();
                    Array.Sort(ports, ComparePortNames);
                    vestibularPort = FindPortByHardwareText(ports, new string[]
                    {
                        "VID_2886&PID_8045", "VID_2886&PID_0145"
                    });
                }
                catch
                {
                }
                finally
                {
                    Interlocked.Exchange(ref serialDiscoveryRunning, 0);
                }

                if (closing || IsDisposed || !IsHandleCreated)
                {
                    return;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        ApplyPortSnapshot(ports, vestibularPort, true);
                    }));
                }
                catch
                {
                }
            });
        }

        private void ApplyPortSnapshot(string[] ports, string vestibularPort, bool attemptAutoConnect)
        {
            if (closing)
            {
                return;
            }

            string previous = portCombo.SelectedItem as string;
            bool changed = portCombo.Items.Count != ports.Length;
            if (!changed)
            {
                for (int i = 0; i < ports.Length; i++)
                {
                    if (!string.Equals(portCombo.Items[i] as string, ports[i], StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                portCombo.Items.Clear();
                portCombo.Items.AddRange(ports);
            }

            string preferred = vestibularPort;
            if (preferred == null && !string.IsNullOrEmpty(previous) && PortExists(ports, previous))
            {
                preferred = previous;
            }
            if (preferred == null && ports.Length > 0)
            {
                preferred = ports[0];
            }
            if (preferred != null && portCombo.Items.Contains(preferred))
            {
                portCombo.SelectedItem = preferred;
            }

            if (serialPort == null || !serialPort.IsOpen)
            {
                SetStatus(ports.Length == 0 ? "Connect VMocion USB" : "VMocion ready to connect");
            }

            if (attemptAutoConnect && vestibularPort != null && (serialPort == null || !serialPort.IsOpen))
            {
                TryConnectSelectedPort(false);
            }
        }

        private void SafeDisarmAndClose()
        {
            armed = false;
            SendNeutralOutput();

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
            portCombo.Enabled = true;
            SetStatus("Ready");
            UpdateUi();
        }

        private void UpdateUi()
        {
            UpdateUi(true);
        }

        private void UpdateUi(bool forceText)
        {
            MotionSnapshot activeGameMotion = GetFreshGameMotion();
            double displayYaw;
            double displayPitch;
            double displayRoll;
            GetVisualTargets(out displayYaw, out displayPitch, out displayRoll);
            if (paused)
            {
                displayYaw = 0.0;
                displayPitch = 0.0;
                displayRoll = 0.0;
            }

            bool headChanged = double.IsNaN(lastRenderedYaw) ||
                Math.Abs(displayYaw - lastRenderedYaw) >= 0.04 ||
                Math.Abs(displayPitch - lastRenderedPitch) >= 0.04 ||
                Math.Abs(displayRoll - lastRenderedRoll) >= 0.04;
            if (headChanged)
            {
                headView.Yaw = displayYaw;
                headView.Pitch = displayPitch;
                headView.Roll = displayRoll;
                headView.VectorX = inputX;
                headView.VectorY = inputY;
                headView.PRate = pRate;
                headView.QRate = qRate;
                headView.RRate = rRate;
                headView.Invalidate();
                lastRenderedYaw = displayYaw;
                lastRenderedPitch = displayPitch;
                lastRenderedRoll = displayRoll;
            }

            bool stickChanged = double.IsNaN(lastRenderedInputX) ||
                Math.Abs(inputX - lastRenderedInputX) >= 0.002 ||
                Math.Abs(inputY - lastRenderedInputY) >= 0.002 ||
                !hasRenderedConnection || lastRenderedConnected != physicalInputConnected;
            if (stickChanged || forceText)
            {
                stickView.VectorX = inputX;
                stickView.VectorY = inputY;
                stickView.Connected = physicalInputConnected;
                stickView.ControllerIndex = physicalInputIndex;
                stickView.SourceName = physicalInputConnected ? "ROG Ally left stick ready" : "Waiting for controller";
                stickView.Invalidate();
                lastRenderedInputX = inputX;
                lastRenderedInputY = inputY;
                lastRenderedConnected = physicalInputConnected;
                hasRenderedConnection = true;
            }

            DateTime now = DateTime.UtcNow;
            if (!forceText && (now - lastUiTextUpdate).TotalMilliseconds < 100.0)
            {
                return;
            }
            lastUiTextUpdate = now;

            bool msfsFresh = UseMsfsPhysics() && msfsMotion.IsFresh;
            bool forzaFresh = UseForzaPhysics() && forzaMotion.IsFresh;
            string inputState = physicalInputConnected ? "LEFT STICK READY" : "CONNECT CONTROLLER";
            if (paused)
            {
                inputState = "PAUSED  |  SOFTWARE PREVIEW";
            }
            else if (activeGameMotion.IsFresh)
            {
                inputState = FriendlyGameName(activeGameMotion.Source) + " MOTION LIVE";
            }
            else if (UseMsfsPhysics())
            {
                inputState = "FLIGHT SIM SELECTED  |  START THE GAME";
            }
            else if (UseForzaPhysics())
            {
                inputState = "FORZA SELECTED  |  START DRIVING";
            }
            inputLabel.Text = inputState;

            armButton.Text = "Preview only";
            armButton.Enabled = false;
            armButton.BackColor = armed ? Theme.Danger : Theme.Purple;
            armButton.ForeColor = Color.White;
            armButton.FlatAppearance.BorderColor = armed ? Theme.Danger : Theme.PurpleBright;
            pauseButton.Text = paused ? "Resume" : "Pause";

            controllerBadge.SetStatus(physicalInputConnected ? "XINPUT " + (physicalInputIndex + 1).ToString() : "SEARCHING",
                physicalInputConnected ? BadgeState.Ready : BadgeState.Neutral);
            bool deviceConnected = serialPort != null && serialPort.IsOpen;
            string selectedPort = portCombo.SelectedItem as string;
            bool verified = deviceConnected && serialPort.Verified;
            deviceBadge.SetStatus(verified ? "USB VERIFIED" : (deviceConnected ? "CHECKING USB" : "DISCONNECTED"),
                verified ? BadgeState.Ready : BadgeState.Neutral);
            outputBadge.SetStatus(verified ? "REPORTED OFF" : "UNVERIFIED", BadgeState.Neutral);
            if (deviceConnected && serialPort.Latest != null)
                SetStatus(verified ? ((!serialPort.Latest.FaultHealthy || serialPort.Latest.FaultLatched) ? "Hardware fault reported; preview only" : "USB verified; preview only") : "Checking firmware; preview only");

            UpdateGameButton(msfsLaunchButton, "FLIGHT SIM", UseMsfsPhysics(), msfsFresh);
            UpdateGameButton(forzaLaunchButton, "FORZA 5", UseForzaPhysics(), forzaFresh);
        }

        private void SetStatus(string text)
        {
            if (statusLabel != null)
            {
                statusLabel.Text = "SYSTEM  /  " + text.ToUpperInvariant();
                toolTip.SetToolTip(statusLabel, text);
            }
        }

        private void LaunchGame(GameKind game)
        {
            string appId;
            string displayName;
            if (game == GameKind.Msfs)
            {
                appId = "Microsoft.FlightSimulator_8wekyb3d8bbwe!App";
                displayName = "Flight Simulator";
                useForzaPhysics = false;
                useMsfsPhysics = true;
            }
            else
            {
                appId = "Microsoft.624F8B84B80_8wekyb3d8bbwe!Forzahorizon5";
                displayName = "Forza Horizon 5";
                useMsfsPhysics = false;
                useForzaPhysics = true;
            }

            msfsInput.ResetStatus();
            forzaInput.ResetStatus();
            SendNeutralOutput();

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
                SetStatus("Opening " + displayName);
            }
            catch (Exception ex)
            {
                SetStatus(displayName + " is not available");
                MessageBox.Show("Could not open " + displayName + ".\r\n\r\n" + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            UpdateUi();
        }

        private static string FriendlyGameName(string source)
        {
            return source != null && source.IndexOf("Forza", StringComparison.OrdinalIgnoreCase) >= 0
                ? "FORZA"
                : "FLIGHT SIM";
        }

        private static void UpdateGameButton(Button button, string name, bool selected, bool live)
        {
            button.Text = name + "\r\n" + (live ? "LIVE" : (selected ? "STARTING" : "OPEN"));
            button.BackColor = live ? Theme.Purple : (selected ? Theme.PurpleDim : Theme.SurfaceDeep);
            button.FlatAppearance.BorderColor = live ? Theme.Success : (selected ? Theme.PurpleBright : Theme.PurpleDim);
        }

        private static TableLayoutPanel MakeInnerLayout(int rows)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = rows;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Margin = new Padding(0);
            layout.Padding = new Padding(0);
            layout.BackColor = Color.Transparent;
            return layout;
        }

        private static void SetFillButton(Button button)
        {
            button.Dock = DockStyle.Fill;
            button.Width = 0;
            button.Margin = new Padding(3, 3, 0, 0);
        }

        private static Image LoadEmbeddedImage(string resourceName)
        {
            try
            {
                using (System.IO.Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    using (Image source = Image.FromStream(stream))
                    {
                        return new Bitmap(source);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static Image LoadEmbeddedImageScaled(string resourceName, int width, int height)
        {
            using (Image source = LoadEmbeddedImage(resourceName))
            {
                if (source == null)
                {
                    return null;
                }

                Bitmap result = new Bitmap(width, height);
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                }
                return result;
            }
        }

        private void ShowDiagnostics(object sender, EventArgs e)
        {
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames();
                Array.Sort(ports, ComparePortNames);
            }
            catch
            {
                ports = new string[0];
            }

            MotionSnapshot gameMotion = GetFreshGameMotion();
            string diagnosticText =
                "VFORCE DIAGNOSTICS\r\n" +
                "Version: " + Assembly.GetExecutingAssembly().GetName().Version.ToString() + "\r\n" +
                "Controller: " + (physicalInputConnected ? physicalInputSource : "not detected") + "\r\n" +
                "Left stick: X " + inputX.ToString("0.000") + "  Y " + inputY.ToString("0.000") + "\r\n" +
                "Game physics: " + (gameMotion.IsFresh ? gameMotion.Source + " live" : "waiting") + "\r\n" +
                "MSFS: " + msfsInputStatus + "\r\n" +
                "Forza: " + forzaInputStatus + "\r\n" +
                "VMocion: " + ((serialPort != null && serialPort.IsOpen) ? serialPort.PortName + " connected" : "not connected") + "\r\n" +
                "Available ports: " + (ports.Length == 0 ? "none" : string.Join(", ", ports)) + "\r\n" +
                "Output: " + ((serialPort != null && serialPort.Verified) ? "REPORTED OFF" : "UNVERIFIED") + "\r\n" +
                "Firmware: " + ((serialPort != null && serialPort.Latest != null) ? serialPort.Latest.Summary : "not verified") + "\r\n" +
                "Last RX: " + ((serialPort != null && serialPort.Latest != null) ? serialPort.Latest.Raw : "none") + "\r\n" +
                "Analog current feedback: unavailable on this PCB\r\n" +
                "Software preview peak: " + FormatCurrentPeakLabel(currentPeakMilliamp) + "\r\n" +
                "Output packets sent: " + packetCount.ToString() + "\r\n" +
                "Last TX: " + lastPacketText;

            Form dialog = new Form();
            dialog.Text = "VFORCE Advanced";
            dialog.Icon = Icon;
            dialog.ClientSize = new Size(700, 440);
            dialog.MinimumSize = new Size(620, 400);
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.BackColor = Theme.Background;
            dialog.ForeColor = Theme.Text;
            dialog.Font = Font;
            dialog.ShowInTaskbar = false;

            TextBox report = new TextBox();
            report.Multiline = true;
            report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Vertical;
            report.Dock = DockStyle.Fill;
            report.Text = diagnosticText;
            report.BackColor = Theme.SurfaceDeep;
            report.ForeColor = Theme.Text;
            report.BorderStyle = BorderStyle.FixedSingle;
            report.Font = new Font("Consolas", 9.5f);
            report.Margin = new Padding(0);
            report.TabStop = false;

            FlowLayoutPanel deviceTools = new FlowLayoutPanel();
            deviceTools.Dock = DockStyle.Top;
            deviceTools.Height = 62;
            deviceTools.FlowDirection = FlowDirection.LeftToRight;
            deviceTools.WrapContents = false;
            deviceTools.Padding = new Padding(10, 9, 10, 7);
            deviceTools.BackColor = Theme.Surface;

            VForceComboBox portPicker = MakeCombo();
            portPicker.Width = 118;
            for (int i = 0; i < ports.Length; i++) portPicker.Items.Add(ports[i]);
            string selectedPort = portCombo.SelectedItem as string;
            if (selectedPort != null && portPicker.Items.Contains(selectedPort)) portPicker.SelectedItem = selectedPort;
            else if (portPicker.Items.Count > 0) portPicker.SelectedIndex = 0;

            Button scan = MakeButton("Scan USB");
            scan.Width = 92;
            scan.Click += delegate
            {
                try
                {
                    string[] freshPorts = SerialPort.GetPortNames();
                    Array.Sort(freshPorts, ComparePortNames);
                    portPicker.Items.Clear();
                    portPicker.Items.AddRange(freshPorts);
                    if (freshPorts.Length > 0) portPicker.SelectedIndex = 0;
                    RefreshPorts();
                    TryAutoConnectVestibularDevice();
                }
                catch
                {
                    scan.Text = "Scan failed";
                }
            };

            Button manualConnect = MakeButton(serialPort != null && serialPort.IsOpen ? "Disconnect" : "Connect");
            manualConnect.Width = 96;
            manualConnect.Click += delegate
            {
                string requestedPort = portPicker.SelectedItem as string;
                dialog.Close();
                if (serialPort != null && serialPort.IsOpen)
                {
                    SafeDisarmAndClose();
                }
                else if (!string.IsNullOrEmpty(requestedPort))
                {
                    if (!portCombo.Items.Contains(requestedPort)) portCombo.Items.Add(requestedPort);
                    portCombo.SelectedItem = requestedPort;
                    TryConnectSelectedPort(true);
                }
            };

            Button resetAdvanced = MakeButton("Reset view");
            resetAdvanced.Width = 100;
            resetAdvanced.Click += delegate { ResetButtonClick(resetAdvanced, EventArgs.Empty); };
            deviceTools.Controls.Add(portPicker);
            deviceTools.Controls.Add(scan);
            deviceTools.Controls.Add(manualConnect);
            deviceTools.Controls.Add(resetAdvanced);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Bottom;
            actions.Height = 52;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Padding = new Padding(8);
            actions.BackColor = Theme.Surface;

            Button close = MakeButton("Close");
            close.DialogResult = DialogResult.OK;
            Button copy = MakeButton("Copy report");
            copy.Width = 104;
            copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(diagnosticText);
                    copy.Text = "Copied";
                }
                catch
                {
                    copy.Text = "Copy failed";
                }
            };
            actions.Controls.Add(close);
            actions.Controls.Add(copy);
            TableLayoutPanel dialogLayout = new TableLayoutPanel();
            dialogLayout.Dock = DockStyle.Fill;
            dialogLayout.ColumnCount = 1;
            dialogLayout.RowCount = 3;
            dialogLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            dialogLayout.BackColor = Theme.Background;
            deviceTools.Dock = DockStyle.Fill;
            actions.Dock = DockStyle.Fill;
            report.Dock = DockStyle.Fill;
            dialogLayout.Controls.Add(deviceTools, 0, 0);
            dialogLayout.Controls.Add(report, 0, 1);
            dialogLayout.Controls.Add(actions, 0, 2);
            dialog.Controls.Add(dialogLayout);
            dialog.AcceptButton = close;
            dialog.CancelButton = close;
            dialog.Shown += delegate { close.Select(); };
            dialog.ShowDialog(this);
            dialog.Dispose();
        }

        private static Label MakeMetric(string name, string value)
        {
            Label label = new Label();
            label.Text = name + ": " + value;
            label.ForeColor = Theme.Text;
            label.BackColor = Theme.SurfaceDeep;
            label.Padding = new Padding(10, 7, 10, 0);
            label.Margin = new Padding(0, 0, 0, 5);
            label.Height = 31;
            label.MinimumSize = new Size(0, 31);
            label.Font = new Font("Consolas", 8.4f);
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
            label.ForeColor = Theme.SecondaryText;
            label.Font = new Font("Bahnschrift SemiCondensed", 8.5f, FontStyle.Bold);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static VForceComboBox MakeCombo()
        {
            VForceComboBox combo = new VForceComboBox();
            combo.BackColor = Theme.SurfaceDeep;
            combo.ForeColor = Theme.Text;
            combo.Height = 32;
            combo.Margin = new Padding(4, 6, 4, 4);
            return combo;
        }

        private static Button MakeButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 82;
            button.Height = 44;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Theme.PurpleDim;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Theme.PurpleDim;
            button.FlatAppearance.MouseDownBackColor = Theme.Purple;
            button.BackColor = Theme.Control;
            button.ForeColor = Theme.Text;
            button.Font = new Font("Bahnschrift SemiCondensed", 8.3f, FontStyle.Bold);
            button.Margin = new Padding(0, 0, 6, 0);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static Button MakeGameButton(string text, Image image)
        {
            Button button = MakeButton(text + "\r\nOPEN");
            button.Width = 154;
            button.Height = 58;
            button.Margin = new Padding(6, 0, 0, 0);
            button.Padding = new Padding(6, 2, 8, 2);
            button.Image = image;
            button.ImageAlign = ContentAlignment.MiddleLeft;
            button.TextAlign = ContentAlignment.MiddleRight;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
            button.Font = new Font("Bahnschrift SemiCondensed", 8.5f, FontStyle.Bold);
            button.AccessibleRole = AccessibleRole.PushButton;
            return button;
        }

        private static Panel MakePanel()
        {
            Panel panel = new VForceCard();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Theme.Surface;
            return panel;
        }

        private static void PaintFooterBorder(object sender, PaintEventArgs e)
        {
            Control control = (Control)sender;
            using (Pen pen = new Pen(Theme.PurpleDim))
            {
                e.Graphics.DrawLine(pen, 0, 0, control.Width, 0);
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

        private static string FindPortByHardwareText(string[] ports, string[] needles)
        {
            HashSet<string> matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (ManagementObjectCollection devices = searcher.Get())
                    foreach (ManagementObject device in devices)
                    {
                        if (!VmocionUsbProtocol.SupportedHardware(device["PNPDeviceID"] as string)) continue;
                        string port = VmocionUsbProtocol.PortInName(device["Name"] as string);
                        if (port != null && PortExists(ports, port)) matches.Add(port);
                    }
            }
            catch { return null; }
            if (matches.Count != 1) return null;
            foreach (string port in matches) return port;
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

        private static double[] JoystickToCommandedRates(double activeX, double activeY, int calibratingDirection, double calibrationIntensity)
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

            double yaw = Clamp(activeX * GvsMaxRate, -GvsMaxRate, GvsMaxRate);
            double pitch = Clamp(activeY * GvsMaxRate, -GvsMaxRate, GvsMaxRate);
            return new double[] { 0.0, pitch, yaw };
        }

        private static double[] PhysicsMotionToCommandedRates(MotionSnapshot motion)
        {
            if (!motion.IsFresh)
            {
                return new double[] { 0.0, 0.0, 0.0 };
            }

            return new double[]
            {
                Clamp(motion.P, -GvsMaxRate, GvsMaxRate),
                Clamp(motion.Q, -GvsMaxRate, GvsMaxRate),
                Clamp(motion.R, -GvsMaxRate, GvsMaxRate)
            };
        }

        private static double[] PqrToCodeMatrixCurrents(double p, double q, double r, double currentPeakMilliamp)
        {
            double safeCurrentPeakMilliamp = Clamp(currentPeakMilliamp, 0.0, MaxCurrentPeakMilliamp);
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
                currents[i] = Clamp(raw[i] * safeCurrentPeakMilliamp / GvsMaxRate, -safeCurrentPeakMilliamp, safeCurrentPeakMilliamp);
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
        private const int MinimumSledPacketBytes = 232;
        private const int MaxPacketsPerPoll = 32;
        private readonly List<UdpClient> clients = new List<UdpClient>();
        private MotionSnapshot latest = MotionSnapshot.Empty("Forza waiting on localhost UDP 5300/5607");
        private bool started;
        private string status = "Forza waiting on localhost UDP 5300/5607";

        public MotionSnapshot Poll()
        {
            EnsureStarted();

            for (int i = 0; i < clients.Count; i++)
            {
                UdpClient client = clients[i];
                int packetBudget = MaxPacketsPerPoll;
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                while (packetBudget-- > 0)
                {
                    byte[] data;
                    try
                    {
                        if (client.Available <= 0)
                        {
                            break;
                        }

                        data = client.Receive(ref remote);
                    }
                    catch (SocketException)
                    {
                        status = "Forza UDP receive error";
                        break;
                    }

                    if (!IPAddress.IsLoopback(remote.Address))
                    {
                        continue;
                    }

                    MotionSnapshot parsed;
                    if (TryParse(data, out parsed))
                    {
                        latest = parsed;
                        status = parsed.Source;
                    }
                    else if (string.Equals(parsed.Source, "Forza race not active", StringComparison.Ordinal))
                    {
                        // A valid Forza packet explicitly says the race stopped. Clear
                        // live motion immediately instead of waiting for freshness expiry.
                        latest = parsed;
                        status = parsed.Source;
                    }
                }
            }

            return latest.IsFresh ? latest : MotionSnapshot.Empty(status);
        }

        public static bool SelfTest()
        {
            byte[] packet = new byte[MinimumSledPacketBytes];
            WriteInt32(packet, 0, 1);
            WriteSingle(packet, 44, 0.2f);
            WriteSingle(packet, 48, 0.3f);
            WriteSingle(packet, 52, 0.1f);
            WriteSingle(packet, 56, 0.4f);
            WriteSingle(packet, 60, -0.2f);
            WriteSingle(packet, 64, 0.1f);

            MotionSnapshot motion;
            if (!TryParse(packet, out motion) ||
                Math.Abs(motion.P - 0.1) >= 0.0001 ||
                Math.Abs(motion.Q - 0.2) >= 0.0001 ||
                Math.Abs(motion.R - 0.3) >= 0.0001 ||
                Math.Abs(motion.RollDeg - (0.1 * 180.0 / Math.PI)) >= 0.0001 ||
                Math.Abs(motion.PitchDeg - (-0.2 * 180.0 / Math.PI)) >= 0.0001)
            {
                return false;
            }

            WriteInt32(packet, 0, 0);
            MotionSnapshot stopped;
            return !TryParse(packet, out stopped) && stopped.Source == "Forza race not active";
        }

        public void ResetStatus()
        {
            status = started ? "Forza waiting on localhost UDP 5300/5607" : "Forza UDP not started";
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
                client.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                clients.Add(client);
                status = "Forza waiting on localhost UDP " + port.ToString();
            }
            catch
            {
                // Another telemetry app may own the port; keep any port that did bind.
            }
        }

        private static bool TryParse(byte[] data, out MotionSnapshot motion)
        {
            motion = MotionSnapshot.Empty("Forza invalid packet");
            if (data == null || data.Length < MinimumSledPacketBytes)
            {
                return false;
            }

            int isRaceOn = ReadInt32(data, 0);
            if (isRaceOn == 0)
            {
                motion = MotionSnapshot.Empty("Forza race not active");
                return false;
            }

            if (isRaceOn != 1)
            {
                return false;
            }

            float angularX = ReadSingle(data, 44);
            float angularY = ReadSingle(data, 48);
            float angularZ = ReadSingle(data, 52);
            float yaw = ReadSingle(data, 56);
            float pitch = ReadSingle(data, 60);
            float roll = ReadSingle(data, 64);

            if (!IsFiniteAndSane(angularX) || !IsFiniteAndSane(angularY) || !IsFiniteAndSane(angularZ) ||
                !IsFiniteAndSane(yaw) || !IsFiniteAndSane(pitch) || !IsFiniteAndSane(roll))
            {
                return false;
            }

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

        private static bool IsFiniteAndSane(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 100.0f;
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
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

        private static void WriteInt32(byte[] data, int offset, int value)
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
        private const uint SimConnectRecvIdQuit = 3;
        private const uint SimConnectRecvIdSimobjectData = 8;
        private const int SimobjectDataOffset = 40;

        private readonly int windowMessage;
        private readonly DispatchProc dispatchProc;
        private IntPtr handle = IntPtr.Zero;
        private DateTime lastConnectAttempt = DateTime.MinValue;
        private MotionSnapshot latest = MotionSnapshot.Empty("MSFS waiting for SimConnect");
        private string status = "MSFS waiting for SimConnect";
        private bool definitionsRegistered;
        private bool disconnectRequested;

        public MsfsSimConnectInput(int msfsWindowMessage)
        {
            windowMessage = msfsWindowMessage;
            dispatchProc = Dispatch;
        }

        public static bool SelfTest()
        {
            MotionSnapshot motion = MapBodyRates(0.1, 0.2, 0.3);
            return Math.Abs(motion.P - 0.3) < 0.0001 &&
                   Math.Abs(motion.Q - 0.1) < 0.0001 &&
                   Math.Abs(motion.R - 0.2) < 0.0001;
        }

        public MotionSnapshot Poll(IntPtr windowHandle)
        {
            EnsureConnected(windowHandle);
            return latest.IsFresh ? latest : MotionSnapshot.Empty(status);
        }

        public void HandleWindowMessage(int message)
        {
            if (message != windowMessage || handle == IntPtr.Zero)
            {
                return;
            }

            DispatchPending();
        }

        public void ResetStatus()
        {
            status = handle == IntPtr.Zero ? "MSFS waiting for SimConnect" : "MSFS SimConnect connected";
            latest = MotionSnapshot.Empty(status);
        }

        private void DispatchPending()
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int hr = SimConnect_CallDispatch(handle, dispatchProc, IntPtr.Zero);
                if (disconnectRequested)
                {
                    MarkDisconnected("MSFS waiting for simulator");
                }
                else if (hr < 0)
                {
                    MarkDisconnected("MSFS SimConnect disconnected");
                }
            }
            catch
            {
                MarkDisconnected("MSFS SimConnect disconnected");
            }
        }

        private void MarkDisconnected(string disconnectedStatus)
        {
            Close();
            status = disconnectedStatus;
            latest = MotionSnapshot.Empty(status);
        }

        private void EnsureConnected(IntPtr windowHandle)
        {
            if (handle != IntPtr.Zero || windowHandle == IntPtr.Zero)
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
                disconnectRequested = false;
                RegisterDefinitions();
                status = "MSFS SimConnect connected";
            }
            catch (DllNotFoundException)
            {
                MarkDisconnected("MSFS SimConnect.dll missing");
            }
            catch
            {
                MarkDisconnected("MSFS SimConnect failed");
            }
        }

        private void RegisterDefinitions()
        {
            if (definitionsRegistered || handle == IntPtr.Zero)
            {
                return;
            }

            AddDouble("ROTATION VELOCITY BODY X", "radians per second");
            AddDouble("ROTATION VELOCITY BODY Y", "radians per second");
            AddDouble("ROTATION VELOCITY BODY Z", "radians per second");

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
            if (data == IntPtr.Zero || cbData < 12)
            {
                return;
            }

            uint receiveId = (uint)Marshal.ReadInt32(data, 8);
            if (receiveId == SimConnectRecvIdQuit)
            {
                // Close after SimConnect_CallDispatch returns; closing from inside its
                // callback risks re-entering the native library.
                disconnectRequested = true;
                return;
            }

            if (receiveId != SimConnectRecvIdSimobjectData || cbData < SimobjectDataOffset + 24)
            {
                return;
            }

            double bodyX = ReadDouble(data, SimobjectDataOffset);
            double bodyY = ReadDouble(data, SimobjectDataOffset + 8);
            double bodyZ = ReadDouble(data, SimobjectDataOffset + 16);
            latest = MapBodyRates(bodyX, bodyY, bodyZ);
            status = latest.Source;
        }

        private static MotionSnapshot MapBodyRates(double bodyX, double bodyY, double bodyZ)
        {
            // Flight Simulator body axes: X=lateral/pitch, Y=vertical/yaw,
            // Z=longitudinal/roll. Convert them to the P/Q/R convention.
            return MotionSnapshot.Fresh("MSFS SimConnect physics", bodyZ, bodyX, bodyY, 0.0, 0.0, 0.0);
        }

        private static double ReadDouble(IntPtr pointer, int offset)
        {
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pointer, offset));
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
            disconnectRequested = false;
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
        // The Ally sample on this system rests at essentially zero. An 8% radial
        // deadzone filters normal wear while avoiding the stock XInput 24% delay.
        private const double LeftThumbDeadzone = 0.08;
        private bool xinput14Unavailable;
        private bool xinput910Unavailable;
        private int preferredXInputIndex = -1;

        public bool TryReadLeftStick(out double x, out double y, out int controllerIndex, out string source)
        {
            x = 0.0;
            y = 0.0;
            controllerIndex = -1;
            source = "No left stick";

            if (preferredXInputIndex >= 0)
            {
                XInputState preferredState;
                if (TryGetState(preferredXInputIndex, out preferredState))
                {
                    NormalizeLeftStick(preferredState.Gamepad.ThumbLX, preferredState.Gamepad.ThumbLY, out x, out y);
                    controllerIndex = preferredXInputIndex;
                    source = "XInput " + (preferredXInputIndex + 1).ToString();
                    return true;
                }
                preferredXInputIndex = -1;
            }

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

            // XInput is the authoritative path for integrated handheld controls such as
            // the ASUS ROG Ally. Do not let the same controller's legacy WinMM mirror,
            // keyboard state, or pointer movement replace its left-stick signal.
            if (found)
            {
                preferredXInputIndex = controllerIndex;
                return true;
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
            using (Brush state = new SolidBrush(Connected ? Theme.Success : Theme.Warning))
            using (Font title = new Font("Bahnschrift SemiCondensed", 10.0f, FontStyle.Bold))
            using (Font regular = new Font("Segoe UI", 8.2f))
            {
                e.Graphics.FillEllipse(state, 18, 20, 8, 8);
                e.Graphics.DrawString(Connected ? "ROG ALLY CONNECTED" : "CONTROLLER NOT FOUND", title, text, 34, 14);
                e.Graphics.DrawString("Real-time left stick", regular, muted, 19, 43);
            }

            int left = 22;
            int right = bounds.Width - 22;
            int barWidth = Math.Max(80, right - left);
            DrawAxisBar(e.Graphics, "Yaw X", VectorX, left, 76, barWidth);
            DrawAxisBar(e.Graphics, "Pitch Y", VectorY, left, 132, barWidth);

            RectangleF plot = new RectangleF(left, 190, barWidth, Math.Max(82, bounds.Height - 226));
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

        private readonly Font titleFont = new Font("Bahnschrift SemiCondensed", 10.0f, FontStyle.Bold);
        private readonly Font chipNameFont = new Font("Segoe UI", 6.8f, FontStyle.Bold);
        private readonly Font chipValueFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private readonly Font compassFont = new Font("Consolas", 8.5f, FontStyle.Bold);

        public HeadView()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            AccessibleName = "Orientation indicator";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                chipNameFont.Dispose();
                chipValueFont.Dispose();
                compassFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = ClientRectangle;
            graphics.Clear(Theme.SurfaceDeep);

            using (Pen grid = new Pen(Color.FromArgb(26, Theme.PurpleBright)))
            {
                for (int x = 0; x < bounds.Width; x += 48) graphics.DrawLine(grid, x, 0, x, bounds.Height);
                for (int y = 0; y < bounds.Height; y += 48) graphics.DrawLine(grid, 0, y, bounds.Width, y);
            }

            TextRenderer.DrawText(graphics, "ORIENTATION", titleFont, new Rectangle(20, 16, 180, 24),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int chipWidth = Math.Min(96, Math.Max(74, (bounds.Width - 80) / 5));
            int chipGap = 8;
            int chipsWidth = chipWidth * 3 + chipGap * 2;
            int chipStart = Math.Max(20, (bounds.Width - chipsWidth) / 2);
            DrawChip(graphics, "YAW", Signed(Yaw) + "°", new Rectangle(chipStart, 48, chipWidth, 54));
            DrawChip(graphics, "PITCH", Signed(Pitch) + "°", new Rectangle(chipStart + chipWidth + chipGap, 48, chipWidth, 54));
            DrawChip(graphics, "ROLL", Signed(Roll) + "°", new Rectangle(chipStart + (chipWidth + chipGap) * 2, 48, chipWidth, 54));

            float availableHeight = Math.Max(160.0f, bounds.Height - 150.0f);
            float radius = Math.Min(230.0f, Math.Min((bounds.Width - 64.0f) / 2.0f, availableHeight / 2.0f));
            radius = Math.Max(78.0f, radius);
            PointF center = new PointF(bounds.Width / 2.0f, 130.0f + availableHeight / 2.0f);
            RectangleF dial = new RectangleF(center.X - radius, center.Y - radius, radius * 2.0f, radius * 2.0f);

            using (Brush glow = new SolidBrush(Color.FromArgb(35, Theme.PurpleBright)))
            {
                graphics.FillEllipse(glow, RectangleF.Inflate(dial, 12.0f, 12.0f));
            }

            GraphicsState state = graphics.Save();
            using (GraphicsPath clipPath = new GraphicsPath())
            {
                clipPath.AddEllipse(dial);
                graphics.SetClip(clipPath);
            }
            graphics.TranslateTransform(center.X, center.Y);
            graphics.RotateTransform((float)-Roll);
            float pitchOffset = (float)Clamp(Pitch / 45.0, -1.0, 1.0) * radius * 0.72f;

            RectangleF sky = new RectangleF(-radius * 2.2f, -radius * 2.2f, radius * 4.4f, radius * 2.2f + pitchOffset);
            RectangleF ground = new RectangleF(-radius * 2.2f, pitchOffset, radius * 4.4f, radius * 2.2f - pitchOffset);
            using (LinearGradientBrush skyBrush = new LinearGradientBrush(sky, Color.FromArgb(72, 43, 130), Theme.SurfaceDeep, 90.0f))
            using (LinearGradientBrush groundBrush = new LinearGradientBrush(ground, Color.FromArgb(42, 20, 68), Color.FromArgb(13, 8, 25), 90.0f))
            using (Pen horizon = new Pen(Theme.PurpleBright, 3.0f))
            {
                graphics.FillRectangle(skyBrush, sky);
                graphics.FillRectangle(groundBrush, ground);
                graphics.DrawLine(horizon, -radius * 2.0f, pitchOffset, radius * 2.0f, pitchOffset);
            }
            DrawPitchLadder(graphics, radius, pitchOffset);
            graphics.Restore(state);

            using (Pen outerGlow = new Pen(Color.FromArgb(80, Theme.PurpleBright), 9.0f))
            using (Pen outer = new Pen(Theme.PurpleBright, 2.0f))
            using (Pen inner = new Pen(Color.FromArgb(155, Theme.Text), 1.0f))
            {
                graphics.DrawEllipse(outerGlow, dial);
                graphics.DrawEllipse(outer, dial);
                graphics.DrawEllipse(inner, RectangleF.Inflate(dial, -5.0f, -5.0f));
            }

            DrawAircraftChevron(graphics, center, radius);
            DrawCompass(graphics, center, radius);
        }

        private void DrawChip(Graphics graphics, string name, string value, Rectangle rect)
        {
            using (Brush fill = new SolidBrush(Color.FromArgb(235, Theme.Control)))
            using (Pen border = new Pen(Theme.PurpleDim))
            {
                graphics.FillRectangle(fill, rect);
                graphics.DrawRectangle(border, rect);
            }
            TextRenderer.DrawText(graphics, name, chipNameFont, new Rectangle(rect.X + 4, rect.Y + 5, rect.Width - 8, 18),
                Theme.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, value, chipValueFont, new Rectangle(rect.X + 4, rect.Y + 25, rect.Width - 8, 23),
                Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static void DrawPitchLadder(Graphics graphics, float radius, float pitchOffset)
        {
            using (Pen ladder = new Pen(Color.FromArgb(185, Theme.Text), 1.4f))
            using (Font font = new Font("Consolas", 7.0f, FontStyle.Bold))
            using (Brush text = new SolidBrush(Theme.Text))
            {
                for (int degrees = -30; degrees <= 30; degrees += 10)
                {
                    if (degrees == 0) continue;
                    float y = pitchOffset - (degrees / 45.0f * radius * 0.72f);
                    float half = degrees % 20 == 0 ? radius * 0.28f : radius * 0.18f;
                    graphics.DrawLine(ladder, -half, y, half, y);
                    graphics.DrawString(Math.Abs(degrees).ToString(), font, text, half + 5.0f, y - 7.0f);
                }
            }
        }

        private static void DrawAircraftChevron(Graphics graphics, PointF center, float radius)
        {
            float wing = Math.Max(36.0f, radius * 0.28f);
            PointF left = new PointF(center.X - wing, center.Y);
            PointF leftInner = new PointF(center.X - 13.0f, center.Y);
            PointF nose = new PointF(center.X, center.Y + 12.0f);
            PointF rightInner = new PointF(center.X + 13.0f, center.Y);
            PointF right = new PointF(center.X + wing, center.Y);
            using (Pen glow = new Pen(Color.FromArgb(75, Theme.PurpleBright), 9.0f))
            using (Pen line = new Pen(Color.White, 3.0f))
            using (Brush centerDot = new SolidBrush(Theme.PurpleBright))
            {
                glow.StartCap = glow.EndCap = LineCap.Round;
                line.StartCap = line.EndCap = LineCap.Round;
                graphics.DrawLines(glow, new PointF[] { left, leftInner, nose, rightInner, right });
                graphics.DrawLines(line, new PointF[] { left, leftInner, nose, rightInner, right });
                graphics.FillEllipse(centerDot, center.X - 4.0f, center.Y + 8.0f, 8.0f, 8.0f);
            }
        }

        private void DrawCompass(Graphics graphics, PointF center, float radius)
        {
            double heading = Yaw % 360.0;
            if (heading < 0.0) heading += 360.0;
            Rectangle rect = new Rectangle((int)(center.X - 64.0f), (int)(center.Y - radius - 17.0f), 128, 30);
            using (Brush fill = new SolidBrush(Theme.SurfaceDeep))
            using (Pen border = new Pen(Theme.PurpleBright))
            {
                graphics.FillRectangle(fill, rect);
                graphics.DrawRectangle(border, rect);
            }
            string text = "HEADING  " + heading.ToString("000") + "°";
            TextRenderer.DrawText(graphics, text, compassFont, rect, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static string Signed(double value)
        {
            return value.ToString("+0.0;-0.0;0.0");
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
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

    internal sealed class VForceComboBox : UserControl
    {
        private readonly ComboBox picker;
        private readonly ComboFace face;

        public VForceComboBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Height = 32;
            MinimumSize = new Size(92, 32);
            BackColor = Theme.SurfaceDeep;
            ForeColor = Theme.Text;
            TabStop = false;

            picker = new ComboBox();
            picker.Dock = DockStyle.Fill;
            picker.DropDownStyle = ComboBoxStyle.DropDownList;
            picker.DrawMode = DrawMode.OwnerDrawFixed;
            picker.ItemHeight = 30;
            picker.IntegralHeight = false;
            picker.DropDownHeight = 224;
            picker.FlatStyle = FlatStyle.Flat;
            picker.BackColor = Theme.SurfaceDeep;
            picker.ForeColor = Theme.Text;
            picker.DrawItem += DrawPickerItem;
            picker.SelectedIndexChanged += delegate { face.Invalidate(); };
            picker.DropDown += delegate { face.Invalidate(); };
            picker.DropDownClosed += delegate
            {
                face.Invalidate();
                face.Focus();
            };
            Controls.Add(picker);

            face = new ComboFace(this);
            face.Dock = DockStyle.Fill;
            face.TabIndex = 0;
            Controls.Add(face);
            face.BringToFront();
        }

        public ComboBox.ObjectCollection Items
        {
            get { return picker.Items; }
        }

        public object SelectedItem
        {
            get { return picker.SelectedItem; }
            set
            {
                picker.SelectedItem = value;
                face.Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return picker.SelectedIndex; }
            set
            {
                picker.SelectedIndex = value;
                face.Invalidate();
            }
        }

        public new string Text
        {
            get { return picker.Text; }
            set
            {
                picker.Text = value;
                face.Invalidate();
            }
        }

        public new string AccessibleName
        {
            get { return base.AccessibleName; }
            set
            {
                base.AccessibleName = value;
                picker.AccessibleName = value;
                face.AccessibleName = value;
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (picker != null)
            {
                picker.Font = Font;
            }
            if (face != null)
            {
                face.Font = Font;
                face.Invalidate();
            }
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            if (picker != null)
            {
                picker.ForeColor = ForeColor;
            }
            if (face != null)
            {
                face.Invalidate();
            }
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (picker != null)
            {
                picker.BackColor = BackColor;
            }
            if (face != null)
            {
                face.Invalidate();
            }
        }

        private void DrawPickerItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Bounds.Width <= 0 || e.Bounds.Height <= 0)
            {
                return;
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? Theme.PurpleDim : Theme.SurfaceDeep;
            Color foreground = Enabled ? Theme.Text : Theme.SecondaryText;
            using (Brush fill = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(fill, e.Bounds);
            }

            string itemText = picker.GetItemText(picker.Items[e.Index]);
            Rectangle textBounds = new Rectangle(e.Bounds.X + 11, e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - 20), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, itemText, Font, textBounds, foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void OpenDropDown()
        {
            if (!Enabled || Items.Count == 0)
            {
                return;
            }

            if (SelectedIndex < 0)
            {
                SelectedIndex = 0;
            }
            picker.Focus();
            picker.DroppedDown = true;
            face.Invalidate();
        }

        private void MoveSelection(int direction)
        {
            if (!Enabled || Items.Count == 0)
            {
                return;
            }

            int next = SelectedIndex < 0 ? 0 : SelectedIndex + direction;
            SelectedIndex = Math.Max(0, Math.Min(Items.Count - 1, next));
        }

        private sealed class ComboFace : Control
        {
            private readonly VForceComboBox owner;
            private bool hovered;

            public ComboFace(VForceComboBox owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.UserPaint | ControlStyles.Selectable |
                    ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                TabStop = true;
                AccessibleRole = AccessibleRole.ComboBox;
            }

            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End)
                {
                    return true;
                }
                return base.IsInputKey(keyData);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hovered = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    Focus();
                    owner.OpenDropDown();
                }
            }

            protected override void OnGotFocus(EventArgs e)
            {
                base.OnGotFocus(e);
                Invalidate();
            }

            protected override void OnLostFocus(EventArgs e)
            {
                base.OnLostFocus(e);
                Invalidate();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4 ||
                    (e.KeyCode == Keys.Down && e.Alt))
                {
                    owner.OpenDropDown();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Down)
                {
                    owner.MoveSelection(1);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Up)
                {
                    owner.MoveSelection(-1);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Home && owner.Items.Count > 0)
                {
                    owner.SelectedIndex = 0;
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.End && owner.Items.Count > 0)
                {
                    owner.SelectedIndex = owner.Items.Count - 1;
                    e.Handled = true;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // The custom face fully paints itself in OnPaint.
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Rectangle bounds = ClientRectangle;
                if (bounds.Width <= 2 || bounds.Height <= 2)
                {
                    return;
                }

                bool active = Focused || owner.picker.Focused || owner.picker.DroppedDown;
                Color borderColor = active ? Theme.PurpleBright : (hovered ? Theme.Purple : Theme.PurpleDim);
                Color textColor = owner.Enabled ? owner.ForeColor : Theme.SecondaryText;
                int arrowWidth = Math.Min(34, Math.Max(28, bounds.Height));
                Rectangle arrowArea = new Rectangle(bounds.Right - arrowWidth, 1, arrowWidth - 1, bounds.Height - 2);

                using (LinearGradientBrush fill = new LinearGradientBrush(bounds,
                    owner.BackColor, Theme.Control, 0.0f))
                using (Brush arrowFill = new SolidBrush(active ? Color.FromArgb(47, 22, 76) : Theme.Control))
                using (Pen border = new Pen(borderColor))
                using (Pen divider = new Pen(Theme.PurpleDim))
                using (Brush arrow = new SolidBrush(owner.Enabled ? Theme.PurpleBright : Theme.SecondaryText))
                {
                    e.Graphics.FillRectangle(fill, bounds);
                    e.Graphics.FillRectangle(arrowFill, arrowArea);
                    e.Graphics.DrawLine(divider, arrowArea.Left, 1, arrowArea.Left, bounds.Bottom - 2);
                    e.Graphics.DrawRectangle(border, new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1));

                    int centerX = arrowArea.Left + arrowArea.Width / 2;
                    int centerY = bounds.Height / 2 + 1;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPolygon(arrow, new Point[]
                    {
                        new Point(centerX - 5, centerY - 3),
                        new Point(centerX + 5, centerY - 3),
                        new Point(centerX, centerY + 3)
                    });
                }

                Rectangle textBounds = new Rectangle(11, 0,
                    Math.Max(0, bounds.Width - arrowWidth - 17), bounds.Height);
                TextRenderer.DrawText(e.Graphics, owner.Text, owner.Font, textBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    internal enum BadgeState
    {
        Neutral,
        Ready,
        Warning,
        Danger
    }

    internal sealed class VForceBackdrop : Panel
    {
        public VForceBackdrop()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            using (LinearGradientBrush background = new LinearGradientBrush(bounds, Theme.BackgroundTop, Theme.Background, 90.0f))
            {
                e.Graphics.FillRectangle(background, bounds);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bloom = new Rectangle(-180, -210, Math.Max(620, bounds.Width / 2), Math.Max(520, bounds.Height));
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(bloom);
                using (PathGradientBrush glow = new PathGradientBrush(path))
                {
                    glow.CenterColor = Color.FromArgb(72, Theme.Purple);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, Theme.PurpleDim) };
                    e.Graphics.FillPath(glow, path);
                }
            }

            using (Brush dot = new SolidBrush(Color.FromArgb(38, Theme.PurpleBright)))
            {
                for (int y = 18; y < bounds.Height; y += 34)
                {
                    int offset = ((y / 34) % 2) * 17;
                    for (int x = 14 + offset; x < bounds.Width; x += 34)
                    {
                        if (x < bounds.Width * 0.46 || y < bounds.Height * 0.22)
                        {
                            e.Graphics.FillEllipse(dot, x, y, 1.6f, 1.6f);
                        }
                    }
                }
            }
        }
    }

    internal sealed class VForceCard : Panel
    {
        public VForceCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateChamferPath(bounds, 10))
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, Theme.Surface, Theme.SurfaceDeep, 120.0f))
            {
                e.Graphics.FillPath(fill, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = CreateChamferPath(bounds, 10))
            using (Pen border = new Pen(Color.FromArgb(190, Theme.PurpleDim)))
            {
                e.Graphics.DrawPath(border, path);
            }

            using (Pen accent = new Pen(Theme.PurpleBright, 2.0f))
            {
                e.Graphics.DrawLine(accent, 1, 1, Math.Min(52, Width - 12), 1);
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Invalidate(true);
        }

        internal static GraphicsPath CreateChamferPath(Rectangle bounds, int cut)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(new Point[]
            {
                new Point(bounds.Left, bounds.Top),
                new Point(bounds.Right - cut, bounds.Top),
                new Point(bounds.Right, bounds.Top + cut),
                new Point(bounds.Right, bounds.Bottom),
                new Point(bounds.Left + cut, bounds.Bottom),
                new Point(bounds.Left, bounds.Bottom - cut)
            });
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class StatusBadge : Control
    {
        private readonly string heading;
        private string detail = "STARTING";
        private BadgeState state = BadgeState.Neutral;

        public StatusBadge(string headingText)
        {
            heading = headingText;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Width = 140;
            Height = 58;
            Margin = new Padding(6, 0, 0, 0);
            BackColor = Color.Transparent;
            AccessibleName = headingText + " status";
            TabStop = false;
        }

        public void SetStatus(string value, BadgeState badgeState)
        {
            value = string.IsNullOrEmpty(value) ? "-" : value;
            if (detail == value && state == badgeState)
            {
                return;
            }

            detail = value;
            state = badgeState;
            AccessibleDescription = heading + ": " + detail;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color stateColor = StateColor(state);
            using (GraphicsPath path = VForceCard.CreateChamferPath(bounds, 8))
            using (Brush fill = new SolidBrush(Color.FromArgb(225, Theme.SurfaceDeep)))
            using (Pen border = new Pen(state == BadgeState.Neutral ? Theme.PurpleDim : Color.FromArgb(180, stateColor)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            using (Brush glow = new SolidBrush(Color.FromArgb(48, stateColor)))
            using (Brush dot = new SolidBrush(stateColor))
            {
                e.Graphics.FillEllipse(glow, 10, 22, 14, 14);
                e.Graphics.FillEllipse(dot, 14, 26, 6, 6);
            }

            using (Font headingFont = new Font("Bahnschrift SemiCondensed", 6.8f, FontStyle.Bold))
            using (Font detailFont = new Font("Segoe UI", 7.8f, FontStyle.Bold))
            {
                TextRenderer.DrawText(e.Graphics, heading, headingFont,
                    new Rectangle(30, 8, Width - 36, 18), Theme.SecondaryText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, detail, detailFont,
                    new Rectangle(30, 27, Width - 36, 22), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        private static Color StateColor(BadgeState value)
        {
            if (value == BadgeState.Ready) return Theme.Success;
            if (value == BadgeState.Warning) return Theme.Warning;
            if (value == BadgeState.Danger) return Theme.Danger;
            return Theme.PurpleBright;
        }
    }

    internal sealed class VForceSlider : Control
    {
        private int minimum;
        private int maximum = 100;
        private int value;
        private bool dragging;

        public event EventHandler Scroll;

        public int Minimum
        {
            get { return minimum; }
            set
            {
                minimum = value;
                if (maximum < minimum) maximum = minimum;
                Value = this.value;
                Invalidate();
            }
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                maximum = Math.Max(minimum, value);
                Value = this.value;
                Invalidate();
            }
        }

        public int Value
        {
            get { return value; }
            set
            {
                int bounded = Math.Max(minimum, Math.Min(maximum, value));
                if (this.value == bounded) return;
                this.value = bounded;
                Invalidate();
                OnScroll(EventArgs.Empty);
            }
        }

        public int SmallChange { get; set; }
        public int LargeChange { get; set; }

        public VForceSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            SmallChange = 1;
            LargeChange = 10;
            Height = 54;
            MinimumSize = new Size(120, 44);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            const int handleInset = 31;
            int left = handleInset;
            int right = Math.Max(left + 1, Width - handleInset - 1);
            int centerY = Height / 2;
            float fraction = maximum == minimum ? 0.0f : (float)(value - minimum) / (maximum - minimum);
            int thumbX = left + (int)((right - left) * fraction);

            using (Pen railGlow = new Pen(Color.FromArgb(38, Theme.PurpleBright), 12.0f))
            using (Pen rail = new Pen(Theme.PurpleDim, 4.0f))
            using (Pen activeGlow = new Pen(Color.FromArgb(72, Theme.PurpleBright), 10.0f))
            using (LinearGradientBrush active = new LinearGradientBrush(new Rectangle(left, centerY - 2, Math.Max(1, thumbX - left), 4),
                Theme.Purple, Theme.PurpleBright, 0.0f))
            using (Pen activeRail = new Pen(active, 4.0f))
            {
                railGlow.StartCap = railGlow.EndCap = LineCap.Round;
                rail.StartCap = rail.EndCap = LineCap.Round;
                activeGlow.StartCap = activeGlow.EndCap = LineCap.Round;
                activeRail.StartCap = activeRail.EndCap = LineCap.Round;
                e.Graphics.DrawLine(railGlow, left, centerY, right, centerY);
                e.Graphics.DrawLine(rail, left, centerY, right, centerY);
                if (thumbX > left)
                {
                    e.Graphics.DrawLine(activeGlow, left, centerY, thumbX, centerY);
                    e.Graphics.DrawLine(activeRail, left, centerY, thumbX, centerY);
                }
            }

            using (Pen tick = new Pen(Color.FromArgb(120, Theme.SecondaryText)))
            {
                for (int i = 0; i <= 5; i++)
                {
                    int x = left + ((right - left) * i / 5);
                    e.Graphics.DrawLine(tick, x, centerY + 9, x, centerY + 13);
                }
            }

            Point[] diamond = new Point[]
            {
                new Point(thumbX, centerY - 22),
                new Point(thumbX + 20, centerY),
                new Point(thumbX, centerY + 22),
                new Point(thumbX - 20, centerY)
            };
            using (Brush thumbGlow = new SolidBrush(Color.FromArgb(60, Theme.PurpleBright)))
            using (Brush thumb = new SolidBrush(Theme.PurpleBright))
            using (Pen thumbBorder = new Pen(Color.White, 1.0f))
            {
                e.Graphics.FillEllipse(thumbGlow, thumbX - 29, centerY - 29, 58, 58);
                e.Graphics.FillPolygon(thumb, diamond);
                e.Graphics.DrawPolygon(thumbBorder, diamond);
            }

            if (Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(1, 1, Width - 3, Height - 3), Theme.Text, Theme.Surface);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                dragging = true;
                SetFromPoint(e.X);
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromPoint(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Handheld desktop-mode scrolling must never change stimulation level.
            // Touch/drag and explicit keyboard focus remain supported.
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= SmallChange;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += SmallChange;
            else if (e.KeyCode == Keys.PageDown) Value -= LargeChange;
            else if (e.KeyCode == Keys.PageUp) Value += LargeChange;
            else if (e.KeyCode == Keys.Home) Value = Minimum;
            else if (e.KeyCode == Keys.End) Value = Maximum;
            else
            {
                base.OnKeyDown(e);
                return;
            }
            e.Handled = true;
        }

        private void SetFromPoint(int x)
        {
            const int handleInset = 31;
            int left = handleInset;
            int right = Math.Max(left + 1, Width - handleInset - 1);
            double fraction = Math.Max(0.0, Math.Min(1.0, (x - left) / (double)(right - left)));
            Value = minimum + (int)Math.Round((maximum - minimum) * fraction);
        }

        private void OnScroll(EventArgs e)
        {
            EventHandler handler = Scroll;
            if (handler != null) handler(this, e);
        }
    }

    internal static class Theme
    {
        public static readonly Color BackgroundTop = Color.FromArgb(1, 1, 5);
        public static readonly Color Background = Color.FromArgb(8, 8, 16);
        public static readonly Color Surface = Color.FromArgb(13, 13, 26);
        public static readonly Color SurfaceDeep = Color.FromArgb(6, 6, 14);
        public static readonly Color Control = Color.FromArgb(21, 21, 39);
        public static readonly Color Line = Color.FromArgb(49, 34, 77);
        public static readonly Color Text = Color.FromArgb(232, 232, 240);
        public static readonly Color SecondaryText = Color.FromArgb(144, 144, 174);
        public static readonly Color Muted = Color.FromArgb(144, 144, 174);
        public static readonly Color Purple = Color.FromArgb(123, 47, 255);
        public static readonly Color PurpleBright = Color.FromArgb(155, 77, 255);
        public static readonly Color PurpleDim = Color.FromArgb(61, 26, 122);
        public static readonly Color Danger = Color.FromArgb(255, 68, 102);
        public static readonly Color Warning = Color.FromArgb(255, 190, 85);
        public static readonly Color Success = Color.FromArgb(67, 214, 163);
        public static readonly Color Cyan = Color.FromArgb(155, 77, 255);
        public static readonly Color Gold = Color.FromArgb(185, 137, 255);
        public static readonly Color Coral = Color.FromArgb(255, 68, 102);
        public static readonly Color Violet = Color.FromArgb(210, 184, 255);
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
