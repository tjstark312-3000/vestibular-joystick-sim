using System;

namespace VestibularJoystickSim
{
    // Host command scale for classic_r8_1_distribution. These are nominal
    // model values; the silent Classic transport supplies no current readback.
    internal static class ClassicNrfProfile
    {
        public const double MaximumNominalMilliamp = 1.5;
        private const double CodesPerMilliamp = 100.0 / MaximumNominalMilliamp;

        public static double[] Mix(double p, double q, double r, double maxRate, double limit)
        {
            if (!Finite(p) || !Finite(q) || !Finite(r) || !Finite(maxRate) ||
                !Finite(limit) || maxRate <= 0.0 || limit <= 0.0)
                return new double[4];

            limit = Math.Min(limit, MaximumNominalMilliamp);
            // Scaling before mixing also avoids overflow for large finite input.
            double inputScale = Math.Max(maxRate, Math.Max(Math.Abs(p), Math.Max(Math.Abs(q), Math.Abs(r))));
            p /= inputScale;
            q /= inputScale;
            r /= inputScale;
            double pitch = 1.5 * q;
            double[] currents = new double[]
            {
                -p + pitch - r,
                -p - pitch + r,
                 p - pitch - r,
                 p + pitch + r
            };
            double peak = Peak(currents);
            // December 2025 PRD relative gains, also used by NRF AXES:
            // pitch is 1.5 times roll/yaw below the shared output limiter.
            // A full-scale pure pitch reaches limit; pure roll/yaw reach 2/3.
            // Keep K constant until the mixed vector needs limiting.
            double denominator = Math.Max(1.5 * (maxRate / inputScale), peak);
            if (denominator == 0.0) return new double[4];
            // One scale retains the zero-sum matrix and axis ratios. Independent
            // clipping would introduce common mode, subsequently removed by NRF.
            for (int i = 0; i < 4; ++i)
                currents[i] = currents[i] / denominator * limit;
            return currents;
        }

        public static int[] Encode(double[] currents)
        {
            if (currents == null || currents.Length != 4)
                throw new ArgumentException("Exactly four nominal channel commands are required.", "currents");
            for (int i = 0; i < 4; ++i)
                if (!Finite(currents[i])) return Neutral();

            double requestedPeak = Math.Min(MaximumNominalMilliamp, Peak(currents));
            // Round the magnitude DOWN before quantization: a 0.50 mA request
            // permits 33 Classic steps (about 0.495 mA), never 34 (0.510 mA).
            int bound = QuantizedBound(requestedPeak);
            if (bound == 0) return Neutral();

            double sourceScale = Math.Max(1.0, Peak(currents));
            double mean = 0.0;
            for (int i = 0; i < 4; ++i) mean += currents[i] / sourceScale * 0.25;
            double[] centered = new double[4];
            double peak = 0.0;
            for (int i = 0; i < 4; ++i)
            {
                centered[i] = currents[i] / sourceScale - mean;
                peak = Math.Max(peak, Math.Abs(centered[i]));
            }
            if (peak == 0.0) return Neutral();

            // Recenter arbitrary callers as the firmware does, then constrain
            // the entire vector to the same requested bound. Mix() already
            // supplies a balanced vector, so this is normally only quantization.
            double scale = Math.Min(sourceScale * CodesPerMilliamp, bound / peak);
            int[] delta = new int[4];
            double[] remainder = new double[4];
            int sum = 0;
            for (int i = 0; i < 4; ++i)
            {
                double ideal = Math.Max(-bound, Math.Min(bound, centered[i] * scale));
                delta[i] = (int)Math.Floor(ideal);
                remainder[i] = ideal - delta[i];
                sum += delta[i];
            }
            // Largest-remainder rounding preserves exact integer zero sum. The
            // shared magnitude bound means the firmware will not renormalize.
            while (sum < 0)
            {
                int best = -1;
                for (int i = 0; i < 4; ++i)
                    if (delta[i] < bound && (best < 0 || remainder[i] > remainder[best])) best = i;
                if (best < 0) return Neutral();
                ++delta[best];
                remainder[best] -= 1.0;
                ++sum;
            }
            while (sum > 0)
            {
                int best = -1;
                for (int i = 0; i < 4; ++i)
                    if (delta[i] > -bound && (best < 0 || remainder[i] < remainder[best])) best = i;
                if (best < 0) return Neutral();
                --delta[best];
                remainder[best] += 1.0;
                --sum;
            }
            int[] bytes = new int[4];
            for (int i = 0; i < 4; ++i) bytes[i] = 128 + delta[i];
            return bytes;
        }

        private static int QuantizedBound(double nominal)
        {
            // Correct only floating-point noise at exact 0.015 mA boundaries.
            // This tolerance is many orders below a command step.
            return Math.Max(0, Math.Min(100, (int)Math.Floor(nominal * CodesPerMilliamp + 1e-10)));
        }

