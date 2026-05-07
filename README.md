# InviteBot

A Discord bot that creates invitations with QR-code overlays and tracks them in
SQLite so it can enforce logical expiry independent of Discord's one-day cap.

## Features

- `/invite create` — generates a uniquely-coded invite, renders a QR-code image
  with the server's overlay, and posts both the link and the image. Admins can
  override duration, use count, and print size per call. Probes the redirect
  domain first; if the load balancer is unreachable the bot transparently
  serves the link via the universal `fallbackDomain` (default `discord.gg`)
  so the QR at the door always works.
- `/invite admin configure` — bootstraps per-server settings (log channel,
  admin role, optional user role). Gated by Discord's **Manage Server**
  permission.
- `/invite admin overlay` — uploads (or replaces) the per-server overlay PNG.
  No SCP required; the bot writes the file itself and normalises it to 300 DPI.
- `/invite admin print` — sets the default long-edge print size (e.g. `90mm`,
  `9cm`, `3.5in`); pass `clear` to remove it.
- `/invite admin domain` — sets the per-server redirect domain used in
  generated invite URLs (e.g. `invite.example.com`); pass `clear` to unset.
  `/invite create` refuses until a domain is set. The bot probes the new
  domain over HTTPS immediately so misconfigured load balancers surface in
  the same response, not at the door.
- `/invite admin export` — emits a JSON backup of the server's settings plus
  its overlay PNG in a single ephemeral response.
- `/invite admin import` — restores a backup produced by `/invite admin export`.
  The JSON is required; the overlay PNG is optional. Cross-guild restores
  (e.g. dev → prod) are permitted and surfaced in the response.
- `/invite admin introduce` — DMs an introduction to a user, role, or
  `@everyone`. The introduction is role-aware: regular users get the
  `/invite create` tour, admins additionally get the full `/invite admin …`
  reference, and members with neither role get a friendly note explaining
  what they'd be able to do if granted access. Targeting more than 10% of
  the server requires `iamsure:true` to prevent accidental mass-DMs.
- `/invite admin welcome` — toggles automatically DM'ing the introduction
  to new members when they join. **Enabled by default.** Disable with
  `/invite admin welcome value:false` if you'd rather only ever introduce
  the bot manually.
- `/invite admin pause | debug | purge | status` — per-server bot controls.
  `status` includes a **live HTTPS probe** of the configured redirect domain
  so you can confirm the load balancer is up without leaving Discord. It
  also reports whether the fallback domain is currently armed (because the
  probe just failed) or has actually been used to serve invites since the
  last recovery.
- **Background health monitor** re-probes every configured redirect domain
  every 5 minutes and posts to the guild's log channel only when the domain
  transitions between up and down (or every tick, when `debug` is on).
- Multi-guild: joins are detected at runtime; new servers self-onboard with no
  restart required. Departed guilds are reconciled at startup and on leave so
  on-disk state stays bounded.
- Automatic background cleanup of expired invites.
- Restart notice posted to each server's log channel includes the build's
  version, git commit, and compilation timestamp for traceability.

## Requirements

- .NET 10 SDK (for building only — published binaries are self-contained)
- A Discord application + bot token

Overlays are uploaded per-server at runtime via `/invite admin overlay`; no
file needs to ship alongside the binary.

## Inviting the bot to a server

