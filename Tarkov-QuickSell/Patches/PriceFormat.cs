using System;
using System.Globalization;

namespace QuickSell.Patches
{
    /// <summary>
    /// Compact price formatting for tooltips and dialogs.
    ///
    /// Tooltips are narrow and the game clips them, so long numbers get cut off mid-digit. Compact
    /// output is a correctness concern here, not just a cosmetic one.
    ///
    /// Note the 1,000,000 boundary: values between 1M and 10M switch to M, so 2,831,000 renders as
    /// "2.83M" rather than "2,831k". Formatters that only switch at 10M leave that whole band
    /// reading as an implausibly large thousands figure.
    /// </summary>
    internal static class PriceFormat
    {
        private const string Rouble = "\u20BD";

        public static string Format(double value, string currency = Rouble)
        {
            value = Math.Round(value);

            if (value >= 10_000_000)
                return $"{currency}{(value / 1_000_000).ToString("#,0", CultureInfo.InvariantCulture)}M";

            if (value >= 1_000_000)
                return $"{currency}{(value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture)}M";

            if (value >= 100_000)
                return $"{currency}{(value / 1_000).ToString("#,0", CultureInfo.InvariantCulture)}k";

            if (value >= 10_000)
                return $"{currency}{(value / 1_000).ToString("0.#", CultureInfo.InvariantCulture)}k";

            return $"{currency}{value.ToString("#,0", CultureInfo.InvariantCulture)}";
        }
    }
}
