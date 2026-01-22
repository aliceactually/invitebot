using Microsoft.Data.Sqlite;

namespace InviteBot {
    public partial class InviteBot {

        private static SqliteConnection? db;
        private static readonly SemaphoreSlim dbLock = new(1, 1);

        // Bump this whenever the on-disk schema changes and add a corresponding case to
        // ApplyMigrations below. The version is stored in SQLite's built-in PRAGMA user_version,
        // which is a 32-bit integer that travels with the database file. A DB stamped with a
        // version higher than this constant is rejected at startup rather than risk corruption.
        private const int CurrentSchemaVersion = 2;

        // Opens (or creates) the SQLite database and runs schema migrations.
        // Throws on failure so Main can log and exit cleanly.
        private static void OpenDatabase(string dbPath) {
            try {
                SqliteConnectionStringBuilder builder = new();
                builder.DataSource = dbPath;
                builder.Mode = SqliteOpenMode.ReadWriteCreate;
                db = new SqliteConnection(builder.ConnectionString);
                db.Open();

                int onDisk = ReadSchemaVersion();
                if (onDisk > CurrentSchemaVersion) {
                    throw new InvalidOperationException(
                        $"Database at \"{dbPath}\" was created by a newer build (schema v{onDisk}); " +
                        $"this build only understands up to v{CurrentSchemaVersion}. Refusing to start to avoid corruption.");
                }
                if (onDisk < CurrentSchemaVersion) {
                    Log.Info("db", $"Migrating database from schema v{onDisk} to v{CurrentSchemaVersion}");
                }
                ApplyMigrations(onDisk);
                if (onDisk != CurrentSchemaVersion) {
                    WriteSchemaVersion(CurrentSchemaVersion);
                    Log.Info("db", $"Schema is now at v{CurrentSchemaVersion}");
                }
            } catch (Exception x) {
                Log.Error("db", $"Unable to open or migrate SQLite3 database at \"{dbPath}\"", x);
                throw;
            }
        }

        private static int ReadSchemaVersion() {
            using SqliteCommand cmd = new("PRAGMA user_version;", db);
            object? raw = cmd.ExecuteScalar();
            return raw is null ? 0 : Convert.ToInt32(raw);
        }

        private static void WriteSchemaVersion(int version) {
            // PRAGMA user_version does not accept parameters; the value is an int we control, not user input.
            using SqliteCommand cmd = new($"PRAGMA user_version = {version};", db);
            cmd.ExecuteNonQuery();
        }

        // Runs each missing migration in order. Migrations are wrapped in a transaction so a
        // half-applied schema cannot survive a crash. Add new versions as additional cases.
        private static void ApplyMigrations(int fromVersion) {
            if (fromVersion >= CurrentSchemaVersion) { return; }

            using SqliteTransaction tx = db!.BeginTransaction();
            try {
                if (fromVersion < 1) { Migrate_0_to_1(tx); }
                if (fromVersion < 2) { Migrate_1_to_2(tx); }
                // Future migrations:
                // if (fromVersion < 3) { Migrate_2_to_3(tx); }

                tx.Commit();
            } catch {
                try { tx.Rollback(); } catch { /* connection may be dead; surface the original */ }
                throw;
            }
        }

        // Baseline schema. This intentionally uses CREATE TABLE IF NOT EXISTS and an additive
        // ALTER for PrintLongEdgeMm so it is idempotent against pre-versioning databases that
        // were created when the bot did not yet stamp PRAGMA user_version. Pre-versioning DBs
        // read as version 0, get this run, and end up stamped at v1 like a fresh install.
        private static void Migrate_0_to_1(SqliteTransaction tx) {
            using (SqliteCommand settingsCmd = new(
                @"CREATE TABLE IF NOT EXISTS guild_settings (
                    GuildId         INTEGER PRIMARY KEY,
                    ChannelId       INTEGER NOT NULL DEFAULT 0,
                    AdminRole       INTEGER NOT NULL DEFAULT 0,
                    UserRole        INTEGER NOT NULL DEFAULT 0,
                    Paused          INTEGER NOT NULL DEFAULT 0 CHECK (Paused IN (0, 1)),
                    Debug           INTEGER NOT NULL DEFAULT 0 CHECK (Debug IN (0, 1)),
                    PrintLongEdgeMm REAL);", db, tx)) {
                settingsCmd.ExecuteNonQuery();
            }

            // Pre-versioning installs may have a guild_settings table without PrintLongEdgeMm.
            // SQLite's ALTER TABLE has no IF NOT EXISTS, so probe pragma first.
            bool hasPrintColumn = false;
            using (SqliteCommand probe = new("PRAGMA table_info(guild_settings);", db, tx))
            using (SqliteDataReader r = probe.ExecuteReader()) {
                while (r.Read()) {
                    if (string.Equals(r.GetString(1), "PrintLongEdgeMm", StringComparison.Ordinal)) {
                        hasPrintColumn = true;
                        break;
                    }
                }
            }
            if (!hasPrintColumn) {
                using SqliteCommand alter = new("ALTER TABLE guild_settings ADD COLUMN PrintLongEdgeMm REAL;", db, tx);
                alter.ExecuteNonQuery();
            }
        }

