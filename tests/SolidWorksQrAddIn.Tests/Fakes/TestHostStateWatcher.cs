using System;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Tests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IHostStateWatcher"/> — tests raise
    /// <see cref="RaiseChanged"/> to simulate a document switch or a
    /// length-unit edit landing behind the UI's back.
    /// </summary>
    public sealed class TestHostStateWatcher : IHostStateWatcher
    {
        public event Action? Changed;

        /// <summary>How many subscribers are attached right now.</summary>
        public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

        /// <summary>Simulate a host-state change (doc switch, unit edit).</summary>
        public void RaiseChanged() => Changed?.Invoke();
    }
}
