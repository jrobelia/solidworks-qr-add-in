using System;
using System.Threading;
using System.Windows.Threading;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// A dedicated STA thread with a running WPF dispatcher, owned by the
    /// add-in. WPF windows need an STA thread with a message pump — and probe
    /// calls into the add-in arrive on MTA COM worker threads, so the window
    /// cannot live on whatever thread happens to invoke it. Giving the window
    /// its own STA thread also keeps WPF message handling out of SolidWorks'
    /// own pump.
    ///
    /// All window creation goes through <see cref="Invoke{T}"/>; the window's
    /// own members self-marshal to this dispatcher, so every caller (menu
    /// callback, COM probe, unit seam) is safe.
    /// </summary>
    internal sealed class AddInWpfThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        public AddInWpfThread()
        {
            var ready = new ManualResetEventSlim(false);
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run(); // pump until InvokeShutdown
            })
            {
                IsBackground = true,
                Name = "SolidWorksQrAddIn.Wpf",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            _dispatcher = dispatcher!;
        }

        public T Invoke<T>(Func<T> func) => _dispatcher.Invoke(func);

        /// <summary>Stops the pump and joins the thread. Idempotent, never throws.</summary>
        public void Shutdown()
        {
            try
            {
                if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                    _dispatcher.InvokeShutdown();
            }
            catch { }
            try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { }
        }

        public void Dispose() => Shutdown();
    }
}
