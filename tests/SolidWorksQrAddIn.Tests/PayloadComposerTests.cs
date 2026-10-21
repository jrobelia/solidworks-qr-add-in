using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Payload;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the payload-composition module at its public seam — the
    /// contract the pane builds its content-type dropdown and per-preset
    /// input fields from: <see cref="PayloadComposer.Presets"/> descriptors
    /// and <see cref="PayloadComposer.Compose"/> output strings and errors.
    /// Assertions compare against known-good payload literals (the strings
    /// QR scanners act on), never against QRCoder internals.
    /// </summary>
    public class PayloadComposerTests
    {
        private static IReadOnlyDictionary<string, string> Fields(
            params (string key, string value)[] pairs)
        {
            var fields = new Dictionary<string, string>();
            foreach (var (key, value) in pairs)
                fields[key] = value;
            return fields;
        }

        // ---------------- descriptor contract (the pane's field model) ----------------

        [Fact]
        public void Presets_declare_the_content_type_presets_in_order()
        {
            // Spec #11 phase-1 list plus spec #21's SW Properties: Raw
            // first — it is the default content type and today's single
            // content box.
            var ids = PayloadComposer.Presets.Select(p => p.Id).ToArray();

            Assert.Equal(
                new[] { "raw", "url", "wifi", "contact", "mail", "sms", "phone", "geo",
                        "swprops" },
                ids);
            Assert.Equal("raw", PayloadComposer.DefaultPresetId);
        }

        [Fact]
        public void Every_preset_has_a_display_name_and_unique_field_keys()
        {
            foreach (var preset in PayloadComposer.Presets)
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.DisplayName));
                Assert.NotEmpty(preset.Fields);
                var keys = preset.Fields.Select(f => f.Key).ToArray();
                Assert.Equal(keys.Length, keys.Distinct().Count());
            }
        }

        [Fact]
        public void Every_choice_field_default_is_a_declared_option()
        {
            // The pane renders the default verbatim — a default outside the
            // declared options would compose a field the UI cannot show.
            foreach (var field in PayloadComposer.Presets.SelectMany(p => p.Fields))
            {
                if (field.Kind != PayloadFieldKind.Choice)
                    continue;
                var optionValues = field.Options.Select(o => o.Value).ToArray();
                Assert.Contains(field.DefaultValue, optionValues);
            }
        }

        // ---------------- SW Properties (spec #21, ticket #45) ----------------

        [Fact]
        public void Swprops_declares_a_single_template_kind_field()
        {
            var preset = PayloadComposer.Presets.Single(p => p.Id == "swprops");

            Assert.Equal("SW Properties", preset.DisplayName);
            var field = Assert.Single(preset.Fields);
            Assert.Equal("template", field.Key);
            Assert.Equal(PayloadFieldKind.Template, field.Kind);
            Assert.False(field.Required);
        }

        [Fact]
        public void Swprops_composes_the_template_verbatim_for_session_resolution()
        {
            // The composer is host-free by contract: $PRP tokens pass
            // through untouched — resolving them against the live part is
            // the session's job, not the composer's.
            var result = PayloadComposer.Compose("swprops",
                Fields(("template", "PART-$PRP:\"PartNo\"\nsecond line")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("PART-$PRP:\"PartNo\"\nsecond line", result.Content);
        }

        [Fact]
        public void Swprops_composes_an_absent_template_as_empty_for_downstream_validation()
        {
            var result = PayloadComposer.Compose("swprops", null);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(string.Empty, result.Content);
        }

        // ---------------- Raw ----------------

        [Fact]
        public void Raw_passes_content_through_byte_exact()
        {
            // Whitespace and line breaks are meaningful payload: the composed
            // string must equal the field value character for character.
            var content = "  leading spaces\nsecond line\r\nthird\ttab  ";

            var result = PayloadComposer.Compose("raw", Fields(("content", content)));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(content, result.Content);
        }

        [Fact]
        public void Raw_passes_empty_content_through_for_downstream_validation()
        {
            // Passthrough means passthrough: empty raw content composes and
            // the generator reports EmptyContent, exactly like the plain
            // content box does today.
            var result = PayloadComposer.Compose("raw", Fields(("content", "")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(string.Empty, result.Content);
        }

        // ---------------- Url ----------------

        [Fact]
        public void Url_with_scheme_composes_verbatim()
        {
            var result = PayloadComposer.Compose("url", Fields(("url", "https://example.com/x?y=1")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("https://example.com/x?y=1", result.Content);
        }

        [Fact]
        public void Url_without_scheme_gets_the_http_prefix()
        {
            var result = PayloadComposer.Compose("url", Fields(("url", "example.com")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("http://example.com", result.Content);
        }

        [Fact]
        public void Url_field_is_required()
        {
            var result = PayloadComposer.Compose("url", Fields(("url", "  ")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("url", result.Error.FieldKey);
        }

        // ---------------- WiFi ----------------

        [Fact]
        public void WiFi_composes_the_scanner_payload()
        {
            // The worked example from the spec: ssid + password + security →
            // the WIFI: string nobody wants to hand-type.
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "MyNet"), ("password", "secret"), ("security", "wpa")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("WIFI:T:WPA;S:MyNet;P:secret;;", result.Content);
        }

        [Fact]
        public void WiFi_defaults_to_wpa2_and_marks_hidden_networks()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "MyNet"), ("password", "secret"), ("hidden", "true")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("WIFI:T:WPA2;S:MyNet;P:secret;H:true;", result.Content);
        }

        [Fact]
        public void WiFi_escapes_reserved_characters()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "a;b\\c"), ("password", "p:w"), ("security", "wpa2")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("WIFI:T:WPA2;S:a\\;b\\\\c;P:p\\:w;;", result.Content);
        }

        [Fact]
        public void WiFi_open_network_ignores_the_password_field()
        {
            // A 'nopass' payload must not carry a password — the field is
            // deliberately dropped so P: stays empty.
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "OpenNet"), ("password", "leftover"), ("security", "nopass")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("WIFI:T:nopass;S:OpenNet;P:;;", result.Content);
        }

        [Fact]
        public void WiFi_secured_network_requires_a_password()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "MyNet"), ("security", "wpa2"), ("password", "")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("password", result.Error.FieldKey);
            Assert.Contains("Password", result.Error.Message);
        }

        [Fact]
        public void WiFi_open_network_needs_no_password()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "OpenNet"), ("security", "nopass")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("WIFI:T:nopass;S:OpenNet;P:;;", result.Content);
        }

        [Fact]
        public void WiFi_ssid_is_required()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("password", "secret"), ("security", "wpa2")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("ssid", result.Error.FieldKey);
        }

        [Fact]
        public void WiFi_rejects_an_unknown_security_value()
        {
            var result = PayloadComposer.Compose("wifi",
                Fields(("ssid", "MyNet"), ("password", "secret"), ("security", "wpa3")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, result.Error!.Kind);
            Assert.Equal("security", result.Error.FieldKey);
            Assert.Contains("wpa3", result.Error.Message);
        }

        // ---------------- PhoneNumber / SMS / Mail ----------------

        [Fact]
        public void PhoneNumber_composes_a_tel_uri()
        {
            var result = PayloadComposer.Compose("phone", Fields(("number", "+15550100")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("tel:+15550100", result.Content);
        }

        [Fact]
        public void PhoneNumber_rejects_letters()
        {
            var result = PayloadComposer.Compose("phone", Fields(("number", "call-mum")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, result.Error!.Kind);
            Assert.Equal("number", result.Error.FieldKey);
        }

        [Fact]
        public void PhoneNumber_is_required()
        {
            var result = PayloadComposer.Compose("phone", Fields(("number", "")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("number", result.Error.FieldKey);
        }

        [Fact]
        public void Sms_composes_number_and_message()
        {
            var result = PayloadComposer.Compose("sms",
                Fields(("number", "+15550100"), ("message", "hello there")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("sms:+15550100?body=hello%20there", result.Content);
        }

        [Fact]
        public void Sms_without_message_composes_the_bare_number()
        {
            var result = PayloadComposer.Compose("sms", Fields(("number", "+15550100")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("sms:+15550100", result.Content);
        }

        [Fact]
        public void Sms_number_is_required()
        {
            var result = PayloadComposer.Compose("sms", Fields(("message", "hi")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("number", result.Error.FieldKey);
        }

        [Fact]
        public void Mail_composes_a_mailto_uri()
        {
            var result = PayloadComposer.Compose("mail",
                Fields(("to", "a@b.com"), ("subject", "Hi"), ("body", "Body text")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("mailto:a@b.com?subject=Hi&body=Body%20text", result.Content);
        }

        [Fact]
        public void Mail_recipient_is_required_and_needs_an_at_sign()
        {
            var missing = PayloadComposer.Compose("mail", Fields(("subject", "Hi")));
            Assert.False(missing.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, missing.Error!.Kind);
            Assert.Equal("to", missing.Error.FieldKey);

            var malformed = PayloadComposer.Compose("mail", Fields(("to", "not-an-address")));
            Assert.False(malformed.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, malformed.Error!.Kind);
            Assert.Equal("to", malformed.Error.FieldKey);
        }

        // ---------------- Geolocation ----------------

        [Fact]
        public void Geolocation_composes_a_geo_uri()
        {
            var result = PayloadComposer.Compose("geo",
                Fields(("latitude", "47.3769"), ("longitude", "8.5417")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("geo:47.3769,8.5417", result.Content);
        }

        [Fact]
        public void Geolocation_google_maps_format_composes_a_maps_link()
        {
            var result = PayloadComposer.Compose("geo",
                Fields(("latitude", "47.3769"), ("longitude", "8.5417"), ("format", "googleMaps")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("http://maps.google.com/maps?q=47.3769,8.5417", result.Content);
        }

        [Fact]
        public void Geolocation_accepts_comma_decimals()
        {
            var result = PayloadComposer.Compose("geo",
                Fields(("latitude", "47,3769"), ("longitude", "8,5417")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("geo:47.3769,8.5417", result.Content);
        }

        [Fact]
        public void Geolocation_rejects_non_numeric_and_out_of_range_coordinates()
        {
            var notNumber = PayloadComposer.Compose("geo",
                Fields(("latitude", "north"), ("longitude", "8.5417")));
            Assert.False(notNumber.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, notNumber.Error!.Kind);
            Assert.Equal("latitude", notNumber.Error.FieldKey);

            var outOfRange = PayloadComposer.Compose("geo",
                Fields(("latitude", "91"), ("longitude", "8.5417")));
            Assert.False(outOfRange.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, outOfRange.Error!.Kind);
            Assert.Equal("latitude", outOfRange.Error.FieldKey);

            var badLongitude = PayloadComposer.Compose("geo",
                Fields(("latitude", "47"), ("longitude", "-181")));
            Assert.False(badLongitude.Succeeded);
            Assert.Equal("longitude", badLongitude.Error!.FieldKey);
        }

        // ---------------- ContactData ----------------

        [Fact]
        public void Contact_composes_a_vcard3_payload_by_default()
        {
            var result = PayloadComposer.Compose("contact", Fields(
                ("firstName", "John"), ("lastName", "Doe"),
                ("phone", "+1-555-0100"), ("email", "john@x.com")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(
                "BEGIN:VCARD\r\n" +
                "VERSION:3.0\r\n" +
                "N:Doe;John;;;\r\n" +
                "FN:John Doe\r\n" +
                "TEL;TYPE=HOME,VOICE:+1-555-0100\r\n" +
                "ADR;TYPE=HOME,PREF:;;;;;;\r\n" +
                "EMAIL:john@x.com\r\n" +
                "END:VCARD",
                result.Content);
        }

        [Fact]
        public void Contact_mecard_format_composes_a_mecard_payload()
        {
            var result = PayloadComposer.Compose("contact", Fields(
                ("format", "mecard"), ("firstName", "John"), ("lastName", "Doe"),
                ("phone", "+1-555-0100"), ("email", "john@x.com")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(
                "MECARD+\r\n" +
                "N:Doe, John\r\n" +
                "TEL:+1-555-0100\r\n" +
                "EMAIL:john@x.com\r\n" +
                "ADR:,,,,,,",
                result.Content);
        }

        [Fact]
        public void Contact_composes_with_a_last_name_only()
        {
            var result = PayloadComposer.Compose("contact", Fields(("lastName", "Doe")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Contains("N:Doe;;;;", result.Content);
            Assert.Contains("FN:Doe", result.Content);
        }

        [Fact]
        public void Contact_needs_at_least_a_first_or_last_name()
        {
            // An all-optional field set can still be semantically empty: a
            // vCard without a name is a malformed payload.
            var result = PayloadComposer.Compose("contact",
                Fields(("phone", "+1-555-0100")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
            Assert.Equal("firstName", result.Error.FieldKey);
            Assert.Contains("name", result.Error.Message);
        }

        [Fact]
        public void Contact_rejects_a_bad_email_and_bad_phone()
        {
            var badEmail = PayloadComposer.Compose("contact",
                Fields(("firstName", "John"), ("email", "nope")));
            Assert.False(badEmail.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, badEmail.Error!.Kind);
            Assert.Equal("email", badEmail.Error.FieldKey);

            var badPhone = PayloadComposer.Compose("contact",
                Fields(("firstName", "John"), ("phone", "call me")));
            Assert.False(badPhone.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.InvalidField, badPhone.Error!.Kind);
            Assert.Equal("phone", badPhone.Error.FieldKey);
        }

        // ---------------- shared behaviour ----------------

        [Fact]
        public void Compose_ignores_field_keys_the_preset_does_not_declare()
        {
            // The pane may hand over a stale map when the engineer switches
            // presets mid-edit — foreign keys must not fail composition.
            var result = PayloadComposer.Compose("url",
                Fields(("url", "example.com"), ("ssid", "leftover")));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("http://example.com", result.Content);
        }

        [Fact]
        public void Compose_accepts_a_null_field_map()
        {
            var result = PayloadComposer.Compose("phone", null);

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.MissingField, result.Error!.Kind);
        }

        // ---------------- unknown preset ----------------

        [Fact]
        public void Unknown_preset_id_returns_an_error_instead_of_throwing()
        {
            var result = PayloadComposer.Compose("bogus", Fields(("content", "x")));

            Assert.False(result.Succeeded);
            Assert.Equal(PayloadComposeErrorKind.UnknownPreset, result.Error!.Kind);
            Assert.Contains("bogus", result.Error.Message);
            Assert.Null(result.Content);
        }
    }
}
