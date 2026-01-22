using Xunit;

namespace InviteBot.Tests {
    // Pure-arithmetic tests for the QR caption sizing rules. The actual rendering is exercised
    // by hand against real overlays; these tests pin down the invariants that a regression
    // would otherwise quietly break (the symptom is a caption that looks wrong on overlays the
    // developer did not happen to test with).
    public class CaptionLayoutTests {

        // 1) Proportional in the mid range.
        [Theory]
        [InlineData(1024u, 1024 * 0.022)]
        [InlineData(2048u, 2048 * 0.022)]
        public void TargetFontSize_IsProportionalInMidRange(uint dim, double expected) {
            double got = InviteBot.CaptionLayout.ComputeTargetFontSize(dim);
            Assert.Equal(expected, got, 6);
        }

        // 2) Floors out below the minimum so tiny overlays still get readable text.
        [Theory]
        [InlineData(64u)]
        [InlineData(256u)]
        [InlineData(512u)]   // 512 * 0.022 = 11.264, still under the 12-pt floor
        public void TargetFontSize_FloorsAtMin(uint dim) {
            Assert.Equal(InviteBot.CaptionLayout.MinFontPx, InviteBot.CaptionLayout.ComputeTargetFontSize(dim));
        }

        // 3) Ceilings out above the maximum so giant prints do not get a billboard caption.
        [Theory]
        [InlineData(4096u)]
        [InlineData(8192u)]
        public void TargetFontSize_CeilingsAtMax(uint dim) {
            Assert.Equal(InviteBot.CaptionLayout.MaxFontPx, InviteBot.CaptionLayout.ComputeTargetFontSize(dim));
        }

        // 4) Width budget is a fixed fraction of dim.
        [Fact]
        public void MaxTextWidthPx_IsFractionOfDim() {
            Assert.Equal(2048 * 0.92, InviteBot.CaptionLayout.ComputeMaxTextWidthPx(2048u), 6);
        }

        // 5) Shrink-to-fit: caption that already fits is returned untouched.
        [Fact]
        public void FitFontSize_ReturnsTargetWhenCaptionFits() {
            double got = InviteBot.CaptionLayout.FitFontSize(targetFontPx: 32, measuredWidthAtTargetPx: 500, maxWidthPx: 1000);
            Assert.Equal(32, got);
        }

        // 6) Shrink-to-fit: caption that is too wide gets scaled down by the width ratio.
        [Fact]
        public void FitFontSize_ScalesDownWhenCaptionOverflows() {
            // Caption is twice as wide as the budget -> font should be halved.
            double got = InviteBot.CaptionLayout.FitFontSize(targetFontPx: 40, measuredWidthAtTargetPx: 2000, maxWidthPx: 1000);
            Assert.Equal(20, got);
        }

        // 7) Shrink-to-fit deliberately does not enforce MinFontPx on the way down. A caption
        //    that is unreadably small is still better than one that clips past the QR's edge,
        //    because the QR itself remains scannable.
        [Fact]
        public void FitFontSize_AllowsResultBelowMinFont() {
            double got = InviteBot.CaptionLayout.FitFontSize(targetFontPx: 12, measuredWidthAtTargetPx: 10000, maxWidthPx: 1000);
            Assert.True(got < InviteBot.CaptionLayout.MinFontPx);
        }

        // 8) Defensive: zero / negative inputs return the target unchanged so we never divide
        //    by zero in production.
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void FitFontSize_HandlesZeroOrNegativeInputs(double measured) {
            Assert.Equal(20, InviteBot.CaptionLayout.FitFontSize(20, measured, 1000));
            Assert.Equal(20, InviteBot.CaptionLayout.FitFontSize(20, 100, measured));
        }

        // 9) Bottom padding scales with dim and floors at 2 px.
        [Theory]
        [InlineData(64u, 2.0)]      // 64 * 0.015 = 0.96, floor wins
        [InlineData(2048u, 2048 * 0.015)]
        public void BottomPadding_ScalesAndFloors(uint dim, double expected) {
            Assert.Equal(expected, InviteBot.CaptionLayout.ComputeBottomPaddingPx(dim), 6);
        }
    }
}
