using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #45 (spec #21) — the payload-template resolver at its Core seam:
    /// <c>$PRP:"name"</c> references substituted against an
    /// <see cref="IPartPropertySource"/>, literal text carried through
    /// verbatim, and failures returned as data NAMING the offending
    /// reference — never thrown, never a silent empty substitution.
    /// </summary>
    public class SwPropertyTemplateTests
    {
        private static FakePartPropertySource PartSource()
        {
            var source = new FakePartPropertySource();
            source.DocumentProperties["PartNo"] = "12345-B";
            source.DocumentProperties["Description"] = "Mounting bracket";
            source.DocumentProperties["Vendor"] = string.Empty;   // present but blank
            source.ConfigurationProperties["Revision"] = "C";
            return source;
        }

        // ---------------- literal passthrough ----------------

        [Fact]
        public void A_template_without_references_resolves_verbatim()
        {
            var template = "  padded text\nsecond line — no tokens  ";

            var result = SwPropertyTemplate.Resolve(template, PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(template, result.Content);
        }

        [Fact]
        public void A_tokenless_template_needs_no_property_source()
        {
            // No references, nothing to resolve against — the literal is
            // the answer even when the session has no source at all.
            var result = SwPropertyTemplate.Resolve("literal only", null);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("literal only", result.Content);
        }

        [Theory]
        [InlineData("$PRP:PartNo")]            // unquoted — not a token, stays literal
        [InlineData("$PRP")]                   // the bare prefix
        [InlineData("cost is $5 PRP today")]   // $-text that never was a reference
        [InlineData("$prp:\"PartNo\"")]        // case-sensitive keyword, like SolidWorks
        public void Unquoted_or_non_reference_dollar_PRP_forms_stay_literal(string template)
        {
            var result = SwPropertyTemplate.Resolve(template, PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(template, result.Content);
        }

        // ---------------- substitution ----------------

        [Fact]
        public void A_single_reference_resolves_its_property_value()
        {
            var result = SwPropertyTemplate.Resolve("SN $PRP:\"PartNo\"", PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("SN 12345-B", result.Content);
        }

        [Fact]
        public void Literal_text_and_references_mix_like_the_ticket_example()
        {
            // The acceptance-criterion example: literal dashes and text
            // around two references, resolved against the two stores.
            var result = SwPropertyTemplate.Resolve(
                "PART-$PRP:\"PartNo\"-$PRP:\"Revision\"", PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("PART-12345-B-C", result.Content);
        }

        [Fact]
        public void A_repeated_reference_resolves_every_occurrence()
        {
            var result = SwPropertyTemplate.Resolve(
                "$PRP:\"Revision\" of $PRP:\"Revision\"", PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("C of C", result.Content);
        }

        [Fact]
        public void Whitespace_and_line_breaks_around_references_stay_verbatim()
        {
            var result = SwPropertyTemplate.Resolve(
                "line 1\n  $PRP:\"Description\"\t$PRP:\"Revision\"\n", PartSource());

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("line 1\n  Mounting bracket\tC\n", result.Content);
        }

        [Fact]
        public void A_property_name_with_spaces_resolves()
        {
            var source = PartSource();
            source.DocumentProperties["Part Number"] = "PN-9";

            var result = SwPropertyTemplate.Resolve("id=$PRP:\"Part Number\"", source);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("id=PN-9", result.Content);
        }

        [Fact]
        public void A_whitespace_property_value_substitutes_verbatim()
        {
            var source = PartSource();
            source.DocumentProperties["Spaced"] = "   ";

            var result = SwPropertyTemplate.Resolve("a$PRP:\"Spaced\"b", source);

            // Whitespace is a real stored value — it resolves, never empty.
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("a   b", result.Content);
        }

        [Fact]
        public void A_configuration_property_shadows_the_document_one()
        {
            var source = PartSource();
            source.DocumentProperties["Rev"] = "A";
            source.ConfigurationProperties["Rev"] = "B";

            var result = SwPropertyTemplate.Resolve("$PRP:\"Rev\"", source);

            // SolidWorks' own precedence — the source's resolver owns it;
            // the template resolver just consumes the verdict.
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("B", result.Content);
        }

        // ---------------- failures: named, never silent ----------------

        [Fact]
        public void A_missing_reference_fails_naming_the_token()
        {
            var result = SwPropertyTemplate.Resolve("x $PRP:\"NoSuch\" y", PartSource());

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, result.Error!.Kind);
            Assert.Contains("$PRP:\"NoSuch\"", result.Error.Message);
        }

        [Fact]
        public void An_empty_reference_fails_distinctly_from_a_missing_one()
        {
            var result = SwPropertyTemplate.Resolve("x $PRP:\"Vendor\"", PartSource());

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"Vendor\"", result.Error!.Message);
            Assert.Contains("empty", result.Error.Message);
        }

        [Fact]
        public void A_reference_without_a_part_document_fails_naming_it()
        {
            var source = PartSource();
            source.Document = HostDocumentKind.None;

            var result = SwPropertyTemplate.Resolve("$PRP:\"PartNo\"", source);

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"PartNo\"", result.Error!.Message);
            Assert.Contains("part", result.Error.Message);
        }

        [Fact]
        public void A_reference_without_a_property_source_fails_naming_it()
        {
            // A session with no property provider has nothing to resolve
            // against — the honest verdict names the reference, it never
            // guesses a value.
            var result = SwPropertyTemplate.Resolve("$PRP:\"PartNo\"", null);

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"PartNo\"", result.Error!.Message);
        }

        [Theory]
        [InlineData("$PRP:\"unclosed")]        // no closing quote
        [InlineData("$PRP:\"\"")]              // an empty name
        [InlineData("a $PRP:\"\" b")]          // remnant mid-text
        public void A_malformed_reference_remnant_fails_instead_of_encoding(string template)
        {
            var result = SwPropertyTemplate.Resolve(template, PartSource());

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, result.Error!.Kind);
            Assert.Contains("$PRP:", result.Error.Message);
        }
    }
}
