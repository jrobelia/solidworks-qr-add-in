using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QRCoder;

namespace SolidWorksQrAddIn.Core.Payload
{
    /// <summary>
    /// The ONE payload-composition module (spec #11 decision — deliberately
    /// not a class-per-preset swarm): structured preset fields in, the
    /// literal content string out, internally dispatching to
    /// <see cref="PayloadGenerator"/> types. Host-free Core code.
    ///
    /// The public surface IS the pane's contract (ticket #27):
    ///
    ///   * <see cref="Presets"/> — the content-type dropdown entries and the
    ///     ordered field descriptors each preset renders as inputs. The pane
    ///     builds controls from <see cref="PayloadFieldDescriptor.Kind"/>
    ///     alone and never hard-codes per-preset UI.
    ///   * <see cref="Compose"/> — preset id + a field map keyed by the
    ///     descriptors' <see cref="PayloadFieldDescriptor.Key"/>s → the
    ///     literal string that would be encoded (whitespace/line breaks
    ///     preserved exactly — it lands in the content box verbatim) or one
    ///     actionable error naming the bad field. Unknown field-map keys are
    ///     ignored; missing keys fall back to the field's
    ///     <see cref="PayloadFieldDescriptor.DefaultValue"/>.
    ///
    /// Errors RETURN — never thrown toward the caller — following the
    /// <c>QrSketchResult</c> idiom, so a malformed payload can never reach
    /// a sketch.
    /// </summary>
    public static class PayloadComposer
    {
        /// <summary>The preset the content-type field starts on — today's single content box.</summary>
        public const string DefaultPresetId = "raw";

        private const string WifiSecurityWpa2 = "wpa2";
        private const string WifiSecurityNopass = "nopass";

        private static readonly IReadOnlyDictionary<string, string> NoFields =
            new Dictionary<string, string>();

        private static readonly PayloadPresetDescriptor[] AllPresets =
        {
            new PayloadPresetDescriptor("raw", "Raw text", new[]
            {
                Field("content", "Content", PayloadFieldKind.MultilineText,
                    hint: "Text or data to encode exactly"),
            }),
            new PayloadPresetDescriptor("url", "URL", new[]
            {
                Field("url", "URL", PayloadFieldKind.Text, required: true,
                    hint: "https://example.com"),
            }),
            new PayloadPresetDescriptor("wifi", "WiFi", new[]
            {
                Field("ssid", "Network name (SSID)", PayloadFieldKind.Text, required: true),
                Field("security", "Security", PayloadFieldKind.Choice,
                    defaultValue: WifiSecurityWpa2,
                    options: Options(
                        (WifiSecurityWpa2, "WPA2"),
                        ("wpa", "WPA"),
                        ("wep", "WEP"),
                        (WifiSecurityNopass, "None (open)"))),
                Field("password", "Password", PayloadFieldKind.Text),
                Field("hidden", "Hidden network (SSID not broadcast)", PayloadFieldKind.Boolean,
                    defaultValue: "false"),
            }),
            new PayloadPresetDescriptor("contact", "Contact (vCard)", new[]
            {
                Field("format", "Card format", PayloadFieldKind.Choice,
                    defaultValue: "vcard3",
                    options: Options(
                        ("vcard3", "vCard 3.0"),
                        ("vcard4", "vCard 4.0"),
                        ("vcard21", "vCard 2.1"),
                        ("mecard", "MeCard"))),
                Field("firstName", "First name", PayloadFieldKind.Text),
                Field("lastName", "Last name", PayloadFieldKind.Text),
                Field("organization", "Organization", PayloadFieldKind.Text),
                Field("title", "Job title", PayloadFieldKind.Text),
                Field("phone", "Phone", PayloadFieldKind.Text, hint: "+1-555-0100"),
                Field("mobile", "Mobile phone", PayloadFieldKind.Text, hint: "+1-555-0101"),
                Field("email", "E-mail", PayloadFieldKind.Text, hint: "name@example.com"),
                Field("website", "Website", PayloadFieldKind.Text, hint: "https://example.com"),
                Field("street", "Street", PayloadFieldKind.Text),
                Field("city", "City", PayloadFieldKind.Text),
                Field("state", "State / region", PayloadFieldKind.Text),
                Field("zip", "ZIP / postal code", PayloadFieldKind.Text),
                Field("country", "Country", PayloadFieldKind.Text),
                Field("note", "Note", PayloadFieldKind.MultilineText),
            }),
            new PayloadPresetDescriptor("mail", "E-mail", new[]
            {
                Field("to", "To", PayloadFieldKind.Text, required: true,
                    hint: "name@example.com"),
                Field("subject", "Subject", PayloadFieldKind.Text),
                Field("body", "Message", PayloadFieldKind.MultilineText),
            }),
            new PayloadPresetDescriptor("sms", "SMS", new[]
            {
                Field("number", "Phone number", PayloadFieldKind.Text, required: true,
                    hint: "+15550100"),
                Field("message", "Message", PayloadFieldKind.MultilineText),
            }),
            new PayloadPresetDescriptor("phone", "Phone call", new[]
            {
                Field("number", "Phone number", PayloadFieldKind.Text, required: true,
                    hint: "+15550100"),
            }),
            new PayloadPresetDescriptor("geo", "Geolocation", new[]
            {
                Field("latitude", "Latitude", PayloadFieldKind.Text, required: true,
                    hint: "47.3769"),
                Field("longitude", "Longitude", PayloadFieldKind.Text, required: true,
                    hint: "8.5417"),
                Field("format", "Link format", PayloadFieldKind.Choice,
                    defaultValue: "geo",
                    options: Options(
                        ("geo", "geo: URI"),
                        ("googleMaps", "Google Maps link"))),
            }),
            new PayloadPresetDescriptor("swprops", "SW Properties", new[]
            {
                // Spec #21: a template field — literal text plus $PRP:"name"
                // references. The composer stays host-free: it passes the
                // template through verbatim and the SESSION resolves it
                // against the live part (see SwPropertyTemplate).
                Field("template", "Template", PayloadFieldKind.Template,
                    hint: "Text plus $PRP:\"name\" tokens — resolved against the active part."),
            }),
        };