Generate an OAuth2 invite URL for your application in the
[Discord Developer Portal](https://discord.com/developers/applications) under
**OAuth2 → URL Generator**. Tick the following:

- **Scopes:** `bot`, `applications.commands`
- **Bot permissions:** `Create Instant Invite`, `View Channel`, `Send
  Messages`, `Attach Files`, `Embed Links`

`/invite admin configure` performs a permission preflight on the chosen log
channel and refuses to save if any of the message-sending permissions are
missing, so misconfigurations surface immediately instead of at the door.

### Privileged Gateway Intents

`/invite admin introduce` (when targeting a role or `@everyone`) and the
auto-welcome on member-join both rely on the **Server Members Intent**, which
is privileged. Enable it for your application in the Discord Developer Portal
under **Bot → Privileged Gateway Intents → Server Members Intent**. Without
it, role enumeration returns empty and `UserJoined` never fires, so neither
feature will work.

## Configuration

Copy `config.json.sample` to `config.json` and fill in at minimum:

| Key                 | Purpose                                                                |
| ------------------- | ---------------------------------------------------------------------- |
| `discord.token`     | Bot token (treat as a secret — `config.json` is gitignored)            |
| `cleanupTimer`      | Cleanup interval in minutes                                            |
| `defaultDuration`   | Default invite duration in minutes (0–1440; 0 = "forever")             |
| `defaultUses`       | Default invite use count (0 = unlimited)                               |
| `foreverDuration`   | Days assigned to "forever" invites in the DB (0 = never)               |
| `database`          | SQLite file path (relative paths sit beside the executable)            |
| `overlayDirectory`  | Directory holding per-guild overlay PNGs (relative to the executable)  |
| `fallbackDomain`    | Universal redirect domain used when a guild's per-guild domain is unreachable. Defaults to `discord.gg` because every Discord invite code natively works as `discord.gg/<code>`, so a fallback is guaranteed to land. Lives here (not per-guild) on purpose: if Discord ever changes this, you update one file instead of pinging every guild owner. |
| `captionFont`       | Font name passed to ImageMagick for the EXP/USES caption. Defaults to `DejaVu Sans Mono` on Linux and `Courier New` elsewhere. Override if your platform has a different monospace face installed. |
| `debug`             | Default debug flag for newly-joined guilds                             |
| `devGuild`          | *Optional.* Guild id to also register slash commands against.          |

Per-server settings live **in the database** and are managed via the
`/invite admin …` subcommands from inside each server. There is no
per-guild config in `config.json`. The full set is:

- log channel, admin role, optional user role (`/invite admin configure`)
- redirect domain (`/invite admin domain`)
- default print long-edge size (`/invite admin print`)
- overlay PNG (`/invite admin overlay`)
- `paused` and `debug` flags (`/invite admin pause`, `/invite admin debug`)
- auto-welcome toggle (`/invite admin welcome`)

Everything except the overlay file itself is captured by `/invite admin
export`; the overlay PNG is bundled into the same response as a separate
attachment.

### Overlay constraints

Uploaded overlays must be:

- **PNG**, with at least one alpha channel area to leave room for the QR.
- **256–4096 px** on each side.
- **≤ 4 MB** in size.

On upload the bot normalises the image to **300 DPI** (resampling with
Lanczos if the source density is set and differs from 300, or stamping the
density in place if no source density is present). This means render-time
print-size maths is unambiguous: 100 mm at 300 DPI is always 1181 px,
regardless of what the designer exported at. Operators who want
pixel-perfect output should design their overlays at 300 DPI to begin with.

### About `devGuild`

Slash commands registered globally can take up to ~1 hour to propagate the
first time. Setting `devGuild` to a guild id additionally registers the command
to that single guild, where it appears immediately. Leave blank in production.

## Building

```pwsh
dotnet build -c Release
```

## Testing

The bot's pure helpers (invite-code generation, domain normalisation, health
formatting, caption layout, fallback routing, units parsing) are covered by
xUnit tests in the sibling `tests/` project:

```pwsh
dotnet test tests/invitebot.tests.csproj --nologo
```

The test project deliberately lives outside the main `invitebot.csproj`
compile glob; see [`.github/copilot-instructions.md`](.github/copilot-instructions.md)
for the Visual Studio gotcha when adding new test files.

## Publishing self-contained binaries

Run the helper script to produce single-file binaries for all six supported
targets in `publish/`:

```pwsh
pwsh ./publish-all.ps1
```

Outputs:

```
publish/win-x64-invitebot.exe
publish/win-arm64-invitebot.exe
publish/linux-x64-invitebot
publish/linux-arm64-invitebot
publish/osx-x64-invitebot
publish/osx-arm64-invitebot
```

A bare `dotnet publish -c Release` (no `-r`) will also fan out to the same
six targets via the MSBuild target in `invitebot.csproj`.

