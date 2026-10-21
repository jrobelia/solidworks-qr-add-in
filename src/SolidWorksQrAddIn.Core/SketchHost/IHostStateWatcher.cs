using System;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Push counterpart of <see cref="ISketchHost.CaptureState"/>: fires
    /// <see cref="Changed"/> whenever display-affecting host state may have
    /// changed behind the UI's back — the active document switched (its
    /// length unit can differ), the document's length unit itself was
    /// edited, the part's active configuration changed, or a custom
    /// property was added/edited/deleted (both re-resolution inputs for a
    /// payload template's <c>$PRP</c> references, spec #21). Subscribers
    /// re-read state through the normal capture path; the event carries no
    /// payload beyond "something changed". The native adapter implements
    /// this over SolidWorks notify events; hosts that cannot observe
    /// changes may simply never fire.
    /// </summary>
    public interface IHostStateWatcher
    {
        /// <summary>Host display state may have changed; re-read it.</summary>
        event Action? Changed;
    }
}
