using Discord;
using Discord.WebSocket;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InviteBot {
    public partial class InviteBot {

        // Builds the slash command tree. Kept verbose for clarity rather than helper-extracted.
        private static SlashCommandBuilder BuildSlashCommand() {
            return new SlashCommandBuilder()
                .WithName("invite")
                .WithDescription("Not visible")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("create")
                    .WithDescription("Creates an invite")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("uses", ApplicationCommandOptionType.Integer, "Maximum uses (admin only)", isRequired: false)
                    .AddOption("duration", ApplicationCommandOptionType.Integer, "Invite duration (admin only)", isRequired: false)
                    .AddOption("size", ApplicationCommandOptionType.String, "Long-edge print size, e.g. 90mm, 9cm, 3.5in (admin only; overrides server default)", isRequired: false)
                ).AddOption(new SlashCommandOptionBuilder()
                    .WithName("admin")
                    .WithDescription("Not visible")
                    .WithType(ApplicationCommandOptionType.SubCommandGroup
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("configure")
                        .WithDescription("Configures the bot for this server (Manage Server only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("channel", ApplicationCommandOptionType.Channel, "Channel for log/notification messages", isRequired: true)
                        .AddOption("adminrole", ApplicationCommandOptionType.Role, "Role permitted to administer the bot", isRequired: true)
                        .AddOption("userrole", ApplicationCommandOptionType.Role, "Role permitted to create invites (omit for everyone)", isRequired: false)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("pause")
                        .WithDescription("Pauses or unpauses the bot for this server (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("value", ApplicationCommandOptionType.Boolean, "value", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("debug")
                        .WithDescription("Enables or disables debug logging for this server (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("value", ApplicationCommandOptionType.Boolean, "value", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("purge")
                        .WithDescription("Purge invites older than x days (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("days", ApplicationCommandOptionType.Integer, "value", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("status")
                        .WithDescription("Returns bot status for this server (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("overlay")
                        .WithDescription("Uploads (or replaces) the overlay image used for this server's invites (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("image", ApplicationCommandOptionType.Attachment, "PNG image to use as the overlay", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("print")
                        .WithDescription("Sets the default long-edge print size for invites (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("size", ApplicationCommandOptionType.String, "Length, e.g. 90mm, 9cm, 3.5in. Pass \"clear\" to remove the default.", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("domain")
                        .WithDescription("Sets the redirect domain used in generated invite URLs (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("value", ApplicationCommandOptionType.String, "e.g. invite.example.com (omit scheme; pass \"clear\" to unset)", isRequired: true)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("export")
                        .WithDescription("Exports this server's settings and overlay as a backup (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("import")
                        .WithDescription("Imports a backup produced by /invite admin export (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("backup", ApplicationCommandOptionType.Attachment, "JSON file produced by /invite admin export", isRequired: true)
                        .AddOption("overlay", ApplicationCommandOptionType.Attachment, "Overlay PNG from the same backup (optional)", isRequired: false)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("introduce")
                        .WithDescription("DMs an introduction to a user, role, or @everyone (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("target", ApplicationCommandOptionType.Mentionable, "User or role to introduce the bot to (use @everyone for the whole server)", isRequired: true)
                        .AddOption("iamsure", ApplicationCommandOptionType.Boolean, "Required when the target covers more than 10% of the server's members", isRequired: false)
                    ).AddOption(new SlashCommandOptionBuilder()
                        .WithName("welcome")
                        .WithDescription("Toggles automatically DM'ing the introduction to new members (admin only)")
                        .WithType(ApplicationCommandOptionType.SubCommand)
                        .AddOption("value", ApplicationCommandOptionType.Boolean, "true to auto-introduce on join (default), false to disable", isRequired: true)
                    )
                );
        }

        private static async Task SlashCommandHandler(SocketSlashCommand command) {
            SocketGuildUser? user = command.User as SocketGuildUser;
            if (user is null) { return; }

            ulong guildId = user.Guild.Id;
            await EnsureGuildAsync(guildId); // covers the race where a command arrives before JoinedGuild is processed
            if (!guilds.TryGetValue(guildId, out GuildContext? ctx) || ctx is null) { return; }

            // Parsing and sanity checking
            if (command.Data.Name != "invite" || command.Data.Options.Count != 1) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) attempted to execute invalid command \"{command.Data.Name}\"");
                return;
            }

            SocketSlashCommandDataOption subCommand = command.Data.Options.First();
            while (subCommand.Type == ApplicationCommandOptionType.SubCommandGroup) { subCommand = subCommand.Options.First(); }
            if (subCommand.Type != ApplicationCommandOptionType.SubCommand) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) attempted to execute invalid subcommand \"{subCommand.Name}\"");
                return;
            }

            // Configure is the bootstrap path: gated by Discord's Manage Server permission, not by our own roles
            if (subCommand.Name == "configure") {
                if (!user.GuildPermissions.ManageGuild) {
                    await command.RespondAsync("You need the Manage Server permission to configure this bot", ephemeral: true);
                    return;
                }
                await HandleConfigure(command, ctx, subCommand);
                return;
            }

            // Every other command requires the bot to have been configured
            if (!ctx.IsConfigured) {
                await command.RespondAsync("This server has not been configured yet. An administrator must run `/invite admin configure` first.", ephemeral: true);
                return;
            }

            SocketTextChannel? channel = ChannelFor(ctx);
            if (channel is null) {
                await command.RespondAsync("The configured log channel is no longer available. Please re-run `/invite admin configure`.", ephemeral: true);
                return;
            }

            // Figure out ackles
            bool isAdmin = false;
            bool isUser = ctx.UserRole == 0; // If no user role is set, everyone is a user
            foreach (SocketRole role in user.Roles) {
                if (role.Id == ctx.AdminRole) { isAdmin = true; isUser = true; break; }
                if (role.Id == ctx.UserRole) { isUser = true; }
            }
            if (ctx.Debug) {
                if (isAdmin) {
                    await channel.SendMessageAsync($"DEBUG: User {user.DisplayName} ({user.Id}) is an administrator");
                } else if (isUser) {
                    await channel.SendMessageAsync($"DEBUG: User {user.DisplayName} ({user.Id}) is a user");
                } else {
                    await channel.SendMessageAsync($"DEBUG: User {user.DisplayName} ({user.Id}) is not a user");
                }
            }

            if (!isUser) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand {subCommand.Name}");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }

            switch (subCommand.Name) {
                case "create":
                    await HandleCreate(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "purge":
                    await HandlePurge(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "debug":
                    await HandleDebug(command, ctx, subCommand, user, isAdmin);
                    return;
                case "pause":
                    await HandlePause(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "status":
                    await HandleStatus(command, ctx, channel, user, isAdmin);
                    return;
                case "overlay":
                    await HandleOverlay(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "print":
                    await HandlePrint(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "domain":
                    await HandleDomain(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "export":
                    await HandleExport(command, ctx, user, isAdmin);
                    return;
                case "import":
                    await HandleImport(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "introduce":
                    await HandleIntroduce(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                case "welcome":
                    await HandleWelcome(command, ctx, channel, subCommand, user, isAdmin);
                    return;
                default:
                    await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) attempted to execute invalid subcommand {subCommand.Name}");
                    return;
            }
        }

        private static async Task HandleConfigure(SocketSlashCommand command, GuildContext ctx, SocketSlashCommandDataOption sub) {
            ulong newChannel = 0, newAdmin = 0, newUser = 0;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                switch (option.Name) {
                    case "channel":
                        if (option.Value is SocketTextChannel tc) { newChannel = tc.Id; }
                        else if (option.Value is IChannel ic) { newChannel = ic.Id; }
                        break;
                    case "adminrole":
                        if (option.Value is SocketRole ar) { newAdmin = ar.Id; }
                        else if (option.Value is IRole ir1) { newAdmin = ir1.Id; }
                        break;
                    case "userrole":
                        if (option.Value is SocketRole ur) { newUser = ur.Id; }
                        else if (option.Value is IRole ir2) { newUser = ir2.Id; }
                        break;
                }
            }

            if (newChannel == 0 || newAdmin == 0) {
                await command.RespondAsync("Both channel and adminrole are required", ephemeral: true);
                return;
            }

            // Confirm the chosen channel actually exists in this guild and is a text channel
            SocketGuild? g = discord?.GetGuild(ctx.GuildId);
            SocketTextChannel? targetChannel = g?.GetTextChannel(newChannel);
            if (g is null || targetChannel is null) {
                await command.RespondAsync("The chosen channel is not a text channel in this server", ephemeral: true);
                return;
            }

            // Verify the bot can actually do its job in this channel before we save the configuration.
            // Without this, every subsequent /invite create or cleanup post would silently fail and the
            // operator would have no idea why.
            ChannelPermissions perms = g.CurrentUser.GetPermissions(targetChannel);
            List<string> missing = new();
            if (!perms.ViewChannel) { missing.Add("View Channel"); }
            if (!perms.SendMessages) { missing.Add("Send Messages"); }
            if (!perms.AttachFiles) { missing.Add("Attach Files"); }
            if (!perms.EmbedLinks) { missing.Add("Embed Links"); }
            if (missing.Count > 0) {
                await command.RespondAsync(
                    $"I don't have the required permissions in <#{newChannel}>: {string.Join(", ", missing)}. " +
                    "Please grant them and run this command again.",
                    ephemeral: true);
                return;
            }

            // The bot also needs Create Instant Invite somewhere in the guild for /invite create to work.
            // Warn (don't block) if it's missing here, since the operator may grant it on another channel.
            if (!perms.CreateInstantInvite) {
                Log.Warn($"guild/{ctx.GuildId}", $"Bot lacks Create Instant Invite in #{targetChannel.Name} - /invite create will fail unless granted elsewhere");
            }

            ctx.ChannelId = newChannel;
            ctx.AdminRole = newAdmin;
            ctx.UserRole = newUser;
            await SaveGuildAsync(ctx);

            await command.RespondAsync(
                $"Configured. Logs go to <#{newChannel}>; admin role <@&{newAdmin}>" +
                (newUser == 0 ? "; user role: everyone." : $"; user role <@&{newUser}>."),
                ephemeral: true);
        }

        private static async Task HandlePurge(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand {sub.Name}");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand {sub.Name}");

            int days = int.MinValue;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                if (option.Name == "days" && option.Type == ApplicationCommandOptionType.Integer) {
                    long value = (long)option.Value;
                    if (value < 0 || value > int.MaxValue) {
                        await command.RespondAsync("The days parameter is out of range", ephemeral: true);
                        return;
                    }
                    days = (int)value;
                }
            }
            if (days == int.MinValue) {
                await DebugLog(ctx, $"The days parameter supplied to {sub.Name} could not be parsed");
                return;
            }
            int purged = await Purge(ctx, days);
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) purged {purged} invites older than {days} days");
            await command.RespondAsync($"{purged} invites older than {days} days have been purged", ephemeral: true);
        }

        private static async Task HandleDebug(SocketSlashCommand command, GuildContext ctx, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand {sub.Name}");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand {sub.Name}");

            bool? requestedDebug = sub.Options.FirstOrDefault(o => o.Name == "value")?.Value as bool?;
            if (requestedDebug is null) {
                await command.RespondAsync("The value parameter is required", ephemeral: true);
                return;
            }
            ctx.Debug = requestedDebug.Value;
            await SaveGuildAsync(ctx);
            await command.RespondAsync(ctx.Debug ? "Debug logging was enabled" : "Debug logging was disabled", ephemeral: true);
        }

        private static async Task HandlePause(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand {sub.Name}");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand {sub.Name}");

            bool? requestedPause = sub.Options.FirstOrDefault(o => o.Name == "value")?.Value as bool?;
            if (requestedPause is null) {
                await command.RespondAsync("The value parameter is required", ephemeral: true);
                return;
            }
            if (ctx.Paused == requestedPause.Value) {
                await command.RespondAsync(ctx.Paused ? "Bot is already paused" : "Bot is already running", ephemeral: true);
                return;
            }
            ctx.Paused = requestedPause.Value;
            await SaveGuildAsync(ctx);
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) {(ctx.Paused ? "paused" : "resumed")} the bot");
            await command.RespondAsync(ctx.Paused ? "Bot paused" : "Bot resumed", ephemeral: true);
        }

        private static async Task HandleOverlay(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand overlay");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand overlay");

            // Download and validation can both take a moment, so defer immediately to avoid the
            // 3-second interaction deadline.
            await command.DeferAsync(ephemeral: true);

            IAttachment? attachment = null;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                if (option.Name == "image" && option.Value is IAttachment a) { attachment = a; break; }
            }
            if (attachment is null) {
                await command.FollowupAsync("An image attachment is required", ephemeral: true);
                return;
            }
            if (attachment.Size > OverlayMaxBytes) {
                await command.FollowupAsync($"That file is too large ({attachment.Size} bytes); the limit is {OverlayMaxBytes / (1024 * 1024)} MB", ephemeral: true);
                return;
            }

            byte[] bytes;
            try {
                bytes = await overlayHttp.GetByteArrayAsync(attachment.Url);
            } catch (Exception x) {
                Log.Warn($"guild/{ctx.GuildId}", $"Failed to download overlay attachment from {attachment.Url}", x);
                await command.FollowupAsync("The bot could not download the attached file from Discord. Please try again.", ephemeral: true);
                return;
            }

            bool replaced = HasOverlay(ctx.GuildId);
            (OverlayCacheEntry? entry, string? note, string? error) = StoreOverlay(ctx.GuildId, bytes);
            if (entry is null) {
                await command.FollowupAsync($"Overlay rejected: {error}", ephemeral: true);
                return;
            }

            string verb = replaced ? "replaced" : "uploaded";
            string suffix = note is null ? "" : $" ({note})";
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) {verb} the overlay image ({entry.Width}\u00d7{entry.Height}){suffix}");
            await command.FollowupAsync($"Overlay {verb} ({entry.Width}\u00d7{entry.Height}){suffix}. New invites will use it immediately.", ephemeral: true);
        }

        private static async Task HandlePrint(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand print");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand print");

            string? raw = null;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                if (option.Name == "size" && option.Value is string s) { raw = s; break; }
            }
            if (raw is null) {
                await command.RespondAsync("The size parameter is required", ephemeral: true);
                return;
            }

            // "clear", "none", "off", or "0" all unset the per-guild default. Renders then fall
            // back to the overlay's native pixel dimensions, which on a 300 DPI overlay is already
            // a print-correct asset.
            string lowered = raw.Trim().ToLowerInvariant();
            if (lowered is "clear" or "none" or "off" or "0") {
                ctx.PrintLongEdgeMm = null;
                await SaveGuildAsync(ctx);
                await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) cleared the default print size");
                await command.RespondAsync("Default print size cleared. Invites will render at the overlay's native dimensions.", ephemeral: true);
                return;
            }

            if (!TryParseLengthMm(raw, out double mm, out string? error)) {
                await command.RespondAsync($"Could not set print size: {error}", ephemeral: true);
                return;
            }

            ctx.PrintLongEdgeMm = mm;
            await SaveGuildAsync(ctx);
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) set the default print size to {FormatMm(mm)}");
            await command.RespondAsync($"Default print size set to {FormatMm(mm)} on the long edge.", ephemeral: true);
        }

        private static async Task HandleDomain(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand domain");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand domain");

            string? raw = null;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                if (option.Name == "value" && option.Value is string s) { raw = s; break; }
            }
            if (raw is null) {
                await command.RespondAsync("The value parameter is required", ephemeral: true);
                return;
            }

            // "clear"/"none"/"off" unset the per-guild domain. Subsequent /invite create calls
            // will refuse until a new domain is set, which is the right behaviour: there is no
            // sensible global fallback now that the bot ships without a process-wide default.
            string lowered = raw.Trim().ToLowerInvariant();
            if (lowered is "clear" or "none" or "off") {
                ctx.Domain = null;
                await SaveGuildAsync(ctx);
                // The domain identity has changed (to "none"), so the health monitor's
                // last-known state for this guild is no longer about the same target. Drop it
                // so we do not post a misleading "back up"/"down" transition the next time a
                // domain is set.
                lastKnownHealthy.TryRemove(ctx.GuildId, out _);
                sawFallbackUse.TryRemove(ctx.GuildId, out _);
                await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) cleared the redirect domain");
                await command.RespondAsync("Redirect domain cleared. `/invite create` will refuse until a new one is set.", ephemeral: true);
                return;
            }

            if (!TryNormaliseDomain(raw, out string normalised, out string? error)) {
                await command.RespondAsync($"Could not set domain: {error}", ephemeral: true);
                return;
            }

            // Capture the prior value before we overwrite it; if the domain is actually
            // changing, the monitor's last-known healthy/unhealthy verdict was about a
            // different target and must not influence transition detection on the new one.
            string? previousDomain = ctx.Domain;
            ctx.Domain = normalised;
            await SaveGuildAsync(ctx);
            if (!string.Equals(previousDomain, normalised, StringComparison.OrdinalIgnoreCase)) {
                lastKnownHealthy.TryRemove(ctx.GuildId, out _);
                sawFallbackUse.TryRemove(ctx.GuildId, out _);
            }
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) set the redirect domain to {normalised}");

            // Probe the new domain immediately so the admin learns about a misconfigured LB
            // here, in the same response, rather than from a guest with a dead QR code.
            // Defer first because the probe can take several seconds in the failure cases.
            await command.DeferAsync(ephemeral: true);
            DomainHealth health = await ProbeDomainAsync(normalised);
            string message = $"Redirect domain set to `{normalised}`. New invites will use `https://{normalised}/<id>`.\n{health.Format(normalised)}";
            await command.FollowupAsync(message, ephemeral: true);
        }

        // Versioned schema for /invite admin export. Bump the version when fields change so a
        // future /invite admin import can refuse anything it does not understand. The shape is
        // deliberately flat and human-editable - a sysadmin restoring a backup by hand is a
        // perfectly reasonable use of this output.
        private sealed class GuildExport {
            [JsonPropertyName("schema")] public string Schema { get; init; } = "invitebot.guild-export";
            [JsonPropertyName("version")] public int Version { get; init; } = 3;
            [JsonPropertyName("exportedUtc")] public string ExportedUtc { get; init; } = "";
            [JsonPropertyName("guildId")] public ulong GuildId { get; init; }
            [JsonPropertyName("guildName")] public string? GuildName { get; init; }
            [JsonPropertyName("channelId")] public ulong ChannelId { get; init; }
            [JsonPropertyName("adminRole")] public ulong AdminRole { get; init; }
            [JsonPropertyName("userRole")] public ulong UserRole { get; init; }
            [JsonPropertyName("paused")] public bool Paused { get; init; }
            [JsonPropertyName("debug")] public bool Debug { get; init; }
            [JsonPropertyName("printLongEdgeMm")] public double? PrintLongEdgeMm { get; init; }
            [JsonPropertyName("domain")] public string? Domain { get; init; }
            // v3 added the auto-welcome toggle. Nullable so we can detect "field absent in
            // older v1/v2 backup" vs "explicit false"; on import, absent defaults to true,
            // matching both the schema column default and the GuildContext field default so a
            // v2 backup restored on a v3 build behaves the same as a fresh install.
            [JsonPropertyName("welcomeNewMembers")] public bool? WelcomeNewMembers { get; init; }
            [JsonPropertyName("overlayFile")] public string? OverlayFile { get; init; }
            [JsonPropertyName("overlayWidth")] public uint? OverlayWidth { get; init; }
            [JsonPropertyName("overlayHeight")] public uint? OverlayHeight { get; init; }
        }

        private static readonly JsonSerializerOptions exportJsonOptions = new() {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private static async Task HandleExport(SocketSlashCommand command, GuildContext ctx, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand export");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand export");

            // Defer ephemeral - the overlay can be a few MB and we may need to read it from disk.
            // Ephemeral throughout: backups contain channel/role IDs and the bot's branding, so we
            // never want this output landing in a public channel by accident.
            await command.DeferAsync(ephemeral: true);

            OverlayCacheEntry? overlay = LoadOverlay(ctx.GuildId);
            string? overlayFileName = overlay is null ? null : $"{ctx.GuildId}.png";
            SocketGuild? guild = discord?.GetGuild(ctx.GuildId);

            GuildExport export = new() {
                ExportedUtc = DateTime.UtcNow.ToString("o"),
                GuildId = ctx.GuildId,
                GuildName = guild?.Name,
                ChannelId = ctx.ChannelId,
                AdminRole = ctx.AdminRole,
                UserRole = ctx.UserRole,
                Paused = ctx.Paused,
                Debug = ctx.Debug,
                PrintLongEdgeMm = ctx.PrintLongEdgeMm,
                Domain = ctx.Domain,
                WelcomeNewMembers = ctx.WelcomeNewMembers,
                OverlayFile = overlayFileName,
                OverlayWidth = overlay?.Width,
                OverlayHeight = overlay?.Height,
            };

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(export, exportJsonOptions);

            // Build the attachment list. The JSON always goes; the overlay only if one exists.
            // Streams must outlive the upload, so we keep them in a using-scope around the call.
            List<FileAttachment> attachments = new(2);
            using MemoryStream jsonStream = new(jsonBytes, writable: false);
            attachments.Add(new FileAttachment(jsonStream, $"invitebot-guild-{ctx.GuildId}.json", "InviteBot guild export"));

            MemoryStream? overlayStream = null;
            try {
                if (overlay is not null && overlayFileName is not null) {
                    overlayStream = new MemoryStream(overlay.Bytes, writable: false);
                    attachments.Add(new FileAttachment(overlayStream, overlayFileName, "Overlay image (300 DPI)"));
                }

                string body = overlay is null
                    ? $"Backup for **{guild?.Name ?? ctx.GuildId.ToString()}**. The JSON contains every persisted setting. No overlay is configured for this server, so none is attached."
                    : $"Backup for **{guild?.Name ?? ctx.GuildId.ToString()}**. The JSON contains every persisted setting; the PNG is the current overlay (already normalised to 300 DPI - re-uploading it via `/invite admin overlay` will restore it as-is).";

                await command.FollowupWithFilesAsync(attachments, body, ephemeral: true);
            } finally {
                overlayStream?.Dispose();
            }
        }

        // Highest schema version this build can read. Anything newer is rejected so a future
        // export shape cannot be silently truncated by an older bot.
        private const int GuildExportMaxVersion = 3;

        private static async Task HandleImport(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketSlashCommandDataOption sub, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand import");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand import");

            // Defer ephemeral - downloading both attachments and validating can take a moment, and
            // backups are sensitive (they contain channel and role IDs) so the response should
            // never become visible to non-admins.
            await command.DeferAsync(ephemeral: true);

            IAttachment? backupAttachment = null;
            IAttachment? overlayAttachment = null;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                if (option.Name == "backup" && option.Value is IAttachment a) { backupAttachment = a; }
                else if (option.Name == "overlay" && option.Value is IAttachment b) { overlayAttachment = b; }
            }
            if (backupAttachment is null) {
                await command.FollowupAsync("A backup JSON attachment is required", ephemeral: true);
                return;
            }

            // Cap the JSON at a generous-but-bounded size so a malicious or accidentally-huge
            // file cannot exhaust memory before we even parse it.
            const int MaxJsonBytes = 256 * 1024;
            if (backupAttachment.Size > MaxJsonBytes) {
                await command.FollowupAsync($"Backup file is too large ({backupAttachment.Size} bytes); the limit is {MaxJsonBytes} bytes", ephemeral: true);
                return;
            }

            byte[] jsonBytes;
            try {
                jsonBytes = await overlayHttp.GetByteArrayAsync(backupAttachment.Url);
            } catch (Exception x) {
                Log.Warn($"guild/{ctx.GuildId}", $"Failed to download backup attachment from {backupAttachment.Url}", x);
                await command.FollowupAsync("The bot could not download the backup file from Discord. Please try again.", ephemeral: true);
                return;
            }

            GuildExport? export;
            try {
                export = JsonSerializer.Deserialize<GuildExport>(jsonBytes);
            } catch (JsonException x) {
                await command.FollowupAsync($"Backup file is not valid JSON: {x.Message}", ephemeral: true);
                return;
            }
            if (export is null) {
                await command.FollowupAsync("Backup file deserialised to null", ephemeral: true);
                return;
            }

            if (!string.Equals(export.Schema, "invitebot.guild-export", StringComparison.Ordinal)) {
                await command.FollowupAsync($"Backup schema is \"{export.Schema}\"; expected \"invitebot.guild-export\"", ephemeral: true);
                return;
            }
            if (export.Version < 1 || export.Version > GuildExportMaxVersion) {
                await command.FollowupAsync($"Backup schema version {export.Version} is not supported by this build (max v{GuildExportMaxVersion})", ephemeral: true);
                return;
            }

            // Cross-guild restores are intentionally permitted (a sysadmin moving config between
            // dev and prod servers is a legitimate use), but we surface the swap so an admin
            // pasting the wrong file by accident notices immediately.
            string crossGuildNote = "";
            if (export.GuildId != ctx.GuildId) {
                crossGuildNote = $" (originally exported from guild {export.GuildId}{(export.GuildName is null ? "" : $" \"{export.GuildName}\"")})";
            }

            // Apply settings. ChannelId/AdminRole/UserRole are stored even if they no longer
            // resolve in this guild - the next /invite admin status will surface "<missing>" and
            // the admin can re-run /invite admin configure to fix them up.
            string? previousDomain = ctx.Domain;
            ctx.ChannelId = export.ChannelId;
            ctx.AdminRole = export.AdminRole;
            ctx.UserRole = export.UserRole;
            ctx.Paused = export.Paused;
            ctx.Debug = export.Debug;
            ctx.PrintLongEdgeMm = export.PrintLongEdgeMm;
            // v2 added Domain. v1 backups have it null, which is the safe default - the admin
            // re-runs /invite admin domain after restoring rather than risking a stale value.
            ctx.Domain = export.Domain;
            // v3 added WelcomeNewMembers. Older backups have it null; default to true to match
            // the schema column DEFAULT and the GuildContext field initialiser, so an old
            // backup restored on a v3 build behaves identically to a fresh install.
            ctx.WelcomeNewMembers = export.WelcomeNewMembers ?? true;
            await SaveGuildAsync(ctx);
            // If the import changed the redirect domain, the periodic health monitor's last
            // verdict was about a different target; drop it so the next probe records fresh
            // state rather than emitting a misleading transition message.
            if (!string.Equals(previousDomain, ctx.Domain, StringComparison.OrdinalIgnoreCase)) {
                lastKnownHealthy.TryRemove(ctx.GuildId, out _);
                sawFallbackUse.TryRemove(ctx.GuildId, out _);
            }

            // Overlay restore is optional and best-effort. If the backup names an overlay file
            // and the admin attached one, we store it; otherwise the existing overlay (if any)
            // is left in place. We never delete an existing overlay just because the backup omits
            // one, because the most common "I forgot the PNG" case shouldn't blow away production.
            string overlayNote;
            if (overlayAttachment is not null) {
                if (overlayAttachment.Size > OverlayMaxBytes) {
                    overlayNote = $" Overlay rejected: file too large ({overlayAttachment.Size} bytes; limit {OverlayMaxBytes / (1024 * 1024)} MB).";
                } else {
                    byte[] overlayBytes;
                    try {
                        overlayBytes = await overlayHttp.GetByteArrayAsync(overlayAttachment.Url);
                    } catch (Exception x) {
                        Log.Warn($"guild/{ctx.GuildId}", $"Failed to download overlay attachment from {overlayAttachment.Url}", x);
                        overlayBytes = Array.Empty<byte>();
                    }
                    if (overlayBytes.Length == 0) {
                        overlayNote = " Overlay download failed; existing overlay (if any) left in place.";
                    } else {
                        (OverlayCacheEntry? entry, string? note, string? error) = StoreOverlay(ctx.GuildId, overlayBytes);
                        if (entry is null) {
                            overlayNote = $" Overlay rejected: {error}.";
                        } else {
                            overlayNote = $" Overlay restored ({entry.Width}\u00d7{entry.Height}{(note is null ? "" : $", {note}")}).";
                        }
                    }
                }
            } else if (export.OverlayFile is not null) {
                overlayNote = " Backup referenced an overlay but none was attached; existing overlay (if any) left in place.";
            } else {
                overlayNote = "";
            }

            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) imported a settings backup{crossGuildNote}");
            await command.FollowupAsync($"Backup imported{crossGuildNote}.{overlayNote}", ephemeral: true);
        }

        private static async Task HandleStatus(SocketSlashCommand command, GuildContext ctx, SocketTextChannel channel, SocketGuildUser user, bool isAdmin) {
            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand status");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand status");

            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            SocketRole? adminRole = channel.Guild.GetRole(ctx.AdminRole);
            SocketRole? userRole = ctx.UserRole == 0 ? null : channel.Guild.GetRole(ctx.UserRole);
            string adminRoleName = adminRole?.Name ?? "<missing>";
            string userRoleDescription = ctx.UserRole == 0
                ? "@everyone (no user role configured)"
                : $"\"{userRole?.Name ?? "<missing>"}\" (ID {ctx.UserRole})";

            string statusMessage = "";
            statusMessage += $"InviteBot {version}\n";
            statusMessage += ctx.Paused ? "The bot is currently paused, " : "The bot is currently running, ";
            statusMessage += ctx.Debug ? "and debug logging is enabled.\n" : "and debug logging is disabled.\n";
            statusMessage += $"The channel \"{channel.Name}\" (ID {channel.Id}) is being used for logging.\n";
            statusMessage += $"The role {userRoleDescription} can use this bot.\n";
            statusMessage += $"The role \"{adminRoleName}\" (ID {ctx.AdminRole}) can administer this bot.\n";
            statusMessage += $"Generated invites will have a duration of {defaultDuration} minutes, and can be used {defaultUses} time(s).\n";
            statusMessage += string.IsNullOrEmpty(ctx.Domain)
                ? "Redirect domain: <not set> - run `/invite admin domain` before creating invites.\n"
                : $"Generated invites will use the domain {ctx.Domain}.\n";
            statusMessage += $"The cleanup task will run every {cleanupTimer} minutes.\n";
            OverlayCacheEntry? overlay = LoadOverlay(ctx.GuildId);
            statusMessage += overlay is null
                ? "No overlay image is configured. Run `/invite admin overlay` to upload one.\n"
                : $"Overlay: {overlay.Width}\u00d7{overlay.Height} ({overlay.Bytes.Length} bytes).\n";
            statusMessage += ctx.PrintLongEdgeMm.HasValue
                ? $"Default print size: {FormatMm(ctx.PrintLongEdgeMm.Value)} on the long edge (rendered at 300 DPI)."
                : "Default print size: not set; invites render at the overlay's native dimensions.";
            statusMessage += ctx.WelcomeNewMembers
                ? "\nAuto-welcome: enabled (new members are DM'd the introduction on join)."
                : "\nAuto-welcome: disabled (new members are not DM'd automatically).";

            // Live health probe of the redirect domain. Deferred because the probe can run for
            // several seconds in the timeout/DNS-failure cases, which would blow the 3-second
            // initial-response window.
            await command.DeferAsync(ephemeral: true);
            if (!string.IsNullOrEmpty(ctx.Domain)) {
                DomainHealth health = await ProbeDomainAsync(ctx.Domain);
                statusMessage += "\n" + health.Format(ctx.Domain);
                // Surface the fallback state explicitly. sawFallbackUse is set by /invite create
                // when a probe failed and reset by the periodic monitor on recovery, so this
                // tells operators "did anything actually have to use the fallback recently?"
                // without having to scroll the log channel.
                if (sawFallbackUse.ContainsKey(ctx.GuildId)) {
                    statusMessage += $"\nFallback in use: at least one invite has been served via `{fallbackDomain}` since the last recovery.";
                } else if (!health.Healthy && !string.IsNullOrEmpty(fallbackDomain)) {
                    statusMessage += $"\nFallback armed: new invites will be served via `{fallbackDomain}` until the redirect domain recovers.";
                }
            }
            await command.FollowupAsync(statusMessage, ephemeral: true);
        }
    }
}
