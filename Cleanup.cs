using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;

namespace InviteBot {
    public partial class InviteBot {

        private static async Task CleanupPeriodic(CancellationToken token = default) {
            using PeriodicTimer timer = new(new TimeSpan(0, cleanupTimer, 0));
            while (true) {
                try {
                    await CleanupAll();
                } catch (Exception x) {
                    Log.Error("cleanup", "Cleanup task threw", x);
                }
                try {
                    if (!await timer.WaitForNextTickAsync(token)) { return; }
                } catch (OperationCanceledException) { return; }
            }
        }

        private static async Task CleanupAll() {
            foreach (GuildContext ctx in guilds.Values) {
                if (!ctx.IsConfigured || ctx.Paused) { continue; }
                try { await Cleanup(ctx); }
                catch (Exception x) {
                    Log.Error("cleanup", $"Cleanup failed for guild {ctx.GuildId}", x);
                    await DebugLog(ctx, $"Cleanup failed: {x.Message}");
                }
            }
        }

        private static async Task Cleanup(GuildContext ctx) {
            if (db is null) { return; }
            SocketTextChannel? channel = ChannelFor(ctx);
            if (channel is null) { return; }
            await DebugLog(ctx, "Cleanup task running");

            // Collect rows that have logically expired but are not yet marked purged
            List<(long Id, string Code)> expired = new();
            string nowIso = DateTime.UtcNow.ToString("o");
            await dbLock.WaitAsync();
            try {
                string selectSql = $"SELECT Id, Invite FROM guild_{ctx.GuildId} WHERE Purged = 0 AND ExpiryDate <= @now;";
                using SqliteCommand selectCmd = new(selectSql, db);
                selectCmd.Parameters.AddWithValue("@now", nowIso);
                using SqliteDataReader reader = selectCmd.ExecuteReader();
                while (reader.Read()) {
                    expired.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            } catch (Exception x) {
                Log.Error("cleanup", $"Cleanup query failed for guild {ctx.GuildId}", x);
                await DebugLog(ctx, $"Cleanup query failed: {x.Message}");
                return;
            } finally { dbLock.Release(); }

            if (expired.Count == 0) {
                await DebugLog(ctx, "Cleanup found no expired invites");
                return;
            }

            // Cache the guild's invite list once to avoid an API call per row
            Dictionary<string, RestInviteMetadata> live;
            try {
                live = (await channel.Guild.GetInvitesAsync()).ToDictionary(i => i.Code, i => i);
            } catch (Exception x) {
                Log.Error("cleanup", $"Cleanup could not fetch invites for guild {ctx.GuildId}", x);
                await DebugLog(ctx, $"Cleanup could not fetch guild invites: {x.Message}");
                return;
            }

            int deleted = 0;
            foreach ((long rowId, string code) in expired) {
                try {
                    if (live.TryGetValue(code, out RestInviteMetadata? inv) && inv is not null) {
                        await inv.DeleteAsync();
                        deleted++;
                        await DebugLog(ctx, $"Cleanup deleted expired invite {code}");
                    } else {
                        await DebugLog(ctx, $"Cleanup found no live Discord invite for {code}; marking purged");
                    }

                    string updateSql = $"UPDATE guild_{ctx.GuildId} SET Purged = 1 WHERE Id = @id;";
                    await dbLock.WaitAsync();
                    try {
                        using SqliteCommand updateCmd = new(updateSql, db);
                        updateCmd.Parameters.AddWithValue("@id", rowId);
                        updateCmd.ExecuteNonQuery();
                    } finally { dbLock.Release(); }
                } catch (Exception x) {
                    Log.Error("cleanup", $"Cleanup failed for invite {code} in guild {ctx.GuildId}", x);
                    await DebugLog(ctx, $"Cleanup failed for invite {code}: {x.Message}");
                }
            }

            if (deleted > 0) {
                try { await channel.SendMessageAsync($"Cleanup removed {deleted} expired invite(s)"); }
                catch (Exception x) { Log.Warn("cleanup", $"Failed to post cleanup summary in guild {ctx.GuildId}", x); }
            }
        }

        private static async Task<int> Purge(GuildContext ctx, int days) {
            SocketTextChannel? channel = ChannelFor(ctx);
            if (channel is null) { return 0; }
            await DebugLog(ctx, "Purge task running");
            int purged = 0;

            foreach (RestInviteMetadata invite in await channel.Guild.GetInvitesAsync()) {
                DateTimeOffset? date = invite.CreatedAt;
                if (date.HasValue && invite.Inviter is not null && channel.Guild.CurrentUser.Id == invite.Inviter.Id) {
                    if (DateTimeOffset.UtcNow - date.Value > TimeSpan.FromDays(days) || days == 0) {
                        await DebugLog(ctx, $"Purging invite {invite.Code} created on {date.Value.UtcDateTime:o}");
                        await invite.DeleteAsync();
                        purged++;

                        // Keep the DB view consistent with Discord
                        if (db is not null) {
                            string updateSql = $"UPDATE guild_{ctx.GuildId} SET Purged = 1 WHERE Invite = @invite;";
                            await dbLock.WaitAsync();
                            try {
                                using SqliteCommand updateCmd = new(updateSql, db);
                                updateCmd.Parameters.AddWithValue("@invite", invite.Code);
                                updateCmd.ExecuteNonQuery();
                            } catch (Exception x) {
                                Log.Error("purge", $"Failed to flag {invite.Code} as purged in guild {ctx.GuildId}", x);
                                await DebugLog(ctx, $"Purge failed to flag {invite.Code} in DB: {x.Message}");
                            } finally { dbLock.Release(); }
                        }
                    }
                }
            }
            return purged;
        }
    }
}