## Running on Linux

```bash
chmod +x linux-x64-invitebot
./linux-x64-invitebot
```

`config.json` and `database.sqlite3` live alongside the binary, and per-guild
overlay PNGs are written into the configured `overlayDirectory` (also beside
the binary by default). The DB is created on first run and persists across
restarts; its schema is versioned via SQLite's `PRAGMA user_version` and is
migrated automatically on startup.

### System dependencies

The bundled native ImageMagick library uses **fontconfig** to resolve font
names at runtime. Without it, invite generation still works but the QR-code
caption falls back to a built-in font and you'll see a `Fontconfig error`
on stderr.

```bash
# RHEL / Rocky / Alma / Amazon Linux / Fedora
sudo dnf install -y fontconfig dejavu-sans-mono-fonts

# Debian / Ubuntu
sudo apt-get install -y fontconfig fonts-dejavu
```

The default `captionFont` is `Courier New` on Windows/macOS and
`DejaVu Sans Mono` on Linux (the latter ships with `fonts-dejavu` /
`dejavu-sans-mono-fonts`). Override with the `captionFont` key in
`config.json` if you have a different monospace face installed.

### Deploying as a systemd service

A sample unit file lives at [`deploy/invitebot.service`](deploy/invitebot.service).
It assumes the binary is at `/opt/invitebot/invitebot` and runs as a dedicated
`invitebot` user.

**One-time setup:**

```bash
# 1. Create the service user and install the binary + assets
sudo useradd --system --home-dir /opt/invitebot --shell /sbin/nologin invitebot
sudo mkdir -p /opt/invitebot
sudo cp linux-x64-invitebot /opt/invitebot/invitebot
sudo cp config.json /opt/invitebot/
sudo chown -R invitebot:invitebot /opt/invitebot
sudo chmod 750 /opt/invitebot
sudo chmod 640 /opt/invitebot/config.json   # contains the bot token

# 2. Install the unit file and (optional) log rotation
sudo cp deploy/invitebot.service /etc/systemd/system/
sudo cp deploy/invitebot.logrotate /etc/logrotate.d/invitebot

# 3. Enable and start
sudo systemctl daemon-reload
sudo systemctl enable --now invitebot
sudo systemctl status invitebot
```

`/var/log/invitebot/` and `/var/cache/invitebot/bundle/` are created and
owned automatically by systemd via `LogsDirectory=` and `CacheDirectory=`.

### SELinux (RHEL / Rocky / Alma / Fedora)

On distributions where SELinux is enforcing, the binary at `/opt/invitebot/`
inherits the `default_t` label and cannot be exec'd by systemd. Symptom in
the journal:

```
invitebot.service: Unable to locate executable '/opt/invitebot/invitebot': Permission denied
```

Relabel it as `bin_t`:

```bash
sudo dnf install -y policycoreutils-python-utils    # if semanage is missing
sudo semanage fcontext -a -t bin_t "/opt/invitebot/invitebot"
sudo restorecon -v /opt/invitebot/invitebot
```

If you also see denials for the extracted native libraries under
`/var/cache/invitebot/bundle/`, relabel them too:

```bash
sudo semanage fcontext -a -t lib_t "/var/cache/invitebot(/.*)?\.so.*"
sudo restorecon -Rv /var/cache/invitebot
```

To confirm SELinux is the cause before applying any of the above:

```bash
sudo ausearch -m AVC -ts recent | grep invitebot
```

### Operational commands

```bash
sudo systemctl status invitebot         # state + recent log lines
sudo systemctl restart invitebot        # graceful: SIGINT, then up to 30s drain
sudo journalctl -u invitebot -f         # systemd-side messages (start/stop/exit)
sudo tail -f /var/log/invitebot/invitebot.log   # bot's own structured log
sudo tail -f /var/log/invitebot/invitebot.err   # warnings + errors only
```

## License

Copyright © 2026 Alice Kallista Saunier. All rights reserved.
Distributed under the GNU General Public License v3.0. See [`LICENSE`](LICENSE)
for the full text.


