namespace InviteBot {

    public partial class InviteBot {

        // Pure decision: given the per-guild redirect domain, the result of probing it, and the
        // process-wide fallback, what domain should /invite create actually embed in the URL?
        //
        // Extracted for unit-testability. The caller (HandleCreate) still owns the side effects:
        // logging, posting the warning to the guild channel, and setting sawFallbackUse. This
        // helper just makes the choice.
        internal static class FallbackRouting {

            internal sealed record RoutingDecision(string EffectiveDomain, bool UsedFallback);

            // Rules:
            //   - If the per-guild domain is missing, /invite create has already refused upstream;
            //     this helper still returns a sensible answer (fallback) rather than throwing, so
            //     callers in tests can exercise the edge case without setting up guild state.
            //   - If the probe says healthy, use the per-guild domain.
            //   - If the probe says unhealthy and a fallback is configured, use the fallback.
            //   - If the probe says unhealthy and no fallback is configured (empty string / null),
            //     stick with the per-guild domain. A broken link the operator can diagnose is
            //     better than a silently-different URL with no fallback target to point at.
            internal static RoutingDecision Decide(string? guildDomain, DomainHealth probe, string? fallbackDomain) {
                if (string.IsNullOrEmpty(guildDomain)) {
                    return new RoutingDecision(fallbackDomain ?? string.Empty, true);
                }
                if (probe.Healthy) {
                    return new RoutingDecision(guildDomain, false);
                }
                if (string.IsNullOrEmpty(fallbackDomain)) {
                    return new RoutingDecision(guildDomain, false);
                }
                return new RoutingDecision(fallbackDomain, true);
            }
        }
    }
}
