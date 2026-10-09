namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Verification support: wraps any ISketchHost and injects failures at
    /// the seam's observable points, so the real abort / cleanup / reporting
    /// path of the command and the active adapter can be exercised end-to-end.
    /// Used by the headless suite and the live probe.
    ///
    /// Injection points:
    ///   - <paramref name="failBegin"/>: BeginSketchInsertion throws with that
    ///     message — a stale support, a switched document, a busy host. No
    ///     sketch is created by the inner host.
    ///   - <paramref name="failOnLine"/>: the Nth (1-based) AddLine throws —
    ///     N=1 means the sketch was created but no edge landed; a larger N is
    ///     a failure partway through the contour edges.
    ///   - <paramref name="failCleanup"/>: the insertion's Abort reports
    ///     failure WITHOUT delegating, leaving the inner session open the way
    ///     a genuinely refused cleanup would. The command's Dispose still
    ///     delegates, modelling the host's own best-effort second attempt —
    ///     over a healthy inner that retry removes the sketch; over an inner
    ///     that also fails cleanup the partial sketch truly remains.
    /// </summary>
    public sealed class FailureInjectingHost : ISketchHost
    {
        private readonly ISketchHost _inner;
        private readonly int _failOnLine;
        private readonly string? _failBegin;
        private readonly bool _failCleanup;

        public FailureInjectingHost(
            ISketchHost inner, int failOnLine = 0, bool failCleanup = false, string? failBegin = null)
        {
            _inner = inner;
            _failOnLine = failOnLine;
            _failCleanup = failCleanup;
            _failBegin = failBegin;
        }

        public SketchHostState CaptureState() => _inner.CaptureState();

        public ISketchInsertion BeginSketchInsertion()
        {
            if (_failBegin != null)
                throw new SketchHostException(_failBegin);
            return new FailureInjectingInsertion(
                _inner.BeginSketchInsertion(), _failOnLine, _failCleanup);
        }

        private sealed class FailureInjectingInsertion : ISketchInsertion
        {
            private readonly ISketchInsertion _inner;
            private readonly int _failOnLine;
            private readonly bool _failCleanup;
            private int _calls;

            public FailureInjectingInsertion(ISketchInsertion inner, int failOnLine, bool failCleanup)
            {
                _inner = inner;
                _failOnLine = failOnLine;
                _failCleanup = failCleanup;
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

            public CleanupOutcome Abort()
            {
                if (_failCleanup)
                {
                    // Cleanup refused — deliberately NOT delegated, so the
                    // inner session stays open exactly like a host whose
                    // delete path failed. The command's Dispose (delegated)
                    // is the host's own best-effort second attempt.
                    return CleanupOutcome.Failed(
                        $"Injected failure: the host could not remove sketch '{SketchName}'.");
                }
                return _inner.Abort();
            }

            public void Dispose() => _inner.Dispose();
        }
    }
}
