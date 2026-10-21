using System;
using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>What resolving one property reference found.</summary>
    public enum PartPropertyResolutionKind
    {
        /// <summary>
        /// A store held the name and the resolved value is the verbatim
        /// string SolidWorks renders for the reference (whitespace counts —
        /// it is a real value, not an empty one).
        /// </summary>
        Resolved,

        /// <summary>A store held the name but its resolved value is the empty string.</summary>
        Empty,

        /// <summary>No readable store held the name.</summary>
        Missing,

        /// <summary>There was no active part document to resolve against.</summary>
        NoDocument,
    }

    /// <summary>
    /// The named result of resolving one property reference — a
    /// <c>$PRP:"name"</c>-style lookup against the active part. Missing,
    /// empty and resolved are distinguishable results; an empty value never
    /// passes silently for a resolved one. Result-as-data, like
    /// <see cref="CleanupOutcome"/> — consumers read <see cref="Kind"/>,
    /// never infer it from the value.
    /// </summary>
    public sealed class PartPropertyResolution
    {
        private PartPropertyResolution(PartPropertyResolutionKind kind, string name, string? value)
        {
            Kind = kind;
            Name = name;
            Value = value;
        }

        /// <summary>What the lookup found.</summary>
        public PartPropertyResolutionKind Kind { get; }

        /// <summary>The reference name that was asked for — verdicts name the offender.</summary>
        public string Name { get; }

        /// <summary>The resolved value verbatim — null unless <see cref="Kind"/> is Resolved.</summary>
        public string? Value { get; }

        public static PartPropertyResolution Resolved(string name, string value) =>
            new PartPropertyResolution(PartPropertyResolutionKind.Resolved, name, value);

        public static PartPropertyResolution Empty(string name) =>
            new PartPropertyResolution(PartPropertyResolutionKind.Empty, name, null);

        public static PartPropertyResolution Missing(string name) =>
            new PartPropertyResolution(PartPropertyResolutionKind.Missing, name, null);

        public static PartPropertyResolution NoDocument(string name) =>
            new PartPropertyResolution(PartPropertyResolutionKind.NoDocument, name, null);
    }

    /// <summary>
    /// Point-in-time snapshot of the resolvable property names for the
    /// active part and its active configuration — the enumerate half of the
    /// seam, carrying which document and configuration the names were read
    /// from so a stale list is diagnosable. <see cref="Document"/> is not
    /// <see cref="HostDocumentKind.Part"/> when there is no active part —
    /// then <see cref="Names"/> is empty and <see cref="ConfigurationName"/>
    /// null.
    /// </summary>
    public sealed class PartPropertySnapshot
    {
        public PartPropertySnapshot(
            HostDocumentKind document, string? documentTitle,
            string? configurationName, IReadOnlyList<string> names)
        {
            Document = document;
            DocumentTitle = documentTitle;
            ConfigurationName = configurationName;
            Names = names;
        }

        /// <summary>The kind of the document read — <see cref="HostDocumentKind.Part"/> when resolvable.</summary>
        public HostDocumentKind Document { get; }

        /// <summary>Active document's title, when the host could report one.</summary>
        public string? DocumentTitle { get; }

        /// <summary>Active configuration's name — null without an active part.</summary>
        public string? ConfigurationName { get; }

        /// <summary>
        /// Every resolvable name, merged from the configuration-specific and
        /// document-level stores with configuration names first and each
        /// name listed once.
        /// </summary>
        public IReadOnlyList<string> Names { get; }
    }

    /// <summary>
    /// One readable property store of the active part — the document-level
    /// store or one configuration's specific store. A host adapts a
    /// CustomPropertyManager to this; the fake adapts dictionaries.
    /// </summary>
    public interface IPartPropertyStore
    {
        /// <summary>Every name this store holds, in the host's order; empty when none.</summary>
        IReadOnlyList<string> GetNames();

        /// <summary>
        /// Reads one name. <see cref="PartPropertyRead.Present"/> says the
        /// store holds it (the value may be empty); absent and present-but-
        /// empty are different answers, never conflated.
        /// </summary>
        PartPropertyRead Read(string name);
    }

    /// <summary>What reading one name out of a store found.</summary>
    public sealed class PartPropertyRead
    {
        private PartPropertyRead(bool present, string value)
        {
            Present = present;
            Value = value;
        }

        /// <summary>The store holds the name.</summary>
        public bool Present { get; }

        /// <summary>The resolved value verbatim — "" when the property is present-but-empty.</summary>
        public string Value { get; }

        /// <summary>The store does not hold the name.</summary>
        public static readonly PartPropertyRead Absent = new PartPropertyRead(false, "");

        /// <summary>The store holds the name; <paramref name="value"/> is the resolved value.</summary>
        public static PartPropertyRead Found(string value) =>
            new PartPropertyRead(true, value ?? "");
    }

    /// <summary>
    /// The part-property seam — a sibling of <see cref="ISketchHost"/>, not
    /// a member of it: the property read is a different capability than the
    /// create-path contract, and hosts/fakes that only draw sketches must
    /// not have to grow property members. The native adapter implements
    /// this over the live document's CustomPropertyManagers; a new source
    /// is used per read so property and configuration edits behind the UI's
    /// back are never hidden by a stale capture.
    /// </summary>
    public interface IPartPropertySource
    {
        /// <summary>
        /// Enumerates the resolvable property names for the active part and
        /// its active configuration — the names a <c>$PRP:"name"</c>
        /// reference could resolve to.
        /// </summary>
        PartPropertySnapshot CaptureProperties();

        /// <summary>
        /// Resolves one property reference by name against the active part's
        /// active configuration. The answer is always named — resolved,
        /// empty, missing or no-document — never a silent empty string.
        /// </summary>
        PartPropertyResolution Resolve(string name);
    }

    /// <summary>
    /// The precedence rule every <see cref="IPartPropertySource"/>
    /// implementation shares: SolidWorks resolves a property reference
    /// against the configuration-specific store FIRST, then the
    /// document-level store, so a configuration property shadows a
    /// same-named document property. Enumeration merges both stores' names
    /// deduplicated, configuration names first.
    /// </summary>
    public static class PartPropertyResolver
    {
        /// <summary>
        /// Merged resolvable-name list: every name in the configuration
        /// store (its order), then the names only the document store adds.
        /// Names differing only by case count once — the first store's
        /// spelling survives.
        /// </summary>
        public static IReadOnlyList<string> MergeNames(
            IPartPropertyStore? configurationStore, IPartPropertyStore? documentStore)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var store in new[] { configurationStore, documentStore })
            {
                if (store == null) continue;
                foreach (var name in store.GetNames() ?? Array.Empty<string>())
                {
                    if (name == null || !seen.Add(name)) continue;
                    merged.Add(name);
                }
            }
            return merged;
        }

        /// <summary>
        /// Resolves one name with configuration-over-document precedence: the
        /// first store that reports the name present supplies the verdict.
        /// A null or empty name can resolve to nothing — it is Missing, and
        /// so is a name no store holds.
        /// </summary>
        public static PartPropertyResolution Resolve(
            string name, IPartPropertyStore? configurationStore, IPartPropertyStore? documentStore)
        {
            if (string.IsNullOrEmpty(name))
                return PartPropertyResolution.Missing(name ?? "");
            foreach (var store in new[] { configurationStore, documentStore })
            {
                if (store == null) continue;
                var read = store.Read(name);
                if (!read.Present) continue;
                return read.Value.Length == 0
                    ? PartPropertyResolution.Empty(name)
                    : PartPropertyResolution.Resolved(name, read.Value);
            }
            return PartPropertyResolution.Missing(name);
        }
    }
}
