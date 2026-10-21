using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// The document's kind as a host fact: reads
    /// <see cref="IModelDoc2.GetType"/> (an swDocumentTypes_e) and maps it to
    /// the seam's <see cref="HostDocumentKind"/>. Shared by every adapter
    /// that classifies the active document — the sketch host's state
    /// capture and the part-property source — so the mapping lives once.
    /// </summary>
    internal static class HostDocumentKinds
    {
        /// <summary>
        /// The document's kind — <see cref="HostDocumentKind.None"/> for a
        /// null document, <see cref="HostDocumentKind.Unknown"/> for an
        /// unlisted type or a failed read: a kind that cannot be determined
        /// must never break the caller, only narrow the verdict.
        /// </summary>
        public static HostDocumentKind Of(IModelDoc2? doc)
        {
            if (doc == null)
                return HostDocumentKind.None;
            try
            {
                return doc.GetType() switch
                {
                    (int)swDocumentTypes_e.swDocPART => HostDocumentKind.Part,
                    (int)swDocumentTypes_e.swDocASSEMBLY => HostDocumentKind.Assembly,
                    (int)swDocumentTypes_e.swDocDRAWING => HostDocumentKind.Drawing,
                    _ => HostDocumentKind.Unknown,
                };
            }
            catch { return HostDocumentKind.Unknown; }
        }
    }
}
