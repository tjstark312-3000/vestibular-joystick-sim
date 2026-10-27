using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace VestibularJoystickSim
{
    internal sealed class PhysicalJoystickInput
    {
        private const int ErrorSuccess = 0;
        private const int JoyNoError = 0;
        private const int JoyReturnAll = 0x000000ff;
        // This input path intentionally reads the left stick only. Right-stick
        // movement, mouse emulation and keyboard mappings do not drive output.
        // Apply an 8% radial deadzone and retain the signed native axis range.
        private const double LeftThumbDeadzone = 0.08;
        private const int ErrorDeviceNotConnected = 1167;
        private const double TakeoverMovement = 0.05;
        private delegate int XInputReader(int index, out XInputState state);
        private delegate bool WinMmReader(uint index, out double x, out double y, out string source);
        private readonly XInputReader read14;
        private readonly XInputReader read910;
        private readonly Func<uint> winMmCount;
        private readonly WinMmReader readWinMm;
        private readonly Func<long> clockTicks;
        private readonly long[] disconnectedUntil = new long[4];
        private bool xinput14Unavailable;
        private bool xinput910Unavailable;
        private int preferredXInputIndex = -1;
        private int selectedXInputIndex = -1;

        public PhysicalJoystickInput()
        {
            read14 = XInputGetState14;
            read910 = XInputGetState910;
            winMmCount = JoyGetNumDevsSafe;
            readWinMm = TryReadWinMmJoystick;
            clockTicks = Stopwatch.GetTimestamp;
        }

        private PhysicalJoystickInput(XInputReader first, XInputReader legacy,
            Func<uint> count, WinMmReader joystick, Func<long> ticks)
        {
            read14 = first;
            read910 = legacy;
            winMmCount = count;
            readWinMm = joystick;
            clockTicks = ticks;
        }

        // -1 selects automatic discovery; 0..3 pins that exact XInput slot.
        // A disconnected pinned slot never falls through to another controller.
        public int SelectedXInputIndex
        {
            get { return selectedXInputIndex; }
            set
            {
                if (value < -1 || value > 3) throw new ArgumentOutOfRangeException("value");
                if (value == selectedXInputIndex) return;
                selectedXInputIndex = value;
                preferredXInputIndex = -1;
                Array.Clear(disconnectedUntil, 0, disconnectedUntil.Length);
            }
        }

        public bool TryReadLeftStick(out double x, out double y, out int controllerIndex, out string source)
        {
            x = y = 0.0;
            controllerIndex = -1;
            source = "No left stick";
            if (selectedXInputIndex >= 0)
            {
                XInputState pinned;
                source = "XInput " + (selectedXInputIndex + 1).ToString() + " left stick unavailable";
                if (!TryGetState(selectedXInputIndex, true, out pinned)) return false;
                NormalizeLeftStick(pinned.Gamepad.ThumbLX, pinned.Gamepad.ThumbLY, out x, out y);
                controllerIndex = selectedXInputIndex;
                source = "XInput " + (controllerIndex + 1).ToString() + " left stick";
                return true;
            }

            bool found = false;
            double bestMagnitude = -1.0;
            int oldPreferred = preferredXInputIndex;
            if (oldPreferred >= 0)
            {
                XInputState preferred;
                if (TryGetState(oldPreferred, false, out preferred))
                {
                    NormalizeLeftStick(preferred.Gamepad.ThumbLX, preferred.Gamepad.ThumbLY, out x, out y);
                    controllerIndex = oldPreferred;
                    found = true;
                    bestMagnitude = Magnitude(x, y);
                    // Preserve the chosen controller while its left stick is active.
                    if (bestMagnitude >= TakeoverMovement)
                    {
                        source = "XInput " + (controllerIndex + 1).ToString() + " left stick";
                        return true;
                    }
                }
                else preferredXInputIndex = -1;
            }

            for (int i = 0; i < 4; i++)
            {
                if (i == oldPreferred) continue;
                XInputState state;
                if (!TryGetState(i, false, out state)) continue;
                double candidateX, candidateY;
                NormalizeLeftStick(state.Gamepad.ThumbLX, state.Gamepad.ThumbLY, out candidateX, out candidateY);
                double magnitude = Magnitude(candidateX, candidateY);
                // An initially centered device must not permanently hide another
                // connected controller when the operator actually moves its stick.
                if (!found || (magnitude >= TakeoverMovement && magnitude > bestMagnitude))
                {
                    found = true;
                    bestMagnitude = magnitude;
                    x = candidateX;
                    y = candidateY;
                    controllerIndex = i;
                }
            }

            if (found)
            {
                preferredXInputIndex = controllerIndex;
                source = "XInput " + (controllerIndex + 1).ToString() + " left stick";
                return true;
            }

            uint count = winMmCount();
            for (uint i = 0; i < count && i < 16; i++)
            {
                double candidateX, candidateY;
                string candidateSource;
                if (!readWinMm(i, out candidateX, out candidateY, out candidateSource)) continue;
                double magnitude = Magnitude(candidateX, candidateY);
                if (!found || magnitude > bestMagnitude)
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

        private bool TryGetState(int controllerIndex, bool bypassDiscoveryCache, out XInputState state)
        {
            state = new XInputState();
            long now = clockTicks();
            // Expensive discovery of absent slots runs at most four times/second.
            // Connected/preferred devices and an explicitly selected slot stay live.
            if (!bypassDiscoveryCache && now < disconnectedUntil[controllerIndex]) return false;
            bool connected = false;
            if (!xinput14Unavailable)
            {
                try { connected = read14(controllerIndex, out state) == ErrorSuccess; }
                catch (DllNotFoundException) { xinput14Unavailable = true; }
                catch (EntryPointNotFoundException) { xinput14Unavailable = true; }
                catch (BadImageFormatException) { xinput14Unavailable = true; }
            }
            if (xinput14Unavailable && !xinput910Unavailable)
            {
                try { connected = read910(controllerIndex, out state) == ErrorSuccess; }
                catch (DllNotFoundException) { xinput910Unavailable = true; }
                catch (EntryPointNotFoundException) { xinput910Unavailable = true; }
                catch (BadImageFormatException) { xinput910Unavailable = true; }
            }
            disconnectedUntil[controllerIndex] = connected ? 0 : now + Math.Max(1, Stopwatch.Frequency / 4);
            return connected;
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
                uint capResult = joyGetDevCaps(new UIntPtr(joystickIndex), out caps, (uint)Marshal.SizeOf(typeof(JoyCaps)));
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

            double center = ((double)min + max) / 2.0;
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

        private static bool AbiIsValid()
        {
            return Marshal.SizeOf(typeof(XInputGamepad)) == 12 &&
                Marshal.SizeOf(typeof(XInputState)) == 16 &&
                Marshal.OffsetOf(typeof(XInputState), "Gamepad").ToInt32() == 4 &&
                Marshal.OffsetOf(typeof(XInputGamepad), "ThumbLX").ToInt32() == 4 &&
                Marshal.OffsetOf(typeof(XInputGamepad), "ThumbLY").ToInt32() == 6 &&
                Marshal.OffsetOf(typeof(XInputGamepad), "ThumbRX").ToInt32() == 8 &&
                Marshal.OffsetOf(typeof(XInputGamepad), "ThumbRY").ToInt32() == 10 &&
                Marshal.SizeOf(typeof(JoyInfoEx)) == 52 &&
                Marshal.SizeOf(typeof(JoyCaps)) == 728 &&
                Marshal.OffsetOf(typeof(JoyCaps), "XMin").ToInt32() == 68 &&
                Marshal.OffsetOf(typeof(JoyCaps), "RegKey").ToInt32() == 144;
        }

        // These tests use injected native readers and exercise the same selection
        // method as the application, without opening USB or producing output.
        public static bool SelfTest()
        {
            if (!AbiIsValid()) return false;
            if (NormalizeAxis(short.MinValue) != -1.0 || NormalizeAxis(short.MaxValue) != 1.0 ||
                NormalizeAxis(0) != 0.0) return false;
            double x, y;
            NormalizeLeftStick(0, 0, out x, out y);
            if (x != 0 || y != 0) return false;
            NormalizeLeftStick(2621, 0, out x, out y);
            if (x != 0 || y != 0) return false;
            NormalizeLeftStick(2622, 0, out x, out y);
            if (!(x > 0 && x < 0.001) || y != 0) return false;
            NormalizeLeftStick(short.MaxValue, 0, out x, out y);
            if (x != 1 || y != 0) return false;
            NormalizeLeftStick(short.MinValue, 0, out x, out y);
            if (x != -1 || y != 0) return false;
            NormalizeLeftStick(0, short.MaxValue, out x, out y);
            if (x != 0 || y != 1) return false;
            NormalizeLeftStick(0, short.MinValue, out x, out y);
            if (x != 0 || y != -1) return false;
            NormalizeLeftStick(short.MaxValue, short.MaxValue, out x, out y);
            if (Math.Abs(x - Math.Sqrt(0.5)) > 0.0000001 || Math.Abs(x - y) > 0.0000001) return false;
            short[] edges = new short[] { short.MinValue, -2622, -2621, 0, 2621, 2622, short.MaxValue };
            foreach (short rawX in edges)
                foreach (short rawY in edges)
                {
                    NormalizeLeftStick(rawX, rawY, out x, out y);
                    if (double.IsNaN(x) || double.IsNaN(y) || Magnitude(x, y) > 1.00000001 ||
                        (rawX < 0 && x > 0) || (rawY < 0 && y > 0)) return false;
                }
            if (NormalizeWinMmAxis(0, 0, 65535, false) != -1 ||
                NormalizeWinMmAxis(65535, 0, 65535, false) != 1 ||
                NormalizeWinMmAxis(0, 0, 65535, true) != 1 ||
                Math.Abs(NormalizeWinMmAxis(32767, 0, 65535, false)) > 0.0001) return false;

            for (int slot = 0; slot < 4; slot++)
            {
                FakeReaders one = new FakeReaders();
                one.Connected[slot] = true;
                one.States[slot].Gamepad.ThumbLX = short.MaxValue;
                if (!ReadMatches(one.Create(), slot, 1, 0)) return false;
            }

            FakeReaders devices = new FakeReaders();
            devices.Connected[0] = devices.Connected[2] = true;
            PhysicalJoystickInput input = devices.Create();
            if (!ReadMatches(input, 0, 0, 0)) return false;
            devices.States[2].Gamepad.ThumbLX = short.MaxValue;
            if (!ReadMatches(input, 2, 1, 0)) return false; // Moving Ally wins over idle slot zero.
            devices.States[0].Gamepad.ThumbLY = short.MaxValue;
            if (!ReadMatches(input, 2, 1, 0)) return false; // Preserve an active preferred pad.
            devices.States[2].Gamepad.ThumbLX = 0;
            if (!ReadMatches(input, 0, 0, 1)) return false;
            input.SelectedXInputIndex = 2;
            if (!ReadMatches(input, 2, 0, 0)) return false; // Explicit pin ignores another moving pad.
            devices.Connected[2] = false;
            int index;
            string source;
            if (input.TryReadLeftStick(out x, out y, out index, out source) || x != 0 || y != 0 ||
                index != -1 || devices.WinMmReadCalls != 0) return false;
            devices.Connected[2] = true;
            devices.States[2].Gamepad.ThumbLY = short.MinValue;
            if (!ReadMatches(input, 2, 0, -1)) return false; // Pinned reconnect is immediate.
            input.SelectedXInputIndex = -1;
            devices.Connected[0] = false;
            if (!ReadMatches(input, 2, 0, -1)) return false;
            devices.Connected[2] = false;
            devices.Connected[3] = true;
            devices.States[3].Gamepad.ThumbLX = short.MinValue;
            devices.Ticks += Stopwatch.Frequency;
            if (!ReadMatches(input, 3, -1, 0)) return false; // Automatic hotplug/reselection.

            FakeReaders rightOnly = new FakeReaders();
            rightOnly.Connected[0] = true;
            rightOnly.States[0].Gamepad.ThumbRX = short.MaxValue;
            rightOnly.States[0].Gamepad.ThumbRY = short.MinValue;
            if (!ReadMatches(rightOnly.Create(), 0, 0, 0)) return false;

            FakeReaders cache = new FakeReaders();
            PhysicalJoystickInput cached = cache.Create();
            cached.TryReadLeftStick(out x, out y, out index, out source);
            cached.TryReadLeftStick(out x, out y, out index, out source);
            for (int i = 0; i < 4; i++) if (cache.Calls14[i] != 1) return false;
            cache.Connected[3] = true;
            cache.States[3].Gamepad.ThumbLX = short.MaxValue;
            cache.Ticks += Stopwatch.Frequency / 4 + 1;
            if (!ReadMatches(cached, 3, 1, 0)) return false;

            FakeReaders fallback = new FakeReaders();
            fallback.Missing14 = true;
            fallback.Connected[1] = true;
            fallback.States[1].Gamepad.ThumbLX = short.MaxValue;
            if (!ReadMatches(fallback.Create(), 1, 1, 0) || fallback.Calls14[0] != 1 ||
                fallback.Calls14[1] != 0 || fallback.LegacyCalls == 0) return false;
            FakeReaders entrypoint = new FakeReaders();
            entrypoint.MissingEntry14 = true;
            entrypoint.Connected[3] = true;
            if (!ReadMatches(entrypoint.Create(), 3, 0, 0) || entrypoint.LegacyCalls == 0) return false;
            FakeReaders winMm = new FakeReaders();
            winMm.Missing14 = winMm.Missing910 = true;
            winMm.WinMmAvailable = true;
            if (!ReadMatches(winMm.Create(), 0, 1, -1) || winMm.WinMmReadCalls != 1) return false;
            FakeReaders priority = new FakeReaders();
            priority.Connected[0] = true;
            priority.WinMmAvailable = true;
            if (!ReadMatches(priority.Create(), 0, 0, 0) || priority.WinMmCountCalls != 0) return false;
            bool rejected = false;
            try { input.SelectedXInputIndex = 4; }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            if (!rejected) return false;
            rejected = false;
            try { input.SelectedXInputIndex = -2; }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            return rejected;
        }

        // A Windows native API/ABI probe. Success with no controllers means only
        // that the APIs loaded and returned ERROR_DEVICE_NOT_CONNECTED correctly.
        // It does not establish Ally hardware input or background game behavior.
        public static bool NativeSelfTest()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !AbiIsValid()) return false;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    XInputState state;
                    int first = XInputGetState14(i, out state);
                    int legacy = XInputGetState910(i, out state);
                    if ((first != ErrorSuccess && first != ErrorDeviceNotConnected) ||
                        (legacy != ErrorSuccess && legacy != ErrorDeviceNotConnected)) return false;
                }
                return joyGetNumDevs() <= 16;
            }
            catch { return false; }
        }

        private static bool ReadMatches(PhysicalJoystickInput input, int wantedIndex, double wantedX, double wantedY)
        {
            double x, y;
            int index;
            string source;
            return input.TryReadLeftStick(out x, out y, out index, out source) && index == wantedIndex &&
                Math.Abs(x - wantedX) < 0.0000001 && Math.Abs(y - wantedY) < 0.0000001;
        }

        private sealed class FakeReaders
        {
            public readonly bool[] Connected = new bool[4];
            public readonly XInputState[] States = new XInputState[4];
            public readonly int[] Calls14 = new int[4];
            public bool Missing14, MissingEntry14, Missing910, WinMmAvailable;
            public int LegacyCalls, WinMmCountCalls, WinMmReadCalls;
            public long Ticks = Stopwatch.Frequency;

            public PhysicalJoystickInput Create()
            {
                return new PhysicalJoystickInput(Read14, Read910, CountWinMm, ReadWinMm, delegate { return Ticks; });
            }
            public int Read14(int index, out XInputState state)
            {
                Calls14[index]++;
                state = States[index];
                if (Missing14) throw new DllNotFoundException();
                if (MissingEntry14) throw new EntryPointNotFoundException();
                return Connected[index] ? ErrorSuccess : ErrorDeviceNotConnected;
            }
            public int Read910(int index, out XInputState state)
            {
                LegacyCalls++;
                state = States[index];
                if (Missing910) throw new DllNotFoundException();
                return Connected[index] ? ErrorSuccess : ErrorDeviceNotConnected;
            }
            public uint CountWinMm()
            {
                WinMmCountCalls++;
                return WinMmAvailable ? 1u : 0u;
            }
            public bool ReadWinMm(uint index, out double x, out double y, out string source)
            {
                WinMmReadCalls++;
                x = 1;
                y = -1;
                source = "Windows joystick left stick";
                return WinMmAvailable;
            }
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState14(int dwUserIndex, out XInputState pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState910(int dwUserIndex, out XInputState pState);

        [DllImport("winmm.dll")]
        private static extern uint joyGetNumDevs();

        [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint joyGetDevCaps(UIntPtr uJoyID, out JoyCaps pjc, uint cbjc);

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

}
