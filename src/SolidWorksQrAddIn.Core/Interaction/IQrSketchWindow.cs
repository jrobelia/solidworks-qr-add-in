using System;

namespace SolidWorksQrAddIn.Core.Interaction
{
    /// <summary>
    /// The view seam between the session and the real WPF window. The add-in
    /// implements this over <c>System.Windows.Window</c>; headless tests use a
    /// fake. Only interaction state crosses — never WPF or SolidWorks objects.
    /// </summary>
    public interface IQrSketchWindow
    {
        /// <summary>The window is shown and not yet closed.</summary>
        bool IsOpen { get; }

        /// <summary>Show the window modeless (the host stays interactive) and activate it.</summary>
        void ShowModeless();

        /// <summary>Bring the already-open window forward / give it focus.</summary>
        void FocusWindow();

        /// <summary>Close the window (same as the user's close affordance).</summary>
        void Close();

        /// <summary>
        /// Re-pull the session's field texts, picture and status into the
        /// view. The session calls this whenever its state changed without
        /// the window driving it — a probe-driven edit, a create outcome —
        /// so the shown fields and bitmap can never diverge from the session.
        /// </summary>
        void SyncFromSession();

        /// <summary>Raised when the window closes, however that happened.</summary>
        event EventHandler? Closed;
    }
}
