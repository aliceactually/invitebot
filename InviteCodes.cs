using System.Security.Cryptography;
using System.Text;

namespace InviteBot {
    public partial class InviteBot {

        // Pure helpers for minting our own short, human-friendly codes. Discord still owns the
        // real invite IDs used by /invite create; this exists so future features (claim links,
        // one-shot tokens, server aliases) have a single, tested place to generate codes from.
        //
        // Design choices, all of which the tests pin down:
        //   * Cryptographically random. Anything code-shaped should never come from System.Random.
        //   * Unambiguous alphabet. We drop 0/O/1/I/L so codes are easy to read aloud and type.
        //   * Uniform distribution. Naive "random byte mod alphabet length" skews toward the
        //     start of the alphabet; rejection sampling avoids that.
        //   * Optional dashed grouping for legibility ("ABCD-EFGH-JKMN") without changing the
        //     amount of entropy.

        // 32 characters - a clean power of two would be nicer for masking, but readability wins.
        // Excluded on purpose: 0, O, 1, I, L.
        internal const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        internal const int CodeMinLength = 4;
        internal const int CodeMaxLength = 32;

        // Generate a code of the requested length. groupSize > 0 inserts a '-' every N
        // characters (the dashes do not count toward `length`). Throws ArgumentOutOfRangeException
        // for invalid arguments - easier to assert against than a sentinel return value.
        internal static string GenerateCode(int length, int groupSize = 0) {
            if (length < CodeMinLength || length > CodeMaxLength) {
                throw new ArgumentOutOfRangeException(nameof(length), length, $"length must be between {CodeMinLength} and {CodeMaxLength}.");
            }
            if (groupSize < 0) {
                throw new ArgumentOutOfRangeException(nameof(groupSize), groupSize, "groupSize must be zero or positive.");
            }

            // Rejection sampling: read a byte, keep it only if it falls inside the largest
            // multiple of the alphabet length that fits in 256. That guarantees a uniform
            // distribution across the alphabet regardless of its size.
            int alphabetLength = CodeAlphabet.Length;
            int rejectionThreshold = 256 - (256 % alphabetLength);

            StringBuilder sb = new(length + (groupSize > 0 ? length / groupSize : 0));
            Span<byte> buffer = stackalloc byte[1];
            int produced = 0;
            while (produced < length) {
                RandomNumberGenerator.Fill(buffer);
                if (buffer[0] >= rejectionThreshold) { continue; }
                if (groupSize > 0 && produced > 0 && produced % groupSize == 0) {
                    sb.Append('-');
                }
                sb.Append(CodeAlphabet[buffer[0] % alphabetLength]);
                produced++;
            }
            return sb.ToString();
        }

        // Validate a code string: right length, only alphabet characters, dashes (if present)
        // appear at the expected groupSize boundaries. Returns false rather than throwing,
        // because this will eventually be called against untrusted user input.
        internal static bool IsValidCode(string? code, int length, int groupSize = 0) {
            if (code is null) { return false; }
            if (length < CodeMinLength || length > CodeMaxLength) { return false; }
            if (groupSize < 0) { return false; }

            int expectedDashes = groupSize > 0 ? (length - 1) / groupSize : 0;
            if (code.Length != length + expectedDashes) { return false; }

            int i = 0;
            int produced = 0;
            while (produced < length) {
                // A dash is expected immediately before every group boundary except the very first character.
                if (groupSize > 0 && produced > 0 && produced % groupSize == 0) {
                    if (i >= code.Length || code[i] != '-') { return false; }
                    i++;
                }
                if (i >= code.Length || CodeAlphabet.IndexOf(code[i]) < 0) { return false; }
                i++;
                produced++;
            }
            return i == code.Length;
        }
    }
}
