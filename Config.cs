using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InviteBot {

    // Strongly-typed view of config.json. JSON property names match the file.
    // Numbers and bools may appear in the file either as native JSON values
    // (preferred) or as quoted strings (legacy); both are accepted.
    public sealed record BotConfig {
        [JsonPropertyName("cleanupTimer")]
        [JsonConverter(typeof(LenientInt32Converter))]
        public int CleanupTimer { get; init; } = 60;

        [JsonPropertyName("defaultDuration")]
        [JsonConverter(typeof(LenientInt32Converter))]
        public int DefaultDuration { get; init; } = 60;

        [JsonPropertyName("defaultUses")]
        [JsonConverter(typeof(LenientInt32Converter))]
        public int DefaultUses { get; init; } = 1;

        [JsonPropertyName("foreverDuration")]
        [JsonConverter(typeof(LenientInt32Converter))]
        public int ForeverDuration { get; init; } = 43200;

        [JsonPropertyName("debug")]
        [JsonConverter(typeof(LenientBooleanConverter))]
        public bool Debug { get; init; } = false;

        [JsonPropertyName("devGuild")]
        [JsonConverter(typeof(LenientUInt64Converter))]
        public ulong DevGuild { get; init; } = 0;

        [JsonPropertyName("overlayDirectory")]
        public string OverlayDirectory { get; init; } = "overlays";

        [JsonPropertyName("database")]
        public string Database { get; init; } = "database.sqlite3";

        // Universal fallback used when a guild's per-guild redirect domain is unreachable.
        // Defaults to discord.gg because every Discord invite code is natively addressable as
        // discord.gg/<code> regardless of which guild minted it - so no matter what the LB does,
        // the QR a guest scans at the door still works. Lives in config (not per-guild) on
        // purpose: if Discord ever changes this, the operator updates one file and every guild
        // benefits, rather than asking N guild owners to fix their own settings.
        [JsonPropertyName("fallbackDomain")]
        public string FallbackDomain { get; init; } = "discord.gg";

        // Font used for the EXP/USES caption baked into the QR image. Configurable so Linux
        // operators do not have to edit InviteCreation.cs - on a stock Debian/Ubuntu install
        // "DejaVu Sans Mono" exists and resolves; on Windows/macOS "Courier New" does. If the
        // requested font is missing, ImageMagick falls back silently to a built-in face.
        [JsonPropertyName("captionFont")]
        public string CaptionFont { get; init; } = OperatingSystem.IsLinux() ? "DejaVu Sans Mono" : "Courier New";

        [JsonPropertyName("discord")]
        public DiscordConfig Discord { get; init; } = new();

        public sealed record DiscordConfig {
            [JsonPropertyName("token")]
            public string? Token { get; init; }
        }

        // Loads, deserialises and validates. Throws ArgumentException with a readable
        // message on any failure so Main can fail fast and the user sees the problem.
        public static BotConfig Load(string path) {
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception x) { throw new ArgumentException($"Unable to read config file \"{path}\": {x.Message}"); }

            BotConfig config;
            try {
                config = JsonSerializer.Deserialize<BotConfig>(text, JsonOptions)
                    ?? throw new ArgumentException("config.json deserialised to null");
            } catch (JsonException x) {
                throw new ArgumentException($"config.json is not valid JSON: {x.Message}");
            } catch (NotSupportedException x) {
                throw new ArgumentException($"config.json contains an unsupported value: {x.Message}");
            }

            config.Validate();
            return config;
        }

        private void Validate() {
            if (ForeverDuration < 0) { throw new ArgumentException("foreverDuration must be >= 0"); }
            // defaultDuration (minutes) may go up to and including foreverDuration (also minutes),
            // since anything longer is tracked via the DB-backed ExpiryDate rather than Discord's
            // own maxAge. foreverDuration:0 means "no upper bound" - so any non-negative value
            // is OK.
            int defaultDurationMax = ForeverDuration == 0 ? int.MaxValue : ForeverDuration;
            if (DefaultDuration < 0 || DefaultDuration > defaultDurationMax) {
                string limitText = ForeverDuration == 0
                    ? "0 (foreverDuration is 0, so any non-negative value is allowed)"
                    : $"{defaultDurationMax} (the configured foreverDuration)";
                throw new ArgumentException($"defaultDuration must be between 0 and {limitText}");
            }
            if (DefaultUses < 0 || DefaultUses > 100) { throw new ArgumentException("defaultUses must be between 0 and 100"); }
            if (CleanupTimer <= 0 || CleanupTimer > 1440) { throw new ArgumentException("cleanupTimer must be between 1 and 1440 minutes (any longer makes cleanup too inaccurate)"); }
            if (string.IsNullOrEmpty(OverlayDirectory)) { throw new ArgumentException("overlayDirectory must be set in config.json"); }
            if (string.IsNullOrEmpty(Discord.Token)) { throw new ArgumentException("discord.token must be set in config.json"); }
            if (string.IsNullOrEmpty(FallbackDomain)) { throw new ArgumentException("fallbackDomain must be set in config.json (use \"discord.gg\" if you have no preference)"); }
            if (string.IsNullOrEmpty(CaptionFont)) { throw new ArgumentException("captionFont must be set in config.json"); }
        }

        private static readonly JsonSerializerOptions JsonOptions = new() {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
    }

    // Accepts an int, a long that fits in an int, or a quoted string containing one.
    internal sealed class LenientInt32Converter : JsonConverter<int> {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            switch (reader.TokenType) {
                case JsonTokenType.Number:
                    return reader.GetInt32();
                case JsonTokenType.String:
                    string? s = reader.GetString();
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) { return v; }
                    throw new JsonException($"Expected an integer but got the string \"{s}\"");
                default:
                    throw new JsonException($"Expected an integer but got token {reader.TokenType}");
            }
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class LenientUInt64Converter : JsonConverter<ulong> {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            switch (reader.TokenType) {
                case JsonTokenType.Number:
                    return reader.GetUInt64();
                case JsonTokenType.String:
                    string? s = reader.GetString();
                    if (string.IsNullOrEmpty(s)) { return 0; } // permits "" as "unset" for optional ids
                    if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)) { return v; }
                    throw new JsonException($"Expected an unsigned integer but got the string \"{s}\"");
                default:
                    throw new JsonException($"Expected an unsigned integer but got token {reader.TokenType}");
            }
        }
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class LenientBooleanConverter : JsonConverter<bool> {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            switch (reader.TokenType) {
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.String:
                    string? s = reader.GetString();
                    if (bool.TryParse(s, out bool v)) { return v; }
                    throw new JsonException($"Expected a boolean but got the string \"{s}\"");
                default:
                    throw new JsonException($"Expected a boolean but got token {reader.TokenType}");
            }
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }
}
