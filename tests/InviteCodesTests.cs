using Xunit;

namespace InviteBot.Tests {

    // Tests for InviteCodes.cs - the cryptographically-random short-code generator.
    //
    // Testing randomness teaches a useful technique: you cannot assert "the output equals X"
    // because X is, by definition, unpredictable. Instead you assert *invariants* - properties
    // that must hold for every possible output:
    //
    //   * The right length comes back.
    //   * Every character is from the allowed alphabet.
    //   * Banned characters (0/O/1/I/L) never appear.
    //   * Two calls in a row don't return the same value (a sanity check that we're not
    //     accidentally returning a constant).
    //   * Bad arguments throw the documented exception.
    //   * The validator agrees with the generator (round-trip).
    //
    // Running the same probabilistic test many times in a loop is fine and catches regressions
    // that a single call would miss - e.g. an off-by-one that only fires when a particular
    // byte value comes out of the RNG.
    public class InviteCodesTests {

        // ---- GenerateCode: shape and alphabet ----

        [Theory]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(12)]
        [InlineData(32)]
        public void GenerateCode_ReturnsRequestedLength(int length) {
            for (int i = 0; i < 200; i++) {
                string code = InviteBot.GenerateCode(length);
                Assert.Equal(length, code.Length);
            }
        }

        [Fact]
        public void GenerateCode_OnlyContainsAlphabetCharacters() {
            for (int i = 0; i < 200; i++) {
                string code = InviteBot.GenerateCode(16);
                foreach (char c in code) {
                    Assert.Contains(c, InviteBot.CodeAlphabet);
                }
            }
        }

        [Theory]
        [InlineData('0')]
        [InlineData('O')]
        [InlineData('1')]
        [InlineData('I')]
        [InlineData('L')]
        public void GenerateCode_NeverContainsAmbiguousCharacters(char banned) {
            // 200 codes * 16 chars = 3200 character draws per banned letter. If our alphabet
            // accidentally regrew one of these we'd see it almost immediately.
            for (int i = 0; i < 200; i++) {
                string code = InviteBot.GenerateCode(16);
                Assert.DoesNotContain(banned, code);
            }
        }

        [Fact]
        public void GenerateCode_ProducesDifferentValuesAcrossCalls() {
            // Not a true entropy test - just a smoke test that we aren't returning a constant.
            // Collision probability for 100 random 12-char codes from a 31-char alphabet is
            // astronomically small, so a duplicate here means a real bug.
            HashSet<string> seen = new();
            for (int i = 0; i < 100; i++) {
                Assert.True(seen.Add(InviteBot.GenerateCode(12)));
            }
        }

        // ---- GenerateCode: dashed grouping ----

        [Theory]
        [InlineData(8, 4, 9)]    // 8 chars + 1 dash  -> "ABCD-EFGH"
        [InlineData(12, 4, 14)]  // 12 chars + 2 dashes -> "ABCD-EFGH-JKMN"
        [InlineData(9, 3, 11)]   // 9 chars + 2 dashes  -> "ABC-DEF-GHJ"
        [InlineData(8, 0, 8)]    // grouping disabled
        public void GenerateCode_InsertsDashesAtGroupBoundaries(int length, int groupSize, int expectedTotalLength) {
            string code = InviteBot.GenerateCode(length, groupSize);
            Assert.Equal(expectedTotalLength, code.Length);

            if (groupSize > 0) {
                // Dashes must land exactly at every Nth alphabet position and nowhere else.
                for (int i = 0; i < code.Length; i++) {
                    bool isDashPosition = (i + 1) % (groupSize + 1) == 0 && i != code.Length - 1;
                    if (isDashPosition) {
                        Assert.Equal('-', code[i]);
                    } else {
                        Assert.NotEqual('-', code[i]);
                    }
                }
            } else {
                Assert.DoesNotContain('-', code);
            }
        }

        // ---- GenerateCode: argument validation ----

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(33)]
        [InlineData(-1)]
        public void GenerateCode_RejectsLengthOutsideBounds(int length) {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => InviteBot.GenerateCode(length));
            Assert.Equal("length", ex.ParamName);
        }

        [Fact]
        public void GenerateCode_RejectsNegativeGroupSize() {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => InviteBot.GenerateCode(8, -1));
            Assert.Equal("groupSize", ex.ParamName);
        }

        // ---- IsValidCode ----

        [Fact]
        public void IsValidCode_AcceptsFreshlyGeneratedCodes() {
            // The generator and validator must agree. If a future change to one of them
            // breaks this round-trip, it's almost always a real bug.
            for (int i = 0; i < 100; i++) {
                Assert.True(InviteBot.IsValidCode(InviteBot.GenerateCode(8), 8));
                Assert.True(InviteBot.IsValidCode(InviteBot.GenerateCode(12, 4), 12, 4));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("ABCDEFG")]      // too short
        [InlineData("ABCDEFGHJ")]    // too long
        [InlineData("ABCDEFG0")]     // contains banned '0'
        [InlineData("ABCDEFGl")]     // contains banned lowercase 'l' (not in alphabet)
        [InlineData("abcdefgh")]     // lowercase not allowed
        public void IsValidCode_RejectsBadInputsAtLength8(string? code) {
            Assert.False(InviteBot.IsValidCode(code, 8));
        }

        [Theory]
        [InlineData("ABCDEFGH", 8, 4)]      // missing dash
        [InlineData("ABC-DEFGH", 8, 4)]     // dash in wrong place
        [InlineData("ABCD-EFG", 8, 4)]      // right dash, wrong content length
        [InlineData("ABCD_EFGH", 8, 4)]     // underscore instead of dash
        public void IsValidCode_RejectsBadGroupingAtLength8Grouped4(string code, int length, int groupSize) {
            Assert.False(InviteBot.IsValidCode(code, length, groupSize));
        }

        [Theory]
        [InlineData("ABCD-EFGH", 8, 4)]
        [InlineData("ABCDEFGH", 8, 0)]
        [InlineData("ABC-DEF-GHJ", 9, 3)]
        public void IsValidCode_AcceptsKnownGoodLiterals(string code, int length, int groupSize) {
            Assert.True(InviteBot.IsValidCode(code, length, groupSize));
        }
    }
}
