using System;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Preview;

namespace SolidWorksQrAddIn.Core.Interaction
{
    /// <summary>
    /// The interaction state behind the one modeless QR window — the
    /// headlessly testable core of ticket 3. Owns three things:
    ///
    ///   1. Content → geometry → picture. <see cref="SetContent"/> stores the
    ///      text EXACTLY as entered (no trim/resolve/rewrite), regenerates the
    ///      QR contour result, and re-renders the picture from it. An invalid
    ///      input clears the picture — a stale preview must never look like a
    ///      valid rendering of bad input.
    ///
    ///   2. Create. <see cref="CreateSketch"/> runs the insertion command with
    ///      the CURRENT content against a freshly-captured host state — the
    ///      command itself revalidates the active part, the selected support,
    ///      and the no-sketch-being-edited rule at call time, so no cached
    ///      request or stale cross-document support can be used.
    ///
    ///   3. The single window. The command entry is <see cref="OpenOrFocusWindow"/>:
    ///      focus the live window or create a fresh one through the injected
    ///      view factory. <see cref="Release"/> (add-in disconnect) closes and
    ///      drops it. The window itself is a thin view; all behaviour lives here.
    ///
    /// Size and growth stay fixed (25 mm, 3%) until the size/growth ticket.
    /// </summary>
    public sealed class QrSketchSession
    {
        /// <summary>Fixed final grown QR outline width (mm) until the size ticket lands.</summary>
        public const double SizeMm = CreateQrSketchCommand.FixedSizeMm;

        /// <summary>Fixed per-side outline growth fraction until the size ticket lands.</summary>
        public const double GrowthFractionPerSide = CreateQrSketchCommand.FixedGrowthFractionPerSide;

        /// <summary>Preview raster resolution (pixels per side, incl. margin).</summary>
        public const int PreviewPixelsAcross = 240;

        private const string PromptText = "Enter the text or URL to encode.";

        private readonly Func<IQrSketchWindow> _windowFactory;
        private readonly CreateQrSketchCommand _command = new CreateQrSketchCommand();
        private IQrSketchWindow? _window;
        private string _content = string.Empty;
        private QrSketchResult? _current;

        public QrSketchSession(Func<IQrSketchWindow> windowFactory)
        {
            _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
            StatusText = PromptText;
        }

        // ---------------- content → geometry → picture ----------------

        /// <summary>The payload exactly as entered — meaningful whitespace included.</summary>
        public string Content => _content;

        /// <summary>The generation result for the current content (null until the first SetContent).</summary>
        public QrSketchResult? CurrentResult => _current;

        /// <summary>The rendered preview — null whenever the current content is invalid.</summary>
        public QrPicture? Picture { get; private set; }

        /// <summary>What the window shows under the picture (prompt, error, or result).</summary>
        public string StatusText { get; private set; }

        /// <summary>True when the current content produced insertable geometry.</summary>
        public bool HasValidRequest => _current?.Succeeded == true;

        /// <summary>
        /// Records <paramref name="content"/> verbatim, regenerates the QR
        /// contour result, and re-renders the picture — or clears the picture
        /// and reports the actionable error. Every change re-runs the full
        /// pipeline so the picture always reflects the CURRENT input.
        /// </summary>
        public QrSketchResult SetContent(string content)
        {
            _content = content ?? string.Empty;
            _current = QrSketchGenerator.Generate(_content, SizeMm, GrowthFractionPerSide);
            if (_current.Succeeded)
            {
                var sketch = _current.Sketch!;
                Picture = QrPictureRenderer.Render(sketch, PreviewPixelsAcross);
                StatusText =
                    $"Ready — QR version {sketch.Info.Version}, " +
                    $"{sketch.Info.CellCount}×{sketch.Info.CellCount} cells at " +
                    $"{sketch.Info.FinalSizeMm:F1} mm. Select Create Sketch.";
            }
            else
            {
                Picture = null;
                StatusText = _current.Error!.Message;
            }
            return _current;
        }

        // ---------------- create sketch ----------------

        /// <summary>
        /// Creates the QR sketch for the CURRENT content through the host
        /// seam. Invalid content fails immediately — the host is never
        /// touched, so nothing partial or stale can reach the part. Valid
        /// requests go through the command's fresh eligibility checks (active
        /// part, current selection, no sketch in edit mode).
        /// </summary>
        public CommandResult CreateSketch(ISketchHost host)
        {
            if (_current == null)
                return CommandResult.Failure(PromptText);
            if (!_current.Succeeded)
                return CommandResult.Failure(_current.Error!.Message);

            var result = _command.Execute(host, _content);
            StatusText = result.Message;
            return result;
        }

        // ---------------- the single modeless window ----------------

        /// <summary>Windows created over the session's life — 1 while the same window is focused.</summary>
        public int WindowsCreated { get; private set; }

        /// <summary>A window exists and is currently open.</summary>
        public bool HasOpenWindow => _window?.IsOpen == true;

        /// <summary>
        /// The command entry point: focus the live window, or build a fresh
        /// one through the view factory and show it modeless. A window the
        /// user closed is forgotten and recreated cleanly.
        /// </summary>
        public void OpenOrFocusWindow()
        {
            if (_window != null && _window.IsOpen)
            {
                _window.FocusWindow();
                return;
            }

            var window = _windowFactory();
            window.Closed += OnWindowClosed;
            _window = window;
            WindowsCreated++;
            window.ShowModeless();
        }

        /// <summary>Programmatic close (probe/tests); the window may simply be gone.</summary>
        public void CloseWindow()
        {
            var window = _window;
            if (window == null) return;
            try { window.Close(); }
            catch { /* a stuck view must not trap the host */ }
            if (ReferenceEquals(_window, window))
                _window = null;
        }

        /// <summary>Disconnect path: close the window and drop every reference to it.</summary>
        public void Release()
        {
            var window = _window;
            _window = null;
            if (window == null) return;
            try { window.Closed -= OnWindowClosed; } catch { }
            try { window.Close(); } catch { }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (sender is IQrSketchWindow w)
            {
                try { w.Closed -= OnWindowClosed; } catch { }
            }
            if (ReferenceEquals(_window, sender))
                _window = null;
        }
    }
}
