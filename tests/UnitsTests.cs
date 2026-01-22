using Xunit;

namespace InviteBot.Tests {

    // Tests for Units.cs - the user-supplied length parser and its display counterpart.
    //
    // These functions are pure (no I/O, no Discord, no SQLite), which makes them the ideal
    // first target for unit tests: they execute in microseconds, never flake, and cover
    // input-validation logic that is easy to break accidentally and hard to spot in production.
    //
    // xUnit primer (xUnit is the .NET test framework Visual Studio's Test Explorer understands
    // out of the box):
    //   [Fact]                       - a single test case.
    //   [Theory] + [InlineData(...)] - the same test body executed once per InlineData row.
    //                                  Each row appears as its own entry in Test Explorer, so a
    //                                  failure tells you exactly which input broke.
    //   Assert.True / Equal / etc.   - the assertions. They throw on failure; the runner
    //                                  catches the throw and marks the row red.
    public class UnitsTests {

        // ---------------------------------------------------------------------
        // TryParseLengthMm - happy paths
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("90",        90.0)]   // bare number defaults to mm
        [InlineData("90mm",      90.0)]
        [InlineData("90 mm",     90.0)]   // whitespace tolerance
        [InlineData("  90mm  ",  90.0)]   // leading/trailing whitespace
        [InlineData("90MM",      90.0)]   // case insensitivity
        [InlineData("9cm",       90.0)]   // cm -> mm
        [InlineData("9 CM",      90.0)]
        [InlineData("0.09m",     90.0)]   // m -> mm
        [InlineData("0.09 m",    90.0)]
        [InlineData("3.5in",     88.9)]   // in -> mm (3.5 * 25.4)
        [InlineData("3.5 inch",  88.9)]
        [InlineData("3.5 inches",88.9)]
        [InlineData("3.5\"",     88.9)]   // double-quote alias for inches
        public void TryParseLengthMm_AcceptsValidInputs(string input, double expectedMm) {
            bool ok = InviteBot.TryParseLengthMm(input, out double mm, out string? error);

            Assert.True(ok, $"expected \"{input}\" to parse but got error: {error}");
            Assert.Null(error);
            // Floating-point unit conversions can drift in the last bit, so compare with tolerance.
            Assert.Equal(expectedMm, mm, precision: 3);
        }

        // ---------------------------------------------------------------------
        // TryParseLengthMm - boundary conditions
        // ---------------------------------------------------------------------

        [Fact]
        public void TryParseLengthMm_AcceptsLowerBoundExactly() {
            Assert.True(InviteBot.TryParseLengthMm("10mm", out double mm, out _));
            Assert.Equal(10.0, mm);
        }

        [Fact]
        public void TryParseLengthMm_AcceptsUpperBoundExactly() {
            Assert.True(InviteBot.TryParseLengthMm("500mm", out double mm, out _));
            Assert.Equal(500.0, mm);
        }

        [Fact]
        public void TryParseLengthMm_RejectsJustBelowLowerBound() {
            Assert.False(InviteBot.TryParseLengthMm("9.99mm", out _, out string? error));
            Assert.NotNull(error);
            Assert.Contains("between", error);
        }

        [Fact]
        public void TryParseLengthMm_RejectsJustAboveUpperBound() {
            Assert.False(InviteBot.TryParseLengthMm("500.01mm", out _, out string? error));
            Assert.NotNull(error);
            Assert.Contains("between", error);
        }

        // ---------------------------------------------------------------------
        // TryParseLengthMm - rejection cases
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("",            "no value")]            // empty string
        [InlineData("   ",         "no value")]            // whitespace only
        [InlineData("abc",         "could not parse")]     // non-numeric junk
        [InlineData("90km",        "could not parse")]     // unsupported unit
        [InlineData("9e1",         "could not parse")]     // exponential rejected by design
        [InlineData("+90",         "could not parse")]     // explicit sign rejected
        [InlineData("-90",         "could not parse")]     // negative rejected at the regex
        [InlineData("90 mm extra", "could not parse")]     // trailing garbage
        public void TryParseLengthMm_RejectsBadInput(string input, string expectedErrorFragment) {
            bool ok = InviteBot.TryParseLengthMm(input, out double mm, out string? error);

            Assert.False(ok);
            Assert.Equal(0.0, mm);
            Assert.NotNull(error);
            Assert.Contains(expectedErrorFragment, error);
        }

        // ---------------------------------------------------------------------
        // FormatMm - status output formatting
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData(90.0,    "90 mm")]      // whole number: no decimal point
        [InlineData(100.0,   "100 mm")]
        [InlineData(88.9,    "88.9 mm")]    // single fractional digit
        [InlineData(88.95,   "89 mm")]      // rounds to nearest, no trailing zeroes
        [InlineData(88.94,   "88.9 mm")]    // rounds down
        [InlineData(10.0,    "10 mm")]
        public void FormatMm_RendersExpectedString(double mm, string expected) {
            Assert.Equal(expected, InviteBot.FormatMm(mm));
        }

        // ---------------------------------------------------------------------
        // Round-trip: parse then format. The display string for a parsed value should
        // itself parse back to the same value, modulo the formatter's deliberate rounding.
        // This catches accidental drift between the two functions if either is changed.
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData("90mm")]
        [InlineData("9cm")]
        [InlineData("0.09m")]
        [InlineData("100mm")]
        public void ParseThenFormat_RoundTripsForCleanValues(string input) {
            Assert.True(InviteBot.TryParseLengthMm(input, out double mm, out _));
            string formatted = InviteBot.FormatMm(mm);
            Assert.True(InviteBot.TryParseLengthMm(formatted, out double mm2, out _));
            Assert.Equal(mm, mm2, precision: 3);
        }
    }
}
