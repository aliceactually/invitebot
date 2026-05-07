using Discord;
using Discord.WebSocket;

namespace InviteBot {
    public partial class InviteBot {

        // Slash-command handler for /invite admin introduce. The pure pieces - what the
        // introduction text says, and whether the recipient set is large enough to require
        // iamsure:true - live in Introduction.cs and IntroductionTargets.cs respectively.
        // This file owns the impure parts: resolving the Mentionable target into a concrete
        // recipient set, opening DM channels, and gracefully tolerating users who have DMs
        // closed.
        //
        // Permissions: gated by the configured admin role, like every other /invite admin
        // subcommand. The caller (SlashCommandHandler) has already proven the invoker is in
        // the admin role before dispatching here, so HandleIntroduce only needs to re-check
        // the isAdmin flag and bail with the same message used elsewhere.
        private static async Task HandleIntroduce(
            SocketSlashCommand command,
            GuildContext ctx,
            SocketTextChannel channel,
            SocketSlashCommandDataOption sub,
            SocketGuildUser user,
            bool isAdmin) {

            if (!isAdmin) {
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand introduce");
                await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                return;
            }
            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand introduce");

            // Pull options out of the slash payload. target is a Mentionable, which Discord
            // resolves to either an IUser or an IRole; @everyone arrives as the guild's
            // everyone-role (whose Id equals the guild Id).
            object? targetValue = null;
            bool iamsure = false;
            foreach (SocketSlashCommandDataOption option in sub.Options) {
                switch (option.Name) {
                    case "target":
                        targetValue = option.Value;
                        break;
                    case "iamsure":
                        if (option.Value is bool b) { iamsure = b; }
                        break;
                }
            }
            if (targetValue is null) {
                await command.RespondAsync("A target user or role is required", ephemeral: true);
                return;
            }

            SocketGuild? guild = discord?.GetGuild(ctx.GuildId);
            if (guild is null) {
                await command.RespondAsync("This server is no longer reachable. Please try again.", ephemeral: true);
                return;
            }

            // Resolve the Mentionable into a concrete list of guild members. Three shapes:
            //   - IUser (single member)
            //   - IRole whose Id == guild.Id (the @everyone role)
            //   - IRole (any other role)
            // SocketRole.Members requires the GuildMembers privileged intent and the member
            // cache to be populated; Program.cs sets both.
            List<SocketGuildUser> resolved = new();
            string targetLabel;
            switch (targetValue) {
                case SocketGuildUser sgu:
                    resolved.Add(sgu);
                    targetLabel = $"<@{sgu.Id}>";
                    break;
                case IUser iu: {
                    SocketGuildUser? gu = guild.GetUser(iu.Id);
                    if (gu is null) {
                        await command.RespondAsync("That user is not a member of this server.", ephemeral: true);
                        return;
                    }
                    resolved.Add(gu);
                    targetLabel = $"<@{gu.Id}>";
                    break;
                }
                case SocketRole sr:
                    if (sr.Id == guild.EveryoneRole.Id) {
                        resolved.AddRange(guild.Users);
                        targetLabel = "@everyone";
                    } else {
                        resolved.AddRange(sr.Members);
                        targetLabel = $"<@&{sr.Id}>";
                    }
                    break;
                case IRole ir: {
                    SocketRole? srLookup = guild.GetRole(ir.Id);
                    if (srLookup is null) {
                        await command.RespondAsync("That role is no longer present in this server.", ephemeral: true);
                        return;
                    }
                    if (srLookup.Id == guild.EveryoneRole.Id) {
                        resolved.AddRange(guild.Users);
                        targetLabel = "@everyone";
                    } else {
                        resolved.AddRange(srLookup.Members);
                        targetLabel = $"<@&{srLookup.Id}>";
                    }
                    break;
                }
                default:
                    await command.RespondAsync("Target must be a user or a role.", ephemeral: true);
                    return;
            }

            // Bots never get the introduction. Most servers have webhook/utility bots that
            // would either reject the DM outright or queue noise into integrations; either way
            // it is not behaviour an admin would want. The bot also skips itself.
            resolved.RemoveAll(m => m.IsBot);

            // Hand off to the pure planner: dedupe, compute fraction, decide whether
            // iamsure:true is required. guild.MemberCount is the authoritative population
            // figure even when the local user cache is partial.
            IntroductionTargets.IntroductionPlan plan = IntroductionTargets.Plan(
                resolved.Select(m => m.Id),
                guild.MemberCount);

            if (plan.RecipientIds.Count == 0) {
                await command.RespondAsync("No human recipients matched that target.", ephemeral: true);
                return;
            }

            if (plan.RequiresConfirmation && !iamsure) {
                int percent = (int)Math.Round(plan.Fraction * 100.0);
                await command.RespondAsync(
                    $"This would DM **{plan.RecipientIds.Count}** of {plan.GuildMemberCount} members ({percent}% of the server). " +
                    $"Re-run with `iamsure:true` to confirm.",
                    ephemeral: true);
                return;
            }

            // From here on we are doing real work that may take a while (one DM per recipient
            // with sequential awaits to stay polite to Discord's rate limiter), so defer the
            // interaction response. Ephemeral throughout - the admin's confirmation is for
            // their eyes only.
            await command.DeferAsync(ephemeral: true);

            // Build a fast lookup so we can iterate recipients in the order produced by Plan
            // (which is the dedupe-stable order the admin's selection implied) while still
            // resolving each id back to the SocketGuildUser we already have in hand.
            Dictionary<ulong, SocketGuildUser> byId = new(resolved.Count);
            foreach (SocketGuildUser m in resolved) { byId[m.Id] = m; }

            int delivered = 0;
            int closedDms = 0;
            int otherFailures = 0;
            foreach (ulong id in plan.RecipientIds) {
                if (!byId.TryGetValue(id, out SocketGuildUser? member) || member is null) { continue; }

                IntroductionDeliveryResult result = await DeliverIntroductionAsync(ctx, member);
                switch (result) {
                    case IntroductionDeliveryResult.Delivered: delivered++; break;
                    case IntroductionDeliveryResult.DmsClosed: closedDms++; break;
                    case IntroductionDeliveryResult.OtherFailure: otherFailures++; break;
                }
            }

            await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) introduced the bot to {targetLabel} ({delivered} delivered, {closedDms} DMs closed, {otherFailures} failed)");
            await channel.SendMessageAsync($"User {user.DisplayName} ({user.Id}) introduced the bot to {targetLabel}");

