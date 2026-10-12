using System;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Push counterpart of <see cref="ISketchHost.CaptureState"/>: fires
    /// <see cref="Changed"/> whenever display-affecting host state may have
    /// changed behind the UI's back — the active document switched (its
    /// length unit can differ) or the document's length unit itself was
    /// edited. Subscribers re-read state through the normal capture path;
    /// the event carries no payload beyond "something changed". The native
    /// adapter implements this over SolidWorks notify events; hosts that
    /// cannot observe changes may simply never fire.
    /// </summary>
    public interface IHostStateWatcher
    {
        /// <summary>Host display state may have changed; re-read it.</summary>
        event Action? Changed;
    }
}
