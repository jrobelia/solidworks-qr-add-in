using System.Windows.Forms;
using System.Windows.Interop;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// Restores text entry in the WPF pane hosted inside SolidWorks' task
    /// pane. SolidWorks' own message pump (a native <c>IsDialogMessage</c>-style
    /// loop on the task-pane HWND) consumes <c>WM_CHAR</c> before the hosted
    /// WPF <c>HwndSource</c> ever sees it: KeyDown/Backspace reach the control
    /// through <see cref="ElementHost"/>, but typed characters never arrive —
    /// "you can delete but not add".
    ///
    /// The canonical fix (the same one used by the CAD+/Xarial task-pane
    /// samples and cited on the SolidWorks forums for "WPF in task pane can't
    /// type") is a WinForms <see cref="IMessageFilter"/>: the filter runs
    /// inside the thread's message loop before dispatch and re-raises every
    /// message through <see cref="ComponentDispatcher"/>, which is exactly the
    /// entry point a hosted <c>HwndSource</c> subscribes to for keyboard
    /// preprocessing (<c>ThreadFilterMessage</c>/<c>ThreadPreprocessMessage</c>).
    /// We forward ALL messages — restricting to a keyboard subset risks
    /// starving other WPF input paths (IME composition, dead keys).
    ///
    /// The filter NEVER eats a message (always returns false) so SolidWorks
    /// and other add-ins' keyboard handling are unaffected, and it is
    /// installed only for the pane's lifetime (ConnectToSW → DisconnectFromSW),
    /// once per connect, on the UI thread that owns the message loop.
    /// </summary>
    internal sealed class PaneKeyboardFilter : IMessageFilter
    {
        // One-shot diagnostic: whether the filter ever runs proves whether
        // WinForms' component-manager preprocessing is wired into
        // SolidWorks' pump at all (Application.AddMessageFilter only sees
        // messages when it is).
        private bool _loggedFirstMessage;

        public bool PreFilterMessage(ref Message m)
        {
            if (!_loggedFirstMessage)
            {
                _loggedFirstMessage = true;
                Diag.Log($"pane keyboard filter saw first message 0x{m.Msg:X4} " +
                    $"on thread {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            }
            try
            {
                // WinForms and WPF carry the same Win32 MSG in different
                // structs — copy it across verbatim.
                var msg = new MSG
                {
                    hwnd = m.HWnd,
                    message = m.Msg,
                    wParam = m.WParam,
                    lParam = m.LParam,
                };
                ComponentDispatcher.RaiseThreadMessage(ref msg);
            }
            catch
            {
                // A misbehaving dispatcher must never break SolidWorks' pump.
            }
            return false; // forward only — let normal dispatch continue
        }
    }
}
