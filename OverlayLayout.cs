namespace InviteBot {

    public partial class InviteBot {

        // Pure, framework-free helper that decides how large a guild's overlay badge may be
        // when it is composited onto the centre of the QR code. Lives in its own file so it can
        // be unit-tested without spinning up ImageMagick. The actual resampling and compositing
        // happen in InviteCreation.cs and consume these results.
        //
        // The whole point of the overlay is to sit a human-readable logo in the centre of the
        // QR and lean on the QR's error correction to recover the modules it obscures. The QR
        // is generated at ErrorCorrectionLevel.H, which can recover ~30% of the codewords. We
        // must therefore keep the obscured *area* comfortably under that budget - and well under
        // it in practice, because the obscured region also has to avoid eating into the safety
        // margin that real-world scanners (cheap phone cameras, poor lighting, print smudging)
        // rely on.
        //
        // Two rules drive the design:
        //   1) The overlay is scaled to fit inside a centred square box whose side is a fixed
        //      fraction of the QR's short edge, preserving the overlay's aspect ratio. Scaling
        //      relative to the short edge keeps the badge proportional across overlays of wildly
        //      different pixel sizes.
        //   2) The overlay is only ever scaled DOWN. A small source logo is left at its native
        //      size rather than being upsampled into a blurry mess - a crisp small badge is both
        //      prettier and safer for scannability than a fuzzy large one.
        internal static class OverlayLayout {

            // The side of the central box the overlay must fit inside, as a fraction of the QR's
            // short edge. 0.30 means the badge spans at most 30% of the QR's width/height, i.e.
            // at most ~9% of the QR's *area* for a square overlay (0.30^2). That sits far inside
            // the ~30% area budget that level-H error correction affords, leaving generous
            // headroom for the finder patterns, the quiet zone, and scanner robustness.
            internal const double MaxOverlayEdgeFraction = 0.30;

            // Result of an overlay-sizing decision. NeedsResize is false when the overlay
            // already fits within the budget box, in which case the caller composites the
            // original bytes untouched and skips the resample.
            internal readonly record struct OverlaySize(uint Width, uint Height, bool NeedsResize);

            // Given the QR's drawn short edge (dim) and the overlay's native pixel dimensions,
            // return the dimensions the overlay should be resized to before being composited at
            // the centre of the QR. Aspect ratio is preserved; the overlay is fit inside a
            // centred square of side dim * MaxOverlayEdgeFraction and never enlarged.
            internal static OverlaySize Compute(uint dim, uint overlayWidth, uint overlayHeight) {
                if (dim == 0 || overlayWidth == 0 || overlayHeight == 0) {
                    return new OverlaySize(System.Math.Max(overlayWidth, 1u), System.Math.Max(overlayHeight, 1u), false);
                }

                double box = dim * MaxOverlayEdgeFraction;

                // Scale factor that fits the overlay inside the box on both axes. Clamp to 1.0 so
                // a source smaller than the budget is left at native size rather than upscaled.
                double scale = System.Math.Min(box / overlayWidth, box / overlayHeight);
                if (scale >= 1.0) {
                    return new OverlaySize(overlayWidth, overlayHeight, false);
                }

                uint targetWidth = (uint)System.Math.Max(1, System.Math.Round(overlayWidth * scale));
                uint targetHeight = (uint)System.Math.Max(1, System.Math.Round(overlayHeight * scale));
                return new OverlaySize(targetWidth, targetHeight, true);
            }
        }
    }
}