        /// <summary>
        /// The phase-1 preset list in dropdown order — Raw first because it
        /// is <see cref="DefaultPresetId"/>.
        /// </summary>
        public static IReadOnlyList<PayloadPresetDescriptor> Presets => AllPresets;

        /// <summary>
        /// Composes the literal payload string for <paramref name="presetId"/>
        /// from <paramref name="fields"/> (keyed by field descriptor Key).
        /// Never throws: every failure returns a
        /// <see cref="PayloadComposeError"/> naming the bad field.
        /// </summary>
        public static PayloadComposeResult Compose(
            string presetId, IReadOnlyDictionary<string, string>? fields)
        {
            var preset = FindPreset(presetId);
            if (preset == null)
            {
                return PayloadComposeResult.Fail(
                    PayloadComposeErrorKind.UnknownPreset, null,
                    "Unknown payload preset '" + (presetId ?? string.Empty) + "'. Known presets: " +
                    string.Join(", ", AllPresets.Select(p => p.Id)) + ".");
            }

            fields ??= NoFields;

            // Resolve every declared field in display order: Choice/Boolean
            // take their default when absent; Required rejects blanks — so
            // per-preset code below only ever sees sane values.
            var values = new Dictionary<string, string?>();
            foreach (var field in preset.Fields)
            {
                fields.TryGetValue(field.Key, out var entered);
                switch (field.Kind)
                {
                    case PayloadFieldKind.Choice:
                    {
                        var value = string.IsNullOrEmpty(entered) ? field.DefaultValue : entered!;
                        var option = field.Options?.FirstOrDefault(
                            o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));
                        if (option == null)
                        {
                            return InvalidField(preset, field,
                                "must be one of " +
                                string.Join(", ", field.Options?.Select(o => o.Value) ??
                                              Enumerable.Empty<string>()) +
                                " — got '" + entered + "'");
                        }
                        values[field.Key] = option.Value;
                        break;
                    }
                    case PayloadFieldKind.Boolean:
                    {
                        var value = string.IsNullOrEmpty(entered) ? field.DefaultValue : entered!;
                        if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                        {
                            return InvalidField(preset, field,
                                "must be 'true' or 'false' — got '" + entered + "'");
                        }
                        values[field.Key] = value.ToLowerInvariant();
                        break;
                    }
                    default:
                    {
                        if (field.Required && string.IsNullOrWhiteSpace(entered))
                        {
                            return PayloadComposeResult.Fail(
                                PayloadComposeErrorKind.MissingField, field.Key,
                                preset.DisplayName + ": '" + field.Label + "' is required.");
                        }
                        values[field.Key] = entered;
                        break;
                    }
                }
            }

