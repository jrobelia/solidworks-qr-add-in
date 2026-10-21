using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Native adapter: <see cref="IPartPropertySource"/> over the live
    /// document's CustomPropertyManagers — the same property stores
    /// SolidWorks' own <c>$PRP:"name"</c> resolution reads, applied with
    /// SolidWorks' own precedence (the active configuration's specific
    /// store first, the document-level <c>""</c> store second) by
    /// <see cref="PartPropertyResolver"/>. The <c>"SW-*"</c> built-ins
    /// (file name, mass, material, ...) are not properties of these
    /// stores — they resolve through the link-variable machinery and
    /// never appear here, which keeps the enumerated set honest: every
    /// listed name is a name this seam can actually resolve.
    /// A new source is used per read and nothing is cached, so property
    /// or configuration edits behind the UI's back are never hidden —
    /// <see cref="CustomPropertyManager.Get5"/> reads with UseCached
    /// false for the same reason.
    /// </summary>
    public sealed class SwPartPropertySource : IPartPropertySource
    {
        private readonly ISldWorks _sw;

        public SwPartPropertySource(ISldWorks sw)
        {
            _sw = sw ?? throw new ArgumentNullException(nameof(sw));
        }

        public PartPropertySnapshot CaptureProperties()
        {
            var doc = _sw.IActiveDoc2 as IModelDoc2;
            var kind = HostDocumentKinds.Of(doc);
            if (doc == null || kind != HostDocumentKind.Part)
                return new PartPropertySnapshot(kind, TitleOf(doc), null,
                    Array.Empty<string>());

            var configuration = ActiveConfigurationName(doc);
            return new PartPropertySnapshot(kind, TitleOf(doc), configuration,
                PartPropertyResolver.MergeNames(
                    StoreFor(doc, configuration), StoreFor(doc, "")));
        }

        public PartPropertyResolution Resolve(string name)
        {
            var doc = _sw.IActiveDoc2 as IModelDoc2;
            if (doc == null || HostDocumentKinds.Of(doc) != HostDocumentKind.Part)
                return PartPropertyResolution.NoDocument(name ?? "");
            var configuration = ActiveConfigurationName(doc);
            return PartPropertyResolver.Resolve(name ?? "",
                StoreFor(doc, configuration), StoreFor(doc, ""));
        }

        // ---------------- host reads, each individually best-effort ----------------

        private static string? TitleOf(IModelDoc2? doc)
        {
            try { return doc?.GetTitle(); } catch { return null; }
        }

        /// <summary>
        /// The active configuration's name — null when the host cannot
        /// report one (then only the document-level store is read).
        /// </summary>
        private static string? ActiveConfigurationName(IModelDoc2 doc)
        {
            try
            {
                var mgr = doc.ConfigurationManager as IConfigurationManager;
                return (mgr?.ActiveConfiguration as IConfiguration)?.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Wraps one CustomPropertyManager as an <see cref="IPartPropertyStore"/>.
        /// <paramref name="configurationName"/> empty selects the document-level
        /// store; null means "no store" (no readable configuration).
        /// </summary>
        private static IPartPropertyStore? StoreFor(IModelDoc2 doc, string? configurationName)
        {
            if (configurationName == null)
                return null;
            try
            {
                // CustomPropertyManager is a parameterized COM property —
                // indexer syntax, not a method call.
                var mgr = (doc.Extension as IModelDocExtension)
                    ?.CustomPropertyManager[configurationName];
                return mgr == null ? null : new SwPropertyStore(mgr);
            }
            catch { return null; }
        }

        /// <summary>
        /// One CustomPropertyManager as the seam's store view. Get5 is the
        /// missing-vs-present discriminator the legacy Get() never had:
        /// <see cref="swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent"/>
        /// is absent, anything else is held — and the resolved value (what
        /// $PRP renders, expression text evaluated) is reported verbatim so
        /// a present-but-empty property reads as Empty, not Missing.
        /// </summary>
        private sealed class SwPropertyStore : IPartPropertyStore
        {
            private readonly ICustomPropertyManager _mgr;

            public SwPropertyStore(ICustomPropertyManager mgr) => _mgr = mgr;

            public IReadOnlyList<string> GetNames()
            {
                object? raw;
                try { raw = _mgr.GetNames(); }
                catch { return Array.Empty<string>(); }
                if (raw is string[] names)
                    return names;
                if (raw is object[] objects)
                {
                    var list = new List<string>(objects.Length);
                    foreach (var o in objects)
                        if (o is string s)
                            list.Add(s);
                    return list;
                }
                return Array.Empty<string>();
            }

            public PartPropertyRead Read(string name)
            {
                int result;
                string resolved;
                try
                {
                    result = _mgr.Get5(name, false, out _, out resolved, out _);
                }
                catch { return PartPropertyRead.Absent; }
                if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                    return PartPropertyRead.Absent;
                return PartPropertyRead.Found(resolved ?? "");
            }
        }
    }
}
