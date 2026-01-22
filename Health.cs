using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Authentication;
using Discord.WebSocket;

namespace InviteBot {
    public partial class InviteBot {

        // Live health check for a per-guild redirect domain. The bot has no way of knowing
        // whether the load balancer behind {domain} is actually serving traffic until somebody
        // taps a QR code at the door, which is a bad time to find out. Probing on demand from
        // /invite admin domain (immediately after a successful set) and from /invite admin status
        // (so admins can spot-check) closes that gap without adding a background ping.
        //
        // We do a single GET to https://{domain}/ with redirects disabled. We are deliberately
        // tolerant about the response: anything 2xx/3xx/4xx means TLS handshook and HTTP
        // responded, which is what we actually care about. 5xx and transport-level failures are
        // what kill an invite link, so those are the ones we surface as "down".

        // Dedicated client: short timeout, no auto-redirect (we want to see the LB's actual reply,
        // not chase it across hostnames), no cookies. Reused across calls so we benefit from the
        // socket pool when an admin spams /invite admin status.
        private static readonly HttpClient healthHttp = BuildHealthClient();

        private static HttpClient BuildHealthClient() {
            SocketsHttpHandler handler = new() {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                UseCookies = false,
            };
            HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("InviteBot-HealthProbe/1.0");
            return client;
        }

        internal enum DomainHealthKind {
            Ok,         // 2xx/3xx/4xx - reached the LB, TLS fine, app responded
            ServerError,// 5xx - LB up but app is unhappy
            Dns,        // hostname did not resolve
            Tls,        // certificate or handshake problem
            Connect,    // TCP refused / unreachable
            Timeout,    // exceeded request timeout
            Other,      // anything we did not specifically classify
        }

        internal sealed record DomainHealth(DomainHealthKind Kind, int? StatusCode, TimeSpan Elapsed, string? Detail) {
            // "Healthy" for routing purposes means the LB answered with anything that is not a
            // 5xx and is not a transport-level failure. 2xx/3xx/4xx all count: a 404 from the LB
            // still proves the box is up and would happily redirect a real /<inviteId> path.
            public bool Healthy => Kind == DomainHealthKind.Ok;

            // Single-line human-readable summary suitable for /invite admin status and the
            // confirmation message from /invite admin domain. Pure formatting; covered by tests.
            public string Format(string domain) {
                long ms = (long)Elapsed.TotalMilliseconds;
                return Kind switch {
                    DomainHealthKind.Ok          => $"Live health: OK ({StatusCode} from {domain} in {ms} ms)",
                    DomainHealthKind.ServerError => $"Live health: server error ({StatusCode} from {domain} in {ms} ms) - the load balancer is reachable but the backend is unhappy",
                    DomainHealthKind.Dns         => $"Live health: DNS lookup for {domain} failed ({Detail ?? "no detail"})",
                    DomainHealthKind.Tls         => $"Live health: TLS handshake with {domain} failed ({Detail ?? "no detail"})",
                    DomainHealthKind.Connect     => $"Live health: could not connect to {domain} ({Detail ?? "no detail"})",
                    DomainHealthKind.Timeout     => $"Live health: {domain} did not respond within {ms} ms",
                    _                            => $"Live health: {domain} unreachable ({Detail ?? "unknown error"})",
                };
            }
        }

        internal static async Task<DomainHealth> ProbeDomainAsync(string domain, CancellationToken ct = default) {
            // Build the URL ourselves rather than letting HttpClient do it from a string so any
            // weird domain content turns into a clean classified failure rather than an exception
            // bubbling up from inside the call.
            Uri uri;
            try {
                UriBuilder b = new("https", domain) { Path = "/" };
                uri = b.Uri;
            } catch (Exception ex) {
                return new DomainHealth(DomainHealthKind.Other, null, TimeSpan.Zero, ex.Message);
            }

            Stopwatch sw = Stopwatch.StartNew();
            try {
                using HttpRequestMessage req = new(HttpMethod.Get, uri);
                using HttpResponseMessage resp = await healthHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                sw.Stop();
                int code = (int)resp.StatusCode;
                DomainHealthKind kind = code >= 500 ? DomainHealthKind.ServerError : DomainHealthKind.Ok;
                return new DomainHealth(kind, code, sw.Elapsed, null);
            } catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
                sw.Stop();
                return new DomainHealth(DomainHealthKind.Timeout, null, sw.Elapsed, null);
            } catch (HttpRequestException ex) {
                sw.Stop();
                // Walk the inner exception chain to classify. Order matters: the TLS check has to
                // come before the generic SocketException check because an AuthenticationException
                // wraps over a SocketException in some scenarios.
                Exception? cur = ex;
                while (cur is not null) {
                    if (cur is AuthenticationException) {
                        return new DomainHealth(DomainHealthKind.Tls, null, sw.Elapsed, cur.Message);
                    }
                    if (cur is SocketException sock) {
                        if (sock.SocketErrorCode == SocketError.HostNotFound) {
                            return new DomainHealth(DomainHealthKind.Dns, null, sw.Elapsed, sock.Message);
                        }
                        return new DomainHealth(DomainHealthKind.Connect, null, sw.Elapsed, sock.Message);
                    }
                    cur = cur.InnerException;
                }
                return new DomainHealth(DomainHealthKind.Other, null, sw.Elapsed, ex.Message);
            } catch (Exception ex) {
                sw.Stop();
                return new DomainHealth(DomainHealthKind.Other, null, sw.Elapsed, ex.Message);
            }
        }

