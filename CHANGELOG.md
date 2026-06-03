# Changelog

## v1.0.2 — 03 Jun 2026

Bug-fix and hardening release. No new user-facing commands; existing
behaviour is made more robust and a couple of rendering bugs are fixed.

### Fixed
- **Overlay covered the entire QR.** Overlays were composited at native size
  over a same-sized canvas, blanketing the whole code and destroying
  scannability. Overlays are now scaled to a centred badge (≤ 30% of the
  QR's short edge, aspect-ratio preserved, never upscaled) before
  compositing, so only the centre is obscured — comfortably inside the
  level-H error-correction budget. Sizing maths lives in the new pure
  `OverlayLayout` helper with xUnit coverage.
- **Font failures crashed every `/invite create`.** A missing caption font
  on the host (e.g. no fontconfig / no `mscorefonts` on Linux) threw out of
  the render and left the user staring at "Rendering QR code…" forever. The
  font is now probed once at startup and the bot refuses to start with an
  actionable message if it cannot be loaded, and the slash-command handler
  has a top-level safety net that surfaces any unhandled error to the user
  instead of hanging the interaction.
- **Stale dev-guild commands lingered.** Changing or clearing `devGuild`
  left the previous guild's slash commands registered forever. Startup now
  records the last dev guild in a new `bot_state` table and clears the
  stale guild-scoped command set when it changes.
- **Invite caption date/time format.** The baked-in expiry caption now
  renders as `07 MAY 2026 0616 UTC` (uppercase invariant month, 24-hour
  time with no separator) instead of a locale-dependent short date/time.

### Changed
- Overlay uploads are now gated by **resolution, not file size**. The
  256–4096 px-per-side limit is unchanged and remains the real constraint;
  any overlay within it is accepted regardless of byte size. The byte cap
  is raised from **4 MB to 32 MB** and demoted to a backstop against
  pathological uploads. The `/invite admin overlay` help text now derives
  its quoted limits from the constants so it can no longer drift.
- `devGuild` is now validated at config load: an implausibly small non-zero
  id (a likely typo) is rejected at startup rather than silently doing
  nothing, and the "bot is not a member of that guild" warning now spells
  out how to fix it.
- Dependencies bumped: `Magick.NET-Q8-AnyCPU` 14.13.0 → 14.13.1 and
  `Microsoft.Data.Sqlite` 10.0.7 → 10.0.8 (routine patch updates).

### Schema
- DB schema bumped to **v5**. New `bot_state` key/value table for bot-wide
  bookkeeping (currently the last-registered `devGuild`). Migration is
  additive; v1.0.2 is a drop-in upgrade from v1.0.1 with no manual steps.

## v1.0.1 — 07 May 2026

Adds the introduction system: a single `/invite admin introduce` command
that DMs a role-aware tour of the bot to a user, role, or `@everyone`, and
an auto-welcome path that does the same thing for new members on join.

### Added

#### Introductions
- `/invite admin introduce target [iamsure]` slash subcommand. Resolves
  `target` (a Discord Mentionable: user, role, or `@everyone`) into a
  deduplicated recipient list, then DMs each recipient the role-appropriate
  introduction. Bots and the bot's own user are skipped. Closed-DM
  recipients and other failures are tallied in the ephemeral confirmation
  rather than aborting the run.
- 10% safety threshold: if the resolved recipient set covers more than 10%
  of the guild's members **and** is at least 5 recipients, the command
  refuses without `iamsure:true` and reports how many DMs would be sent.
  The 5-recipient floor keeps the threshold from getting in the way in
  small test guilds.
- Introduction copy is role-aware: regular users see the `/invite create`
  tour, admins additionally see the full `/invite admin …` reference, and
  members with neither role get a friendly note about what they could do
  if granted access. Long compositions are split across multiple DMs to
  stay within Discord's 2000-char per-message cap.

#### Auto-welcome
- New `discord.UserJoined` handler auto-DMs the introduction to every new
  member of every configured guild, gated on a per-guild
  `WelcomeNewMembers` toggle (default **enabled**).
- `/invite admin welcome value:bool` slash subcommand toggles the
  per-guild auto-welcome behaviour. Idempotent ("already enabled/disabled"
  short-circuits) and audit-logged to the configured log channel.
- Auto-welcome is suppressed for unconfigured guilds (no admin role / log
  channel), bots, the bot's own user, and members whose DMs are closed
  (the latter is logged at `debug` only — surfacing it publicly would leak
  the new member's DM-privacy setting to anyone with channel-read).

#### Schema
- DB schema bumped to **v4**. New `WelcomeNewMembers INTEGER NOT NULL
  DEFAULT 1` column added to `guild_settings` (v3), plus three nullable
  per-guild override columns `DefaultDuration`, `DefaultUses`, and
  `ForeverDuration` (v4). Existing rows pick up the defaults / `NULL` on
  migration; v1.0.1 is a drop-in upgrade from v1.0.0 with no manual steps.
- `/invite admin export` schema bumped to **v4**: now includes
  `welcomeNewMembers`, `defaultDuration`, `defaultUses`, and
  `foreverDuration`. Older v1/v2/v3 backups continue to import; the
  missing fields restore as `null` (i.e. inherit the bot-wide defaults)
  for the override trio, and `welcomeNewMembers` defaults to `true` on
  restore so older backups behave like a fresh install rather than
  coming back disabled.

#### Operational
- The `GuildMembers` privileged gateway intent is now required (for
  `SocketRole.Members` enumeration in `/invite admin introduce` and for
  the `UserJoined` event). Enable it in the Discord Developer Portal under
  **Bot → Privileged Gateway Intents → Server Members Intent**.
- `/invite admin status` now reports the auto-welcome state alongside the
  rest of the per-guild configuration, plus the effective
  `defaultDuration`, `defaultUses`, and `foreverDuration` and whether
  each one is per-server or inherited.

#### Per-server defaults
- `/invite admin defaultduration value:<minutes>`,
  `/invite admin defaultuses value:<count>`, and
  `/invite admin foreverduration value:<minutes>` slash subcommands let
  each guild override the bot-wide values from `config.json`. Pass
  `value:-1` to clear the override and re-inherit. Per-server
  `defaultDuration` is validated against the effective `foreverDuration`
  the same way `/invite create duration` is, and lowering
  `foreverDuration` below the current `defaultDuration` is refused with a
  message rather than silently truncating.

#### Tests
- New pure helpers `Introduction` (copy generation + chunk packing) and
  `IntroductionTargets` (recipient deduplication + threshold planning),
  with 34 new xUnit tests pinning content invariants and threshold
  semantics. The admin-section drift detector test now also fails if a
  newly-added `/invite admin …` subcommand is not mentioned in the
  introduction copy.

### Changed
- `Program.cs` startup now requests `GatewayIntents.GuildMembers` and sets
  `AlwaysDownloadUsers = true` so role member lists are populated for
  introduction fan-out.
- The admin section of the introduction documents itself: it lists every
  current `/invite admin …` subcommand including `introduce` and
  `welcome`, so the auto-welcome DM new members receive includes the
  off-switch their admins can use.

## v1.0.0 — 21 Jan 2026

First tagged release. Multi-guild, self-onboarding, with per-guild redirect
domains, live health monitoring, and a transparent fallback so the QR at
the door always resolves.

**Breaking change vs. the pre-tag prototype.** This release reshapes the
database schema and removes per-guild keys from `config.json`. There is no
migration path from the prototype; delete any existing `database.sqlite3`
before upgrading.

### Added

#### Multi-guild
- Multi-guild support. The bot tracks per-guild settings in a new
  `guild_settings` table and creates per-guild invite tables on demand.
- `/invite admin configure` slash subcommand for bootstrapping a server
  (channel, admin role, optional user role). Gated by Discord's **Manage
  Server** permission since the admin role is unset on first run.
- `JoinedGuild` / `LeftGuild` handlers so newly-invited servers self-onboard
  and removed servers drop from the in-memory map without a restart.
- Departed guilds are reconciled at startup and on leave so on-disk state
  stays bounded.
- `pause` and `debug` flags are now per-guild and persisted in the DB.
- Optional `devGuild` config key for instant slash-command iteration during
  development (global registration can take up to ~1 hour to propagate).

#### Per-guild redirect domain + fallback
- `/invite admin domain` sets a per-guild redirect domain used in generated
  invite URLs. `/invite create` refuses until a domain is set.
- Process-wide `fallbackDomain` config key (default `discord.gg`) used when
  a guild's redirect domain is unreachable. Invites continue to mint and
  resolve transparently via the fallback so the guest at the door is never
  blocked by an LB outage.
- `/invite create` probes the per-guild domain over HTTPS before minting and
  posts a notice in the log channel when it falls back.

#### Live health monitoring
- `/invite admin status` performs a live HTTPS probe of the configured
  domain and reports its result alongside whether the fallback is currently
  armed (probe just failed) or has actually been used to serve invites
  since the last recovery.
- Background monitor re-probes every 5 minutes and posts to each guild's
  log channel only when the domain transitions between up and down (every
  tick when `debug` is on).
- Health-monitor state is cleared automatically when a guild leaves, when
  the domain is unset, when it is changed, or when an import replaces it.

#### Per-guild overlays
- `/invite admin overlay` uploads or replaces the per-guild overlay PNG via
  Discord attachment — no SCP required.
- Overlays are validated (PNG, 256–4096 px per side, ≤ 4 MB) and normalised
  to 300 DPI on upload so render-time print-size maths is unambiguous.

#### Print sizing
- `/invite admin print` sets the default long-edge print size for the
  guild (e.g. `90mm`, `9cm`, `3.5in`); pass `clear` to remove it.
- `/invite create` accepts a `size` option that overrides the per-guild
  default for a single invite.
- Pure `PrintScaling` helper computes target pixel dimensions from a
  millimetre long-edge at 300 DPI, preserves aspect ratio across
  orientations, and skips the resample when the overlay is already at
  target size.

#### Backup / restore
- `/invite admin export` emits a JSON backup of the guild's settings plus
  its overlay PNG in a single ephemeral response.
- `/invite admin import` restores a backup produced by `/invite admin
  export`. The JSON is required; the overlay PNG is optional. Cross-guild
  restores (e.g. dev → prod) are permitted and surfaced in the response.

#### Caption rendering
- Caption sizing is now derived from the overlay's actual short edge, with
  shrink-to-fit on long expiry strings and a clamped min/max so it works
  across any overlay size or orientation.
- New `captionFont` config key. Defaults to `DejaVu Sans Mono` on Linux and
  `Courier New` elsewhere; override if your platform has a different
  monospace face installed.

#### Operational
- Restart notice posted to each server's log channel includes the build's
  version, git commit, and compilation timestamp for traceability.
- Permission preflight in `/invite admin configure` confirms the bot has
  View Channel / Send Messages / Attach Files / Embed Links in the chosen
  log channel before saving, and warns if Create Instant Invite is missing.
- `/invite create` shows progress updates in the ephemeral response while
  it probes the domain, asks Discord for an invite, and renders the QR.
- Multi-platform self-contained publishing: `pwsh ./publish-all.ps1` (or a
  bare `dotnet publish -c Release`) produces single-file binaries for
  win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64.
- Sample systemd unit and logrotate config under `deploy/`, with
  documented SELinux relabel steps for RHEL-family distros.

#### Tests + docs
- xUnit test project at `tests/invitebot.tests.csproj` covering the pure
  helpers (units parsing, invite-code generation, domain normalisation,
  caption layout, fallback routing, print scaling, health formatting).
- `README.md`, `CHANGELOG.md`, `.github/copilot-instructions.md`.

### Changed
- Slash command is now registered **globally** rather than to a single guild.
- `config.json` no longer contains `adminRole`, `userRole`, or
  `discord.channel`. Use `/invite admin configure` instead.
- `Cleanup` and `Purge` iterate every configured, non-paused guild.
- Failures sending to a configured log channel no longer break the calling
  flow; they are swallowed and the user-facing response still completes.

### Removed
- Backwards compatibility with the previous single-guild DB schema and
  config layout.
