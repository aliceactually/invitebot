using System.Reflection;

namespace InviteBot {
    public partial class InviteBot {

        // Build-time metadata stamped into the assembly by the StampBuildMetadata MSBuild target
        // in invitebot.csproj. Read once, lazily, on first access. Both fall back to "unknown" if
        // the attribute is missing (e.g. tests, or a build that bypassed the target).
        private static string? _buildVersion;
        private static string? _buildCommit;
        private static string? _buildTimestamp;

        public static string BuildVersion =>
            _buildVersion ??= Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        public static string BuildCommit =>
            _buildCommit ??= ReadAssemblyMetadata("GitCommit") ?? "unknown";

        // Returned in ISO 8601 / "o" format - the same shape the MSBuild target stamped in.
        // Callers that want something friendlier should DateTime.Parse and reformat.
        public static string BuildTimestampUtc =>
            _buildTimestamp ??= ReadAssemblyMetadata("BuildTimestampUtc") ?? "unknown";

        private static string? ReadAssemblyMetadata(string key) {
            foreach (AssemblyMetadataAttribute attr in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()) {
                if (attr.Key == key) { return attr.Value; }
            }
            return null;
        }

        // Renders the stamped ISO-8601 build timestamp as a short, restart-notice-friendly string.
        // Anything we cannot parse falls through unchanged so an "unknown" stays "unknown".
        public static string FormatBuildTimestamp(string iso) {
            if (DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime t)) {
                return t.ToUniversalTime().ToString("dd MMM yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            }
            return iso;
        }
    }
}
