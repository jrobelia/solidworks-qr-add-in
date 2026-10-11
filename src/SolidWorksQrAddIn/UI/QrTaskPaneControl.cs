using System;
using System.Windows.Forms;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// Thin WinForms wrapper that gives SolidWorks a native HWND for the task
    /// pane while hosting the real UI — <see cref="QrPaneView"/> — in WPF via
    /// <see cref="ElementHost"/> (the standard task-pane embedding pattern;
    /// task-pane controls live on SolidWorks' own STA UI thread, so no
    /// dedicated WPF pump is needed).
    ///
    /// The control doubles as the session's <see cref="IQrSketchWindow"/> view:
    /// the pane IS the add-in surface, so "open" means the tab is shown and
    /// "focus" means activating the tab. The activate/hide callbacks are
    /// supplied by the add-in (wired to ITaskpaneView.ShowView/HideView) so
    /// this class never touches the task-pane COM object itself — and the
    /// control can be created before the view exists: the callbacks are only
    /// invoked after ConnectToSW has both.
    ///
    /// Callers arrive on arbitrary threads (menu callback, COM probe), so every
    /// session-facing member marshals onto this control's UI thread first.
    /// </summary>
    public sealed class QrTaskPaneControl : UserControl, IQrSketchWindow
    {
        private readonly QrPaneView _view;
        private readonly Action _activatePane;
        private readonly Action _hidePane;
        private volatile bool _isOpen;

        /// <param name="session">Session state the hosted view renders and drives.</param>
        /// <param name="hostProvider">Builds a fresh host adapter per Create — never cached.</param>
        /// <param name="activatePane">Brings the task-pane tab forward (ShowView).</param>
        /// <param name="hidePane">Removes the task-pane tab (HideView).</param>
        public QrTaskPaneControl(
            QrSketchSession session,
            Func<ISketchHost> hostProvider,
            Action activatePane,
            Action hidePane)
        {
            _activatePane = activatePane ?? throw new ArgumentNullException(nameof(activatePane));
            _hidePane = hidePane ?? throw new ArgumentNullException(nameof(hidePane));
            _view = new QrPaneView(session, hostProvider);
            Controls.Add(new PaneElementHost { Dock = DockStyle.Fill, Child = _view });
            Dock = DockStyle.Fill;
        }

        // ---------------- IQrSketchWindow ----------------

        /// <summary>The pane tab is currently shown (not hidden/closed).</summary>
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Show the pane: bring the tab forward so the engineer lands on the
        /// QR UI, and re-pull the session state so a tab re-shown after a
        /// probe-driven edit can never be stale.
        /// </summary>
        public void ShowModeless() => MarshalToUi(() =>
        {
            _activatePane();
            _view.SyncFromSession();
            _isOpen = true;
        });

        /// <summary>Re-activate an already-shown tab (same user intent as ShowModeless).</summary>
        public void FocusWindow() => MarshalToUi(() =>
        {
            if (!_isOpen)
                throw new InvalidOperationException("A hidden QR pane cannot be focused.");
            _activatePane();
        });

        /// <summary>
        /// Hide the pane tab. Any debounced field edit is flushed first so the
        /// session keeps the real inputs — the next show re-pulls truth anyway.
        /// </summary>
        public void Close() => MarshalToUi(() =>
        {
            if (!_isOpen) return;
            _view.FlushPendingEdits();
            _isOpen = false;
            try { _hidePane(); }
            catch { /* a hidden-or-stuck pane view must not trap the host */ }
            Closed?.Invoke(this, EventArgs.Empty);
        });

        /// <summary>Session→view sync; marshal then delegate to the WPF view.</summary>
        public void SyncFromSession() => MarshalToUi(() => _view.SyncFromSession());

        /// <summary>Raised when the pane is hidden via <see cref="Close"/>.</summary>
        public event EventHandler? Closed;

        private void MarshalToUi(Action action)
        {
            if (!IsHandleCreated || !InvokeRequired)
                action();
            else
                Invoke(action);
        }
    }
}
