using Discord;
using Discord.WebSocket;

namespace InviteBot {
    public partial class InviteBot {

        // Truly process-wide configuration, populated once in Main().
        private static int cleanupTimer;
        private static int defaultDuration;
        private static int defaultUses;
        private static int foreverDuration;
        private static bool defaultDebug;
        // Universal fallback domain (e.g. "discord.gg"). Used when a guild's per-guild redirect
        // domain is failing the live probe so /invite create can still hand out a working link.
        private static string fallbackDomain = "discord.gg";
        // Font name passed to ImageMagick for the EXP/USES caption baked into invite QRs.
        private static string captionFont = "Courier New";
        private static DiscordSocketClient? discord;

        public static async Task Main() {

            // Load and validate the typed configuration in one go.
            BotConfig config;
            try {
                config = BotConfig.Load("config.json");
            } catch (Exception x) {
                Log.Error("startup", "Failed to load config.json", x);
                return;
            }
            cleanupTimer = config.CleanupTimer;
            defaultDuration = config.DefaultDuration;
            defaultUses = config.DefaultUses;
            foreverDuration = config.ForeverDuration;
            defaultDebug = config.Debug;
            fallbackDomain = config.FallbackDomain;
            captionFont = config.CaptionFont;
            Log.DebugEnabled = defaultDebug;
            Log.Info("startup", $"Configuration loaded (debug={defaultDebug}, cleanupTimer={cleanupTimer}m, fallbackDomain={fallbackDomain})");

            // Optional dev guild: when set, the slash command is also registered to this guild,
            // which propagates instantly. Global registration can take up to ~1 hour on first publish.
            ulong devGuild = config.DevGuild;

            // Resolve the overlay directory. Per-guild overlays live as <guildId>.png files inside
            // it; an operator uploads them via /invite admin overlay and the bot writes the file
            // itself, so no manual SCP is required. Relative paths sit beside the executable so a
            // self-contained Linux deployment stays self-contained.
            overlayDirectory = config.OverlayDirectory;
            if (!Path.IsPathRooted(overlayDirectory)) { overlayDirectory = Path.Combine(AppContext.BaseDirectory, overlayDirectory); }
            try { Directory.CreateDirectory(overlayDirectory); }
            catch (Exception x) { Log.Error("startup", $"Unable to create overlay directory \"{overlayDirectory}\"", x); return; }
            Log.Info("startup", $"Overlay directory: {overlayDirectory}");

            string discordToken = config.Discord.Token!;

            // Open (or create) the database. Relative paths sit alongside the executable so the DB
            // persists across restarts; absolute paths are honoured as-is.
            string dbPath = config.Database;
            if (!Path.IsPathRooted(dbPath)) { dbPath = Path.Combine(AppContext.BaseDirectory, dbPath); }
            OpenDatabase(dbPath);
            await HydrateGuildsAsync();

            // Create a DiscordSocketClient with the intents we need
            var intents = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages };
            discord = new DiscordSocketClient(intents);
            discord.Log += global::InviteBot.Log.FromDiscord;

            // Wait for the gateway Ready event instead of busy-polling guild.IsSynced
            TaskCompletionSource readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task ReadyHandler() { readyTcs.TrySetResult(); return Task.CompletedTask; }
            discord.Ready += ReadyHandler;

            await discord.LoginAsync(TokenType.Bot, discordToken, true);
            await discord.StartAsync();
            await readyTcs.Task;
            discord.Ready -= ReadyHandler;

            // Make sure every guild we are currently in has a settings row and an invite table
            foreach (SocketGuild g in discord.Guilds) { await EnsureGuildAsync(g.Id); }

            // Reconcile against persisted state: any guild we have a row for but are no longer a
            // member of was left while the bot was offline. Drop their settings, invite table, and
            // overlay file so the on-disk footprint stays bounded over the bot's lifetime.
            HashSet<ulong> liveGuildIds = new(discord.Guilds.Select(g => g.Id));
            foreach (ulong persistedId in await ListPersistedGuildsAsync()) {
                if (!liveGuildIds.Contains(persistedId)) {
                    Log.Info("startup", $"Guild {persistedId} is in the database but the bot is no longer a member; reconciling");
                    await ForgetGuildAsync(persistedId);
                }
            }

            discord.JoinedGuild += async g => { await EnsureGuildAsync(g.Id); };
            discord.LeftGuild += async g => { await ForgetGuildAsync(g.Id); };