            string summary = $"Introduced the bot to {targetLabel}: **{delivered}** DM(s) delivered";
            if (closedDms > 0) { summary += $", **{closedDms}** recipient(s) had DMs closed"; }
            if (otherFailures > 0) { summary += $", **{otherFailures}** other failure(s)"; }
            summary += ".";
            await command.FollowupAsync(summary, ephemeral: true);
        }

        // Outcome of a single recipient's DM. Kept narrow so the loop above and the auto-
        // welcome path in HandleUserJoined can both make the same decisions about counting,
        // logging, and (in the auto-welcome case) staying silent on closed-DM users.
        private enum IntroductionDeliveryResult {
            Delivered,
            DmsClosed,
            OtherFailure,
        }

        // Sends the role-appropriate introduction to a single guild member. Computes the
        // user/admin role flags from the member's current Discord roles against the guild's
        // configured role IDs - so a member who happens to be in both the user and admin
        // roles gets the full admin tour, and a member with neither gets the friendly
        // no-access note. Catches the common "DMs closed" case and a generic fallback so
        // the caller never has to reason about Discord.Net exception shapes.
        private static async Task<IntroductionDeliveryResult> DeliverIntroductionAsync(GuildContext ctx, SocketGuildUser member) {
            bool isMemberAdmin = member.Roles.Any(r => r.Id == ctx.AdminRole);
            bool isMemberUser = ctx.UserRole == 0 || isMemberAdmin || member.Roles.Any(r => r.Id == ctx.UserRole);
            IReadOnlyList<string> chunks = Introduction.Build(isMemberUser, isMemberAdmin);

            try {
                IDMChannel dm = await member.CreateDMChannelAsync();
                foreach (string chunk in chunks) {
                    await dm.SendMessageAsync(chunk);
                }
                return IntroductionDeliveryResult.Delivered;
            } catch (Discord.Net.HttpException hx) when (
                hx.DiscordCode == DiscordErrorCode.CannotSendMessageToUser ||
                (int)hx.HttpCode == 403) {
                return IntroductionDeliveryResult.DmsClosed;
            } catch (Exception x) {
                Log.Warn($"guild/{ctx.GuildId}", $"Failed to deliver introduction DM to {member.Id}", x);
                return IntroductionDeliveryResult.OtherFailure;
            }
        }

