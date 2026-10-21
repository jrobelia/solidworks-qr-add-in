using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IPartPropertySource"/> — the active part's two
    /// property stores as plain dictionaries, resolved through the same
    /// <see cref="PartPropertyResolver"/> precedence the native adapter
    /// applies, so what the tests exercise is the real merge/order rule.
    /// <see cref="DocumentProperties"/> is the document-level store;
    /// <see cref="ConfigurationProperties"/> is the active configuration's
    /// store (it shadows same-named document properties, SolidWorks' own
    /// $PRP precedence).
    /// </summary>
    public sealed class FakePartPropertySource : IPartPropertySource
    {
        /// <summary>The kind the fake host reports — Part by default.</summary>
        public HostDocumentKind Document { get; set; } = HostDocumentKind.Part;

        /// <summary>Active document's title as the host would report it.</summary>
        public string? DocumentTitle { get; set; } = "FakePart";

        /// <summary>Active configuration name — null models a read failure.</summary>
        public string? ConfigurationName { get; set; } = "Default";

        /// <summary>Document-level property store (the Custom tab).</summary>
        public Dictionary<string, string> DocumentProperties { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Active configuration's property store — wins on a name clash.</summary>
        public Dictionary<string, string> ConfigurationProperties { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many <see cref="CaptureProperties"/> calls were made.</summary>
        public int CaptureCount { get; private set; }

        /// <summary>How many <see cref="Resolve"/> calls were made.</summary>
        public int ResolveCount { get; private set; }

        public PartPropertySnapshot CaptureProperties()
        {
            CaptureCount++;
            if (Document != HostDocumentKind.Part)
                return new PartPropertySnapshot(Document, DocumentTitle, null,
                    Array.Empty<string>());
            return new PartPropertySnapshot(Document, DocumentTitle, ConfigurationName,
                PartPropertyResolver.MergeNames(
                    new DictStore(ConfigurationProperties),
                    new DictStore(DocumentProperties)));
        }

        public PartPropertyResolution Resolve(string name)
        {
            ResolveCount++;
            if (Document != HostDocumentKind.Part)
                return PartPropertyResolution.NoDocument(name);
            return PartPropertyResolver.Resolve(name,
                new DictStore(ConfigurationProperties),
                new DictStore(DocumentProperties));
        }

        /// <summary>One dictionary as an <see cref="IPartPropertyStore"/>.</summary>
        private sealed class DictStore : IPartPropertyStore
        {
            private readonly IReadOnlyDictionary<string, string> _values;

            public DictStore(IReadOnlyDictionary<string, string> values) => _values = values;

            public IReadOnlyList<string> GetNames() =>
                new List<string>(_values.Keys);

            public PartPropertyRead Read(string name) =>
                _values.TryGetValue(name, out string? value)
                    ? PartPropertyRead.Found(value)
                    : PartPropertyRead.Absent;
        }
    }
}
