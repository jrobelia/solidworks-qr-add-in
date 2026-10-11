using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Interop;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// <see cref="ElementHost"/> that actually receives typed characters when
    /// embedded in SolidWorks' task pane.
    ///
    /// Root cause of "can delete/backspace/paste but not type": the native
    /// task-pane container asks each child window which keys it wants via
    /// <c>WM_GETDLGCODE</c> before dispatching. The default ElementHost /
    /// HwndSource answer does not include <c>DLGC_WANTCHARS</c>, so
    /// SolidWorks' pump never translates WM_KEYDOWN into WM_CHAR for the
    /// hosted WPF window — special keys (which travel as WM_KEY* messages)
    /// work, but text input never arrives. This is the documented failure
    /// for WPF-in-ElementHost inside native hosts (SolidWorks task panes,
    /// property-manager pages, MFC dialogs), fixed identically in xCAD and
    /// the community samples: answer <c>WM_GETDLGCODE</c> with the full
    /// wanted set.
    ///
    /// The message can be addressed to either HWND in the pair — the
    /// ElementHost window or the nested WPF HwndSource window — so both are
    /// covered: <see cref="WndProc"/> ORs the wanted flags into the base
    /// answer, and a lazily-installed <see cref="HwndSourceHook"/> answers
    /// directly on the inner source. The companion
    /// <see cref="PaneKeyboardFilter"/> stays installed as well: it costs
    /// nothing and covers any thread-message path the DLGCODE answer does
    /// not.
    /// </summary>
    internal sealed class PaneElementHost : ElementHost
    {
        private const int WM_GETDLGCODE = 0x0087;
        private const int WM_CHAR = 0x0102;
        private const int DLGC_WANTARROWS = 0x0001;
        private const int DLGC_WANTTAB = 0x0002;
        private const int DLGC_WANTALLKEYS = 0x0004;
        private const int DLGC_WANTCHARS = 0x0080;
        private const int WantedDlgCode =
            DLGC_WANTARROWS | DLGC_WANTTAB | DLGC_WANTALLKEYS | DLGC_WANTCHARS;

        private bool _sourceHooked;
        private bool _loggedDlgCode;
        private bool _loggedChar;

        protected override void WndProc(ref Message m)
        {
            EnsureSourceHook();
            base.WndProc(ref m);
            if (m.Msg == WM_GETDLGCODE)
            {
                m.Result = (IntPtr)(m.Result.ToInt64() | WantedDlgCode);
                LogDlgCodeOnce("ElementHost WndProc");
            }
            else if (m.Msg == WM_CHAR)
            {
                LogCharOnce("ElementHost WndProc");
            }
        }

        /// <summary>
        /// The nested WPF window is a child HWND of the ElementHost — created
        /// when the host is sited — so the hook is installed lazily on the
        /// first message once <see cref="ElementHost.Child"/> exists.
        /// Best-effort: a hook failure leaves the WndProc answer in place.
        /// </summary>
        private void EnsureSourceHook()
        {
            if (_sourceHooked || Child == null)
                return;
            try
            {
                if (PresentationSource.FromVisual(Child) is HwndSource source)
                {
                    source.AddHook(SourceHook);
                    _sourceHooked = true;
                    Diag.Log("pane host HwndSource hook installed");
                }
            }
            catch { /* keyboard interop is best-effort — never break the pane */ }
        }

        private IntPtr SourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETDLGCODE)
            {
                // Take over the answer entirely: the base HwndSource response
                // lacks DLGC_WANTCHARS, which is the whole problem.
                handled = true;
                LogDlgCodeOnce("HwndSource hook");
                return (IntPtr)WantedDlgCode;
            }
            if (msg == WM_CHAR)
                LogCharOnce("HwndSource hook");
            return IntPtr.Zero;
        }

        private void LogDlgCodeOnce(string where)
        {
            if (_loggedDlgCode) return;
            _loggedDlgCode = true;
            Diag.Log($"pane host answered WM_GETDLGCODE via {where}");
        }

        private void LogCharOnce(string where)
        {
            if (_loggedChar) return;
            _loggedChar = true;
            Diag.Log($"pane host saw WM_CHAR via {where}");
        }
    }
}
