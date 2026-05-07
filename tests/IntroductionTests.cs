using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace InviteBot.Tests {
    // Pure-content tests for the introduction message builder. The actual DM delivery is
    // exercised by hand (open a server, run /invite admin introduce against yourself); these
    // tests pin down the invariants that a regression would otherwise quietly break.
    public class IntroductionTests {

        // 1) Header is non-empty and identifies the bot. Guards against a future "let's make
        //    this configurable" refactor accidentally dropping the bot's name from the intro.
        [Fact]
        public void Header_MentionsBotByName() {
            string header = InviteBot.Introduction.BuildHeader();
            Assert.False(string.IsNullOrWhiteSpace(header));
            Assert.Contains("InviteBot", header);
        }

        // 2) User section explicitly mentions /invite create. This is the one command a
        //    non-admin can run, so if the section ever stops mentioning it the introduction
        //    has stopped being useful.
        [Fact]
        public void UserSection_MentionsInviteCreate() {
            Assert.Contains("/invite create", InviteBot.Introduction.BuildUserSection());
        }

        // 3) Admin section mentions every current admin subcommand. If a new subcommand is
        //    added to BuildSlashCommand() in Commands.cs, this test fails until the intro
        //    is updated to match - which is the whole point of having it here.
        [Theory]
        [InlineData("/invite admin configure")]
        [InlineData("/invite admin domain")]
        [InlineData("/invite admin overlay")]
        [InlineData("/invite admin print")]
        [InlineData("/invite admin status")]
        [InlineData("/invite admin pause")]
        [InlineData("/invite admin debug")]
        [InlineData("/invite admin purge")]
        [InlineData("/invite admin export")]
        [InlineData("/invite admin import")]
        [InlineData("/invite admin introduce")]
        [InlineData("/invite admin welcome")]
        public void AdminSection_MentionsEverySubcommand(string subcommand) {
            Assert.Contains(subcommand, InviteBot.Introduction.BuildAdminSection());
        }

        // 4) No-access note exists and is friendly (does not start with "Sorry" or "Error").
        [Fact]
        public void NoAccessNote_IsFriendly() {
            string note = InviteBot.Introduction.BuildNoAccessNote();
            Assert.False(string.IsNullOrWhiteSpace(note));
            Assert.False(note.StartsWith("Sorry"));
            Assert.False(note.StartsWith("Error"));
        }

        // 5) Build for a regular user emits the header + the user section, but NOT the admin
        //    section or the no-access note.
        [Fact]
        public void Build_UserOnly_HasHeaderAndUserSectionOnly() {
            IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser: true, isAdmin: false);
            string joined = string.Join("\n", chunks);
            Assert.Contains("InviteBot", joined);
            Assert.Contains("/invite create", joined);
            Assert.DoesNotContain("Administrator commands", joined);
            Assert.DoesNotContain("don't currently have access", joined);
        }

        // 6) Build for an admin emits the header + user section + admin section.
        //    (Admins can run /invite create too, so the user section is always included for
        //    them - they need the basics before the admin tour.)
        [Fact]
        public void Build_Admin_HasUserAndAdminSections() {
            IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser: true, isAdmin: true);
            string joined = string.Join("\n", chunks);
            Assert.Contains("/invite create", joined);
            Assert.Contains("/invite admin configure", joined);
            Assert.Contains("/invite admin introduce", joined);
        }

        // 7) Build for someone with neither role emits the header + the no-access note,
        //    and does not include either the user-section heading or the admin-section
        //    heading. (The note itself may mention /invite create as part of "if granted,
        //    you'll be able to..." - that's fine; what we're guarding against is presenting
        //    the full user tour to someone who cannot use it.)
        [Fact]
        public void Build_NoRoles_HasNoAccessNote() {
            IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser: false, isAdmin: false);
            string joined = string.Join("\n", chunks);
            Assert.Contains("InviteBot", joined);
            Assert.Contains("don't currently have access", joined);
            Assert.DoesNotContain("What you can do", joined);
            Assert.DoesNotContain("Administrator commands", joined);
        }

        // 8) Edge case: admin-only (no user role) still includes the admin section. In
        //    practice every admin is also a user, but the helper is general and the test
        //    pins that contract.
        [Fact]
        public void Build_AdminWithoutUserRole_StillHasAdminSection() {
            IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser: false, isAdmin: true);
            string joined = string.Join("\n", chunks);
            Assert.Contains("Administrator commands", joined);
            Assert.DoesNotContain("don't currently have access", joined);
        }

        // 9) Every emitted chunk fits within Discord's 2000-character DM limit, even for the
        //    biggest possible composition (admin + user). If a future copy expansion blows
        //    the budget on a single section, this fails and forces a restructure rather
        //    than discovering it at runtime when Discord rejects the DM.
        [Fact]
        public void Build_EveryChunkFitsDiscordLimit() {
            foreach ((bool isUser, bool isAdmin) in new[] { (true, false), (false, true), (true, true), (false, false) }) {
                IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser, isAdmin);
                Assert.NotEmpty(chunks);
                Assert.All(chunks, c => Assert.True(
                    c.Length <= InviteBot.Introduction.DiscordMessageMaxLength,
                    $"chunk for (isUser={isUser}, isAdmin={isAdmin}) was {c.Length} chars, limit is {InviteBot.Introduction.DiscordMessageMaxLength}"));
            }
        }

        // 10) Build always returns at least one chunk - never null, never empty list - so the
        //     call site can iterate without a null/empty guard.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Build_AlwaysReturnsAtLeastOneChunk(bool isUser, bool isAdmin) {
            IReadOnlyList<string> chunks = InviteBot.Introduction.Build(isUser, isAdmin);
            Assert.NotNull(chunks);
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        }
    }
}
