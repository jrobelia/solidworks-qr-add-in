using System;
using SolidWorks.Interop.sldworks;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// <see cref="IHostStateWatcher"/> over SolidWorks notify events: the
    /// application's <c>ActiveDocChangeNotify</c> (a document switch can
    /// mean a different length unit) plus the active document's
    /// <c>UnitsChangeNotify</c> (a unit edit lands behind the pane's back).
    /// The event carries no payload — subscribers re-capture the state they
    /// care about through the normal host seam. Best-effort: a missed or
    /// unhookable event only ever leaves a stale display string, never a
    /// broken insertion — so every hookup and callback is wrapped.
    /// </summary>
    public sealed class SwHostStateWatcher : IHostStateWatcher, IDisposable
    {
        private readonly ISldWorks _sw;
        private ModelDoc2? _watchedDoc;
        private bool _appHooked;

        public event Action? Changed;

        /// <param name="sw">The live SolidWorks application object.</param>
        public SwHostStateWatcher(ISldWorks sw)
        {
            _sw = sw ?? throw new ArgumentNullException(nameof(sw));
            try
            {
                // Events live on the coclass — ISldWorks exposes only the
                // sink-interface methods (a cast to DSldWorksEvents finds a
                // method group, not an event).
                if (_sw is SldWorks app)
                {
                    app.ActiveDocChangeNotify += OnActiveDocChanged;
                    _appHooked = true;
                }
                else
                {
                    Diag.Log("host state watcher: app object is not SldWorks — skipped");
                }
                HookDoc(_sw.IActiveDoc2 as ModelDoc2);
            }
            catch (Exception ex)
            {
                Diag.Log($"host state watcher skipped: {ex.Message}");
            }
        }

        /// <summary>Move the per-document unit hook onto the active document.</summary>
        private void HookDoc(ModelDoc2? doc)
        {
            try { HookUnits(_watchedDoc, attach: false); }
            catch { /* a dead document must not trap unhooking */ }
            _watchedDoc = doc;
            try
            {
                if (doc != null)
                    HookUnits(doc, attach: true);
            }
            catch (Exception ex)
            {
                Diag.Log($"document unit watch skipped: {ex.Message}");
                _watchedDoc = null;
            }
        }

        /// <summary>
        /// Attach or detach the document's UnitsChangeNotify — the event is
        /// declared per document coclass (PartDoc / AssemblyDoc /
        /// DrawingDoc), so the hook follows the document's actual kind.
        /// </summary>
        private void HookUnits(ModelDoc2? doc, bool attach)
        {
            switch (doc)
            {
                case PartDoc part:
                    if (attach) part.UnitsChangeNotify += OnUnitsChanged;
                    else part.UnitsChangeNotify -= OnUnitsChanged;
                    break;
                case AssemblyDoc asmb:
                    if (attach) asmb.UnitsChangeNotify += OnUnitsChanged;
                    else asmb.UnitsChangeNotify -= OnUnitsChanged;
                    break;
                case DrawingDoc drw:
                    if (attach) drw.UnitsChangeNotify += OnUnitsChanged;
                    else drw.UnitsChangeNotify -= OnUnitsChanged;
                    break;
            }
        }

        private int OnActiveDocChanged()
        {
            // The new active document gets the unit hook too — its length
            // unit may differ from the previous document's.
            try { HookDoc(_sw.IActiveDoc2 as ModelDoc2); }
            catch { }
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        private int OnUnitsChanged()
        {
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        /// <summary>Detach every hook — an unloaded add-in must never be called back.</summary>
        public void Dispose()
        {
            try
            {
                if (_appHooked && _sw is SldWorks app)
                    app.ActiveDocChangeNotify -= OnActiveDocChanged;
            }
            catch { }
            _appHooked = false;
            HookDoc(null);
        }
    }
}
