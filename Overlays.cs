using System.Collections.Concurrent;
using System.Net.Http;
using ImageMagick;

namespace InviteBot {
    public partial class InviteBot {

        // Single shared HttpClient for downloading uploaded overlay attachments from Discord's CDN.
        // Discord's docs explicitly call out that attachment URLs are time-limited but always work
        // for the bot during interaction handling, so a vanilla client with sane defaults is fine.
        private static readonly HttpClient overlayHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

        // Resolved at startup from BotConfig.OverlayDirectory; absolute paths are honoured as-is,
        // relative ones sit beside the executable so the bot stays self-contained when deployed.
        private static string overlayDirectory = "overlays";

        // Constraints applied to uploaded overlays. Generous enough to accept anything reasonable
        // a designer would produce; tight enough to keep memory and decode time bounded.
        private const int OverlayMinDimension = 256;
        private const int OverlayMaxDimension = 4096;
        private const int OverlayMaxBytes = 4 * 1024 * 1024;

        // All stored overlays are normalised to this density. Doing the resample once at upload
        // time means render-time print-size maths is unambiguous and every guild's file is in the
        // same coordinate system regardless of what the source designer exported at.
        private const double OverlayTargetDpi = 300.0;
        // Skip the resample if the source is within this fraction of the target, to avoid pointless
        // re-encoding of files that are already effectively 300 DPI.
        private const double OverlayDpiTolerance = 0.01;

        // Per-guild overlay cache. Bytes and dimensions are captured together so a /invite create
        // never has to call back to disk during the render. A change to the file's last-write time
        // invalidates the entry on next access, so an operator re-uploading produces immediate effect
        // without needing a restart or any explicit invalidation hook.
        private sealed class OverlayCacheEntry {
            public byte[] Bytes = Array.Empty<byte>();
            public uint Width;
            public uint Height;
            public DateTime LastWriteUtc;
        }

        private static readonly ConcurrentDictionary<ulong, OverlayCacheEntry> overlayCache = new();

        // Returns the on-disk path for a guild's overlay. The file may or may not exist.
        private static string OverlayPathFor(ulong guildId) =>
            Path.Combine(overlayDirectory, $"{guildId}.png");

        // True when the guild has an overlay file on disk and we can read it.
        private static bool HasOverlay(ulong guildId) {
            try { return File.Exists(OverlayPathFor(guildId)); }
            catch { return false; }
        }

        // Loads (or returns from cache) the overlay for a guild. Returns null when no overlay
        // exists or when reading/decoding fails - callers are expected to surface a friendly
        // "upload an overlay first" message rather than throw.
        private static OverlayCacheEntry? LoadOverlay(ulong guildId) {
            string path = OverlayPathFor(guildId);
            FileInfo info;
            try {
                info = new FileInfo(path);
                if (!info.Exists) { return null; }
            } catch (Exception x) {
                Log.Warn($"guild/{guildId}", $"Failed to stat overlay \"{path}\"", x);
                return null;
            }

            DateTime mtime = info.LastWriteTimeUtc;
            if (overlayCache.TryGetValue(guildId, out OverlayCacheEntry? cached) && cached.LastWriteUtc == mtime) {
                return cached;
            }

            try {
                byte[] bytes = File.ReadAllBytes(path);
                using MagickImage probe = new(bytes);
                OverlayCacheEntry entry = new() {
                    Bytes = bytes,
                    Width = probe.Width,
                    Height = probe.Height,
                    LastWriteUtc = mtime,
                };
                overlayCache[guildId] = entry;
                return entry;
            } catch (Exception x) {
                Log.Error($"guild/{guildId}", $"Failed to load overlay \"{path}\"", x);
                return null;
            }
        }

