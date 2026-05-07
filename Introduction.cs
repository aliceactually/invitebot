namespace InviteBot {

    public partial class InviteBot {

        // Pure builder for the text that /invite admin introduce (and the auto-welcome on
        // GuildMemberAdded, in phase 3) sends to a recipient. Lives in its own file so the
        // copy can be unit-tested - both for content (does the admin section actually mention
        // /invite admin domain?) and for shape (does the assembled message fit in a Discord
        // DM, which is hard-capped at 2000 characters?).
        //
        // Pure on purpose: takes role flags as primitives, returns plain strings, has zero
        // Discord.Net dependencies. The call sites in Introduce.cs and Program.cs handle the
        // impure parts (resolving roles, opening DM channels, surviving closed-DM users).
        internal static class Introduction {

            // Discord's DM (and channel) message length cap. Build() guarantees every chunk it
            // returns is at or below this; tests pin that guarantee even at the largest
            // possible composition (admin + user sections together).
            internal const int DiscordMessageMaxLength = 2000;

            // Headline that appears at the top of every introduction, regardless of role.
            // Kept short so the role-specific sections that follow get most of the budget.
            internal static string BuildHeader() {
                return
                    "\ud83d\udc4b Hi! I'm **InviteBot**. I help this server hand out short-lived, " +
                    "trackable Discord invites with a printable QR-code overlay - useful for events, " +
                    "front doors, signage, or anywhere a guest needs to scan a code to join.";
            }

            // What the user role (or @everyone, when no user role is configured) can do.
            internal static string BuildUserSection() {
                return
                    "**What you can do**\n" +
                    "\n" +
                    "- `/invite create` - generates a fresh invite link and posts both the link and a " +
                    "QR-code image (with this server's overlay) that only you can see. The link expires " +
                    "automatically; if it has a use limit it stops working once that limit is reached.\n" +
                    "\n" +
                    "That's it for regular use. The bot's response is always ephemeral - only you see it - " +
                    "so feel free to experiment.";
            }

            // The full admin command tree. Mirrors what BuildSlashCommand() in Commands.cs
            // actually registers; if a subcommand is added or renamed there it must be
            // updated here too. Tests assert that every current subcommand name appears at
            // least once so a rename will fail loudly rather than silently drift.
            internal static string BuildAdminSection() {
                return
                    "**Administrator commands**\n" +
                    "\n" +
                    "You also have access to the `/invite admin \u2026` subcommand tree. All responses are " +
                    "ephemeral (only you see them).\n" +
                    "\n" +
                    "- `/invite admin configure channel adminrole [userrole]` - bootstraps the bot for this " +
                    "server. **Required first.** Omit `userrole` to let everyone use `/invite create`.\n" +
                    "- `/invite admin domain value` - sets the per-server redirect domain used in invite " +
                    "URLs (e.g. `invite.example.com`). Pass `clear` to unset. The bot probes the new domain " +
                    "over HTTPS immediately.\n" +
                    "- `/invite admin overlay image` - uploads (or replaces) the PNG composited behind the " +
                    "QR. 256\u20134096 px per side, \u2264 4 MB. Normalised to 300 DPI on upload.\n" +
                    "- `/invite admin print size` - sets the default long-edge print size for invite images " +
                    "(e.g. `90mm`, `9cm`, `3.5in`). Pass `clear` to remove the default.\n" +
                    "- `/invite admin status` - current configuration, plus a live HTTPS probe of the " +
                    "redirect domain and whether the fallback is currently armed.\n" +
                    "- `/invite admin pause value` - temporarily disables `/invite create` for this server.\n" +
                    "- `/invite admin debug value` - verbose logging for this server.\n" +
                    "- `/invite admin purge days` - manually purges invites older than N days (the cleanup " +
                    "loop runs automatically too).\n" +
                    "- `/invite admin export` - emits a JSON backup of this server's settings plus its " +
                    "overlay PNG.\n" +
                    "- `/invite admin import backup [overlay]` - restores a backup produced by `export`. " +
                    "Cross-guild restores are permitted.\n" +
                    "- `/invite admin introduce target [iamsure]` - sends this introduction to the chosen " +
                    "user, role, or `@everyone`. `iamsure:true` is required when the target covers more " +
                    "than 10% of the server's members.\n" +
                    "- `/invite admin welcome value` - toggles automatically DM'ing this introduction to " +
                    "new members when they join. Enabled by default.\n" +
                    "\n" +
                    "**Permissions.** `/invite admin configure` is gated by Discord's **Manage Server** " +
                    "permission (the admin role is unset on first run). Every other admin subcommand " +
                    "requires the configured admin role.";
            }

            // Sent to recipients who currently have neither the user role nor the admin role.
            // Most common case: a freshly-joined member of a server that has a user role
            // configured. The note is deliberately friendly rather than apologetic.
            internal static string BuildNoAccessNote() {
                return
                    "You don't currently have access to use my commands in this server. If an " +
                    "administrator grants you the user role, you'll be able to run `/invite create`.";
            }

            // Composes the appropriate sections for a recipient and returns one or more
            // ready-to-send messages. Splits across multiple DMs if (and only if) the
            // composed text would exceed Discord's per-message limit. Always returns at
            // least one chunk.
            internal static IReadOnlyList<string> Build(bool isUser, bool isAdmin) {
                System.Collections.Generic.List<string> sections = new() { BuildHeader() };
                if (isUser) {
                    sections.Add(BuildUserSection());
                }
                if (isAdmin) {
                    sections.Add(BuildAdminSection());
                }
                if (!isUser && !isAdmin) {
                    sections.Add(BuildNoAccessNote());
                }
                return Pack(sections);
            }

            // Joins sections into as few messages as possible while keeping each one within
            // DiscordMessageMaxLength. Sections are never split mid-way; if a single section
            // would on its own exceed the limit (which would be a copy bug, caught by tests)
            // it is emitted as its own oversized chunk and the caller will see Discord
            // reject it - far more debuggable than a silently truncated message.
            private static IReadOnlyList<string> Pack(System.Collections.Generic.IReadOnlyList<string> sections) {
                System.Collections.Generic.List<string> chunks = new();
                System.Text.StringBuilder current = new();
                foreach (string section in sections) {
                    // +2 accounts for the "\n\n" separator we would insert between sections.
                    int projected = current.Length == 0 ? section.Length : current.Length + 2 + section.Length;
                    if (current.Length > 0 && projected > DiscordMessageMaxLength) {
                        chunks.Add(current.ToString());
                        current.Clear();
                    }
                    if (current.Length > 0) { current.Append("\n\n"); }
                    current.Append(section);
                }
                if (current.Length > 0) { chunks.Add(current.ToString()); }
                return chunks;
            }
        }
    }
}
