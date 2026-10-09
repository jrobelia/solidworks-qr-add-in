namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Verification support: wraps any ISketchHost and makes the Nth
    /// AddLine call throw a SketchInsertionException, so the real abort /
    /// cleanup path of the command and the active adapter can be exercised
    /// end-to-end. Used by the headless suite and the live probe.
    /// </summary>
    public sealed class FailureInjectingHost : ISketchHost
    {
        private readonly ISketchHost _inner;
        private readonly int _failOnLine;

        public FailureInjectingHost(ISketchHost inner, int failOnLine)
        {
            _inner = inner;
            _failOnLine = failOnLine;
        }

        public SketchHostState CaptureState() => _inner.CaptureState();

        public ISketchInsertion BeginSketchInsertion() =>
            new FailureInjectingInsertion(_inner.BeginSketchInsertion(), _failOnLine);

        private sealed class FailureInjectingInsertion : ISketchInsertion
        {
            private readonly ISketchInsertion _inner;
            private readonly int _failOnLine;
            private int _calls;

            public FailureInjectingInsertion(ISketchInsertion inner, int failOnLine)
            {
                _inner = inner;
                _failOnLine = failOnLine;
            }

            public string SketchName => _inner.SketchName;
            public int LineCount => _inner.LineCount;

            public void AddLine(double x1, double y1, double x2, double y2)
            {
                _calls++;
                if (_calls == _failOnLine)
                    throw new SketchInsertionException(
                        $"Injected failure: host could not insert line {_failOnLine}.");
                _inner.AddLine(x1, y1, x2, y2);
            }

            public void KeepOpen() => _inner.KeepOpen();
            public CleanupOutcome Abort() => _inner.Abort();
            public void Dispose() => _inner.Dispose();
        }
    }
}
