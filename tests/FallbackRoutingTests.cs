using Xunit;

namespace InviteBot.Tests {
    // Routing decision tests. The helper itself is pure: given a probe result and a fallback,
    // pick a domain. The side effects (logging, posting to the channel, setting
    // sawFallbackUse) stay in HandleCreate and are not covered here.
    public class FallbackRoutingTests {

        private static InviteBot.DomainHealth Healthy() =>
            new(InviteBot.DomainHealthKind.Ok, 200, System.TimeSpan.FromMilliseconds(50), null);

        private static InviteBot.DomainHealth Unhealthy(InviteBot.DomainHealthKind kind = InviteBot.DomainHealthKind.Connect) =>
            new(kind, null, System.TimeSpan.FromMilliseconds(50), "boom");

        [Fact]
        public void Healthy_UsesGuildDomain() {
            var d = InviteBot.FallbackRouting.Decide("invite.example.com", Healthy(), "discord.gg");
            Assert.Equal("invite.example.com", d.EffectiveDomain);
            Assert.False(d.UsedFallback);
        }

        [Theory]
        [InlineData((int)InviteBot.DomainHealthKind.ServerError)]
        [InlineData((int)InviteBot.DomainHealthKind.Dns)]
        [InlineData((int)InviteBot.DomainHealthKind.Tls)]
        [InlineData((int)InviteBot.DomainHealthKind.Connect)]
        [InlineData((int)InviteBot.DomainHealthKind.Timeout)]
        [InlineData((int)InviteBot.DomainHealthKind.Other)]
        public void Unhealthy_UsesFallbackDomain(int kindValue) {
            // Parameter is int rather than DomainHealthKind because the enum is internal and
            // xUnit requires public test method signatures.
            var kind = (InviteBot.DomainHealthKind)kindValue;
            var d = InviteBot.FallbackRouting.Decide("invite.example.com", Unhealthy(kind), "discord.gg");
            Assert.Equal("discord.gg", d.EffectiveDomain);
            Assert.True(d.UsedFallback);
        }

        [Fact]
        public void Unhealthy_NoFallback_KeepsGuildDomain() {
            // No fallback configured: stick with the guild domain so a broken link is at least
            // diagnosable rather than silently swapped for an empty target.
            var d = InviteBot.FallbackRouting.Decide("invite.example.com", Unhealthy(), null);
            Assert.Equal("invite.example.com", d.EffectiveDomain);
            Assert.False(d.UsedFallback);
        }

        [Fact]
        public void Unhealthy_EmptyFallback_KeepsGuildDomain() {
            var d = InviteBot.FallbackRouting.Decide("invite.example.com", Unhealthy(), "");
            Assert.Equal("invite.example.com", d.EffectiveDomain);
            Assert.False(d.UsedFallback);
        }

        [Fact]
        public void NullGuildDomain_FallsBackImmediately() {
            // /invite create refuses upstream when the guild has no domain, but the helper
            // still returns a sensible answer rather than throwing.
            var d = InviteBot.FallbackRouting.Decide(null, Healthy(), "discord.gg");
            Assert.Equal("discord.gg", d.EffectiveDomain);
            Assert.True(d.UsedFallback);
        }
    }
}
