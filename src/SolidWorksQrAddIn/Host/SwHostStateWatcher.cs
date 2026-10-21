using System;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// <see cref="IHostStateWatcher"/> over SolidWorks notify events: the
    /// application's <c>ActiveDocChangeNotify</c> (a document switch can
    /// mean a different length unit) plus the active document's
    /// <c>UnitsChangeNotify</c> (a unit edit lands behind the pane's back),
    /// <c>DestroyNotify2</c> (the watched document's close — the last
    /// document's close emits no <c>ActiveDocChangeNotify</c>, so this is
    /// the only signal that transition produces; because the synchronous
    /// callback still observes the dying document through
    /// <see cref="ISldWorks.IActiveDoc2"/> the watcher re-notifies once the
    /// close has settled) and — on parts —
    /// <c>ActiveConfigChangePostNotify</c> and the custom-property
    /// add/change/delete notifications: the inputs a payload template's
    /// <c>$PRP</c> resolution depends on (spec #21).
    /// The event carries no payload — subscribers re-capture the state they
    /// care about through the normal host seam. Best-effort: a missed or
    /// unhookable event only ever leaves a stale display string, never a
    /// broken insertion — so every hookup and callback is wrapped.
    /// </summary>
    public sealed class SwHostStateWatcher : IHostStateWatcher, IDisposable
    {
        /// <summary>
        /// One-shot delay between a document's post-destroy notification
        /// and the settlement re-notification. During the synchronous
        /// DestroyNotify2 callback the application still hands the dying
        /// document out of <see cref="ISldWorks.IActiveDoc2"/> — by the
        /// time this fires the close has completed and a fresh read sees
        /// the true active document (null when it was the last one). 400 ms
        /// matches the harness's own post-notify settle window.
        /// </summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

        private readonly ISldWorks _sw;
        private ModelDoc2? _watchedDoc;
        private bool _appHooked;
        private readonly Timer _settleTimer;

        public event Action? Changed;

        /// <param name="sw">The live SolidWorks application object.</param>
        public SwHostStateWatcher(ISldWorks sw)
        {
            _sw = sw ?? throw new ArgumentNullException(nameof(sw));
            _settleTimer = new Timer(OnSettled, null, Timeout.Infinite, Timeout.Infinite);
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

        /// <summary>Move the per-document hooks onto the active document.</summary>
        private void HookDoc(ModelDoc2? doc)
        {
            try { HookDocEvents(_watchedDoc, attach: false); }
            catch { /* a dead document must not trap unhooking */ }
            _watchedDoc = doc;
            try
            {
                if (doc != null)
                    HookDocEvents(doc, attach: true);
            }
            catch (Exception ex)
            {
                Diag.Log($"document watch skipped: {ex.Message}");
                _watchedDoc = null;
            }
        }

        /// <summary>
        /// Attach or detach the document's change notifications — the unit
        /// edit and the post-destroy notification on every document kind
        /// plus, for parts, the configuration switch and custom-property
        /// edits a <c>$PRP</c> resolution re-runs on (spec #21). Events are
        /// declared per document coclass (PartDoc / AssemblyDoc /
        /// DrawingDoc), so the hook follows the document's actual kind.
        /// </summary>
        private void HookDocEvents(ModelDoc2? doc, bool attach)
        {
            switch (doc)
            {
                case PartDoc part:
                    if (attach)
                    {
                        part.UnitsChangeNotify += OnUnitsChanged;
                        part.DestroyNotify2 += OnDocDestroyed;
                        part.ActiveConfigChangePostNotify += OnActiveConfigChanged;
                        part.AddCustomPropertyNotify += OnPartPropertyChange;
                        part.ChangeCustomPropertyNotify += OnPartPropertyEdit;
                        part.DeleteCustomPropertyNotify += OnPartPropertyChange;
                    }
                    else
                    {
                        part.UnitsChangeNotify -= OnUnitsChanged;
                        part.DestroyNotify2 -= OnDocDestroyed;
                        part.ActiveConfigChangePostNotify -= OnActiveConfigChanged;
                        part.AddCustomPropertyNotify -= OnPartPropertyChange;
                        part.ChangeCustomPropertyNotify -= OnPartPropertyEdit;
                        part.DeleteCustomPropertyNotify -= OnPartPropertyChange;
                    }
                    break;
                case AssemblyDoc asmb:
                    if (attach)
                    {
                        asmb.UnitsChangeNotify += OnUnitsChanged;
                        asmb.DestroyNotify2 += OnDocDestroyed;
                    }
                    else
                    {
                        asmb.UnitsChangeNotify -= OnUnitsChanged;
                        asmb.DestroyNotify2 -= OnDocDestroyed;
                    }
                    break;
                case DrawingDoc drw:
                    if (attach)
                    {
                        drw.UnitsChangeNotify += OnUnitsChanged;
                        drw.DestroyNotify2 += OnDocDestroyed;
                    }
                    else
                    {
                        drw.UnitsChangeNotify -= OnUnitsChanged;
                        drw.DestroyNotify2 -= OnDocDestroyed;
                    }
                    break;
            }
        }

        /// <summary>
        /// The hook set follows whatever the application's active document
        /// is right now, then the change is reported — the shared response
        /// to a document switch, a destroy, and the post-close settle tick.
        /// </summary>
        private void RehookAndNotify()
        {
            try { HookDoc(_sw.IActiveDoc2 as ModelDoc2); }
            catch { }
            try { Changed?.Invoke(); } catch { }
        }

        private int OnActiveDocChanged()
        {
            // The new active document gets the unit hook too — its length
            // unit may differ from the previous document's.
            RehookAndNotify();
            return 0;
        }

        private int OnUnitsChanged()
        {
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        /// <summary>
        /// The watched document was destroyed — its units and properties
        /// can never change again. Reports the change exactly like a
        /// document switch AND arms the one-shot settle re-notification:
        /// live-verified, the synchronous callback still observes the dying
        /// document through <see cref="ISldWorks.IActiveDoc2"/>, so the
        /// re-resolution raised here reads pre-close state; the settle tick
        /// re-reads once the close has completed — that is what reports the
        /// no-document transition the last document's close produces.
        /// </summary>
        private int OnDocDestroyed(int destroyType)
        {
            RehookAndNotify();
            try { _settleTimer.Change(SettleDelay, Timeout.InfiniteTimeSpan); }
            catch { }
            return 0;
        }

        /// <summary>The close-settle tick — the post-destroy re-read (see <see cref="OnDocDestroyed"/>).</summary>
        private void OnSettled(object? state) => RehookAndNotify();

        /// <summary>The part's active configuration switched — the resolution inputs changed.</summary>
        private int OnActiveConfigChanged()
        {
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        /// <summary>A custom property appeared or disappeared on the watched part.</summary>
        private int OnPartPropertyChange(string propName, string configuration, string value, int valueType)
        {
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        /// <summary>A custom property's value was edited on the watched part.</summary>
        private int OnPartPropertyEdit(
            string propName, string configuration, string oldValue, string newValue, int valueType)
        {
            try { Changed?.Invoke(); } catch { }
            return 0;
        }

        /// <summary>Detach every hook — an unloaded add-in must never be called back.</summary>
        public void Dispose()
        {
            try { _settleTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch { }
            _settleTimer.Dispose();
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
