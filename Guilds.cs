using Discord.WebSocket;
using System.Collections.Concurrent;

namespace InviteBot {
    public partial class InviteBot {

        // Per-guild runtime state. Mutable fields are guarded indirectly via dbLock for any
        // setting that is also persisted; the in-memory snapshot is updated on the same thread
        // that performs the DB write.
        private sealed class GuildContext {
            public ulong GuildId;
            public ulong ChannelId;
            public ulong AdminRole;
            public ulong UserRole;
            public bool Paused;
            public bool Debug;
            // Long-edge target size for the rendered invite image, in millimetres. Null means
            // "no resize" - the bot emits the raw composited image at the overlay's native pixel
            // dimensions, which on a 300 DPI overlay is already a print-correct asset. Set via
            // /invite admin print and overridable per-call by /invite create.
            public double? PrintLongEdgeMm;
            // Per-guild redirect domain. Generated invite URLs are https://{Domain}/{inviteId}.
            // Null means "not set" - /invite create refuses until an admin runs /invite admin domain,
            // because there is no sensible global default we could fall back to.
            public string? Domain;
            // When true, new members joining this guild are automatically DM'd the same
            // introduction the admin would send via /invite admin introduce. Defaults to true on
            // a fresh guild row (set by the schema's column DEFAULT, by the EnsureGuildAsync
            // INSERT, and by the GuildContext field initialiser below) so the welcome behaviour
            // is opt-out rather than opt-in - admins who don't want it explicitly disable it
            // with /invite admin welcome value:false. Auto-welcome additionally requires the
            // guild to be configured (admin role + log channel set), to suppress sending an
            // introduction that describes commands no role can yet use.
            public bool WelcomeNewMembers = true;

            public bool IsConfigured => ChannelId != 0 && AdminRole != 0;
        }

        // Per-guild contexts, keyed by guild id
        private static readonly ConcurrentDictionary<ulong, GuildContext> guilds = new();

        private static SocketTextChannel? ChannelFor(GuildContext ctx) {
            if (discord is null || ctx.ChannelId == 0) { return null; }
            SocketGuild? g = discord.GetGuild(ctx.GuildId);
            return g?.GetTextChannel(ctx.ChannelId);
        }

        // Best-effort log to the configured channel. Always mirrored to the console so debug
        // information cannot vanish if the channel is misconfigured or the send fails.
        private static async Task DebugLog(GuildContext ctx, string message) {
            if (!ctx.Debug) { return; }
            // Per-guild debug is explicit opt-in, so mirror at Info severity to bypass the
            // global Log.DebugEnabled gate; otherwise enabling debug for a single guild would
            // still leave the console silent.
            Log.Info($"guild/{ctx.GuildId}/debug", message);
            SocketTextChannel? ch = ChannelFor(ctx);
            if (ch is null) { return; }
            try { await ch.SendMessageAsync($"DEBUG: {message}"); }
            catch (Exception x) { Log.Warn($"guild/{ctx.GuildId}", "Failed to deliver debug message to channel", x); }
        }
    }
}
