namespace InviteBot {

    public partial class InviteBot {

        // Pure, framework-free helpers that decide what pixel dimensions a composited invite
        // image needs to take on so that, when stamped at the print DPI, it reproduces the
        // requested physical long-edge size. Lives in its own file so it can be unit-tested
        // without spinning up ImageMagick. The actual resampling happens in InviteCreation.cs
        // and consumes these results.
        //
        // Three rules drive the design:
        //   1) The aspect ratio of the source overlay must be preserved exactly. Print buyers
        //      expect a 2:3 overlay to print as a 2:3 photo, regardless of which edge they
        //      pinned the size to.
        //   2) The "long edge" must end up at the requested pixel count, never one off due to
        //      rounding asymmetry between width and height.
        //   3) If the overlay already has the right pixel size, do not resample at all. A
        //      Lanczos pass against an already-correct image is wasted CPU and a tiny but
        //      non-zero quality hit.
        internal static class PrintScaling {

            // Exact mm-per-inch. Defined here (rather than reusing Units.cs's parser-internal
            // constant) so this helper has no dependency on the parser.
            internal const double MillimetresPerInch = 25.4;

            // Result of a print-scaling decision. NeedsResize is false when the overlay is
            // already the requested size to within a single pixel on the long edge - the
            // caller should skip the resample in that case.
            internal readonly record struct PrintTarget(uint Width, uint Height, bool NeedsResize);

            // Convert a physical long-edge in millimetres to a pixel count at the given DPI.
            // Always returns at least 1 px so a degenerate input (e.g. clamped-to-zero through
            // some future bug) cannot produce a zero-dimension Magick image.
            internal static uint LongEdgePixels(double longEdgeMm, double dpi) {
                if (longEdgeMm <= 0 || dpi <= 0) { return 1; }
                return (uint)System.Math.Max(1, System.Math.Round(longEdgeMm / MillimetresPerInch * dpi));
            }

            // Given the current pixel dimensions of the composited image and a desired print
            // long-edge in millimetres, return the (width, height) the image should be
            // resampled to so that the long edge matches the request and the aspect ratio is
            // preserved. The short edge is rounded; this is the same arithmetic Magick's
            // single-axis Resize performs internally, but pinned here so it is testable and
            // so the caller can pass both dimensions explicitly (avoiding the "one zero
            // dimension" comparison gotcha against the original size).
            internal static PrintTarget Compute(uint currentWidth, uint currentHeight, double longEdgeMm, double dpi) {
                if (currentWidth == 0 || currentHeight == 0) {
                    return new PrintTarget(System.Math.Max(currentWidth, 1u), System.Math.Max(currentHeight, 1u), false);
                }

                uint targetLongEdge = LongEdgePixels(longEdgeMm, dpi);
                bool widthIsLong = currentWidth >= currentHeight;

                uint targetWidth, targetHeight;
                if (widthIsLong) {
                    targetWidth = targetLongEdge;
                    double ratio = (double)currentHeight / currentWidth;
                    targetHeight = (uint)System.Math.Max(1, System.Math.Round(targetLongEdge * ratio));
                } else {
                    targetHeight = targetLongEdge;
                    double ratio = (double)currentWidth / currentHeight;
                    targetWidth = (uint)System.Math.Max(1, System.Math.Round(targetLongEdge * ratio));
                }

                bool needsResize = targetWidth != currentWidth || targetHeight != currentHeight;
                return new PrintTarget(targetWidth, targetHeight, needsResize);
            }
        }
    }
}