        // Validates an uploaded image, normalises it to OverlayTargetDpi, and writes the result to
        // the per-guild overlay path. Returns the resolved entry on success along with a short
        // human-readable note about what normalisation (if any) was applied, or a human-readable
        // error reason on failure. Always overwrites any existing file - re-uploading is the
        // obvious intent and the response shows the stored dimensions so the operator knows it
        // took effect.
        private static (OverlayCacheEntry? entry, string? note, string? error) StoreOverlay(ulong guildId, byte[] bytes) {
            if (bytes.Length == 0) { return (null, null, "the uploaded file is empty"); }
            if (bytes.Length > OverlayMaxBytes) { return (null, null, $"the uploaded file is larger than {OverlayMaxBytes / (1024 * 1024)} MB"); }

            uint width, height;
            byte[] storedBytes;
            string? note = null;
            try {
                using MagickImage image = new(bytes);
                width = image.Width;
                height = image.Height;

                if (width < OverlayMinDimension || height < OverlayMinDimension) {
                    return (null, null, $"the image is too small ({width}\u00d7{height}); each side must be at least {OverlayMinDimension}px");
                }
                if (width > OverlayMaxDimension || height > OverlayMaxDimension) {
                    return (null, null, $"the image is too large ({width}\u00d7{height}); each side must be at most {OverlayMaxDimension}px");
                }

                // Normalise density. PNGs frequently arrive with no pHYs chunk (Density.X == 0) or
                // with units in PixelsPerCentimeter; treat those uniformly. A missing/zero density
                // is taken to mean "this image is already in our target coordinate system" - we
                // stamp the target density without resampling, because resampling against an
                // invented source DPI would be worse than leaving the pixels alone.
                Density d = image.Density;
                double sourceDpi = 0.0;
                if (d.X > 0) {
                    sourceDpi = d.Units == DensityUnit.PixelsPerCentimeter ? d.X * 2.54 : d.X;
                }

                bool resampled = false;
                if (sourceDpi > 0 && Math.Abs(sourceDpi - OverlayTargetDpi) / OverlayTargetDpi > OverlayDpiTolerance) {
                    double scale = OverlayTargetDpi / sourceDpi;
                    uint newWidth = (uint)Math.Max(1, Math.Round(width * scale));
                    uint newHeight = (uint)Math.Max(1, Math.Round(height * scale));

                    if (newWidth < OverlayMinDimension || newHeight < OverlayMinDimension) {
                        return (null, null, $"after normalising from {sourceDpi:0.#} to {OverlayTargetDpi:0} DPI the image would be {newWidth}\u00d7{newHeight}px, which is below the {OverlayMinDimension}px minimum; please supply a higher-resolution source");
                    }
                    if (newWidth > OverlayMaxDimension || newHeight > OverlayMaxDimension) {
                        return (null, null, $"after normalising from {sourceDpi:0.#} to {OverlayTargetDpi:0} DPI the image would be {newWidth}\u00d7{newHeight}px, which exceeds the {OverlayMaxDimension}px maximum; please supply a lower-resolution source");
                    }

                    image.FilterType = FilterType.Lanczos;
                    image.Resize(newWidth, newHeight);
                    width = image.Width;
                    height = image.Height;
                    resampled = true;
                    note = $"normalised from {sourceDpi:0.#} to {OverlayTargetDpi:0} DPI";
                }

                image.Density = new Density(OverlayTargetDpi, OverlayTargetDpi, DensityUnit.PixelsPerInch);
                image.Format = MagickFormat.Png;
                storedBytes = image.ToByteArray();

                if (storedBytes.Length > OverlayMaxBytes) {
                    return (null, null, $"after normalisation the encoded image would be larger than {OverlayMaxBytes / (1024 * 1024)} MB; please supply a smaller source");
                }
                if (!resampled && sourceDpi == 0) { note = $"no source density metadata; stamped {OverlayTargetDpi:0} DPI"; }
            } catch (Exception x) {
                Log.Warn($"guild/{guildId}", "Rejected overlay upload: image could not be decoded", x);
                return (null, null, "the uploaded file could not be decoded as an image");
            }

            try {
                Directory.CreateDirectory(overlayDirectory);
                string path = OverlayPathFor(guildId);
                File.WriteAllBytes(path, storedBytes);
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                OverlayCacheEntry entry = new() {
                    Bytes = storedBytes,
                    Width = width,
                    Height = height,
                    LastWriteUtc = mtime,
                };
                overlayCache[guildId] = entry;
                return (entry, note, null);
            } catch (Exception x) {
                Log.Error($"guild/{guildId}", $"Failed to write overlay to \"{OverlayPathFor(guildId)}\"", x);
                return (null, null, "the bot could not write the overlay file (check permissions on the overlays directory)");
            }
        }
    }
}
