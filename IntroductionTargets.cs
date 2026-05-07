namespace InviteBot {

    public partial class InviteBot {

        // Pure decision: given a resolved list of recipient ids and the guild's member count,
        // does this introduce-blast cross the safety threshold that requires the admin to
        // re-run with iamsure:true?
        //
        // Extracted for unit-testability. The caller (HandleIntroduce in phase 2) still owns
        // resolving the user/role/@everyone target into ids and looking up the guild's member
        // count - this helper just answers the safety question on a primitive input so we can
        // pin the threshold rules in tests without spinning up Discord state.
        internal static class IntroductionTargets {

            // The blast threshold. A target whose recipient set covers more than this fraction
            // of the guild's members requires explicit confirmation. 10% is a guess at the
            // sweet spot between "harmless single-user introduction" and "spammed every member
            // of a 500-person server"; named here so tuning is a one-line change.
            internal const double ConfirmationFraction = 0.10;

            // Floor on the absolute recipient count before the fraction rule kicks in. Without
            // it, a 5-person test guild would trip confirmation on a 1-recipient introduce
            // (1/5 = 20%), which would feel obstructive. With a floor of 5, the admin of a
            // small guild can introduce specific members without ceremony but still has to
            // confirm an @everyone broadcast (which trivially exceeds the floor).
            internal const int MinRecipientsForConfirmation = 5;

            // Result of the planning step. Fraction is exposed so the call site can format a
            // helpful "this will DM N of M users (X%)" message in the iamsure-required path.
            internal sealed record IntroductionPlan(
                System.Collections.Generic.IReadOnlyList<ulong> RecipientIds,
                int GuildMemberCount,
                double Fraction,
                bool RequiresConfirmation);

            // Deduplicates the recipient ids (overlapping role membership and a separately
            // selected user can produce the same id twice), computes the coverage fraction,
            // and applies the threshold + floor rules. Defensive on degenerate inputs:
            //   - Null or empty recipientIds -> empty plan, no confirmation required.
            //   - Non-positive guildMemberCount -> fraction reported as 0; confirmation still
            //     gated only by the floor, so a zero-population guild can never trip it.
            internal static IntroductionPlan Plan(
                System.Collections.Generic.IEnumerable<ulong>? recipientIds,
                int guildMemberCount) {
                System.Collections.Generic.List<ulong> unique;
                if (recipientIds is null) {
                    unique = new System.Collections.Generic.List<ulong>();
                } else {
                    System.Collections.Generic.HashSet<ulong> seen = new();
                    unique = new System.Collections.Generic.List<ulong>();
                    foreach (ulong id in recipientIds) {
                        if (seen.Add(id)) { unique.Add(id); }
                    }
                }

                double fraction = guildMemberCount > 0 ? (double)unique.Count / guildMemberCount : 0.0;
                bool requiresConfirmation =
                    unique.Count >= MinRecipientsForConfirmation &&
                    fraction > ConfirmationFraction;

                return new IntroductionPlan(unique, guildMemberCount, fraction, requiresConfirmation);
            }
        }
    }
}
