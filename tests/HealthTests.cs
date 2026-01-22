using Xunit;

namespace InviteBot.Tests {
    // The live probe (ProbeDomainAsync) talks to the network and is intentionally not unit-
    // tested here - that is integration territory. What we *can* lock down is the pure output
    // formatter, because every variant of it is part of the user-facing surface that admins
    // read in /invite admin status and /invite admin domain. If somebody accidentally drops
    // the latency or the status code from the OK line, these tests will catch it.
    public class HealthTests {

        [Fact]
        public void Format_Ok_IncludesStatusCodeAndLatencyAndDomain() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Ok, 200, TimeSpan.FromMilliseconds(123), null);
            string s = h.Format("alicepalace.net");
            Assert.Contains("OK", s);
            Assert.Contains("200", s);
            Assert.Contains("123", s);
            Assert.Contains("alicepalace.net", s);
        }

        [Fact]
        public void Format_ServerError_MentionsBackend() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.ServerError, 503, TimeSpan.FromMilliseconds(45), null);
            string s = h.Format("invite.example.com");
            Assert.Contains("503", s);
            Assert.Contains("invite.example.com", s);
            Assert.Contains("backend", s, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Format_Dns_MentionsDns() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Dns, null, TimeSpan.Zero, "no such host");
            string s = h.Format("nope.example.com");
            Assert.Contains("DNS", s, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nope.example.com", s);
        }

        [Fact]
        public void Format_Tls_MentionsTls() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Tls, null, TimeSpan.FromMilliseconds(800), "cert expired");
            string s = h.Format("expired.example.com");
            Assert.Contains("TLS", s, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cert expired", s);
        }

        [Fact]
        public void Format_Connect_MentionsConnect() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Connect, null, TimeSpan.FromMilliseconds(50), "connection refused");
            string s = h.Format("down.example.com");
            Assert.Contains("connect", s, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("down.example.com", s);
        }

        [Fact]
        public void Format_Timeout_IncludesElapsedMs() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Timeout, null, TimeSpan.FromMilliseconds(8000), null);
            string s = h.Format("slow.example.com");
            Assert.Contains("8000", s);
            Assert.Contains("slow.example.com", s);
        }

        [Fact]
        public void Format_Other_FallsBackToDetail() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Other, null, TimeSpan.Zero, "weird thing happened");
            string s = h.Format("odd.example.com");
            Assert.Contains("odd.example.com", s);
            Assert.Contains("weird thing happened", s);
        }

        [Fact]
        public void Format_Other_WithoutDetail_StillReadable() {
            InviteBot.DomainHealth h = new(InviteBot.DomainHealthKind.Other, null, TimeSpan.Zero, null);
            string s = h.Format("odd.example.com");
            Assert.Contains("odd.example.com", s);
            Assert.False(string.IsNullOrWhiteSpace(s));
        }
    }
}