            try
            {
                switch (preset.Id)
                {
                    case "raw":
                        return PayloadComposeResult.Ok(values["content"] ?? string.Empty);
                    case "url":
                        return PayloadComposeResult.Ok(
                            new PayloadGenerator.Url(values["url"]!).ToString());
                    case "wifi":
                        return ComposeWifi(preset, values);
                    case "phone":
                    {
                        var number = values["number"]!;
                        if (!IsPhoneNumber(number))
                        {
                            return InvalidField(preset,
                                FieldNamed(preset, "number"),
                                PhoneNumberReason(number));
                        }
                        return PayloadComposeResult.Ok(
                            new PayloadGenerator.PhoneNumber(number).ToString());
                    }
                    case "sms":
                    {
                        var number = values["number"]!;
                        if (!IsPhoneNumber(number))
                        {
                            return InvalidField(preset,
                                FieldNamed(preset, "number"),
                                PhoneNumberReason(number));
                        }
                        var message = values["message"];
                        return PayloadComposeResult.Ok(
                            new PayloadGenerator.SMS(number, message ?? string.Empty).ToString());
                    }
                    case "mail":
                    {
                        var to = values["to"]!;
                        if (!LooksLikeEmailAddress(to))
                        {
                            return InvalidField(preset,
                                FieldNamed(preset, "to"), EmailReason(to));
                        }
                        return PayloadComposeResult.Ok(
                            new PayloadGenerator.Mail(
                                to, values["subject"], values["body"]).ToString());
                    }
                    case "contact":
                        return ComposeContact(preset, values);
                    case "geo":
                        return ComposeGeo(preset, values);
                    case "swprops":
                        // Host-free by contract: the template passes through
                        // verbatim — $PRP resolution is the session's job.
                        return PayloadComposeResult.Ok(values["template"] ?? string.Empty);
                    default:
                        return PayloadComposeResult.Fail(
                            PayloadComposeErrorKind.GeneratorFailure, null,
                            "Payload preset '" + preset.Id + "' is not implemented yet.");
                }
            }
            catch (Exception ex)
            {
                return PayloadComposeResult.Fail(
                    PayloadComposeErrorKind.GeneratorFailure, null,
                    preset.DisplayName + " payload generation failed: " + ex.Message);
            }
        }

        private static PayloadComposeResult ComposeWifi(
            PayloadPresetDescriptor preset, Dictionary<string, string?> values)
        {
            var security = values["security"]!;
            var password = values["password"];
            PayloadGenerator.WiFi.Authentication mode;
            switch (security)
            {
                case "wpa": mode = PayloadGenerator.WiFi.Authentication.WPA; break;
                case "wep": mode = PayloadGenerator.WiFi.Authentication.WEP; break;
                case WifiSecurityNopass: mode = PayloadGenerator.WiFi.Authentication.nopass; break;
                default: mode = PayloadGenerator.WiFi.Authentication.WPA2; break;
            }

            // An open network has no password to encode — the field is
            // dropped so the payload carries an empty P: instead of a
            // leftover secret.
            if (mode == PayloadGenerator.WiFi.Authentication.nopass)
            {
                password = string.Empty;
            }
            else if (string.IsNullOrEmpty(password))
            {
                var passwordField = FieldNamed(preset, "password");
                return PayloadComposeResult.Fail(
                    PayloadComposeErrorKind.MissingField, passwordField.Key,
                    preset.DisplayName + ": '" + passwordField.Label +
                    "' is required for a secured network — pick 'None (open)' security " +
                    "for a password-less network.");
            }

            return PayloadComposeResult.Ok(
                new PayloadGenerator.WiFi(
                    values["ssid"]!, password!,
                    mode, values["hidden"] == "true").ToString());
        }

        private static PayloadComposeResult ComposeContact(
            PayloadPresetDescriptor preset, Dictionary<string, string?> values)
        {
            // Every contact field is optional, but a vCard without a name is
            // a malformed payload — at least one name field must hold text.
            if (string.IsNullOrWhiteSpace(values["firstName"]) &&
                string.IsNullOrWhiteSpace(values["lastName"]))
            {
                var nameField = FieldNamed(preset, "firstName");
                return PayloadComposeResult.Fail(
                    PayloadComposeErrorKind.MissingField, nameField.Key,
                    preset.DisplayName + ": a contact needs a first or last name.");
            }

            var phone = values["phone"];
            if (!string.IsNullOrEmpty(phone) && !IsPhoneNumber(phone!))
            {
                return InvalidField(preset,
                    FieldNamed(preset, "phone"), PhoneNumberReason(phone!));
            }
            var mobile = values["mobile"];
            if (!string.IsNullOrEmpty(mobile) && !IsPhoneNumber(mobile!))
            {
                return InvalidField(preset,
                    FieldNamed(preset, "mobile"), PhoneNumberReason(mobile!));
            }
            var email = values["email"];
            if (!string.IsNullOrEmpty(email) && !LooksLikeEmailAddress(email!))
            {
                return InvalidField(preset,
                    FieldNamed(preset, "email"), EmailReason(email!));
            }

            PayloadGenerator.ContactData.ContactOutputType outputType;
            switch (values["format"])
            {
                case "vcard4": outputType = PayloadGenerator.ContactData.ContactOutputType.VCard4; break;
                case "vcard21": outputType = PayloadGenerator.ContactData.ContactOutputType.VCard21; break;
                case "mecard": outputType = PayloadGenerator.ContactData.ContactOutputType.MeCard; break;
                default: outputType = PayloadGenerator.ContactData.ContactOutputType.VCard3; break;
            }

            return PayloadComposeResult.Ok(
                new PayloadGenerator.ContactData(
                    outputType,
                    values["firstName"] ?? string.Empty,
                    values["lastName"] ?? string.Empty,
                    phone: phone, mobilePhone: mobile, email: email,
                    website: values["website"], street: values["street"],
                    city: values["city"], zipCode: values["zip"],
                    country: values["country"], note: values["note"],
                    stateRegion: values["state"], org: values["organization"],
                    orgTitle: values["title"]).ToString());
        }

