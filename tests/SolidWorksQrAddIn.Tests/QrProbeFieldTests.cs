using System;
using System.Collections.Generic;
using QRCoder;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #67, ticket #65 — the collapsed probe field surface. The ten
    /// per-field probe setters fold into <c>SetQrField(name, value)</c>;
    /// <see cref="QrProbeFields"/> owns the name vocabulary (the same
    /// <c>WindowStateProbe</c> echo names) and resolves a name + value into
    /// the session edit to apply, with the former setters' verdict
    /// semantics intact: parse-level refusals answer a harness-level
    /// "ERROR ..." line (unknown ECC level, unknown cell shape, a bad
    /// toggle value) and everything else reaches the session and answers
    /// its VALID/INVALID verdict. A name outside the session vocabulary is
    /// a preset-field key — <see cref="QrSketchSession.SetPresetField"/>
    /// already refuses undeclared keys openly.
    /// </summary>
    public class QrProbeFieldTests
    {
        private const string Payload = "SW-QR-MVP";

        private static (QrSketchSession session, List<FakeQrWindow> windows) NewSession()
        {
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            return (session, windows);
        }

        /// <summary>
        /// Resolves name+value and applies the edit — the add-in's
        /// <c>SetQrField</c> body without the COM/probe strings.
        /// </summary>
        private static QrSketchResult Apply(QrSketchSession session, string name, string value)
        {
            Assert.True(QrProbeFields.TryResolve(name, value, out var edit, out var error),
                $"expected a session edit for '{name}', got the harness-level refusal: {error}");
            Assert.NotNull(edit);
            return edit!(session);
        }

        /// <summary>Two verdicts are the same when success, error kind, and geometry agree.</summary>
        private static void AssertSameVerdict(QrSketchResult viaField, QrSketchResult direct)
        {
            Assert.Equal(direct.Succeeded, viaField.Succeeded);
            Assert.Equal(direct.Error?.Kind, viaField.Error?.Kind);
            Assert.Equal(direct.Sketch?.Info.CellCount, viaField.Sketch?.Info.CellCount);
        }

        // ---------------- session-field vocabulary ----------------

        [Theory]
        [InlineData("content", "x")]
        [InlineData("size", "40")]
        [InlineData("growth", "8")]
        [InlineData("ecc", "M")]
        [InlineData("invert", "true")]
        [InlineData("ringsplit", "false")]
        [InlineData("shape", "square")]
        [InlineData("param", "90")]
        [InlineData("preset", "raw")]
        public void Session_field_names_resolve_to_an_edit(string name, string value)
        {
            Assert.True(QrProbeFields.TryResolve(name, value, out var edit, out var error),
                $"'{name}' must resolve — got: {error}");
            Assert.NotNull(edit);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("SIZE")]
        [InlineData("Size")]
        [InlineData(" size ")]
        [InlineData("RINGSPLIT")]
        [InlineData("PRESET")]
        public void Session_field_names_match_case_insensitively(string name)
        {
            Assert.True(QrProbeFields.TryResolve(name, "false", out var edit, out _));
            Assert.NotNull(edit);
        }

        [Fact]
        public void Content_field_matches_SetContent()
        {
            var (session, _) = NewSession();
            AssertSameVerdict(Apply(session, "content", Payload), session.SetContent(Payload));
            Assert.Equal(Payload, session.Content);
        }

        [Fact]
        public void Content_field_keeps_the_empty_content_refusal()
        {
            var (session, _) = NewSession();
            var via = Apply(session, "content", "");
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, via.Error!.Kind);
        }

        [Fact]
        public void Size_field_matches_SetSizeText_valid_and_invalid()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            AssertSameVerdict(Apply(session, "size", "40"), session.SetSizeText("40"));
            var via = Apply(session, "size", "abc");
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidSize, via.Error!.Kind);
            Assert.Equal("abc", session.SizeText);   // rejected text kept verbatim
        }

        [Fact]
        public void Growth_field_matches_SetGrowthText_valid_and_invalid()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            AssertSameVerdict(Apply(session, "growth", "8"), session.SetGrowthText("8"));
            Assert.False(Apply(session, "growth", "50").Succeeded);
        }

        [Fact]
        public void Ecc_field_parses_like_the_old_setter()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.True(Apply(session, "ecc", "h").Succeeded);   // case-insensitive
            Assert.Equal(QRCodeGenerator.ECCLevel.H, session.EccLevel);
            Assert.True(Apply(session, "ecc", "M").Succeeded);
            Assert.Equal(QRCodeGenerator.ECCLevel.M, session.EccLevel);
        }

        [Fact]
        public void Ecc_field_refuses_an_unknown_level_with_the_same_error_line()
        {
            Assert.False(QrProbeFields.TryResolve("ecc", "bogus", out var edit, out var error));
            Assert.Null(edit);
            Assert.Equal("ERROR unknown ECC level 'bogus' — expected L, M, Q or H.", error);
            // Nothing reaches the session: an unparseable pick is refused
            // before it can invalidate the pending request.
            var (session, _) = NewSession();
            Assert.True(session.SetContent(Payload).Succeeded);
            Assert.False(QrProbeFields.TryResolve("ecc", "bogus", out _, out _));
            Assert.True(session.HasValidRequest);
        }

        [Fact]
        public void Shape_field_parses_like_the_old_setter()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.True(Apply(session, "shape", "circle").Succeeded);
            Assert.Equal(QrCellShape.Circle, session.CellShape);
            Assert.True(Apply(session, "shape", "SQUARE").Succeeded);
            Assert.Equal(QrCellShape.Square, session.CellShape);
        }

        [Fact]
        public void Shape_field_refuses_an_unknown_shape_with_the_same_error_line()
        {
            Assert.False(QrProbeFields.TryResolve("shape", "bogus", out var edit, out var error));
            Assert.Null(edit);
            Assert.Equal("ERROR unknown cell shape 'bogus' — expected square or circle.", error);
        }

        [Fact]
        public void Param_field_matches_SetShapeParameterText()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            AssertSameVerdict(Apply(session, "param", "80"), session.SetShapeParameterText("80"));
            Assert.False(Apply(session, "param", "abc").Succeeded);
            // Under Square the text is stored verbatim — inert, never dropped.
            session.SetCellShape(QrCellShape.Square);
            Apply(session, "param", "80");
            Assert.Equal("80", session.ShapeParameterText);
        }

        [Fact]
        public void Invert_field_matches_SetInvert()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.True(Apply(session, "invert", "true").Succeeded);
            Assert.True(session.Invert);
            Assert.True(Apply(session, "invert", "FALSE").Succeeded);
            Assert.False(session.Invert);
        }

        [Fact]
        public void Invert_field_refuses_a_non_toggle_value()
        {
            Assert.False(QrProbeFields.TryResolve("invert", "yes", out var edit, out var error));
            Assert.Null(edit);
            Assert.Equal("ERROR unknown invert value 'yes' — expected true or false.", error);
        }

        [Fact]
        public void RingSplit_field_matches_SetRingSplit()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.True(Apply(session, "ringsplit", "true").Succeeded);
            Assert.True(session.RingSplit);
            Assert.True(Apply(session, "ringsplit", "false").Succeeded);
            Assert.False(session.RingSplit);
        }

        [Fact]
        public void RingSplit_field_keeps_the_below_M_ecc_refusal()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetEccLevel(QRCodeGenerator.ECCLevel.L);
            var via = Apply(session, "ringsplit", "true");
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidEccLevel, via.Error!.Kind);
        }

        [Fact]
        public void RingSplit_field_refuses_a_non_toggle_value()
        {
            Assert.False(QrProbeFields.TryResolve("ringsplit", "1", out var edit, out var error));
            Assert.Null(edit);
            Assert.Equal("ERROR unknown ringsplit value '1' — expected true or false.", error);
        }

        [Fact]
        public void Preset_field_matches_SetContentType()
        {
            var (session, _) = NewSession();
            AssertSameVerdict(Apply(session, "preset", "wifi"), session.SetContentType("wifi"));
            Assert.Equal("wifi", session.ContentPresetId);
            var via = Apply(session, "preset", "bogus");
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, via.Error!.Kind);
        }

        // ---------------- preset-field keys ----------------

        [Fact]
        public void An_unknown_top_level_name_is_a_preset_field_key()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            // "ssid" is a wifi preset field — the write lands like the
            // pane's preset input, not a session-field edit (the verdict
            // itself is INVALID: wifi still misses its required password).
            Apply(session, "ssid", "QRNET");
            Assert.Equal("QRNET", session.PresetFields["ssid"]);
        }

        [Fact]
        public void An_undeclared_preset_key_fails_openly_naming_it()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            var via = Apply(session, "nonesuch", "x");
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, via.Error!.Kind);
            Assert.Contains("nonesuch", via.Error!.Message);
        }

        [Fact]
        public void Preset_keys_are_not_case_normalized()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            // Field keys pass through verbatim — "SSID" is not "ssid".
            Assert.False(Apply(session, "SSID", "x").Succeeded);
            Apply(session, "ssid", "QRNET");
            Assert.Equal("QRNET", session.PresetFields["ssid"]);
        }

        [Fact]
        public void Content_names_the_box_under_every_preset()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            // "content" is the session-field name (the raw preset's only
            // field IS the content box — under Raw the two paths already
            // converge on SetContent).
            var via = Apply(session, "content", "manual text");
            Assert.Equal("manual text", session.Content);
            Assert.Equal("raw", session.ContentPresetId);   // a verbatim edit reverts to Raw
        }

        // ---------------- malformed invocations ----------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_field_name_is_a_harness_level_refusal(string? name)
        {
            Assert.False(QrProbeFields.TryResolve(name, "x", out var edit, out var error));
            Assert.Null(edit);
            Assert.NotNull(error);
            Assert.StartsWith("ERROR", error);
        }

        [Fact]
        public void A_null_value_reaches_the_session_as_empty_text()
        {
            var (session, _) = NewSession();
            var via = Apply(session, "content", null!);
            Assert.False(via.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, via.Error!.Kind);
        }
    }
}
