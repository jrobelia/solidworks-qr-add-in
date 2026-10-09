using System;
using SolidWorksQrAddIn.Core.Interaction;

namespace SolidWorksQrAddIn.Tests.Fakes
{
    /// <summary>
    /// In-memory IQrSketchWindow: records how the session drives it and lets
    /// a test simulate the user closing the window — the window-state half of
    /// the modeless interaction contract.
    /// </summary>
    public sealed class FakeQrWindow : IQrSketchWindow
    {
        public bool IsOpen { get; private set; }
        public int ShowCount { get; private set; }
        public int FocusCount { get; private set; }
        public int CloseCount { get; private set; }

        public event EventHandler? Closed;

        public void ShowModeless()
        {
            IsOpen = true;
            ShowCount++;
        }

        public void FocusWindow()
        {
            if (!IsOpen)
                throw new InvalidOperationException("A closed window cannot be focused.");
            FocusCount++;
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            CloseCount++;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The user clicking the window's close affordance.</summary>
        public void SimulateUserClose() => Close();
    }
}
