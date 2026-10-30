using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("VFORCE Vestibular Simulation Console")]
[assembly: AssemblyProduct("VFORCE Vestibular Simulation Console")]
[assembly: AssemblyDescription("Joystick and simulator telemetry bridge for VMocion-compatible vestibular hardware.")]
[assembly: AssemblyVersion("1.2.6.0")]
[assembly: AssemblyFileVersion("1.2.6.0")]

namespace VestibularJoystickSim
{
    internal static class App
    {
        private const string SingleInstanceName = @"Local\VMocion.VForce.VestibularJoystickSimulation";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--capture-telemetry")
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return MainForm.CaptureTelemetry(args);
            }
            if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return RunSelfTests(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "vforce-self-test-results.txt"));
            }

            if (args.Length > 1 && args[0] == "--ui-smoke-test")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return MainForm.UiSmokeTest(args[1]);
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
                RuntimeDiagnostics.Start();
                MainForm form = null;
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    if (form != null) form.StopAfterUnexpectedError(e.Exception);
                    else RuntimeDiagnostics.Record("STARTUP_UI_ERROR " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    // Managed worker failures terminate the process; record them, never resume.
                    if (form != null) form.DisconnectForFatalError();
                    RuntimeDiagnostics.Record("FATAL " + e.ExceptionObject);
                    RuntimeDiagnostics.Stop();
                };
                try { form = new MainForm(); Application.Run(form); }
                catch (Exception ex)
                {
                    if (form != null) form.DisconnectForFatalError();
                    RuntimeDiagnostics.Record("MAIN_ERROR " + ex);
                    MessageBox.Show("VFORCE stopped. Details: " + RuntimeDiagnostics.LogPath + "\r\n\r\n" + ex.Message,
                        "VFORCE", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                finally { RuntimeDiagnostics.Record("EXIT"); RuntimeDiagnostics.Stop(); }
                GC.KeepAlive(instanceMutex);
            }
            return 0;
        }
        private static int RunSelfTests(string reportPath)
        {
            string[] names = new string[] { "Packet framing", "NRF profile and 6561 vector cases", "Serial sender and reset boundary", "Forza parser", "MSFS parser and pause state", "GUI motion/calibration transforms", "Joystick ABI/raw mapping and controller selection", "Native Windows XInput API", "Forza UDP loopback/retry/stale status", "Asynchronous runtime fault log" };
            Func<bool>[] tests = new Func<bool>[] { PacketBuilder.SelfTest, ClassicNrfProfile.SelfTest, SerialOutputTests.Run, ForzaUdpTelemetryInput.SelfTest, MsfsSimConnectInput.SelfTest, MainForm.SelfTest, PhysicalJoystickInput.SelfTest, PhysicalJoystickInput.NativeSelfTest, ForzaUdpTelemetryInput.LoopbackSelfTest, RuntimeDiagnostics.SelfTest };
            List<string> report = new List<string>();
            bool passed = true;
            for (int i = 0; i < tests.Length; i++)
            {
                try
                {
                    bool result = tests[i]();
                    report.Add((result ? "PASS " : "FAIL ") + names[i]);
                    passed &= result;
                }
                catch (Exception ex) { passed = false; report.Add("FAIL " + names[i] + ": " + ex); }
            }
            report.Add("Scope: host/model tests only; no physical NRF, running game, or measured analog output.");
            File.WriteAllLines(reportPath, report.ToArray());
            return passed ? 0 : 2;
        }
    }

    internal sealed class VForceButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            // WinForms' default disabled flat-button text is black on this
            // purple theme. Keep disabled calibration labels readable.
            e.Graphics.Clear(BackColor);
            using (Pen border = new Pen(Theme.PurpleDim))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Theme.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class MainForm : Form
    {
        private enum GameKind
        {
            Msfs,
            Forza
        }

        private const int SerialBaud = 9600;
        private const double Deadzone = 0.01;
        // These constants mirror VMocion-main\legacy_demo-main\gvs.py.
        private const double GvsMaxRate = 3.14 / 4.0;
        private const double GvsMaxRamp = 0.03;
        private const double PacketValueToVolts = 0.15;
        private const double MaxCurrentPeakMilliamp = ClassicNrfProfile.MaximumNominalMilliamp;
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
        private readonly Button calLeftButton, calRightButton, stopCalButton;
        private readonly bool offlineMode;
        private Control smokeSurface;
        private readonly Button controllerButton;
        private readonly System.ComponentModel.IContainer menuComponents = new System.ComponentModel.Container();
        private ContextMenuStrip controllerMenu, forzaMenu, msfsMenu;
        private int offlineLaunchRequests;
        private string offlineLaunchTarget;
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

        private readonly SerialOutput serialOutput;
        private string connectedPortName;
        private readonly Stopwatch connectionAge = new Stopwatch();
        private readonly Stopwatch calibrationClock = new Stopwatch();
        private bool armed { get { return serialOutput.Status.Armed; } }
        private long lastTick;
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
        private string lastPacketText { get { return serialOutput.Status.LastPacket; } }
        private double currentPeakMilliamp = DefaultCurrentPeakMilliamp;
        private bool paused;
        private int packetCount { get { return serialOutput.Status.PacketCount; } }
        private int calibrationDirection;
        private long lastRuntimeReportTicks;
        private bool reportingUnexpectedError;

        public MainForm() : this(false) { }

        private MainForm(bool offline)
        {
            offlineMode = offline;
            serialOutput = new SerialOutput(new OutputClock(), !offline);
            SuspendLayout();
            Text = "VFORCE | Vestibular Simulation Console";
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1180, 650);
            MinimumSize = new Size(1140, 660);
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
            Label gainTitle = MakeSectionTitle("NOMINAL COMMAND LIMIT");
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
            gainSlider.AccessibleName = "Peak stimulation current";
            gainSlider.AccessibleDescription = "Nominal command limit from zero to 1.50 milliamps; delivered current is not measured.";
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

            calLeftButton = MakeButton("Left Cal");
            calRightButton = MakeButton("Right Cal");
            stopCalButton = MakeButton("Stop Cal");
            calLeftButton.AccessibleName = "Left Cal";
            calRightButton.AccessibleName = "Right Cal";
            calLeftButton.Click += delegate { StartCalibration(1); };
            calRightButton.Click += delegate { StartCalibration(-1); };
            stopCalButton.Click += delegate { CancelCalibration(); };

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
            actionFlow.RowCount = 2;
            actionFlow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            actionFlow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
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
            SetFillButton(calLeftButton);
            SetFillButton(calRightButton);
            SetFillButton(stopCalButton);
            actionFlow.Controls.Add(calLeftButton, 0, 1);
            actionFlow.Controls.Add(calRightButton, 1, 1);
            actionFlow.Controls.Add(stopCalButton, 2, 1);
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

            Panel stickHeader = new Panel();
            stickHeader.Dock = DockStyle.Top;
            stickHeader.Height = 36;
            stickHeader.BackColor = Color.Transparent;
            Label leftTitle = MakeSectionTitle("LEFT STICK");
            leftTitle.Dock = DockStyle.Fill;
            controllerButton = MakeButton("Controller: Auto");
            controllerButton.Dock = DockStyle.Right;
            controllerButton.Width = 124;
            controllerButton.AccessibleName = "Controller selection";
            controllerButton.Click += ShowControllerMenu;
            toolTip.SetToolTip(controllerButton, "ROG Ally: select Gamepad Mode and enable Embedded Controller. Move the left stick, or select its XInput slot. Changing this selection disarms output.");
            stickHeader.Controls.Add(leftTitle);
            stickHeader.Controls.Add(controllerButton);
            leftPanel.Controls.Add(stickHeader);

            inputLabel = MakeMetric("Input", "ROG Ally: Gamepad Mode / Embedded Controller on");
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
            safetyFooter.Text = "OUTPUT STARTS SAFE   |   STOP IF UNCOMFORTABLE";
            safetyFooter.Dock = DockStyle.Fill;
            safetyFooter.ForeColor = Theme.Warning;
            safetyFooter.Font = new Font("Segoe UI", 8.0f, FontStyle.Bold);
            safetyFooter.TextAlign = ContentAlignment.MiddleRight;
            safetyFooter.AutoEllipsis = true;
            footerLayout.Controls.Add(safetyFooter, 1, 0);

            toolTip.SetToolTip(gainSlider, "Nominal command limit for NRF v0.8.1. Starts at 0.50 mA; delivered current is not measured.");
            toolTip.SetToolTip(portCombo, "Detects XIAO NRF52840 application ports. Choose the NRF COM port manually when needed.");
            toolTip.SetToolTip(refreshButton, "Rescan Windows serial devices.");
            toolTip.SetToolTip(connectButton, "Connect or disconnect the selected VMocion serial device.");
            toolTip.SetToolTip(armButton, "Explicitly enable live output. Output always starts disarmed.");
            toolTip.SetToolTip(msfsLaunchButton, "Choose telemetry only, Steam launch, or Microsoft Store launch.");
            toolTip.SetToolTip(forzaLaunchButton, "Choose telemetry only, Steam launch, or Microsoft Store launch.");
            toolTip.SetToolTip(calLeftButton, "Five-second left directional ramp at the nominal command limit. Requires explicit Arm; click again to stop.");
            toolTip.SetToolTip(calRightButton, "Five-second right directional ramp at the nominal command limit. Requires explicit Arm; click again to stop.");
            toolTip.SetToolTip(stopCalButton, "Stop the directional test and disarm output.");
            toolTip.SetToolTip(safetyFooter, safetyFooter.Text);

            portCombo.TabIndex = 0;
            refreshButton.TabIndex = 1;
            connectButton.TabIndex = 2;
            armButton.TabIndex = 3;
            pauseButton.TabIndex = 4;
            diagnosticsButton.TabIndex = 5;
            gainSlider.TabIndex = 6;

            lastTick = Stopwatch.GetTimestamp();
            useMsfsPhysics = false;
            useForzaPhysics = false;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 25;
            timer.Tick += TimerTick;
            if (!offlineMode) timer.Start();

            visualTimer = new System.Windows.Forms.Timer();
            visualTimer.Interval = 16;
            visualTimer.Tick += VisualTimerTick;
            if (!offlineMode) visualTimer.Start();

            if (!offlineMode) { RefreshPorts(); TryAutoConnectVestibularDevice(); }
            UpdateUi();
            Shown += delegate
            {
                if (!offlineMode && Screen.FromControl(this).WorkingArea.Height <= 800)
                {
                    WindowState = FormWindowState.Maximized;
                }
                connectButton.Select();
            };
            ResumeLayout(true);
        }

        public static bool SelfTest()
        {
            return ClassicNrfProfile.SelfTest() &&
                   PacketValuesEqual(BodyToAlignedHeadRates(0.1, -0.2, 0.3), new double[] { 0.1, -0.2, 0.3 }) &&
                   Math.Abs(SlewRate(0, 1) - GvsMaxRamp) < 1e-12 &&
                   Math.Abs(SlewRate(0, -1) + GvsMaxRamp) < 1e-12 &&
                   CalibrationIntensity(0) == 0 && CalibrationIntensity(2.5) == 1 && CalibrationIntensity(5) == 0 &&
                   PacketValuesEqual(JoystickToCommandedRates(0, 0, -1, 1), new double[] { GvsMaxRate, 0, 0 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(1.0, 0.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 1.0, 0, 0.0), new double[] { 0.0, GvsMaxRate, 0.0 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.5, 0.0, 0, 0.0), new double[] { 0.0, 0.0, GvsMaxRate * 0.5 }) &&
                   PacketValuesEqual(JoystickToCommandedRates(0.0, 0.0, 1, 1.0), new double[] { -GvsMaxRate, 0.0, 0.0 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0)), new double[] { 0.1, 0.2, 0.3 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("Forza", 2.0, -2.0, 2.0, 0.0, 0.0, 0.0)), new double[] { GvsMaxRate, -GvsMaxRate, GvsMaxRate }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Empty("MSFS off")), new double[] { 0.0, 0.0, 0.0 }) &&
                   PacketValuesEqual(PhysicsMotionToCommandedRates(MotionSnapshot.Fresh("MSFS", -0.1, 0.2, -0.3, 0.0, 0.0, 0.0)), new double[] { -0.1, 0.2, -0.3 }) &&
                   SelectGameMotion(true, MotionSnapshot.Fresh("MSFS", 0.1, 0.2, 0.3, 0.0, 0.0, 0.0), true, MotionSnapshot.Fresh("Forza", -0.1, -0.2, -0.3, 0.0, 0.0, 0.0)).Source == "MSFS" &&
                   SelectGameMotion(false, MotionSnapshot.Empty("MSFS off"), true, MotionSnapshot.Fresh("Forza", -0.1, -0.2, -0.3, 0.0, 0.0, 0.0)).Source == "Forza" &&
                   ForzaUdpTelemetryInput.SelfTest() &&
                   MsfsSimConnectInput.SelfTest();
        }

        private static string CaptureNumber(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string CaptureCsv(string value)
        {
            value = value ?? "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string CaptureJson(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        public static int CaptureTelemetry(string[] args)
        {
            string requested = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string output = null;
            double seconds = 30;
            try
            {
                if (requested != "forza" && requested != "msfs") throw new ArgumentException("Source must be forza or msfs.");
                for (int i = 2; i < args.Length; i += 2)
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("Capture option requires a value.");
                    if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    else if (args[i] == "--out") output = args[i + 1];
                    else throw new ArgumentException("Unknown capture option: " + args[i]);
                }
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 1 || seconds > 300)
                    throw new ArgumentException("Capture duration must be between 1 and 300 seconds.");
                if (string.IsNullOrEmpty(output)) throw new ArgumentException("Specify --out capture.csv.");
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Capture usage: --capture-telemetry <forza|msfs> --seconds 30 --out capture.csv. " + ex.Message);
                return 4;
            }

            int rows = 0, freshRows = 0, listeningPorts = 0;
            string error = null;
            DateTime startedUtc = DateTime.UtcNow;
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                // The offline constructor disables serial discovery, connection,
                // the output worker and UI timers. This branch never calls Arm,
                // Connect, OpenGame or any input-injection API.
                using (MainForm form = new MainForm(true))
                using (StreamWriter writer = new StreamWriter(output, false, new UTF8Encoding(false)))
                {
                    form.SelectGame(requested == "forza" ? GameKind.Forza : GameKind.Msfs);
                    IntPtr messageWindow = form.Handle; // SimConnect needs a real message target.
                    writer.WriteLine("utc,elapsed_ms,requested_source,source,fresh,latest_sample_age_ms,native_timestamp_ms,raw_prefix_hex,native_x_rad_s,native_y_rad_s,native_z_rad_s,body_p_rad_s,body_q_rad_s,body_r_rad_s,aligned_command_p_rad_s,aligned_command_q_rad_s,aligned_command_r_rad_s,slew_preview_p_rad_s,slew_preview_q_rad_s,slew_preview_r_rad_s,nominal_preview_a_mA,nominal_preview_b_mA,nominal_preview_c_mA,nominal_preview_d_mA,preview_byte_a,preview_byte_b,preview_byte_c,preview_byte_d,preview_packet_hex,actual_packets_sent,serial_connected,armed");
                    MotionSnapshot lastValid = MotionSnapshot.Empty("No sample");
                    double previewP = 0, previewQ = 0, previewR = 0;
                    long nextPoll = 0;
                    try
                    {
                        while (elapsed.Elapsed.TotalSeconds < seconds)
                        {
                            Application.DoEvents();
                            if (elapsed.ElapsedMilliseconds < nextPoll) { Thread.Sleep(1); continue; }
                            nextPoll = elapsed.ElapsedMilliseconds + 25;
                            form.UpdateMsfsInput();
                            form.UpdateForzaInput();
                            if (requested == "forza")
                            {
                                listeningPorts = form.forzaInput.ListeningPortCount;
                                if (form.forzaInput.HasBindFailure)
                                    throw new InvalidOperationException("Cannot bind Forza UDP 5300/5607. Close the existing VFORCE GUI and other telemetry receivers, then retry capture.");
                            }
                            MotionSnapshot observed = requested == "forza" ? form.forzaMotion : form.msfsMotion;
                            bool fresh = observed.IsFresh;
                            if (fresh) { lastValid = observed; freshRows++; }
                            MotionSnapshot raw = fresh ? observed : lastValid;
                            double[] command = form.BuildCommandedRates(0, 0, 0);
                            double[] aligned = BodyToAlignedHeadRates(command[0], command[1], command[2]);
                            previewP = SlewRate(previewP, aligned[0]);
                            previewQ = SlewRate(previewQ, aligned[1]);
                            previewR = SlewRate(previewR, aligned[2]);
                            double[] nominal = PqrToCodeMatrixCurrents(previewP, previewQ, previewR, DefaultCurrentPeakMilliamp);
                            int[] bytes = CurrentsToUartBytes(nominal);
                            byte[] packet = SerialOutput.Frame(new int[] { 10, bytes[0], bytes[1], bytes[2], bytes[3] });
                            OutputStatus actual = form.serialOutput.Status;
                            if (actual.Connected || actual.Armed || actual.PacketCount != 0)
                                throw new InvalidOperationException("Capture violated its no-USB/no-transmission invariant.");
                            string[] row = new string[] {
                                CaptureCsv(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)), elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                                CaptureCsv(requested), CaptureCsv(observed.Source), fresh ? "true" : "false", CaptureNumber(raw.AgeMilliseconds),
                                raw.NativeTimestampMs.HasValue ? raw.NativeTimestampMs.Value.ToString(CultureInfo.InvariantCulture) : "", CaptureCsv(raw.RawPrefixHex),
                                CaptureNumber(raw.NativeX), CaptureNumber(raw.NativeY), CaptureNumber(raw.NativeZ),
                                CaptureNumber(raw.P), CaptureNumber(raw.Q), CaptureNumber(raw.R),
                                CaptureNumber(aligned[0]), CaptureNumber(aligned[1]), CaptureNumber(aligned[2]),
                                CaptureNumber(previewP), CaptureNumber(previewQ), CaptureNumber(previewR),
                                CaptureNumber(nominal[0]), CaptureNumber(nominal[1]), CaptureNumber(nominal[2]), CaptureNumber(nominal[3]),
                                bytes[0].ToString(CultureInfo.InvariantCulture), bytes[1].ToString(CultureInfo.InvariantCulture),
                                bytes[2].ToString(CultureInfo.InvariantCulture), bytes[3].ToString(CultureInfo.InvariantCulture),
                                CaptureCsv(BitConverter.ToString(packet)), actual.PacketCount.ToString(CultureInfo.InvariantCulture), "false", "false"
                            };
                            writer.WriteLine(string.Join(",", row));
                            rows++;
                        }
                    }
                    finally
                    {
                        writer.Flush();
                        form.Close();
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            elapsed.Stop();
            string warning = requested == "forza" && listeningPorts == 1 ?
                "Only one Forza UDP port could be opened. Close other telemetry receivers before relying on this capture." : null;
            string summary = "{\n" +
                "  \"scope\": \"shadow_telemetry_preview_no_usb\",\n" +
                "  \"source\": " + CaptureJson(requested) + ",\n" +
                "  \"started_utc\": " + CaptureJson(startedUtc.ToString("o", CultureInfo.InvariantCulture)) + ",\n" +
                "  \"duration_seconds\": " + CaptureNumber(elapsed.Elapsed.TotalSeconds) + ",\n" +
                "  \"rows\": " + rows.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"fresh_rows\": " + freshRows.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"forza_listening_ports\": " + listeningPorts.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"head_frame\": \"aligned_body_head_assumption_no_tracking\",\n" +
                "  \"nominal_limit_mA\": " + CaptureNumber(DefaultCurrentPeakMilliamp) + ",\n" +
                "  \"matrix_reference\": " + CaptureJson(ClassicNrfProfile.MappingReference) + ",\n" +
                "  \"command_axis_convention\": " + CaptureJson(ClassicNrfProfile.AxisConvention) + ",\n" +
                "  \"physical_game_axis_signs_verified\": false,\n" +
                "  \"matrix_gain_mA_per_rad_s\": " + CaptureNumber(DefaultCurrentPeakMilliamp / GvsMaxRate) + ",\n" +
                "  \"matrix_limiter\": \"shared_peak_bound_then_balanced_integer_quantization\",\n" +
                "  \"usb_open_count\": 0,\n  \"actual_packets_sent\": 0,\n  \"serial_connected\": false,\n  \"armed\": false,\n" +
                "  \"note\": \"Packet and current columns are computed previews, never transmitted or measured; stale rows retain the last raw sample with fresh=false.\",\n" +
                "  \"warning\": " + CaptureJson(warning) + ",\n" +
                "  \"error\": " + CaptureJson(error) + "\n}\n";
            try { File.WriteAllText(output + ".summary.json", summary, new UTF8Encoding(false)); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 4; }
            return error == null ? 0 : 4;
        }

        private sealed class SmokeTransport : IOutputTransport
        {
            public bool IsOpen { get; private set; }
            public void Open() { IsOpen = true; }
            public void Write(byte[] packet) { if (!IsOpen) throw new InvalidOperationException("Mock port closed"); }
            public void SettleAfterReset() { }
            public void Close() { IsOpen = false; }
            public void Dispose() { Close(); }
        }

        private static void SmokeRequire(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private void SaveSmokeFrame(string directory, string name)
        {
            Control surface = smokeSurface ?? this;
            surface.PerformLayout();
            UpdateUi();
            surface.Refresh();
            Application.DoEvents();
            File.AppendAllText(Path.Combine(directory, "layout.txt"), name + ": rendered_client=" + surface.ClientSize + " bitmap=" + surface.Size + " runner_screen=" + Screen.PrimaryScreen.Bounds + "\r\n");
            Button[] controls = new Button[] { calLeftButton, calRightButton, stopCalButton, armButton, pauseButton, connectButton, msfsLaunchButton, forzaLaunchButton, controllerButton };
            foreach (Button control in controls)
            {
                Rectangle box = surface.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                SmokeRequire(control.Visible && surface.ClientRectangle.Contains(box), "Clipped control: " + control.Text);
                SmokeRequire(control.Width >= 65 && control.Height >= 28, "Small control: " + control.Text);
                for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                {
                    Rectangle clip = surface.RectangleToClient(parent.RectangleToScreen(parent.ClientRectangle));
                    SmokeRequire(clip.Contains(box), "Control clipped by parent: " + control.Text);
                }
            }
            using (Bitmap bitmap = new Bitmap(surface.Width, surface.Height))
            {
                surface.DrawToBitmap(bitmap, new Rectangle(0, 0, surface.Width, surface.Height));
                bitmap.Save(Path.Combine(directory, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public static int UiSmokeTest(string directory)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Directory.CreateDirectory(directory);
            try
            {
                using (MainForm form = new MainForm(true))
                using (Panel surface = new Panel())
                {
                    // Render the actual view tree independently of the hosted
                    // runner's small desktop. Form.SetBoundsCore otherwise clamps
                    // a normal window to MaxWindowTrackSize and clips the bitmap.
                    form.smokeSurface = surface;
                    surface.Size = new Size(1280, 800);
                    surface.Controls.Add(form.Controls[0]);
                    surface.CreateControl();
                    surface.Show();
                    form.SaveSmokeFrame(directory, "purple-1280x800");
                    SmokeRequire(!form.armed && !form.calLeftButton.Enabled && !form.calRightButton.Enabled, "Calibration enabled while disarmed");
                    surface.Size = new Size(1180, 650);
                    form.SaveSmokeFrame(directory, "purple-1180x650");
                    surface.Size = new Size(1124, 621);
                    form.SaveSmokeFrame(directory, "purple-minimum");
                    surface.Size = new Size(1280, 720);
                    form.SaveSmokeFrame(directory, "purple-1280x720");
                    form.serialOutput.Connect(new SmokeTransport());
                    form.connectedPortName = "SIMULATED";
                    form.serialOutput.Arm();
                    form.UpdateUi();
                    form.calLeftButton.PerformClick();
                    Thread.Sleep(120);
                    form.UpdateVestibularOutput();
                    form.UpdateVestibularOutput(); // Advance two control ticks through the slew/quantization boundary.
                    form.SendVestibularOutput();
                    form.serialOutput.Pump();
                    SmokeRequire(form.calibrationDirection == 1 && form.currentBytes[0] == 128 && form.currentBytes[1] > 128 && form.currentBytes[2] == 128 && form.currentBytes[3] < 128, "Left Cal did not match PDF left-roll direction");
                    form.SaveSmokeFrame(directory, "purple-left-cal-mock");
                    form.stopCalButton.PerformClick();
                    SmokeRequire(!form.armed && form.calibrationDirection == 0, "Stop Cal did not disarm");
                    form.serialOutput.Arm();
                    form.UpdateUi();
                    form.calRightButton.PerformClick();
                    Thread.Sleep(120);
                    form.UpdateVestibularOutput();
                    form.UpdateVestibularOutput(); // Advance two control ticks through the slew/quantization boundary.
                    form.SendVestibularOutput();
                    form.serialOutput.Pump();
                    SmokeRequire(form.calibrationDirection == -1 && form.currentBytes[0] == 128 && form.currentBytes[1] < 128 && form.currentBytes[2] == 128 && form.currentBytes[3] > 128, "Right Cal did not match PDF right-roll direction");
                    form.SaveSmokeFrame(directory, "purple-right-cal-mock");
                    form.PauseButtonClick(null, EventArgs.Empty);
                    SmokeRequire(!form.armed && form.calibrationDirection == 0, "Pause did not stop calibration");
                    form.SelectGame(GameKind.Forza);
                    SmokeRequire(form.useForzaPhysics && !form.useMsfsPhysics && !form.armed, "Forza source selection");
                    form.SelectGame(GameKind.Msfs);
                    SmokeRequire(form.useMsfsPhysics && !form.useForzaPhysics && !form.armed, "MSFS source selection");
                    form.SelectController(2);
                    SmokeRequire(form.physicalInput.SelectedXInputIndex == 2 && !form.armed && form.calibrationDirection == 0, "Explicit controller selection did not disarm");
                    form.SelectController(-1);
                    SmokeRequire(form.physicalInput.SelectedXInputIndex == -1, "Auto controller selection");
                    form.RunAlignedFrameRegression(directory);
                    form.SafeDisarmAndClose();
                    form.serialOutput.Connect(new SmokeTransport());
                    form.serialOutput.Arm();
                    form.StopAfterUnexpectedError(new InvalidOperationException("injected UI callback failure"));
                    SmokeRequire(!form.serialOutput.Status.Connected && !form.armed && !form.Enabled && form.paused,
                        "Unexpected UI error must disconnect, stop simulation and prevent reconnect");
                    form.serialOutput.Dispose();
                    form.msfsInput.Dispose();
                    form.forzaInput.Dispose();
                    form.toolTip.Dispose();
                }
                RunMenuLifecycleSmoke(directory);
                File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: aligned-frame game/joystick cross-axis regression at varied visual attitudes; game/controller native menu-click lifecycle and repeated reopen; device-free Windows view-tree renders at exact 1280x800, 1280x720, 1180x650 and minimum client size1124x621; Left Cal/Right Cal directions via mock transport; Stop Cal/Pause disarm; game source switching. No physical hardware or running game tested.");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "result.txt"), ex.ToString());
                return 3;
            }
        }

        private void RunAlignedFrameRegression(string directory)
        {
            string report = Path.Combine(directory, "aligned-frame-regression.txt");
            File.WriteAllText(report, "Device-free actual GUI command-path regression with mock transport; no physical output or head tracker.\r\n");
            File.AppendAllText(report, "Mapping: Packet Definition PDF pages 4-5; application +P right roll, +Q front pitch, +R right yaw. Native game physical signs remain unverified.\r\n");
            double[][] attitudes = new double[][] {
                new double[] { 0, 0, 0 }, new double[] { 28, 55, 65 }, new double[] { -28, -55, -65 }
            };
            GameKind[] games = new GameKind[] { GameKind.Forza, GameKind.Msfs };
            int cases = 0;
            foreach (GameKind game in games)
            for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int[] baseline = null;
                foreach (double[] attitude in attitudes)
                {
                    SelectGame(game);
                    paused = false;
                    inputX = inputY = 0;
                    serialOutput.Arm(); // Mock transport only, established by UiSmokeTest.
                    roll = attitude[0]; pitch = attitude[1]; yaw = attitude[2];
                    double[] expected = new double[3];
                    expected[axis] = sign * 0.3;
                    for (int tick = 0; tick < 12; tick++)
                    {
                        MotionSnapshot sample = MotionSnapshot.Fresh("fixture " + game, expected[0], expected[1], expected[2], attitude[0], attitude[1], attitude[2]);
                        if (game == GameKind.Forza) forzaMotion = sample;
                        else msfsMotion = sample;
                        UpdateVestibularOutput();
                    }
                    SmokeRequire(PacketValuesEqual(new double[] { pRate, qRate, rRate }, expected),
                        "Pure game axis acquired cross-axis motion from display attitude");
                    SmokeRequire(IntValuesEqual(currentBytes, PdfDirectionPacket(axis, sign, 12)), "Game command did not match independent PDF direction vector");
                    if (baseline == null) baseline = (int[])currentBytes.Clone();
                    else SmokeRequire(IntValuesEqual(currentBytes, baseline), "Display attitude changed channel packet");
                    File.AppendAllText(report, "PASS " + game + " axis=" + axis + " sign=" + sign + " visual=" +
                        string.Join("/", Array.ConvertAll(attitude, CaptureNumber)) + " bytes=" +
                        string.Join("/", Array.ConvertAll(currentBytes, delegate(int value) { return value.ToString(CultureInfo.InvariantCulture); })) + "\r\n");
                    cases++;
                }
            }
            // Preserve the designed left-stick yaw/pitch mapping independently of
            // game selection and independently of the animated display attitude.
            for (int axis = 0; axis < 2; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            foreach (double[] attitude in attitudes)
            {
                serialOutput.Disarm();
                ResetCommandHistory();
                useForzaPhysics = useMsfsPhysics = false;
                paused = false;
                inputX = axis == 0 ? sign : 0;
                inputY = axis == 1 ? sign : 0;
                roll = attitude[0]; pitch = attitude[1]; yaw = attitude[2];
                serialOutput.Arm();
                for (int tick = 0; tick < 32; tick++) UpdateVestibularOutput();
                double[] expected = axis == 0 ? new double[] { 0, 0, sign * GvsMaxRate } : new double[] { 0, sign * GvsMaxRate, 0 };
                SmokeRequire(PacketValuesEqual(new double[] { pRate, qRate, rRate }, expected), "Joystick yaw/pitch mapping changed with display attitude");
                SmokeRequire(IntValuesEqual(currentBytes, PdfDirectionPacket(axis == 0 ? 2 : 1, sign, 33)), "Joystick command did not match independent PDF direction vector");
                cases++;
            }
            serialOutput.Disarm();
            ResetCommandHistory();
            inputX = inputY = 0;
            File.AppendAllText(report, "PASS " + cases + " pure-axis game/joystick cases; body/head alignment is an explicit assumption.\r\n");
        }

        private static int[] PdfDirectionPacket(int axis, int sign, int magnitude)
        {
            // Independent table oracle, not the production Mix implementation.
            int[][] table = new int[][] { new int[] { 0, -1, 0, 1 }, new int[] { 1, -1, 1, -1 }, new int[] { 1, -1, -1, 1 } };
            int[] result = new int[4];
            for (int channel = 0; channel < 4; channel++) result[channel] = 128 + table[axis][channel] * sign * magnitude;
            return result;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        private static extern IntPtr SendMenuTestMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private static void SmokeClickMenuItem(ContextMenuStrip menu, int index)
        {
            SmokeRequire(menu.Visible && !menu.IsDisposed, "Menu did not open");
            ToolStripItem item = menu.Items[index];
            SmokeRequire(item.Enabled && item.Available, "Menu item is unavailable: " + item.Text);
            int clicked = 0, closed = 0;
            EventHandler clickHandler = delegate { clicked++; };
            ToolStripDropDownClosedEventHandler closeHandler = delegate { closed++; };
            item.Click += clickHandler;
            menu.Closed += closeHandler;
            try
            {
                // Exercise WinForms' actual mouse-up/item-click/close dispatch.
                // ToolStripItem.PerformClick alone misses the dropdown lifecycle
                // that used the menu after its Closed handler had disposed it.
                Rectangle bounds = item.Bounds;
                int x = bounds.Left + bounds.Width / 2;
                int y = bounds.Top + bounds.Height / 2;
                IntPtr location = new IntPtr((y << 16) | (x & 0xffff));
                SendMenuTestMessage(menu.Handle, 0x0200, IntPtr.Zero, location); // WM_MOUSEMOVE
                SendMenuTestMessage(menu.Handle, 0x0201, new IntPtr(1), location); // WM_LBUTTONDOWN
                SendMenuTestMessage(menu.Handle, 0x0202, IntPtr.Zero, location); // WM_LBUTTONUP
                Application.DoEvents();
                SmokeRequire(clicked == 1 && closed == 1, "Native menu click/close was not dispatched exactly once: " + item.Text);
                SmokeRequire(!menu.Visible && !menu.IsDisposed && !item.IsDisposed,
                    "Dropdown was disposed during click dispatch: " + item.Text);
            }
            finally
            {
                item.Click -= clickHandler;
                menu.Closed -= closeHandler;
            }
        }

        private static void RunMenuLifecycleSmoke(string directory)
        {
            string report = Path.Combine(directory, "menu-lifecycle.txt");
            File.WriteAllText(report, "Device-free native WinForms menu-click regression; output remains disarmed and external launch is suppressed.\r\n");
            using (MainForm form = new MainForm(true))
            {
                form.Show();
                Application.DoEvents();
                ContextMenuStrip forza = form.GetGameMenu(GameKind.Forza);
                ContextMenuStrip msfs = form.GetGameMenu(GameKind.Msfs);
                ContextMenuStrip controller = form.GetControllerMenu();
                GameKind[] games = new GameKind[] { GameKind.Forza, GameKind.Msfs };
                for (int round = 0; round < 2; round++)
                {
                    foreach (GameKind game in games)
                    {
                        ContextMenuStrip menu = form.GetGameMenu(game);
                        for (int i = 0; i < menu.Items.Count; i++)
                        {
                            int launchesBefore = form.offlineLaunchRequests;
                            Button button = game == GameKind.Forza ? form.forzaLaunchButton : form.msfsLaunchButton;
                            button.PerformClick(); // Calls the same LaunchGame method as a user click.
                            Application.DoEvents();
                            SmokeRequire(object.ReferenceEquals(menu, form.GetGameMenu(game)), "Game menu was recreated");
                            SmokeClickMenuItem(menu, i);
                            SmokeRequire(form.useForzaPhysics == (game == GameKind.Forza) &&
                                form.useMsfsPhysics == (game == GameKind.Msfs) && !form.armed && form.calibrationDirection == 0,
                                "Menu click failed source selection or changed safe output state");
                            string expected = null;
                            if (game == GameKind.Forza && i == 1) expected = "steam://rungameid/1551360";
                            else if (game == GameKind.Msfs && i == 1) expected = "steam://rungameid/1250410";
                            else if (game == GameKind.Msfs && i == 2) expected = "steam://rungameid/2537590";
                            else if (i > 0) expected = "shell:AppsFolder\\" + (game == GameKind.Forza ?
                                "Microsoft.624F8B84B80_8wekyb3d8bbwe!Forzahorizon5" : "Microsoft.FlightSimulator_8wekyb3d8bbwe!App");
                            SmokeRequire(form.offlineLaunchRequests == launchesBefore + (expected == null ? 0 : 1) &&
                                (expected == null || form.offlineLaunchTarget == expected), "Wrong game launch route");
                            File.AppendAllText(report, "PASS round " + round + " " + game + " item " + i + " " + menu.Items[i].Text + "\r\n");
                        }
                    }
                    for (int i = 0; i < controller.Items.Count; i++)
                    {
                        form.controllerButton.PerformClick();
                        Application.DoEvents();
                        SmokeClickMenuItem(controller, i);
                        SmokeRequire(form.physicalInput.SelectedXInputIndex == i - 1 && !form.armed && form.calibrationDirection == 0,
                            "Controller menu did not select the exact slot safely");
                        File.AppendAllText(report, "PASS round " + round + " controller item " + i + "\r\n");
                    }
                    form.forzaLaunchButton.PerformClick();
                    Application.DoEvents();
                    forza.Close(ToolStripDropDownCloseReason.AppClicked);
                    SmokeRequire(!forza.IsDisposed, "Dismissed menu was disposed");
                }
                form.Close();
                form.Dispose();
                SmokeRequire(forza.IsDisposed && msfs.IsDisposed && controller.IsDisposed,
                    "Form did not dispose its owned dropdown menus");
                File.AppendAllText(report, "PASS all 24 native menu item clicks, repeated reopen/dismiss and form-owned disposal. No games launched; no USB transport opened.\r\n");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menuComponents.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            timer.Stop();
            visualTimer.Stop();
            SafeDisarmAndClose();
            serialOutput.Dispose();
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
                StopAfterUnexpectedError(ex);
            }
        }

        internal void DisconnectForFatalError()
        {
            try { serialOutput.Disconnect(); } catch { }
        }

        internal void StopAfterUnexpectedError(Exception ex)
        {
            // Disarm before reporting any exception, including menu/native callbacks.
            DisconnectForFatalError();
            RuntimeDiagnostics.Record("UI_ERROR " + ex);
            if (reportingUnexpectedError || closing) return;
            reportingUnexpectedError = true;
            timer.Stop(); visualTimer.Stop(); paused = true;
            ResetCommandHistory();
            SetStatus("Fault - output disconnected; restart app");
            Enabled = false;
            if (offlineMode) return;
            MessageBox.Show("VFORCE stopped the simulation and disconnected output. Restart the app before continuing.\r\n\r\n" +
                ex.Message + "\r\n\r\nDiagnostic log: " + RuntimeDiagnostics.LogPath,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void TimerTickCore()
        {
            DateTime now = DateTime.UtcNow;
            if ((!serialOutput.Status.Connected) && (now - lastSerialScan).TotalSeconds >= 5.0)
            {
                lastSerialScan = now;
                TryAutoConnectVestibularDevice();
            }

            long controlTick = Stopwatch.GetTimestamp();
            double dt = Math.Max(0.0, Math.Min((controlTick - lastTick) / (double)Stopwatch.Frequency, 0.05));
            lastTick = controlTick;
            UpdatePhysicalStickInput();
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
            if (controlTick - lastRuntimeReportTicks >= Stopwatch.Frequency)
            {
                lastRuntimeReportTicks = controlTick;
                RuntimeDiagnostics.Record("STATE selected=" + (UseForzaPhysics() ? "forza" : UseMsfsPhysics() ? "msfs" : "joystick") +
                    " game=" + GetFreshGameMotion().IsFresh + " forza={" + forzaInputStatus + "; " + forzaInput.Diagnostics +
                    "} msfs={" + msfsInputStatus + "; " + msfsInput.Diagnostics + "} " + OutputDiagnosticText());
            }
        }

        private void VisualTimerTick(object sender, EventArgs e)
        {
            try
            {
                UpdatePhysicalStickInput();
                UpdateUi(false);
            }
            catch (Exception ex) { StopAfterUnexpectedError(ex); }
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

            double[] commanded = (!armed || paused) ? new double[3] : BuildCommandedRates(activeX, activeY, calibrationIntensity);
            commandedP = commanded[0];
            commandedQ = commanded[1];
            commandedR = commanded[2];
            // Game telemetry already reports body-frame angular velocity. Without
            // an actual head-pose input, explicitly assume the head is aligned to
            // that frame. Display animation is never a head-orientation sensor.
            double[] headPqr = BodyToAlignedHeadRates(commandedP, commandedQ, commandedR);

            pRate = SlewRate(pRate, headPqr[0]);
            qRate = SlewRate(qRate, headPqr[1]);
            rRate = SlewRate(rRate, headPqr[2]);

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
            serialOutput.Publish(currentBytes);
        }

        private void ResetCommandHistory()
        {
            StopCalibration();
            pRate = qRate = rRate = commandedP = commandedQ = commandedR = 0.0;
            for (int i = 0; i < 4; i++)
            {
                packetValues[i] = currentMilliampValues[i] = dacValues[i] = 0.0;
                currentBytes[i] = 128;
            }
        }

        private void SendNeutralOutput()
        {
            ResetCommandHistory();
            try { serialOutput.Neutral(); }
            catch (Exception ex) { SetStatus("Neutral failed - disarmed: " + ex.Message); }
        }

        private void ConnectButtonClick(object sender, EventArgs e)
        {
            if (serialOutput.Status.Connected)
            {
                SafeDisarmAndClose();
                return;
            }

            TryConnectSelectedPort(true);
        }

        private bool TryConnectSelectedPort(bool interactive)
        {
            if (offlineMode || portCombo.SelectedItem == null) return false;
            try
            {
                SerialPort port = new SerialPort(portCombo.SelectedItem.ToString(), SerialBaud);
                port.DataBits = 8;
                port.Parity = Parity.None;
                port.StopBits = StopBits.One;
                port.Handshake = Handshake.None;
                port.DtrEnable = true;
                port.RtsEnable = false;
                port.WriteBufferSize = 4096;
                port.WriteTimeout = 100;
                port.ReadTimeout = 250;
                serialOutput.Connect(new SerialOutputTransport(port));
                connectedPortName = portCombo.SelectedItem.ToString();
                connectionAge.Restart();
                ResetCommandHistory();
                SetStatus("NRF serial open - wait 3 seconds, then Arm; no firmware ACK");
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                serialOutput.Disconnect();
                SetStatus("Connect failed: " + ex.Message);
                if (interactive) MessageBox.Show("Could not open serial port: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateUi();
                return false;
            }
        }

        private void SelectController(int index)
        {
            serialOutput.Disarm();
            ResetCommandHistory();
            physicalInput.SelectedXInputIndex = index;
            physicalInputConnected = false;
            physicalInputIndex = -1;
            inputX = inputY = 0.0;
            SetStatus("Controller selected - move the left stick while disarmed");
            UpdateUi();
        }

        private ContextMenuStrip GetControllerMenu()
        {
            if (controllerMenu != null) return controllerMenu;
            controllerMenu = new ContextMenuStrip(menuComponents);
            controllerMenu.Items.Add("Auto - use active left stick", null, delegate { SelectController(-1); });
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                controllerMenu.Items.Add("XInput " + (i + 1).ToString(), null, delegate { SelectController(index); });
            }
            return controllerMenu;
        }

        private void ShowControllerMenu(object sender, EventArgs e)
        {
            GetControllerMenu().Show(controllerButton, new Point(0, controllerButton.Height));
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
            if (!serialOutput.Status.Connected) return;
            if (!armed && connectionAge.ElapsedMilliseconds < 3000)
            {
                SetStatus("Wait 3 seconds after connection before Arm");
                return;
            }
            try
            {
                ResetCommandHistory();
                if (armed) { serialOutput.Disarm(); SetStatus("Disarmed"); }
                else { serialOutput.Arm(); SetStatus("Arm sent - firmware does not acknowledge"); }
            }
            catch (Exception ex) { SetStatus("Serial error - disarmed: " + ex.Message); }
            UpdateUi();
        }

        private void PauseButtonClick(object sender, EventArgs e)
        {
            paused = !paused;
            if (paused)
            {
                StopCalibration();
                ResetCommandHistory();
                serialOutput.Disarm();
                SetStatus("Paused - disarmed");
            }
            else if (armed)
            {
                SetStatus("Output armed - legacy gvs.py");
            }
            else if (serialOutput.Status.Connected)
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
            if (!armed || paused) { SetStatus("Connect and Arm before calibration"); return; }
            if (calibrationDirection == (direction < 0 ? -1 : 1)) { CancelCalibration(); return; }
            SendNeutralOutput();
            calibrationDirection = direction < 0 ? -1 : 1;
            calibrationClock.Restart();
            SetStatus(direction > 0 ? "Left Cal - five-second directional test" : "Right Cal - five-second directional test");
            UpdateUi();
        }

        private void StopCalibration()
        {
            calibrationDirection = 0;
            calibrationClock.Reset();
        }

        private void CancelCalibration()
        {
            ResetCommandHistory();
            serialOutput.Disarm();
            SetStatus("Calibration stopped - disarmed");
            UpdateUi();
        }

        private static double CalibrationIntensity(double seconds)
        {
            if (seconds < 0 || seconds >= 5.0) return 0.0;
            return seconds <= 2.5 ? seconds / 2.5 : (5.0 - seconds) / 2.5;
        }

        private double GetCalibrationIntensity()
        {
            if (calibrationDirection == 0) return 0.0;
            if (!armed || paused) { StopCalibration(); return 0.0; }
            double elapsed = calibrationClock.Elapsed.TotalSeconds;
            if (elapsed >= 5.0)
            {
                ResetCommandHistory();
                serialOutput.Disarm();
                SetStatus("Calibration complete - disarmed");
                return 0.0;
            }
            return CalibrationIntensity(elapsed);
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
            if (offlineMode || closing || serialOutput.Status.Connected)
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
            if (preferred == null && PortExists(ports, "COM4"))
            {
                preferred = "COM4";
            }
            if (preferred == null && ports.Length > 0)
            {
                preferred = ports[0];
            }
            if (preferred != null && portCombo.Items.Contains(preferred))
            {
                portCombo.SelectedItem = preferred;
            }

            if (!serialOutput.Status.Connected)
            {
                SetStatus(ports.Length == 0 ? "Connect VMocion USB" : "VMocion ready to connect");
            }

            if (attemptAutoConnect && vestibularPort != null && (!serialOutput.Status.Connected))
            {
                TryConnectSelectedPort(false);
            }
        }

        private void SafeDisarmAndClose()
        {
            serialOutput.Disconnect();
            ResetCommandHistory();
            connectedPortName = null;
            connectionAge.Reset();
            SetStatus("Ready");
            UpdateUi();
        }

        private void UpdateUi()
        {
            UpdateUi(true);
        }

        private void UpdateUi(bool forceText)
        {
            OutputStatus output = serialOutput.Status;
            controllerButton.Text = physicalInput.SelectedXInputIndex < 0 ? "Controller: Auto" : "Controller: " + (physicalInput.SelectedXInputIndex + 1).ToString();
            if (!output.Armed && calibrationDirection != 0) ResetCommandHistory();
            connectButton.Text = output.Connected ? "Disconnect" : "Connect";
            portCombo.Enabled = !output.Connected;
            armButton.Enabled = output.Connected && !paused && (output.Armed || connectionAge.ElapsedMilliseconds >= 3000);
            calLeftButton.Enabled = calRightButton.Enabled = output.Armed && !paused;
            stopCalButton.Enabled = calibrationDirection != 0;
            calLeftButton.BackColor = calibrationDirection > 0 ? Theme.Warning : Theme.PurpleDim;
            calRightButton.BackColor = calibrationDirection < 0 ? Theme.Warning : Theme.PurpleDim;
            calLeftButton.ForeColor = calibrationDirection > 0 ? Color.Black : Theme.Text;
            calRightButton.ForeColor = calibrationDirection < 0 ? Color.Black : Theme.Text;
            if (output.Error != null) SetStatus(output.Error);
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
                inputState = "PAUSED  |  OUTPUT NEUTRAL";
            }
            else if (activeGameMotion.IsFresh)
            {
                inputState = FriendlyGameName(activeGameMotion.Source) + " MOTION LIVE";
            }
            else if (UseMsfsPhysics())
            {
                inputState = msfsInputStatus.ToUpperInvariant();
            }
            else if (UseForzaPhysics())
            {
                inputState = forzaInputStatus.ToUpperInvariant();
            }
            inputLabel.Text = inputState;

            armButton.Text = armed ? "Disarm" : "Arm";
            armButton.BackColor = armed ? Theme.Danger : Theme.Purple;
            armButton.ForeColor = Color.White;
            armButton.FlatAppearance.BorderColor = armed ? Theme.Danger : Theme.PurpleBright;
            pauseButton.Text = paused ? "Resume" : "Pause";

            controllerBadge.SetStatus(physicalInputConnected ? "XINPUT " + (physicalInputIndex + 1).ToString() : "SEARCHING",
                physicalInputConnected ? BadgeState.Ready : BadgeState.Neutral);
            bool deviceConnected = serialOutput.Status.Connected;
            string selectedPort = portCombo.SelectedItem as string;
            deviceBadge.SetStatus(deviceConnected ? connectedPortName : (string.IsNullOrEmpty(selectedPort) ? "SCANNING" : "AUTO " + selectedPort),
                deviceConnected ? BadgeState.Ready : BadgeState.Neutral);
            outputBadge.SetStatus(armed ? "ARM SENT" : (paused ? "PAUSED" : "SAFE"),
                armed ? BadgeState.Danger : (paused ? BadgeState.Warning : BadgeState.Neutral));

            UpdateGameButton(msfsLaunchButton, "FLIGHT SIM", UseMsfsPhysics(), msfsFresh);
            UpdateGameButton(forzaLaunchButton, "FORZA 5", UseForzaPhysics(), forzaFresh);
        }

        private string lastLoggedStatus;
        private void SetStatus(string text)
        {
            if (lastLoggedStatus != text) { RuntimeDiagnostics.Record("STATUS " + text); lastLoggedStatus = text; }
            if (statusLabel != null)
            {
                statusLabel.Text = "SYSTEM  /  " + text.ToUpperInvariant();
                toolTip.SetToolTip(statusLabel, text);
            }
        }

        private void SelectGame(GameKind game)
        {
            serialOutput.Disarm();
            ResetCommandHistory();
            useMsfsPhysics = game == GameKind.Msfs;
            useForzaPhysics = game == GameKind.Forza;
            msfsInput.ResetStatus();
            forzaInput.ResetStatus();
            msfsMotion = MotionSnapshot.Empty("MSFS waiting");
            forzaMotion = MotionSnapshot.Empty("Forza waiting");
            SetStatus("Game selected - waiting for telemetry; press Arm when ready");
            UpdateUi();
        }

        private ContextMenuStrip GetGameMenu(GameKind game)
        {
            ContextMenuStrip existing = game == GameKind.Forza ? forzaMenu : msfsMenu;
            if (existing != null) return existing;
            ContextMenuStrip menu = new ContextMenuStrip(menuComponents);
            menu.Items.Add("Use telemetry from running game", null, delegate { SelectGame(game); });
            if (game == GameKind.Forza)
                menu.Items.Add("Launch Forza Horizon 5 - Steam", null, delegate { OpenGame(game, "steam://rungameid/1551360"); });
            else
            {
                menu.Items.Add("Launch Flight Simulator 2020 - Steam", null, delegate { OpenGame(game, "steam://rungameid/1250410"); });
                menu.Items.Add("Launch Flight Simulator 2024 - Steam", null, delegate { OpenGame(game, "steam://rungameid/2537590"); });
            }
            menu.Items.Add("Launch Microsoft Store edition", null, delegate {
                string appId = game == GameKind.Forza ? "Microsoft.624F8B84B80_8wekyb3d8bbwe!Forzahorizon5" : "Microsoft.FlightSimulator_8wekyb3d8bbwe!App";
                OpenGame(game, "shell:AppsFolder\\" + appId);
            });
            // WinForms still uses this dropdown after its Closed event while
            // dispatching an item click. Keep it alive for the form's lifetime.
            if (game == GameKind.Forza) forzaMenu = menu;
            else msfsMenu = menu;
            return menu;
        }

        private void LaunchGame(GameKind game)
        {
            Button anchor = game == GameKind.Forza ? forzaLaunchButton : msfsLaunchButton;
            GetGameMenu(game).Show(anchor, new Point(0, anchor.Height));
        }

        private void OpenGame(GameKind game, string target)
        {
            SelectGame(game);
            if (offlineMode)
            {
                offlineLaunchRequests++;
                offlineLaunchTarget = target;
                SetStatus("Offline menu test - external launch suppressed");
                return;
            }
            try
            {
                if (target.StartsWith("shell:", StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                SetStatus("Launch requested - waiting for game telemetry");
            }
            catch (Exception ex)
            {
                SetStatus("Launch unavailable - start game manually; telemetry remains selected");
                MessageBox.Show("Start the game manually, then return to VFORCE.\r\n\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            button.Text = name + "\r\n" + (live ? "LIVE" : (selected ? "WAITING" : "SELECT"));
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

        private string OutputDiagnosticText()
        {
            OutputStatus output = serialOutput.Status;
            return "serial open=" + output.Connected + " arm sent=" + output.Armed + " packets=" + output.PacketCount +
                " TX=" + output.LastPacket + " snapshot age ms=" + output.SnapshotAgeMs +
                " TX age ms=" + output.LastSendAgeMs + " max write ms=" + output.MaxWriteMs +
                " max gap ms=" + output.MaxSendGapMs + " error=" + (output.Error ?? "none") +
                " body PQR=" + CaptureNumber(GetFreshGameMotion().P) + "/" + CaptureNumber(GetFreshGameMotion().Q) + "/" + CaptureNumber(GetFreshGameMotion().R) +
                " command PQR=" + CaptureNumber(pRate) + "/" + CaptureNumber(qRate) + "/" + CaptureNumber(rRate) +
                " preview ABCD=" + string.Join("/", Array.ConvertAll(currentBytes, v => v.ToString()));
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
                "Frame model: aligned body/head assumption; no head tracking input\r\n" +
                "Mapping: Packet Definition PDF pages 4-5; A/B/C/D = DAC 1/2/3/4\r\n" +
                "Command +P/+Q/+R: right roll / front pitch / right yaw\r\n" +
                "Native game physical directions and electrode wiring require bench verification\r\n" +
                "Game body P/Q/R (rad/s): " + gameMotion.P.ToString("0.000") + " / " + gameMotion.Q.ToString("0.000") + " / " + gameMotion.R.ToString("0.000") + "\r\n" +
                "Display attitude does not change the output frame\r\n" +
                "MSFS: " + msfsInputStatus + "\r\n" +
                "Forza: " + forzaInputStatus + "\r\n" +
                "Forza counters: " + forzaInput.Diagnostics + "\r\n" +
                "MSFS counters: " + msfsInput.Diagnostics + "\r\n" +
                "Runtime log: " + RuntimeDiagnostics.LogPath + "\r\n" +
                "Sender: " + OutputDiagnosticText() + "\r\n" +
                "VMocion: " + ((serialOutput.Status.Connected) ? connectedPortName + " serial open (no firmware ACK)" : "not connected") + "\r\n" +
                "Available ports: " + (ports.Length == 0 ? "none" : string.Join(", ", ports)) + "\r\n" +
                "Output: " + (armed ? "ARM REQUESTED / UNACKNOWLEDGED" : "DISARMED / SAFE") + "\r\n" +
                "Nominal command limit (not measured): " + FormatCurrentPeakLabel(currentPeakMilliamp) + "\r\n" +
                "Packets sent: " + packetCount.ToString() + "\r\n" +
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

            Button manualConnect = MakeButton(serialOutput.Status.Connected ? "Disconnect" : "Connect");
            manualConnect.Width = 96;
            manualConnect.Click += delegate
            {
                string requestedPort = portPicker.SelectedItem as string;
                dialog.Close();
                if (serialOutput.Status.Connected)
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
            Button button = new VForceButton();
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

        private static double[] BodyToAlignedHeadRates(double p, double q, double r)
        {
            return new double[] { p, q, r };
        }

        private static double SlewRate(double previous, double target)
        {
            return previous + Clamp(target - previous, -GvsMaxRamp, GvsMaxRamp);
        }

        private static string FindPortByHardwareText(string[] ports, string[] needles)
        {
            string selected = null;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (ManagementObjectCollection devices = searcher.Get())
                {
                    foreach (ManagementObject device in devices)
                    {
                        string text = ((device["Name"] as string) ?? "") + " " + ((device["PNPDeviceID"] as string) ?? "");
                        if (!ContainsAny(text, needles)) continue;
                        string port = ExtractPortName(text, ports);
                        if (port == null) continue;
                        // Multiple application boards require manual COM selection.
                        if (selected != null && !string.Equals(selected, port, StringComparison.OrdinalIgnoreCase)) return null;
                        selected = port;
                    }
                }
            }
            catch { return null; }
            return selected;
        }

        private static string ExtractPortName(string text, string[] ports)
        {
            for (int i = 0; i < ports.Length; i++)
            {
                string port = ports[i];
                if (text.IndexOf("(" + port + ")", StringComparison.OrdinalIgnoreCase) >= 0)
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

        private static double[] JoystickToCommandedRates(double activeX, double activeY, int calibratingDirection, double calibrationIntensity)
        {
            if (calibratingDirection != 0)
            {
                return new double[]
                {
                    -calibratingDirection * GvsMaxRate * calibrationIntensity,
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
            return ClassicNrfProfile.Mix(p, q, r, GvsMaxRate, currentPeakMilliamp);
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
            return ClassicNrfProfile.Encode(currents);
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
                e.Graphics.DrawString(Connected ? "CONTROLLER CONNECTED" : "CONTROLLER NOT FOUND", title, text, 34, 14);
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