        // Auto-welcome on guild join. Wired from Program.cs to discord.UserJoined. Behaviour:
        //   - Skip bots (a webhook/integration bot doesn't want a tour).
        //   - Skip self (the bot itself joining a new guild is handled by the restart-style
        //     notice elsewhere; DM'ing ourselves would be both pointless and rate-limit fuel).
        //   - Skip guilds we have no context for, or that aren't configured: until an admin
        //     has run /invite admin configure there is no log channel, no admin role, no user
        //     role, and the introduction would describe commands the new member literally
        //     cannot use. The first introduction in a fresh guild should be a deliberate
        //     /invite admin introduce by the administrator who set the bot up.
        //   - Deliver the role-appropriate introduction silently. Closed DMs are normal and
        //     expected (many users default to closed DMs); we do not surface those in the
        //     log channel because that would constitute privacy-sensitive noise about user
        //     DM settings. Genuine failures get a debug-log line if debug is enabled.
        private static async Task HandleUserJoined(SocketGuildUser member) {
            try {
                if (member.IsBot) { return; }
                if (discord is not null && member.Id == discord.CurrentUser.Id) { return; }

                if (!guilds.TryGetValue(member.Guild.Id, out GuildContext? ctx) || ctx is null) { return; }
                if (!ctx.IsConfigured) { return; }
                if (!ctx.WelcomeNewMembers) { return; }

                IntroductionDeliveryResult result = await DeliverIntroductionAsync(ctx, member);
                switch (result) {
                    case IntroductionDeliveryResult.Delivered:
                        await DebugLog(ctx, $"Auto-introduced the bot to new member {member.DisplayName} ({member.Id})");
                        break;
                    case IntroductionDeliveryResult.DmsClosed:
                        await DebugLog(ctx, $"Auto-introduction skipped for new member {member.DisplayName} ({member.Id}): DMs closed");
                        break;
                    case IntroductionDeliveryResult.OtherFailure:
                        await DebugLog(ctx, $"Auto-introduction failed for new member {member.DisplayName} ({member.Id}); see warnings");
                        break;
                }
            } catch (Exception x) {
                // The gateway will swallow exceptions thrown from event handlers, but it will
                // also log them in a way that's hard to attribute. Catch and log explicitly so
                // a regression here cannot silently kill subsequent UserJoined dispatches.
                Log.Warn($"guild/{member.Guild.Id}", $"Unhandled exception in UserJoined handler for {member.Id}", x);
            }
        }
            // Slash-command handler for /invite admin welcome. Toggles ctx.WelcomeNewMembers and
            // persists. Mirrors the shape of HandlePause/HandleDebug: one boolean parameter, an
            // already-set short-circuit, a SaveGuildAsync, an audit line in the log channel, and
            // an ephemeral confirmation to the admin.
            private static async Task HandleWelcome(
                SocketSlashCommand command,
                GuildContext ctx,
                SocketTextChannel channel,
                SocketSlashCommandDataOption sub,
                SocketGuildUser user,
                bool isAdmin) {

                if (!isAdmin) {
                    await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was denied access to subcommand welcome");
                    await command.RespondAsync("You are not authorised to use this command", ephemeral: true);
                    return;
                }
                await DebugLog(ctx, $"User {user.DisplayName} ({user.Id}) was granted access to subcommand welcome");

                bool? requestedWelcome = sub.Options.FirstOrDefault(o => o.Name == "value")?.Value as bool?;
                if (requestedWelcome is null) {
                    await command.RespondAsync("The value parameter is required", ephemeral: true);
                    return;
                }
                if (ctx.WelcomeNewMembers == requestedWelcome.Value) {
                    await command.RespondAsync(
                        ctx.WelcomeNewMembers
                            ? "Auto-welcome is already enabled"
                            : "Auto-welcome is already disabled",
                        ephemeral: true);
                    return;
                }
                ctx.WelcomeNewMembers = requestedWelcome.Value;
                await SaveGuildAsync(ctx);
                await channel.SendMessageAsync(
                    $"User {user.DisplayName} ({user.Id}) {(ctx.WelcomeNewMembers ? "enabled" : "disabled")} auto-welcome for new members");
                await command.RespondAsync(
                    ctx.WelcomeNewMembers
                        ? "Auto-welcome enabled. New members will be DM'd the introduction when they join."
                        : "Auto-welcome disabled. New members will no longer be DM'd automatically (you can still send introductions manually with `/invite admin introduce`).",
                    ephemeral: true);
            }
        }
    }