        // Background health monitor. Re-probes every guild's redirect domain on a fixed cadence
        // and posts to the guild's log channel only on transitions (OK -> down, down -> OK) so
        // operators are not spammed with a "still down" message every five minutes. Debug guilds
        // get an Info log on every probe so they can watch the loop tick. Guilds without a domain
        // configured are skipped silently - there is nothing to probe.
        //
        // The cadence is deliberately not configurable yet: 5 minutes is short enough that an
        // outage is noticed quickly without piling load onto a flaky LB.
        private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(5);

        // Last known healthy/unhealthy state per guild, used purely for transition detection.
        // null means "never probed yet"; on the first tick we record state without posting,
        // because the bot just restarted and the user already saw the restart notice.
        private static readonly ConcurrentDictionary<ulong, bool> lastKnownHealthy = new();

        // Set by /invite create when it falls back to discord.gg because the per-guild domain
        // was down. The next successful health check clears it and posts a recovery notice that
        // explicitly mentions the failed invite was served via fallback. Without this flag, a
        // brief outage during a single /invite create would never get an "all clear" because
        // the periodic loop would see the same down->down transition.
        internal static readonly ConcurrentDictionary<ulong, bool> sawFallbackUse = new();

        private static async Task HealthCheckPeriodic(CancellationToken token = default) {
            using PeriodicTimer timer = new(HealthCheckInterval);
            // Tick once immediately so the first state snapshot lands without a 5-minute delay.
            await ProbeAllGuildsAsync(token);
            while (true) {
                try {
                    if (!await timer.WaitForNextTickAsync(token)) { return; }
                } catch (OperationCanceledException) { return; }
                try { await ProbeAllGuildsAsync(token); }
                catch (Exception x) { Log.Error("health", "Health check loop threw", x); }
            }
        }

        private static async Task ProbeAllGuildsAsync(CancellationToken token) {
            foreach (GuildContext ctx in guilds.Values) {
                if (token.IsCancellationRequested) { return; }
                if (!ctx.IsConfigured) { continue; }
                if (string.IsNullOrEmpty(ctx.Domain)) { continue; }

                DomainHealth health;
                try { health = await ProbeDomainAsync(ctx.Domain, token); }
                catch (OperationCanceledException) { return; }
                catch (Exception x) {
                    Log.Error($"health/{ctx.GuildId}", $"Probe of {ctx.Domain} threw", x);
                    continue;
                }

                // Debug guilds: log every probe regardless of state, so the loop is observable.
                if (ctx.Debug) {
                    await DebugLog(ctx, $"Periodic health check: {health.Format(ctx.Domain)}");
                }

                bool nowHealthy = health.Healthy;
                bool firstSeen = !lastKnownHealthy.TryGetValue(ctx.GuildId, out bool wasHealthy);
                lastKnownHealthy[ctx.GuildId] = nowHealthy;

                // First probe ever for this guild: record state silently. We do not want a
                // restart-time "OK" or "DOWN" post on top of the existing restart notice, and
                // the down case will get re-announced on the next tick if it persists.
                if (firstSeen) { continue; }

                if (wasHealthy == nowHealthy && !sawFallbackUse.ContainsKey(ctx.GuildId)) {
                    // Steady state and nobody hit the fallback in the meantime; nothing to say.
                    continue;
                }

                SocketTextChannel? ch = ChannelFor(ctx);
                if (ch is null) { continue; }
                try {
                    if (!nowHealthy) {
                        await ch.SendMessageAsync($"\u26a0\ufe0f Redirect domain `{ctx.Domain}` is unreachable. New invites will be served via the fallback (`{fallbackDomain}`) until it recovers.\n{health.Format(ctx.Domain)}");
                    } else if (sawFallbackUse.TryRemove(ctx.GuildId, out _)) {
                        // Recovery after a fallback was actually used during the outage.
                        await ch.SendMessageAsync($"\u2705 Redirect domain `{ctx.Domain}` is back up. ({health.Format(ctx.Domain)})\nNote: at least one invite was served via the fallback (`{fallbackDomain}`) while it was down; those links remain valid.");
                    } else {
                        await ch.SendMessageAsync($"\u2705 Redirect domain `{ctx.Domain}` is back up. ({health.Format(ctx.Domain)})");
                    }
                } catch (Exception x) {
                    Log.Warn($"health/{ctx.GuildId}", "Failed to post health transition notice", x);
                }
            }
        }
    }
}
