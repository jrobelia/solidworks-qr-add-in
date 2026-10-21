using System;
using System.Linq;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #44 (spec #21) — the part-property source seam, exercised at the
    /// <see cref="IPartPropertySource"/> contract surface through
    /// <see cref="FakePartPropertySource"/> (which delegates to the same
    /// <see cref="PartPropertyResolver"/> precedence logic the native adapter
    /// uses). Coverage: name enumeration across the document-level and
    /// configuration-specific stores, single-name resolution returning the
    /// value verbatim, and the explicit missing/empty results that must never
    /// collapse into a silent empty string.
    /// </summary>
    public class PartPropertySourceTests
    {
        private static FakePartPropertySource PartSource()
        {
            var source = new FakePartPropertySource();
            source.DocumentProperties["Material"] = "6061-T6";
            source.DocumentProperties["EmptyNote"] = "";
            source.ConfigurationProperties["Finish"] = "Anodized";
            return source;
        }

        // ---------------- enumeration ----------------

        [Fact]
        public void Capture_reports_the_active_part_and_configuration()
        {
            var source = PartSource();

            var snapshot = source.CaptureProperties();

            Assert.Equal(HostDocumentKind.Part, snapshot.Document);
            Assert.Equal("Default", snapshot.ConfigurationName);
        }

        [Fact]
        public void Capture_lists_document_and_configuration_names()
        {
            var source = PartSource();

            var names = source.CaptureProperties().Names;

            Assert.Equal(
                new[] { "Finish", "Material", "EmptyNote" }.OrderBy(n => n),
                names.OrderBy(n => n));
        }

        [Fact]
        public void Capture_lists_a_name_stored_in_both_stores_only_once()
        {
            var source = PartSource();
            source.DocumentProperties["Rev"] = "A";
            source.ConfigurationProperties["Rev"] = "B";

            var names = source.CaptureProperties().Names;

            Assert.Equal(1, names.Count(n =>
                string.Equals(n, "Rev", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void Capture_lists_configuration_names_before_document_ones()
        {
            var source = PartSource();

            var names = source.CaptureProperties().Names;

            Assert.Equal("Finish", names[0]);
        }

        [Fact]
        public void Capture_without_a_part_lists_no_names_and_no_configuration()
        {
            var source = PartSource();
            source.Document = HostDocumentKind.Assembly;

            var snapshot = source.CaptureProperties();

            Assert.Equal(HostDocumentKind.Assembly, snapshot.Document);
            Assert.Null(snapshot.ConfigurationName);
            Assert.Empty(snapshot.Names);
        }

        // ---------------- resolution ----------------

        [Fact]
        public void Resolve_returns_a_document_property_verbatim()
        {
            var resolution = PartSource().Resolve("Material");

            Assert.Equal(PartPropertyResolutionKind.Resolved, resolution.Kind);
            Assert.Equal("6061-T6", resolution.Value);
        }

        [Fact]
        public void Resolve_returns_a_configuration_property_verbatim()
        {
            var resolution = PartSource().Resolve("Finish");

            Assert.Equal(PartPropertyResolutionKind.Resolved, resolution.Kind);
            Assert.Equal("Anodized", resolution.Value);
        }

        [Fact]
        public void Resolve_prefers_the_configuration_store_on_a_name_clash()
        {
            var source = PartSource();
            source.DocumentProperties["Rev"] = "A";
            source.ConfigurationProperties["Rev"] = "B";

            var resolution = source.Resolve("Rev");

            Assert.Equal(PartPropertyResolutionKind.Resolved, resolution.Kind);
            Assert.Equal("B", resolution.Value);
        }

        [Fact]
        public void Resolve_lets_a_present_empty_configuration_value_shadow_a_document_value()
        {
            var source = PartSource();
            source.DocumentProperties["Rev"] = "A";
            source.ConfigurationProperties["Rev"] = "";

            var resolution = source.Resolve("Rev");

            // The store held the name — present-but-empty is its verdict, never
            // a fall-through to the document store and never silent.
            Assert.Equal(PartPropertyResolutionKind.Empty, resolution.Kind);
            Assert.Null(resolution.Value);
        }

        [Fact]
        public void Resolve_reports_missing_when_no_store_holds_the_name()
        {
            var resolution = PartSource().Resolve("NoSuchProp");

            Assert.Equal(PartPropertyResolutionKind.Missing, resolution.Kind);
            Assert.Null(resolution.Value);
        }

        [Fact]
        public void Resolve_names_the_reference_it_looked_for()
        {
            var resolution = PartSource().Resolve("NoSuchProp");

            Assert.Equal("NoSuchProp", resolution.Name);
        }

        [Fact]
        public void Resolve_reports_empty_distinct_from_missing()
        {
            var resolution = PartSource().Resolve("EmptyNote");

            Assert.Equal(PartPropertyResolutionKind.Empty, resolution.Kind);
            Assert.Null(resolution.Value);
        }

        [Fact]
        public void Resolve_treats_a_whitespace_value_as_resolved_verbatim()
        {
            var source = PartSource();
            source.DocumentProperties["SpacedNote"] = "   ";

            var resolution = source.Resolve("SpacedNote");

            // Whitespace is a real stored value — it resolves, it is not empty.
            Assert.Equal(PartPropertyResolutionKind.Resolved, resolution.Kind);
            Assert.Equal("   ", resolution.Value);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Resolve_an_unusable_name_is_missing(string? name)
        {
            var resolution = PartSource().Resolve(name!);

            Assert.Equal(PartPropertyResolutionKind.Missing, resolution.Kind);
        }

        [Fact]
        public void Resolve_without_a_part_reports_no_document()
        {
            var source = PartSource();
            source.Document = HostDocumentKind.None;

            var resolution = source.Resolve("Material");

            Assert.Equal(PartPropertyResolutionKind.NoDocument, resolution.Kind);
            Assert.Null(resolution.Value);
        }
    }
}
