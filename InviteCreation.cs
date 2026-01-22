using Discord;
using Discord.WebSocket;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Data.Sqlite;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace InviteBot {
    public partial class InviteBot {

        // Handles /invite create: produces a fresh invite, persists it, renders the QR overlay,
        // and posts both the link and the rendered image. Lives in its own file because the
        // imaging pipeline dwarfs every other handler.
        private static async Task HandleCreate(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand {sub.Name}");

            // Acknowledge immediately; QR rendering can otherwise race Discord's 3-second deadline
            await command.DeferAsync(ephemeral: true);

            // Tiny progress helper. Discord shows "thinking..." after DeferAsync but gives no
            // hint of progress; for a /invite create that probes the LB, builds the invite, and
            // renders a QR with overlay, several seconds of dead air is normal. Editing the
            // ephemeral response in-place gives the user something to read while we work.
            async Task Progress(string text) {
                try { await command.ModifyOriginalResponseAsync(p => p.Content = text); }
                catch (Exception x) { Log.Warn("invite", "Failed to update progress message", x); }
            }

            await Progress("\u23f3 Working...");

            if (ctx.Paused) {
                await command.FollowupAsync("The bot is currently paused", ephemeral: true);
                return;
            }

            // The redirect domain is per-guild now (see /invite admin domain). Without it we
            // cannot construct an invite URL, so refuse here rather than emit a broken link.
            if (string.IsNullOrEmpty(ctx.Domain)) {
                await command.FollowupAsync(
                    "This server has no redirect domain set yet. An administrator needs to run `/invite admin domain <value>` before invites can be created.",
                    ephemeral: true);
                return;
            }

            // Probe the per-guild redirect domain before we spend cycles minting an invite. If
            // it is unhealthy we still create the invite but route the URL through the
            // process-wide fallback (typically discord.gg) so the guest at the door gets a
            // working link no matter what. Record the fallback so the periodic health monitor
            // can post a recovery notice that mentions it.
            await Progress("\u23f3 Checking redirect domain...");
            DomainHealth health = await ProbeDomainAsync(ctx.Domain);
            FallbackRouting.RoutingDecision decision = FallbackRouting.Decide(ctx.Domain, health, fallbackDomain);
            string effectiveDomain = decision.EffectiveDomain;
            bool usedFallback = decision.UsedFallback;
            if (usedFallback) {
                sawFallbackUse[ctx.GuildId] = true;
                await DebugLog(ctx, $"Domain probe failed for {ctx.Domain}; falling back to {fallbackDomain}. {health.Format(ctx.Domain)}");
                try {
                    await channel.SendMessageAsync($"\u26a0\ufe0f Redirect domain `{ctx.Domain}` is unreachable; serving this invite via fallback (`{fallbackDomain}`).\n{health.Format(ctx.Domain)}");
                } catch (Exception x) { Log.Warn("invite", $"Failed to post fallback notice for guild {ctx.GuildId}", x); }
            }

            // Parse options, for administrators only
            int duration = defaultDuration;
            int uses = defaultUses;
            double? printLongEdgeMm = ctx.PrintLongEdgeMm;
            if (isAdmin) {
                foreach (SocketSlashCommandDataOption option in sub.Options) {
                    switch (option.Name) {
                        case "duration":
                            if (option.Value is not long durationValue) { break; }
                            if (durationValue < 0 || durationValue > 1440) { await command.FollowupAsync("The duration parameter is out of range", ephemeral: true); return; }
                            duration = (int)durationValue;
                            break;
                        case "uses":
                            if (option.Value is not long usesValue) { break; }
                            if (usesValue < 0 || usesValue > 100) { await command.FollowupAsync("The uses parameter is out of range", ephemeral: true); return; }
                            uses = (int)usesValue;
                            break;
                        case "size":
                            if (option.Value is not string sizeValue) { break; }
                            // "clear"/"none"/"off"/"0" override the per-guild default down to "no resize" for this one
                            // invite, which is occasionally useful for debugging or for a quick screen-only render.
                            string lowered = sizeValue.Trim().ToLowerInvariant();
                            if (lowered is "clear" or "none" or "off" or "0") { printLongEdgeMm = null; break; }
                            if (!TryParseLengthMm(sizeValue, out double mm, out string? error)) {
                                await command.FollowupAsync($"The size parameter is invalid: {error}", ephemeral: true);
                                return;
                            }
                            printLongEdgeMm = mm;
                            break;
                        default:
                            break;
                    }
                }
            }

            // Retrieve an invite, and construct the URL
            await Progress("\u23f3 Asking Discord for an invite...");
            IInviteMetadata invite = await channel.CreateInviteAsync(maxAge: duration * 60, maxUses: uses, isTemporary: false, isUnique: true);
            string inviteUrl = $"https://{effectiveDomain}/{invite.Id}";

            // Persist the invite so the cleanup loop can enforce a logical expiry independent of Discord's 1-day cap
            try {
                DateTime creationDate = DateTime.UtcNow;
                DateTime expiryDate;
                if (duration == 0) {
                    expiryDate = foreverDuration == 0 ? DateTime.MaxValue : creationDate.AddDays(foreverDuration);
                } else {
                    expiryDate = creationDate.AddMinutes(duration);
                }
                if (db is not null) {
                    string insertSql = $"INSERT INTO guild_{ctx.GuildId} (Invite, User, Uses, CreationDate, ExpiryDate, Purged) VALUES (@invite, @user, @uses, @created, @expiry, 0);";
                    await dbLock.WaitAsync();
                    try {
                        using SqliteCommand insertCmd = new(insertSql, db);
                        insertCmd.Parameters.AddWithValue("@invite", invite.Id);
                        insertCmd.Parameters.AddWithValue("@user", (long)user.Id);
                        insertCmd.Parameters.AddWithValue("@uses", uses);
                        insertCmd.Parameters.AddWithValue("@created", creationDate.ToString("o"));
                        insertCmd.Parameters.AddWithValue("@expiry", expiryDate.ToString("o"));
                        insertCmd.ExecuteNonQuery();
                    } finally { dbLock.Release(); }
                }
                await DebugLog(ctx, $"Recorded invite {invite.Id} with expiry {expiryDate:o}");
            } catch (Exception x) {
                Log.Error("invite", $"Failed to record invite {invite.Id} for guild {ctx.GuildId}", x);
                await DebugLog(ctx, $"Failed to record invite {invite.Id}: {x.Message}");
            }

            // Resolve the per-guild overlay every call. Guilds may upload overlays of differing
            // sizes and aspect ratios, so dimensions, the long side, and the QR's quiet zone all
            // have to be computed fresh - there is no global "the" overlay any more.
            await Progress("\u23f3 Loading overlay...");
            OverlayCacheEntry? overlay = LoadOverlay(ctx.GuildId);
            if (overlay is null) {
                await command.FollowupAsync(
                    "This server has no overlay yet. An administrator needs to run `/invite admin overlay` and attach a PNG before invites can be created.",
                    ephemeral: true);
                return;
            }
            uint outWidth = overlay.Width;
            uint outHeight = overlay.Height;

            // Render the QR code. Every Magick object below is local to this call - the overlay
            // bytes themselves are immutable in the cache, but each render builds its own
            // MagickImage so concurrent /invite create executions never share Magick state
            // (which would not be safe).
            //
            // Margin = 2 gives the caption room to sit inside the QR's quiet zone without
            // overwriting modules on small overlays. Margin = 1 is the spec minimum and the
            // caption (which is drawn on top of the QR image) used to overflow into the data
            // area on overlays smaller than ~512 px.
            await Progress("\u23f3 Rendering QR code...");
            uint dim = Math.Min(outWidth, outHeight);
            BarcodeWriterPixelData qrWriter = new() {
                Format = BarcodeFormat.QR_CODE,
                Options = new QrCodeEncodingOptions {
                    ErrorCorrection = ErrorCorrectionLevel.H,
                    Height = (int)dim,
                    Width = (int)dim,
                    Margin = 2
                }
            };
            using MagickImage qr = new(qrWriter.Write(inviteUrl).Pixels, new PixelReadSettings(dim, dim, StorageType.Char, PixelMapping.BGRA));

            // Build the caption text
            string durationText = "EXP ";
            if (duration == 0) { durationText += "NEVER"; }
            else {
                DateTime expiry = DateTime.UtcNow.AddMinutes(duration);
                durationText += $"{expiry.ToShortDateString()} {expiry.ToShortTimeString()} UTC";
            }
            string usesText = "USES ";
            if (uses == 0) { usesText += "∞"; } else { usesText += uses.ToString(); }
            string caption = $"{durationText} / {usesText}";

            // Caption sizing rules live in CaptionLayout (pure, unit-tested). What stays here
            // is the impure step: rendering a 1x1 measurement image so ImageMagick can tell us
            // how wide the caption actually is at the target size. That measurement feeds into
            // CaptionLayout.FitFontSize for shrink-to-fit on long expiry strings.
            double targetFontPx = CaptionLayout.ComputeTargetFontSize(dim);
            double maxTextWidthPx = CaptionLayout.ComputeMaxTextWidthPx(dim);
            using (MagickImage measurer = new(MagickColors.White, 1, 1)) {
                measurer.Settings.Font = captionFont;
                measurer.Settings.FontPointsize = targetFontPx;
                ITypeMetric? metric = measurer.FontTypeMetrics(caption);
                if (metric is not null) {
                    targetFontPx = CaptionLayout.FitFontSize(targetFontPx, metric.TextWidth, maxTextWidthPx);
                }
            }
            double bottomPaddingPx = CaptionLayout.ComputeBottomPaddingPx(dim);

            var text = new Drawables()
                    .FontPointSize(targetFontPx)
                    .Font(captionFont)
                    .FillColor(MagickColors.Black)
                    .StrokeColor(MagickColors.None)
                    .TextAlignment(TextAlignment.Center)
                    .Text(dim / 2.0, dim - bottomPaddingPx, caption);
            qr.Draw(text);

            // Create a white background sized to the guild's overlay and composite the QR code
            // onto it, then composite the overlay itself on top. The overlay is materialised per
            // call from the immutable cached bytes.
            byte[] b = new byte[outWidth * outHeight * 4];
            Array.Fill(b, (byte)0xFF);
            using MagickImage output = new(b, new PixelReadSettings(outWidth, outHeight, StorageType.Char, PixelMapping.BGRA));
            output.Composite(qr, Gravity.Center, CompositeOperator.Over);
            using (MagickImage perCallOverlay = new(overlay.Bytes)) {
                output.Composite(perCallOverlay, Gravity.Center, CompositeOperator.Over);
            }

            // Apply the print size, if any. Overlays are stored normalised to 300 DPI (see
            // OverlayTargetDpi in Overlays.cs), so the long-edge millimetre target translates
            // directly to a pixel count. Pixel arithmetic and the no-resample fast-path live in
            // PrintScaling so they can be unit-tested without ImageMagick. We then stamp 300 DPI
            // on the output so a "print at 100%" workflow reproduces the requested physical size.
            output.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
            if (printLongEdgeMm.HasValue) {
                PrintScaling.PrintTarget target = PrintScaling.Compute(output.Width, output.Height, printLongEdgeMm.Value, 300.0);
                if (target.NeedsResize) {
                    output.FilterType = FilterType.Lanczos;
                    output.Resize(target.Width, target.Height);
                }
            }

            // Render the composited image into an in-memory stream so we don't touch disk
            await Progress("\u23f3 Encoding image...");
            using MemoryStream stream = new();
            await output.WriteAsync(stream, MagickFormat.Png);
            stream.Position = 0;
            string fallbackSuffix = usedFallback ? $" (served via fallback `{fallbackDomain}` because `{ctx.Domain}` is unreachable)" : "";
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) created an invitation ({invite.Id}): {durationText} / {usesText}{fallbackSuffix}");
            await command.FollowupWithFileAsync(stream, $"{invite.Id}.png", $"{inviteUrl}\r{durationText} / {usesText}", ephemeral: true);
        }
    }
}
