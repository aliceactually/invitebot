using Xunit;

namespace InviteBot.Tests {
    // Pure-arithmetic tests for the overlay-badge sizing rules. The render-time correctness
    // (does the composited image actually scan?) is exercised by hand against real overlays and
    // real scanners; these tests pin down the invariants that a regression would otherwise
    // quietly break. The symptom of the original bug was an overlay that covered the ENTIRE QR
    // because it was composited at native size, destroying scannability.
    public class OverlayLayoutTests {

        // 1) A large overlay is scaled down to fit inside the central budget box (30% of the
        //    QR's short edge). A 1000x1000 overlay on a 1000 px QR must end up at ~300x300.
        [Fact]
        public void Compute_ScalesLargeSquareDownToBudget() {
            var s = InviteBot.OverlayLayout.Compute(dim: 1000, overlayWidth: 1000, overlayHeight: 1000);
            Assert.Equal(300u, s.Width);
            Assert.Equal(300u, s.Height);
            Assert.True(s.NeedsResize);
        }

        // 2) The badge never exceeds the budget box on either axis, regardless of orientation.
        [Theory]
        [InlineData(1000u, 800u)]
        [InlineData(800u, 1000u)]
        [InlineData(4000u, 1000u)]
        [InlineData(1000u, 4000u)]
        public void Compute_NeverExceedsBudgetBox(uint ow, uint oh) {
            uint dim = 1200;
            uint box = (uint)(dim * InviteBot.OverlayLayout.MaxOverlayEdgeFraction); // 360
            var s = InviteBot.OverlayLayout.Compute(dim, ow, oh);
            Assert.True(s.Width <= box);
            Assert.True(s.Height <= box);
        }

        // 3) Aspect ratio is preserved when scaling down. A 2:1 overlay stays 2:1.
        [Fact]
        public void Compute_PreservesAspectRatioWhenScaling() {
            var s = InviteBot.OverlayLayout.Compute(dim: 1000, overlayWidth: 2000, overlayHeight: 1000);
            // Fit a 2:1 image inside a 300x300 box -> width pinned at 300, height 150.
            Assert.Equal(300u, s.Width);
            Assert.Equal(150u, s.Height);
            Assert.True(s.NeedsResize);
        }

        // 4) An overlay already smaller than the budget box is left at native size and not
        //    upscaled - a crisp small badge beats a blurry enlarged one for scannability.
        [Fact]
        public void Compute_DoesNotUpscaleSmallOverlay() {
            var s = InviteBot.OverlayLayout.Compute(dim: 1000, overlayWidth: 100, overlayHeight: 80);
            Assert.Equal(100u, s.Width);
            Assert.Equal(80u, s.Height);
            Assert.False(s.NeedsResize);
        }

        // 5) An overlay exactly at the budget edge is not resized (scale == 1.0).
        [Fact]
        public void Compute_AtExactBudgetIsNotResized() {
            // 30% of 1000 is 300; a 300x300 overlay already fits exactly.
            var s = InviteBot.OverlayLayout.Compute(dim: 1000, overlayWidth: 300, overlayHeight: 300);
            Assert.Equal(300u, s.Width);
            Assert.Equal(300u, s.Height);
            Assert.False(s.NeedsResize);
        }

        // 6) The obscured area stays well under the level-H error-correction budget (~30% of
        //    codewords). For a square badge the area fraction is MaxOverlayEdgeFraction^2 ~= 9%,
        //    comfortably inside the recoverable range with headroom for scanner robustness.
        [Fact]
        public void Compute_SquareBadgeAreaIsWellUnderErrorCorrectionBudget() {
            uint dim = 1000;
            var s = InviteBot.OverlayLayout.Compute(dim, overlayWidth: 5000, overlayHeight: 5000);
            double areaFraction = (double)(s.Width * s.Height) / (dim * dim);
            Assert.True(areaFraction < 0.12, $"badge covered {areaFraction:P0} of the QR, too close to the EC limit");
        }

        // 7) Defensive: degenerate inputs never produce a zero-dimension Magick image and never
        //    request a resize against nonsense data.
        [Theory]
        [InlineData(0u, 500u, 500u)]
        [InlineData(1000u, 0u, 500u)]
        [InlineData(1000u, 500u, 0u)]
        public void Compute_HandlesZeroDimensionsDefensively(uint dim, uint ow, uint oh) {
            var s = InviteBot.OverlayLayout.Compute(dim, ow, oh);
            Assert.True(s.Width >= 1);
            Assert.True(s.Height >= 1);
            Assert.False(s.NeedsResize);
        }
    }
}
