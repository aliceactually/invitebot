namespace InviteBot {
    public partial class InviteBot {

        // Per-guild redirect domain validation. The domain ends up in invite URLs
        // (https://{domain}/{inviteId}) so we are picky about what we accept and we
        // normalise away the easy-to-paste mistakes:
        //   * a leading scheme ("https://example.com" -> "example.com")
        //   * a trailing slash ("example.com/" -> "example.com")
        //   * surrounding whitespace
        //   * uppercase letters (DNS is case-insensitive; we lowercase for consistency)
        //
        // Anything beyond that is rejected with a readable error rather than silently
        // mangled - the value is going into a public-facing URL, so "garbage in" must not
        // become "broken link out".

        internal const int DomainMinLength = 3;
        internal const int DomainMaxLength = 253; // DNS hard limit on total length

        internal static bool TryNormaliseDomain(string? input, out string normalised, out string? error) {
            normalised = "";
            error = null;

            if (input is null) { error = "value is required"; return false; }
            string s = input.Trim();
            if (s.Length == 0) { error = "value is required"; return false; }

            // Strip a scheme if the user pasted a full URL. Only http/https - anything else
            // is suspicious enough to reject outright.
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(8); }
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(7); }
            else if (s.Contains("://", StringComparison.Ordinal)) { error = "only http:// and https:// schemes are accepted"; return false; }

            // Strip a single trailing slash; reject anything with a path/query/fragment.
            if (s.EndsWith('/')) { s = s.Substring(0, s.Length - 1); }
            if (s.Contains('/')) { error = "domain must not contain a path"; return false; }
            if (s.Contains('?') || s.Contains('#')) { error = "domain must not contain a query or fragment"; return false; }
            if (s.Contains(' ')) { error = "domain must not contain spaces"; return false; }

            if (s.Length < DomainMinLength) { error = $"domain is too short (min {DomainMinLength} characters)"; return false; }
            if (s.Length > DomainMaxLength) { error = $"domain is too long (max {DomainMaxLength} characters)"; return false; }

            // We require at least one dot. localhost-style names are technically valid hostnames,
            // but a Discord redirect domain in production will always be FQDN-shaped, and this
            // single check catches the most common typo (forgetting the TLD).
            if (!s.Contains('.')) { error = "domain must contain at least one dot (e.g. example.com)"; return false; }
            if (s.StartsWith('.') || s.EndsWith('.')) { error = "domain must not start or end with a dot"; return false; }
            if (s.Contains("..", StringComparison.Ordinal)) { error = "domain must not contain consecutive dots"; return false; }

            // Validate each label: letters, digits, hyphens; no leading/trailing hyphen; length 1-63.
            string[] labels = s.Split('.');
            foreach (string label in labels) {
                if (label.Length == 0 || label.Length > 63) { error = "each label must be 1 to 63 characters"; return false; }
                if (label.StartsWith('-') || label.EndsWith('-')) { error = "labels must not start or end with a hyphen"; return false; }
                foreach (char c in label) {
                    bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                    if (!ok) { error = $"unsupported character '{c}' in domain"; return false; }
                }
            }

            normalised = s.ToLowerInvariant();
            return true;
        }
    }
}
