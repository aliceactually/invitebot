using Xunit;

namespace InviteBot.Tests {

    // Tests for Domain.cs - per-guild redirect-domain validation/normalisation.
    //
    // This helper is the front line of defence for a value that goes straight into a
    // public URL (https://{domain}/{inviteId}). Every variant a real admin might paste in
    // - with scheme, with trailing slash, in mixed case, with a space at the end - must
    // either normalise cleanly or fail loudly. These tests pin down both halves.
    public class DomainTests {

        // ---- Acceptance and normalisation ----

        [Theory]
        [InlineData("example.com",                 "example.com")]
        [InlineData("EXAMPLE.com",                 "example.com")]   // lowercased
        [InlineData("  example.com  ",             "example.com")]   // trimmed
        [InlineData("https://example.com",         "example.com")]   // scheme stripped
        [InlineData("HTTPS://Example.COM",         "example.com")]   // mixed case, scheme
        [InlineData("http://example.com",          "example.com")]
        [InlineData("example.com/",                "example.com")]   // trailing slash
        [InlineData("https://example.com/",        "example.com")]
        [InlineData("alicepalace.net",             "alicepalace.net")]
        [InlineData("a.b",                         "a.b")]           // minimum viable
        [InlineData("sub.domain.example.co.uk",    "sub.domain.example.co.uk")]
        [InlineData("xn--bcher-kva.example",       "xn--bcher-kva.example")] // punycode
        public void TryNormaliseDomain_AcceptsAndNormalises(string input, string expected) {
            Assert.True(InviteBot.TryNormaliseDomain(input, out string normalised, out string? error));
            Assert.Equal(expected, normalised);
            Assert.Null(error);
        }

        // ---- Rejection ----

        [Theory]
        [InlineData(null,                          "required")]
        [InlineData("",                            "required")]
        [InlineData("   ",                         "required")]
        [InlineData("ab",                          "too short")]
        [InlineData("nodot",                       "dot")]                 // missing TLD
        [InlineData(".example.com",                "start or end with a dot")]
        [InlineData("example.com.",                "start or end with a dot")]
        [InlineData("example..com",                "consecutive dots")]
        [InlineData("ftp://example.com",           "http:// and https://")] // unsupported scheme
        [InlineData("example.com/path",            "path")]
        [InlineData("example.com?query",           "query or fragment")]
        [InlineData("example.com#frag",            "query or fragment")]
        [InlineData("exa mple.com",                "spaces")]
        [InlineData("-leading.example.com",        "start or end with a hyphen")]
        [InlineData("trailing-.example.com",       "start or end with a hyphen")]
        [InlineData("under_score.example.com",     "unsupported character")]
        [InlineData("emoji\U0001F600.example.com", "unsupported character")]
        public void TryNormaliseDomain_RejectsBadInputs(string? input, string expectedErrorFragment) {
            Assert.False(InviteBot.TryNormaliseDomain(input, out string normalised, out string? error));
            Assert.Equal("", normalised);
            Assert.NotNull(error);
            Assert.Contains(expectedErrorFragment, error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryNormaliseDomain_RejectsLabelLongerThan63() {
            string oversizedLabel = new string('a', 64);
            string input = $"{oversizedLabel}.com";
            Assert.False(InviteBot.TryNormaliseDomain(input, out _, out string? error));
            Assert.NotNull(error);
            Assert.Contains("1 to 63", error);
        }

        [Fact]
        public void TryNormaliseDomain_RejectsTotalLongerThan253() {
            // Build a long-but-valid-looking domain past the DNS hard cap.
            string label = new string('a', 50); // 50 chars
            string input = string.Join('.', label, label, label, label, label, label) + ".example"; // ~6*51 + 8 = 314
            Assert.False(InviteBot.TryNormaliseDomain(input, out _, out string? error));
            Assert.NotNull(error);
            Assert.Contains("too long", error);
        }
    }
}
