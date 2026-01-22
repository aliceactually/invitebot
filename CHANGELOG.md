# Changelog

## v1.0.0 — 2025-11-13

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