            // Post a brief restart notice to every configured guild's invitebot channel.
            // Deliberately quiet: one short line, only in the bot's own channel, never in mod channels.
            // Permissions can drift between runs (admin revokes Send Messages, channel deleted, etc.),
            // so we re-check before posting and skip silently when we cannot write.
            string restartNotice;
            try {
                restartNotice = $"InviteBot {BuildVersion} (commit {BuildCommit}, built {FormatBuildTimestamp(BuildTimestampUtc)}) restarted.";
            } catch (Exception x) {
                Log.Warn("startup", "Failed to assemble build provenance for restart notice", x);
                restartNotice = "InviteBot restarted.";
            }
            Log.Info("startup", $"Posting restart notice to {guilds.Values.Count(c => c.IsConfigured)} configured guild(s) of {guilds.Count} known: \"{restartNotice}\"");
            foreach (GuildContext ctx in guilds.Values) {
                if (!ctx.IsConfigured) {
                    Log.Info($"guild/{ctx.GuildId}", "Skipping restart notice: guild is not configured (no channel set)");
                    continue;
                }
                SocketGuild? guild = discord.GetGuild(ctx.GuildId);
                SocketTextChannel? ch = guild?.GetTextChannel(ctx.ChannelId);
                if (guild is null || ch is null) {
                    Log.Warn($"guild/{ctx.GuildId}", $"Configured channel {ctx.ChannelId} is no longer reachable; skipping restart notice");
                    continue;
                }
                ChannelPermissions perms = guild.CurrentUser.GetPermissions(ch);
                if (!perms.ViewChannel || !perms.SendMessages) {
                    Log.Warn($"guild/{ctx.GuildId}", $"Missing View/Send permissions in #{ch.Name}; skipping restart notice");
                    continue;
                }
                try { await ch.SendMessageAsync(restartNotice); }
                catch (Exception x) { Log.Warn($"guild/{ctx.GuildId}", "Failed to post restart notice", x); }
            }

            // Wire up graceful shutdown. Both handlers can fire after the CTS has been disposed
            // (ProcessExit in particular runs during normal shutdown, which is *after* the using
            // block below releases the CTS), so swallow ObjectDisposedException defensively rather
            // than letting it tear the process down with an unhandled exception on the way out.
            using CancellationTokenSource shutdownCts = new();
            void RequestShutdown() {
                try { shutdownCts.Cancel(); }
                catch (ObjectDisposedException) { /* already torn down; nothing to do */ }
            }
            Console.CancelKeyPress += (_, args) => {
                args.Cancel = true; // prevent immediate process kill; let the cleanup path run
                RequestShutdown();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown();

            // Reconcile any invites that expired while the bot was offline before scheduling the timer
            await CleanupAll();

            Task cleanupLoop = CleanupPeriodic(shutdownCts.Token);
            Task healthLoop = HealthCheckPeriodic(shutdownCts.Token);

            // Build and register the slash command tree
            ApplicationCommandProperties built = BuildSlashCommand().Build();
            await discord.Rest.BulkOverwriteGlobalCommands(new ApplicationCommandProperties[] { built });
            // Also register to the dev guild (if configured) so iteration is instant
            if (devGuild != 0) {
                SocketGuild? dev = discord.GetGuild(devGuild);
                if (dev is not null) {
                    try {
                        await dev.BulkOverwriteApplicationCommandAsync(new ApplicationCommandProperties[] { built });
                        Log.Info("startup", $"Registered slash commands to dev guild {devGuild}");
                    }
                    catch (Exception x) { Log.Error("startup", "Failed to register dev-guild slash command", x); }
                } else {
                    Log.Warn("startup", $"devGuild {devGuild} is configured but the bot is not a member of that guild");
                }
            }
            discord.SlashCommandExecuted += SlashCommandHandler;

            // Block until shutdown is requested, then drain the cleanup loop and dispose resources cleanly
            try { await Task.Delay(Timeout.Infinite, shutdownCts.Token); }
            catch (OperationCanceledException) { /* expected on shutdown */ }

            Log.Info("shutdown", "Shutdown requested; stopping...");
            try { await cleanupLoop; } catch (OperationCanceledException) { } catch (Exception x) { Log.Warn("shutdown", "Cleanup loop did not exit cleanly", x); }
            try { await healthLoop; } catch (OperationCanceledException) { } catch (Exception x) { Log.Warn("shutdown", "Health loop did not exit cleanly", x); }
            try { await discord.LogoutAsync(); } catch (Exception x) { Log.Warn("shutdown", "Discord logout failed", x); }
            try { await discord.StopAsync(); } catch (Exception x) { Log.Warn("shutdown", "Discord stop failed", x); }
            await discord.DisposeAsync();
            db?.Dispose();
            Log.Info("shutdown", "Stopped");
        }
    }
}