        // v1 -> v2: per-guild redirect Domain. Previously the domain was a single process-wide
        // setting in config.json; now every guild owns its own. Existing rows get NULL, which
        // surfaces in /invite admin status as "<not set>" and forces the admin to run
        // /invite admin domain before the next /invite create succeeds.
        private static void Migrate_1_to_2(SqliteTransaction tx) {
            bool hasDomainColumn = false;
            using (SqliteCommand probe = new("PRAGMA table_info(guild_settings);", db, tx))
            using (SqliteDataReader r = probe.ExecuteReader()) {
                while (r.Read()) {
                    if (string.Equals(r.GetString(1), "Domain", StringComparison.Ordinal)) {
                        hasDomainColumn = true;
                        break;
                    }
                }
            }
            if (!hasDomainColumn) {
                using SqliteCommand alter = new("ALTER TABLE guild_settings ADD COLUMN Domain TEXT;", db, tx);
                alter.ExecuteNonQuery();
            }
        }

        // Loads any previously-known guilds from the DB so we survive restarts cleanly.
        private static async Task HydrateGuildsAsync() {
            if (db is null) { return; }
            await dbLock.WaitAsync();
            try {
                using SqliteCommand loadCmd = new("SELECT GuildId, ChannelId, AdminRole, UserRole, Paused, Debug, PrintLongEdgeMm, Domain FROM guild_settings;", db);
                using SqliteDataReader reader = loadCmd.ExecuteReader();
                while (reader.Read()) {
                    GuildContext ctx = new() {
                        GuildId = (ulong)reader.GetInt64(0),
                        ChannelId = (ulong)reader.GetInt64(1),
                        AdminRole = (ulong)reader.GetInt64(2),
                        UserRole = (ulong)reader.GetInt64(3),
                        Paused = reader.GetInt32(4) != 0,
                        Debug = reader.GetInt32(5) != 0,
                        PrintLongEdgeMm = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        Domain = reader.IsDBNull(7) ? null : reader.GetString(7),
                    };
                    guilds[ctx.GuildId] = ctx;
                }
            } finally { dbLock.Release(); }
        }

        // Ensures a guild has a settings row, an in-memory context, and a per-guild invite table.
        private static async Task EnsureGuildAsync(ulong guildId) {
            if (db is null) { return; }

            await dbLock.WaitAsync();
            try {
                using SqliteCommand insertSettings = new(
                    "INSERT OR IGNORE INTO guild_settings (GuildId, Debug) VALUES (@id, @debug);", db);
                insertSettings.Parameters.AddWithValue("@id", (long)guildId);
                insertSettings.Parameters.AddWithValue("@debug", defaultDebug ? 1 : 0);
                insertSettings.ExecuteNonQuery();

                // guildId is a ulong so cannot carry SQL injection; do not generalise this to untrusted input
                using SqliteCommand createTable = new(
                    $@"CREATE TABLE IF NOT EXISTS guild_{guildId} (
                        Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        Invite          TEXT NOT NULL,
                        User            INTEGER NOT NULL,
                        Uses            INTEGER NOT NULL,
                        CreationDate    DATE NOT NULL,
                        ExpiryDate      DATE NOT NULL,
                        Purged          INTEGER NOT NULL CHECK (Purged IN (0, 1)));", db);
                createTable.ExecuteNonQuery();

                using SqliteCommand createIdx = new(
                    $"CREATE INDEX IF NOT EXISTS idx_guild_{guildId}_expiry ON guild_{guildId}(Purged, ExpiryDate);", db);
                createIdx.ExecuteNonQuery();
            } finally { dbLock.Release(); }

            guilds.GetOrAdd(guildId, id => new GuildContext { GuildId = id, Debug = defaultDebug });
        }

