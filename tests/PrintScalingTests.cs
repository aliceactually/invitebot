using Xunit;

namespace InviteBot.Tests {
    // Pure-arithmetic tests for the print-scaling rules. Render-time correctness is exercised
    // by hand against real overlays and a real printer; these tests pin down the invariants
    // that a regression would otherwise quietly break (the symptom is an invite that prints
    // at the wrong physical size or with a subtly stretched aspect ratio).
    public class PrintScalingTests {

        // 1) mm-to-pixel conversion at 300 DPI. 100 mm at 300 DPI is 100/25.4*300 ~= 1181.1,
        //    which rounds to 1181.
        [Fact]
        public void LongEdgePixels_Converts100mmAt300Dpi() {
            Assert.Equal(1181u, InviteBot.PrintScaling.LongEdgePixels(100.0, 300.0));
        }

        // 2) Exactly one inch at 300 DPI is 300 px.
        [Fact]
        public void LongEdgePixels_OneInchIs300PxAt300Dpi() {
            Assert.Equal(300u, InviteBot.PrintScaling.LongEdgePixels(25.4, 300.0));
        }

        // 3) Defensive: non-positive input never produces a zero-dimension target.
        [Theory]
        [InlineData(0.0, 300.0)]
        [InlineData(-5.0, 300.0)]
        [InlineData(100.0, 0.0)]
        [InlineData(100.0, -1.0)]
        public void LongEdgePixels_ClampsToOnePixel(double mm, double dpi) {
            Assert.Equal(1u, InviteBot.PrintScaling.LongEdgePixels(mm, dpi));
        }

        // 4) Landscape: width is the long edge, height scales by the aspect ratio.
        [Fact]
        public void Compute_LandscapePinsWidthAndScalesHeight() {
            var t = InviteBot.PrintScaling.Compute(currentWidth: 3000, currentHeight: 2000, longEdgeMm: 100.0, dpi: 300.0);
            Assert.Equal(1181u, t.Width);
            // 1181 * (2000/3000) = 787.33 -> 787
            Assert.Equal(787u, t.Height);
            Assert.True(t.NeedsResize);
        }

        // 5) Portrait: height is the long edge, width scales by the aspect ratio.
        [Fact]
        public void Compute_PortraitPinsHeightAndScalesWidth() {
            var t = InviteBot.PrintScaling.Compute(currentWidth: 2000, currentHeight: 3000, longEdgeMm: 100.0, dpi: 300.0);
            Assert.Equal(1181u, t.Height);
            Assert.Equal(787u, t.Width);
            Assert.True(t.NeedsResize);
        }

        // 6) Square overlays are treated as width-is-long; both dimensions equal the target.
        [Fact]
        public void Compute_SquareProducesSquare() {
            var t = InviteBot.PrintScaling.Compute(currentWidth: 1500, currentHeight: 1500, longEdgeMm: 100.0, dpi: 300.0);
            Assert.Equal(1181u, t.Width);
            Assert.Equal(1181u, t.Height);
            Assert.True(t.NeedsResize);
        }

        // 7) Aspect ratio is preserved to within a fraction of a pixel after rounding.
        [Theory]
        [InlineData(3000u, 2000u)]
        [InlineData(2000u, 3000u)]
        [InlineData(1234u, 5678u)]
        [InlineData(4096u, 4097u)]
        public void Compute_PreservesAspectRatio(uint w, uint h) {
            var t = InviteBot.PrintScaling.Compute(w, h, longEdgeMm: 150.0, dpi: 300.0);
            double sourceAspect = (double)w / h;
            double targetAspect = (double)t.Width / t.Height;
            // Within 1% - rounding can never shift a dimension by more than half a pixel.
            Assert.InRange(targetAspect / sourceAspect, 0.99, 1.01);
        }

        // 8) The long edge of the result always equals the requested long-edge pixel count
        //    exactly, regardless of orientation.
        [Theory]
        [InlineData(3000u, 2000u)]
        [InlineData(2000u, 3000u)]
        [InlineData(1500u, 1500u)]
        public void Compute_LongEdgeMatchesRequestExactly(uint w, uint h) {
            var t = InviteBot.PrintScaling.Compute(w, h, longEdgeMm: 100.0, dpi: 300.0);
            uint expected = InviteBot.PrintScaling.LongEdgePixels(100.0, 300.0);
            Assert.Equal(expected, System.Math.Max(t.Width, t.Height));
        }

        // 9) Already-correct images are flagged as not needing a resample. This is the bug
        //    the previous implementation had: comparing against a zero short-edge meant the
        //    Lanczos pass ran every time even when the overlay already matched the request.
        [Fact]
        public void Compute_FlagsNoResizeWhenAlreadyAtTarget() {
            // Build an overlay whose pixel size is already 100 mm @ 300 DPI on the long edge.
            uint longEdge = InviteBot.PrintScaling.LongEdgePixels(100.0, 300.0); // 1181
            // Pick an aspect ratio that yields integer dimensions (avoids rounding noise).
            uint shortEdge = longEdge / 2; // 590
            var t = InviteBot.PrintScaling.Compute(longEdge, shortEdge, longEdgeMm: 100.0, dpi: 300.0);
            Assert.Equal(longEdge, t.Width);
            Assert.False(t.NeedsResize);
        }

        // 10) Defensive: zero-dimension input does not throw and never returns 0 dimensions.
        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(0u, 100u)]
        [InlineData(100u, 0u)]
        public void Compute_HandlesZeroDimensionsDefensively(uint w, uint h) {
            var t = InviteBot.PrintScaling.Compute(w, h, longEdgeMm: 100.0, dpi: 300.0);
            Assert.True(t.Width >= 1);
            Assert.True(t.Height >= 1);
            Assert.False(t.NeedsResize);
        }
    }
}