        private static PayloadComposeResult ComposeGeo(
            PayloadPresetDescriptor preset, Dictionary<string, string?> values)
        {
            var latitude = values["latitude"]!;
            var longitude = values["longitude"]!;
            if (!TryParseCoordinate(latitude, -90.0, 90.0))
            {
                return InvalidField(preset, FieldNamed(preset, "latitude"),
                    "must be a number from -90 to 90 — got '" + latitude + "'");
            }
            if (!TryParseCoordinate(longitude, -180.0, 180.0))
            {
                return InvalidField(preset, FieldNamed(preset, "longitude"),
                    "must be a number from -180 to 180 — got '" + longitude + "'");
            }

            var encoding = values["format"] == "googleMaps"
                ? PayloadGenerator.Geolocation.GeolocationEncoding.GoogleMaps
                : PayloadGenerator.Geolocation.GeolocationEncoding.GEO;
            return PayloadComposeResult.Ok(
                new PayloadGenerator.Geolocation(latitude, longitude, encoding).ToString());
        }

        /// <summary>
        /// Geographic coordinates accept either '.' or ',' as the decimal
        /// separator — the PayloadGenerator itself normalizes ',' to '.'.
        /// </summary>
        private static bool TryParseCoordinate(string value, double min, double max)
        {
            var normalized = value.Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
                       out double parsed) &&
                   parsed >= min && parsed <= max;
        }

        /// <summary>
        /// Phone-like fields accept the dialable set only — digits, spaces
        /// and the usual separators/pauses — so 'call-mum' can never become
        /// a tel: payload.
        /// </summary>
        private static bool IsPhoneNumber(string value)
        {
            foreach (var c in value)
            {
                if (!(char.IsDigit(c) || c == ' ' || c == '+' || c == '-' || c == '(' ||
                      c == ')' || c == '.' || c == ',' || c == ';' || c == '*' || c == '#'))
                    return false;
            }
            return value.Length > 0;
        }

        private static string PhoneNumberReason(string value) =>
            "may only contain digits, spaces and + - ( ) . , ; * # — got '" + value + "'";

        private static bool LooksLikeEmailAddress(string value) =>
            value.IndexOf('@') >= 1 && !value.Contains(" ");

        private static string EmailReason(string value) =>
            "must be an e-mail address like name@example.com — got '" + value + "'";

        private static PayloadPresetDescriptor? FindPreset(string presetId) =>
            AllPresets.FirstOrDefault(
                p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The descriptor for one declared field — every key below is declared.</summary>
        private static PayloadFieldDescriptor FieldNamed(
            PayloadPresetDescriptor preset, string key) =>
            preset.Fields.First(f => f.Key == key);

        private static PayloadComposeResult InvalidField(
            PayloadPresetDescriptor preset, PayloadFieldDescriptor field, string reason) =>
            PayloadComposeResult.Fail(
                PayloadComposeErrorKind.InvalidField, field.Key,
                preset.DisplayName + ": '" + field.Label + "' " + reason + ".");

        private static PayloadFieldDescriptor Field(
            string key, string label, PayloadFieldKind kind, bool required = false,
            string defaultValue = "", string? hint = null,
            IReadOnlyList<PayloadFieldOption>? options = null) =>
            new PayloadFieldDescriptor(key, label, kind, required, defaultValue, hint, options);

        private static IReadOnlyList<PayloadFieldOption> Options(
            params (string value, string label)[] options) =>
            options.Select(o => new PayloadFieldOption(o.value, o.label)).ToArray();
    }
}