        private static int[] Neutral() { return new int[] { 128, 128, 128, 128 }; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static double Peak(double[] values)
        {
            double peak = 0.0;
            for (int i = 0; i < values.Length; ++i) peak = Math.Max(peak, Math.Abs(values[i]));
            return peak;
        }

        public static bool SelfTest()
        {
            if (!Equals(Encode(Mix(1.0, 0.0, 0.0, 1.0, 0.5)), 106, 106, 150, 150) ||
                !Equals(Encode(Mix(-1.0, 0.0, 0.0, 1.0, 0.5)), 150, 150, 106, 106) ||
                !Equals(Encode(Mix(1.0, 0.0, 0.0, 1.0, 1.5)), 62, 62, 194, 194) ||
                !Equals(Encode(Mix(0.0, 1.0, 0.0, 1.0, 1.5)), 228, 28, 28, 228) ||
                !Equals(Encode(Mix(0.0, 0.0, 1.0, 1.0, 1.5)), 62, 194, 62, 194) ||
                !Equals(Encode(Mix(0.0, 0.0, 0.0, 1.0, 1.5)), 128, 128, 128, 128) ||
                !Equals(Encode(new double[] { 0.5, 0.5, 0.5, 0.5 }), 128, 128, 128, 128) ||
                !Equals(Encode(new double[] { double.NaN, 0.0, 0.0, 0.0 }), 128, 128, 128, 128) ||
                !Equals(Encode(Mix(double.PositiveInfinity, 0.0, 0.0, 1.0, 0.5)), 128, 128, 128, 128))
                return false;

            // Independent PRD/NRF gain contract: the pitch coefficient must
            // remain 1.5x the roll/yaw coefficient before vector saturation.
            // Test the floating point mix before byte quantization.
            double[] lowRoll = Mix(0.3, 0.0, 0.0, 1.0, 1.5);
            double[] lowPitch = Mix(0.0, 0.3, 0.0, 1.0, 1.5);
            double[] lowYaw = Mix(0.0, 0.0, 0.3, 1.0, 1.5);
            double[] combination = Mix(0.2, -0.1, 0.3, 1.0, 1.5);
            double[] expectedCombination = new double[] { -0.65, 0.25, 0.05, 0.35 };
            if (Math.Abs(lowPitch[0] + 1.5 * lowRoll[0]) > 1e-12 ||
                Math.Abs(lowYaw[0] - lowRoll[0]) > 1e-12) return false;
            for (int i = 0; i < 4; ++i)
                if (Math.Abs(combination[i] - expectedCombination[i]) > 1e-12) return false;

            double[] axes = new double[] { -2.0, -1.0, -0.75, -0.25, 0.0, 0.25, 0.75, 1.0, 2.0 };
            double[] limits = new double[] { -1.0, 0.0, 0.01, 0.015, 0.1, 0.5, 1.0, 1.5, 2.5 };
            foreach (double limit in limits)
            foreach (double p in axes)
            foreach (double q in axes)
            foreach (double r in axes)
            {
                double[] mixed = Mix(p, q, r, 1.0, limit);
                double bound = Math.Max(0.0, Math.Min(MaximumNominalMilliamp, limit));
                double sum = mixed[0] + mixed[1] + mixed[2] + mixed[3];
                if (Math.Abs(sum) > 1e-12 || Peak(mixed) > bound + 1e-12) return false;
                int[] bytes = Encode(mixed);
                int byteSum = 0;
                int integerBound = QuantizedBound(Peak(mixed));
                for (int i = 0; i < 4; ++i)
                {
                    byteSum += bytes[i];
                    if (bytes[i] < 28 || bytes[i] > 228 || Math.Abs(bytes[i] - 128) > integerBound)
                        return false;
                }
                if (byteSum != 512) return false;
            }

            // Common-mode removal must not amplify above the selected bound.
            int[] recentered = Encode(new double[] { -0.5, -0.5, -0.5, 0.5 });
            if (recentered[0] + recentered[1] + recentered[2] + recentered[3] != 512) return false;
            for (int i = 0; i < 4; ++i)
                if (Math.Abs(recentered[i] - 128) > 33) return false;
            double[] extreme = Mix(double.MaxValue, -double.MaxValue, double.MaxValue, 1.0, 1.5);
            for (int i = 0; i < 4; ++i)
                if (!Finite(extreme[i]) || Math.Abs(extreme[i]) > 1.5) return false;
            return true;
        }

        private static bool Equals(int[] values, int a, int b, int c, int d)
        { return values[0] == a && values[1] == b && values[2] == c && values[3] == d; }
    }
}