        // Persists the mutable settings on a context to disk.
        private static async Task SaveGuildAsync(GuildContext ctx) {
            if (db is null) { return; }
            await dbLock.WaitAsync();
            try {
                using SqliteCommand cmd = new(
                    @"UPDATE guild_settings
                      SET ChannelId = @ch, AdminRole = @ar, UserRole = @ur, Paused = @p, Debug = @d, PrintLongEdgeMm = @print, Domain = @domain
                      WHERE GuildId = @id;", db);
                cmd.Parameters.AddWithValue("@id", (long)ctx.GuildId);
                cmd.Parameters.AddWithValue("@ch", (long)ctx.ChannelId);
                cmd.Parameters.AddWithValue("@ar", (long)ctx.AdminRole);
                cmd.Parameters.AddWithValue("@ur", (long)ctx.UserRole);
                cmd.Parameters.AddWithValue("@p", ctx.Paused ? 1 : 0);
                cmd.Parameters.AddWithValue("@d", ctx.Debug ? 1 : 0);
                cmd.Parameters.AddWithValue("@print", ctx.PrintLongEdgeMm.HasValue ? ctx.PrintLongEdgeMm.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@domain", string.IsNullOrEmpty(ctx.Domain) ? DBNull.Value : (object)ctx.Domain);
                cmd.ExecuteNonQuery();
            } finally { dbLock.Release(); }
        }

        // Returns every guild id currently persisted in guild_settings. Used at startup to find
        // guilds the bot was kicked from while offline so their state can be reconciled away.
        private static async Task<List<ulong>> ListPersistedGuildsAsync() {
            List<ulong> ids = new();
            if (db is null) { return ids; }
            await dbLock.WaitAsync();
            try {
                using SqliteCommand cmd = new("SELECT GuildId FROM guild_settings;", db);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) { ids.Add((ulong)r.GetInt64(0)); }
            } finally { dbLock.Release(); }
            return ids;
        }

        // Removes every trace of a guild: the in-memory context, the per-guild invite table, the
        // settings row, the overlay file, and the overlay cache entry. Used when the bot is
        // kicked from a guild (live, via LeftGuild) and when startup reconciliation discovers a
        // persisted guild we are no longer a member of. Each step is best-effort and logged on
        // failure - we do not want one missing artefact to prevent the rest from being cleaned up.
        private static async Task ForgetGuildAsync(ulong guildId) {
            guilds.TryRemove(guildId, out _);
            overlayCache.TryRemove(guildId, out _);
            // Health monitor state is keyed on guildId and never expires on its own; clear it
            // here so a guild that leaves and later rejoins (or whose ID is reused) starts from
            // a clean slate rather than inheriting the prior tenant's last-known status.
            lastKnownHealthy.TryRemove(guildId, out _);
            sawFallbackUse.TryRemove(guildId, out _);

            if (db is not null) {
                await dbLock.WaitAsync();
                try {
                    // guildId is a ulong so cannot carry SQL injection; do not generalise to untrusted input
                    using SqliteCommand drop = new($"DROP TABLE IF EXISTS guild_{guildId};", db);
                    drop.ExecuteNonQuery();

                    using SqliteCommand del = new("DELETE FROM guild_settings WHERE GuildId = @id;", db);
                    del.Parameters.AddWithValue("@id", (long)guildId);
                    del.ExecuteNonQuery();
                } catch (Exception x) {
                    Log.Warn($"guild/{guildId}", "Failed to drop persisted state during ForgetGuildAsync", x);
                } finally { dbLock.Release(); }
            }

            try {
                string path = OverlayPathFor(guildId);
                if (File.Exists(path)) { File.Delete(path); }
            } catch (Exception x) {
                Log.Warn($"guild/{guildId}", "Failed to delete overlay file during ForgetGuildAsync", x);
            }

            Log.Info($"guild/{guildId}", "Removed all persisted state for departed guild");
        }
    }
}
