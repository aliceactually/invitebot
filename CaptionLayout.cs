namespace InviteBot {

    public partial class InviteBot {

        // Pure, framework-free helpers that decide how big the QR caption should be and where
        // it should sit. Lives in its own file so it can be unit-tested without spinning up
        // ImageMagick. The actual drawing happens in InviteCreation.cs and consumes these
        // results.
        //
        // Three rules drive the design:
        //   1) The caption must scale with the QR pixel size, or text tuned for one overlay
        //      looks illegible on smaller ones and absurd on larger ones.
        //   2) The result must be clamped to a sensible absolute range so neither extreme is
        //      catastrophic on its own.
        //   3) Long invite captions on narrow QRs must shrink to fit instead of clipping past
        //      the edge of the QR module area.
        internal static class CaptionLayout {

            // Target font size as a fraction of the QR's short edge. ~2.2% reads as roughly
            // one QR module on a typical url-length code, which is the visual density that
            // worked best across the test overlays we tried.
            internal const double TargetFontFraction = 0.022;

            // Absolute bounds. Below 12pt the caption is unreadable on mobile screens; above
            // 64pt it dominates the QR even on very large prints.
            internal const double MinFontPx = 12.0;
            internal const double MaxFontPx = 64.0;

            // Width budget for the caption inside the QR's quiet zone. 92% leaves a small
            // visual margin on each side so the text never butts against the QR's modules.
            internal const double MaxTextWidthFraction = 0.92;

            // Bottom padding as a fraction of dim, with a 2-px floor so tiny overlays do not
            // collapse the caption against the very edge of the image.
            internal const double BottomPaddingFraction = 0.015;
            internal const double MinBottomPaddingPx = 2.0;

            // Initial target size before any width-based shrink-to-fit is applied. Used by
            // InviteCreation.cs as the FontPointSize for the metric measurement pass.
            internal static double ComputeTargetFontSize(uint dim) {
                return System.Math.Clamp(dim * TargetFontFraction, MinFontPx, MaxFontPx);
            }

            // Maximum pixel width the caption may occupy inside the QR.
            internal static double ComputeMaxTextWidthPx(uint dim) {
                return dim * MaxTextWidthFraction;
            }

            // Shrink-to-fit: given the size we wanted and the actual measured width the
            // caption would occupy at that size, scale the size down (never up) so the caption
            // fits within the budget. This deliberately does not enforce MinFontPx on the way
            // down - a caption that is technically too small to read is still better than one
            // that is truncated past the QR's edge, because at least the QR itself remains
            // scannable.
            internal static double FitFontSize(double targetFontPx, double measuredWidthAtTargetPx, double maxWidthPx) {
                if (measuredWidthAtTargetPx <= 0 || maxWidthPx <= 0) { return targetFontPx; }
                if (measuredWidthAtTargetPx <= maxWidthPx) { return targetFontPx; }
                return targetFontPx * (maxWidthPx / measuredWidthAtTargetPx);
            }

            // Vertical inset from the bottom of the QR for the caption baseline.
            internal static double ComputeBottomPaddingPx(uint dim) {
                return System.Math.Max(MinBottomPaddingPx, dim * BottomPaddingFraction);
            }
        }
    }
}
