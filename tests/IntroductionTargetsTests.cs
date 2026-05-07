using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace InviteBot.Tests {
    // Pure tests for the recipient-planning + safety-threshold rules. The actual role/user
    // resolution happens in Discord land; this helper just answers "given these ids and this
    // member count, do we need iamsure:true?".
    public class IntroductionTargetsTests {

        // 1) Empty input yields an empty plan with no confirmation required. Defensive
        //    against a future call site that resolves a role to zero members.
        [Fact]
        public void Plan_EmptyRecipients_DoesNotRequireConfirmation() {
            var plan = InviteBot.IntroductionTargets.Plan(System.Array.Empty<ulong>(), guildMemberCount: 100);
            Assert.Empty(plan.RecipientIds);
            Assert.False(plan.RequiresConfirmation);
        }

        // 2) Null recipients are handled defensively too - same outcome as empty.
        [Fact]
        public void Plan_NullRecipients_DoesNotRequireConfirmation() {
            var plan = InviteBot.IntroductionTargets.Plan(null, guildMemberCount: 100);
            Assert.Empty(plan.RecipientIds);
            Assert.False(plan.RequiresConfirmation);
        }

        // 3) Duplicates are deduplicated. A user who is also a member of a separately-targeted
        //    role must only appear once in the recipient list.
        [Fact]
        public void Plan_Deduplicates() {
            ulong[] ids = { 1, 2, 3, 2, 1, 4 };
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 100);
            Assert.Equal(new ulong[] { 1, 2, 3, 4 }, plan.RecipientIds);
        }

        // 4) A single-user introduction in a small guild does not trip confirmation, even
        //    when the recipient is technically more than 10% of the membership. The floor
        //    (MinRecipientsForConfirmation) protects this case.
        [Fact]
        public void Plan_SingleUserInSmallGuild_DoesNotRequireConfirmation() {
            // 1 of 5 = 20%, well over 10%, but below the floor of 5 recipients.
            var plan = InviteBot.IntroductionTargets.Plan(new ulong[] { 42 }, guildMemberCount: 5);
            Assert.False(plan.RequiresConfirmation);
        }

        // 5) Hitting exactly the floor with exactly the threshold does NOT require
        //    confirmation - the rule is "more than 10%", not "at least 10%". 5/50 = 10%
        //    exactly, floor met, but fraction is not strictly over the threshold.
        [Fact]
        public void Plan_ExactlyThreshold_DoesNotRequireConfirmation() {
            ulong[] ids = Enumerable.Range(1, 5).Select(i => (ulong)i).ToArray();
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 50);
            Assert.Equal(0.10, plan.Fraction, 6);
            Assert.False(plan.RequiresConfirmation);
        }

        // 6) Just over the threshold (with the floor met) requires confirmation. 6/50 = 12%.
        [Fact]
        public void Plan_JustOverThreshold_RequiresConfirmation() {
            ulong[] ids = Enumerable.Range(1, 6).Select(i => (ulong)i).ToArray();
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 50);
            Assert.True(plan.Fraction > 0.10);
            Assert.True(plan.RequiresConfirmation);
        }

        // 7) @everyone (every member is a recipient) trivially trips both rules.
        [Fact]
        public void Plan_Everyone_RequiresConfirmation() {
            ulong[] ids = Enumerable.Range(1, 200).Select(i => (ulong)i).ToArray();
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 200);
            Assert.Equal(1.0, plan.Fraction, 6);
            Assert.True(plan.RequiresConfirmation);
        }

        // 8) Defensive: zero or negative member count does not throw and never trips
        //    confirmation by way of a divide-by-zero NaN comparison.
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Plan_NonPositiveMemberCount_DoesNotThrowOrConfirm(int memberCount) {
            var plan = InviteBot.IntroductionTargets.Plan(new ulong[] { 1, 2, 3 }, guildMemberCount: memberCount);
            Assert.Equal(0.0, plan.Fraction);
            Assert.False(plan.RequiresConfirmation);
        }

        // 9) Plan reports the deduplicated recipient count and the supplied member count
        //    verbatim, so call sites can format "DMing N of M (X%)" without re-deriving
        //    anything.
        [Fact]
        public void Plan_ReportsCountsForCallSiteFormatting() {
            ulong[] ids = { 1, 2, 3, 3, 4 };
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 80);
            Assert.Equal(4, plan.RecipientIds.Count);
            Assert.Equal(80, plan.GuildMemberCount);
            Assert.Equal(4.0 / 80.0, plan.Fraction, 6);
        }

        // 10) Floor-met but fraction-under does NOT require confirmation. 5 recipients in a
        //     1000-member guild is 0.5% - well under the threshold - and must not trip.
        [Fact]
        public void Plan_FloorMetButFractionUnder_DoesNotRequireConfirmation() {
            ulong[] ids = Enumerable.Range(1, 5).Select(i => (ulong)i).ToArray();
            var plan = InviteBot.IntroductionTargets.Plan(ids, guildMemberCount: 1000);
            Assert.False(plan.RequiresConfirmation);
        }
    }
}
