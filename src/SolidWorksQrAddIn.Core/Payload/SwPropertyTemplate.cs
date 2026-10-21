using System.Collections.Generic;
using System.Text.RegularExpressions;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Core.Payload
{
    /// <summary>
    /// The payload-template resolver (spec #21, ticket #45). A template is
    /// literal text mixing verbatim characters with property references in
    /// SolidWorks' own note syntax — <c>$PRP:"name"</c> (the quoted form;
    /// <c>$PRP:name</c>, bare <c>$PRP</c> and friends are plain text).
    /// <see cref="Resolve"/> substitutes every reference through an
    /// <see cref="IPartPropertySource"/> — the configuration-over-document
    /// precedence lives in the source's <see cref="PartPropertyResolver"/>,
    /// this class only consumes its verdicts — and returns the resolved
    /// literal, the text the QR encodes (GLOSSARY: the resolved payload).
    ///
    /// Failures RETURN, never throw — the <see cref="PayloadComposeResult"/>
    /// idiom — and name the offending reference in its <c>$PRP:"name"</c>
    /// form, the way the engineer typed it. A malformed remnant —
    /// <c>$PRP:"</c> left after substitution (an empty or unclosed name) —
    /// fails openly too instead of silently encoding an almost-reference.
    /// </summary>
    public static class SwPropertyTemplate
    {
        // $PRP:"name" — the name is one or more non-quote characters; a name
        // containing a quote is simply not tokenizable (the regex IS the
        // grammar, matching the prototype's settled syntax).
        private static readonly Regex ReferencePattern =
            new Regex(@"\$PRP:""([^""]+)""", RegexOptions.Compiled);

        // On the template with every valid reference removed, a $PRP:"
        // remnant can only be a malformed token (empty or unclosed name) —
        // captured to name it in the error.
        private static readonly Regex RemnantPattern =
            new Regex(@"\$PRP:""[^""]*""?", RegexOptions.Compiled);

        /// <summary>
        /// The reference token for a property name — <c>$PRP:"name"</c>, the
        /// same quoted grammar <see cref="ReferencePattern"/> tokenizes.
        /// Surfaces that INSERT a reference (the pane's property dropdown)
        /// build it here so the token syntax is authored once. A name
        /// containing a double quote is not representable — the grammar
        /// excludes it; the token it produces can never round-trip.
        /// </summary>
        public static string ReferenceFor(string name) =>
            "$PRP:\"" + (name ?? throw new System.ArgumentNullException(nameof(name))) + "\"";

        /// <summary>
        /// Resolves <paramref name="template"/> against
        /// <paramref name="source"/> — a fresh read per call, never cached:
        /// property and configuration edits behind the UI's back must land
        /// in the resolved literal. A null source resolves like no active
        /// part document — nothing to read against — so tokenless text
        /// still composes verbatim while a reference fails honestly.
        /// </summary>
        /// <returns>
        /// The resolved literal on success, or a
        /// <see cref="PayloadComposeErrorKind.InvalidField"/> error naming
        /// the offending reference.
        /// </returns>
        public static PayloadComposeResult Resolve(string? template, IPartPropertySource? source)
        {
            var text = template ?? string.Empty;

            // Unique reference names in order of first appearance — the
            // first failure reported is the leftmost offender, and a
            // repeated name resolves once but substitutes everywhere.
            var names = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (Match match in ReferencePattern.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (seen.Add(name))
                    names.Add(name);
            }

            // The malformed-token guard scans the TEMPLATE with its valid
            // references stripped — deliberately never the resolved output:
            // a property VALUE that happens to contain "$PRP:" text is
            // literal content the author never typed (the QR may encode it
            // verbatim), not a token that failed to substitute.
            var sansReferences = ReferencePattern.Replace(text, string.Empty);
            if (names.Count == 0)
            {
                var remnant = RemnantPattern.Match(sansReferences);
                return remnant.Success
                    ? Malformed(remnant.Value)
                    : PayloadComposeResult.Ok(text);
            }

            var resolved = text;
            foreach (var name in names)
            {
                var token = ReferenceFor(name);
                var resolution = source?.Resolve(name)
                                 ?? PartPropertyResolution.NoDocument(name);
                switch (resolution.Kind)
                {
                    case PartPropertyResolutionKind.Resolved
                        when resolution.Value != null && resolution.Value.Length > 0:
                        resolved = resolved.Replace(token, resolution.Value);
                        break;
                    case PartPropertyResolutionKind.Resolved:
                    case PartPropertyResolutionKind.Empty:
                        return Fail(
                            "Property reference " + token + " resolves to an " +
                            "empty value — a present-but-blank property cannot be encoded.");
                    case PartPropertyResolutionKind.Missing:
                        return Fail(
                            "Property reference " + token + " names no property " +
                            "of the active part.");
                    default:
                        return Fail(
                            "Property reference " + token + " needs an active " +
                            "part document to resolve against.");
                }
            }

            var leftover = RemnantPattern.Match(sansReferences);
            return leftover.Success
                ? Malformed(leftover.Value)
                : PayloadComposeResult.Ok(resolved);
        }

        private static PayloadComposeResult Fail(string message) =>
            PayloadComposeResult.Fail(PayloadComposeErrorKind.InvalidField, null, message);

        private static PayloadComposeResult Malformed(string remnant) =>
            Fail("'" + remnant + "' looks like a property reference but is " +
                 "malformed — the syntax is $PRP:\"name\" (a name in double quotes).");
    }
}
