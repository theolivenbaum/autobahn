using System;

namespace Autobahn.Ui
{
    /// <summary>
    /// Turning numbers into the strings a dashboard shows.
    /// </summary>
    /// <remarks>
    /// Written out rather than composed from format strings: this code is transpiled to
    /// JavaScript, where a .NET numeric format string is emulated rather than native, and the
    /// half-dozen shapes a load test needs are shorter to write than they are to specify.
    ///
    /// Everything here is defensive about NaN and infinity. A percentile with no measurements
    /// behind it is genuinely absent, and a tile reading "NaN" is worse than one reading "-".
    /// </remarks>
    internal static class Format
    {
        public const string Absent = "-";

        /// <summary>A count, grouped in threes: 1234567 becomes 1,234,567.</summary>
        public static string Count(double value)
        {
            if (!IsReal(value)) return Absent;

            var negative = value < 0;
            var digits = Whole(Math.Abs(value));

            return (negative ? "-" : "") + Group(digits);
        }

        /// <summary>A rate, with enough precision to be useful at both 0.4/s and 40,000/s.</summary>
        public static string Rate(double value)
        {
            if (!IsReal(value)) return Absent;
            if (value >= 100) return Count(value);

            return Fixed(value, value >= 10 ? 1 : 2);
        }

        /// <summary>A latency in milliseconds, with the precision the magnitude deserves.</summary>
        public static string Milliseconds(double value)
        {
            if (!IsReal(value)) return Absent;
            if (value >= 1000) return Fixed(value / 1000, 2) + " s";
            if (value >= 100) return Fixed(value, 0) + " ms";

            return Fixed(value, value >= 10 ? 1 : 2) + " ms";
        }

        /// <summary>A byte count in the largest unit that keeps it readable.</summary>
        public static string Bytes(double value)
        {
            if (!IsReal(value)) return Absent;

            var negative = value < 0;
            var size = Math.Abs(value);
            var text =
                size >= 1024d * 1024 * 1024 ? Fixed(size / (1024d * 1024 * 1024), 2) + " GB" :
                size >= 1024d * 1024 ? Fixed(size / (1024d * 1024), 2) + " MB" :
                size >= 1024 ? Fixed(size / 1024, 1) + " KB" :
                Fixed(size, 0) + " B";

            return (negative ? "-" : "") + text;
        }

        /// <summary>A share already expressed as 0..1.</summary>
        public static string Percent(double ratio)
        {
            if (!IsReal(ratio)) return Absent;

            var percent = ratio * 100;

            // A tiny but non-zero error rate is the interesting one, and rounding it to "0%"
            // is exactly the wrong answer.
            if (percent > 0 && percent < 0.01) return "<0.01%";

            return Fixed(percent, percent >= 10 ? 1 : 2) + "%";
        }

        /// <summary>A signed change, for the delta a KPI tile carries.</summary>
        public static string Delta(double value, Func<double, string> render)
        {
            if (!IsReal(value) || value == 0) return "";

            return (value > 0 ? "+" : "-") + render(Math.Abs(value));
        }

        /// <summary>Seconds as hh:mm:ss, or mm:ss when there is no hour to show.</summary>
        public static string Duration(double seconds)
        {
            if (!IsReal(seconds) || seconds < 0) return Absent;

            var total = (int)Math.Floor(seconds);
            var hours = total / 3600;
            var minutes = total / 60 % 60;
            var rest = total % 60;

            return hours > 0
                ? Pad(hours) + ":" + Pad(minutes) + ":" + Pad(rest)
                : Pad(minutes) + ":" + Pad(rest);
        }

        /// <summary>A wall-clock time of day from milliseconds since the epoch.</summary>
        public static string Clock(double epochMs)
        {
            if (!IsReal(epochMs) || epochMs <= 0) return Absent;

            var date = new DateTime(1970, 1, 1).AddMilliseconds(epochMs).ToLocalTime();

            return Pad(date.Hour) + ":" + Pad(date.Minute) + ":" + Pad(date.Second);
        }

        /// <summary>A date and time of day, for a run that finished some days ago.</summary>
        public static string DateAndClock(double epochMs)
        {
            if (!IsReal(epochMs) || epochMs <= 0) return Absent;

            var date = new DateTime(1970, 1, 1).AddMilliseconds(epochMs).ToLocalTime();

            return date.Year + "-" + Pad(date.Month) + "-" + Pad(date.Day)
                   + " " + Pad(date.Hour) + ":" + Pad(date.Minute);
        }

        /// <summary>A number with exactly this many decimal places, zeros included.</summary>
        public static string Fixed(double value, int decimals)
        {
            if (!IsReal(value)) return Absent;
            if (decimals <= 0) return Count(Math.Round(value));

            var factor = Math.Pow(10, decimals);
            var scaled = Math.Round(Math.Abs(value) * factor);

            var whole = Math.Floor(scaled / factor);
            var fraction = Whole(scaled - whole * factor);

            while (fraction.Length < decimals) fraction = "0" + fraction;

            return (value < 0 ? "-" : "") + Group(Whole(whole)) + "." + fraction;
        }

        private static bool IsReal(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// A non-negative whole number as digits, with no exponent and no decimal point.
        /// </summary>
        /// <remarks>
        /// <c>ToString()</c> on a double reaches JavaScript's own number formatting, which is
        /// close enough to .NET's for every value a load test produces - but not for one large
        /// enough to come back in exponential form, so the exponent case is caught rather than
        /// rendered as "1e+21 requests".
        /// </remarks>
        private static string Whole(double value)
        {
            var text = Math.Floor(value).ToString();

            return text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0 ? "many" : text;
        }

        private static string Group(string digits)
        {
            if (digits.Length <= 3) return digits;

            var grouped = "";

            for (var i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) grouped += ",";
                grouped += digits[i];
            }

            return grouped;
        }

        private static string Pad(int value) => value < 10 ? "0" + value : value.ToString();
    }
}
