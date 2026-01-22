using System.Globalization;
using System.Text.RegularExpressions;

namespace InviteBot {
    public partial class InviteBot {

        // Bounds enforced on print-size requests. 10 mm at 300 DPI is ~118 px on the long edge,
        // well below anything useful; 500 mm at 300 DPI is ~5906 px, beyond which Magick starts
        // to chew through memory for what is meant to be a printable QR card.
        private const double PrintLongEdgeMinMm = 10.0;
        private const double PrintLongEdgeMaxMm = 500.0;

        // Accept a friendly grab-bag of units. Default is millimetres when no unit is given,
        // because that's what the rest of the surface (status output, DB column) speaks. The
        // regex deliberately rejects exponential notation, leading +/-, and anything weirder than
        // "<number><optional unit>" so that operator typos surface as parse failures rather than
        // silently-accepted nonsense.
        private static readonly Regex LengthPattern = new(
            @"^\s*(?<value>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>mm|cm|m|in|""|inch|inches)?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Parses a user-supplied length string into millimetres. Returns false with a friendly
        // human-readable error on any failure. Accepts "90", "90mm", "9 cm", "0.09 m", "3.5 in",
        // and 3.5" - the leading-default-mm case keeps the common path zero-friction.
        internal static bool TryParseLengthMm(string input, out double mm, out string? error) {
            mm = 0.0;
            error = null;
            if (string.IsNullOrWhiteSpace(input)) {
                error = "no value supplied";
                return false;
            }

            Match m = LengthPattern.Match(input);
            if (!m.Success) {
                error = $"could not parse \"{input}\" as a length (try e.g. 90mm, 9cm, 0.09m, or 3.5in)";
                return false;
            }
            if (!double.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) {
                error = $"could not parse \"{input}\" as a number";
                return false;
            }

            string unit = m.Groups["unit"].Success ? m.Groups["unit"].Value.ToLowerInvariant() : "mm";
            mm = unit switch {
                "mm" => value,
                "cm" => value * 10.0,
                "m" => value * 1000.0,
                "in" or "inch" or "inches" or "\"" => value * 25.4,
                _ => value,
            };

            if (double.IsNaN(mm) || double.IsInfinity(mm) || mm <= 0) {
                error = "value must be positive";
                return false;
            }
            if (mm < PrintLongEdgeMinMm || mm > PrintLongEdgeMaxMm) {
                error = $"value must be between {PrintLongEdgeMinMm:0.#} mm and {PrintLongEdgeMaxMm:0.#} mm (got {mm:0.##} mm)";
                return false;
            }
            return true;
        }

        // Friendly inverse for status output: emits whole-millimetre values plain, fractional ones
        // to one decimal place, and never trails zeroes.
        internal static string FormatMm(double mm) =>
            mm == Math.Floor(mm)
                ? $"{mm:0} mm"
                : $"{mm:0.#} mm";
    }
}
