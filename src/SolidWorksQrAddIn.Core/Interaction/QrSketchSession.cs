using System;
using System.Globalization;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Preview;

namespace SolidWorksQrAddIn.Core.Interaction
{
    /// <summary>
    /// The interaction state behind the one modeless QR window — the
    /// headlessly testable core. Owns three things:
    ///
    ///   1. Inputs → geometry → picture. <see cref="SetContent"/>,
    ///      <see cref="SetSizeText"/> and <see cref="SetGrowthText"/> store
    ///      each input EXACTLY as entered (no trim/resolve/rewrite, no silent
    ///      replacement of rejected values), re-validate, regenerate the QR
    ///      contour result, and re-render the picture from it. An invalid
    ///      input clears the picture — a stale preview must never look like a
    ///      valid rendering of bad input.
    ///
    ///      The size text is the FINAL GROWN QR outline width in millimetres,
    ///      excluding the clear margin (spec "Size and growth"); the growth
    ///      text is the percentage of cell width grown per side. Scaling is
    ///      uniform: growth changes preserve the entered outline size through
    ///      the cell-pitch recalculation p = S/(N + 2r).
    ///
    ///   2. Create. <see cref="CreateSketch"/> runs the insertion command with
    ///      the CURRENT inputs against a freshly-captured host state — the
    ///      command itself revalidates the active part, the selected support,
    ///      and the no-sketch-being-edited rule at call time. Because every
    ///      edit re-validates immediately, a failure result is always the
    ///      current request: an earlier valid request can never be created
    ///      while the current inputs are invalid. Editing inputs only ever
    ///      changes the pending request and the picture — existing sketches
    ///      are never resized or regenerated.
    ///
    ///   3. The single window. The command entry is <see cref="OpenOrFocusWindow"/>:
    ///      focus the live window or create a fresh one through the injected
    ///      view factory. <see cref="Release"/> (add-in disconnect) closes and
    ///      drops it. The window itself is a thin view; all behaviour lives here.
    /// </summary>
    public sealed class QrSketchSession
    {
        /// <summary>The size the field starts with (mm): the final grown QR outline width.</summary>
        public const double InitialSizeMm = 25.0;

        /// <summary>The per-side outline growth the field starts with, as a cell-width fraction (3%).</summary>
        public const double InitialGrowthFractionPerSide = 0.03;

        /// <summary>Preview raster resolution (pixels per side, incl. margin).</summary>
        public const int PreviewPixelsAcross = 240;

        private const string PromptText = "Enter the text or URL to encode.";

        private readonly Func<IQrSketchWindow> _windowFactory;
        private readonly CreateQrSketchCommand _command = new CreateQrSketchCommand();
        private IQrSketchWindow? _window;
        private string _content = string.Empty;
        private string _sizeText = "25";
        private string _growthText = "3";
        private double? _sizeMm = InitialSizeMm;
        private double? _growthFraction = InitialGrowthFractionPerSide;
        private QrSketchResult? _current;

        public QrSketchSession(Func<IQrSketchWindow> windowFactory)
        {
            _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
            StatusText = PromptText;
        }

        // ---------------- inputs → geometry → picture ----------------

        /// <summary>The payload exactly as entered — meaningful whitespace included.</summary>
        public string Content => _content;

        /// <summary>The size field exactly as entered (millimetres; "25" initially).</summary>
        public string SizeText => _sizeText;

        /// <summary>The growth field exactly as entered (percent per side; "3" initially).</summary>
        public string GrowthText => _growthText;

        /// <summary>
        /// The parsed size in force (mm), or null while the size text holds no
        /// usable value — never a clamped or substituted number.
        /// </summary>
        public double? RequestedSizeMm => _sizeMm;

        /// <summary>
        /// The parsed per-side growth fraction in force, or null while the
        /// growth text holds no usable value.
        /// </summary>
        public double? RequestedGrowthFractionPerSide => _growthFraction;

        /// <summary>The generation result for the current inputs (null until the first edit).</summary>
        public QrSketchResult? CurrentResult => _current;

        /// <summary>The rendered preview — null whenever the current inputs are invalid.</summary>
        public QrPicture? Picture { get; private set; }

        /// <summary>What the window shows under the picture (prompt, error, or result).</summary>
        public string StatusText { get; private set; }

        /// <summary>True when the current inputs produced insertable geometry.</summary>
        public bool HasValidRequest => _current?.Succeeded == true;

        /// <summary>
        /// Records <paramref name="content"/> verbatim and re-validates the
        /// whole request — current content, size and growth — so the picture
        /// and pending request always reflect the CURRENT inputs.
        /// </summary>
        public QrSketchResult SetContent(string content)
        {
            _content = content ?? string.Empty;
            return Regenerate();
        }

        /// <summary>
        /// Records the size field verbatim (millimetres — the final grown QR
        /// outline width, excluding the clear margin) and re-validates the
        /// whole request.
        /// </summary>
        public QrSketchResult SetSizeText(string sizeText)
        {
            _sizeText = sizeText ?? string.Empty;
            return Regenerate();
        }

        /// <summary>
        /// Records the growth field verbatim (percent of cell width per side)
        /// and re-validates the whole request.
        /// </summary>
        public QrSketchResult SetGrowthText(string growthText)
        {
            _growthText = growthText ?? string.Empty;
            return Regenerate();
        }

        private QrSketchResult Regenerate()
        {
            // Field-level validity first so the error names the field that is
            // actually wrong; the generator still re-checks everything itself.
            if (!TryParseSize(_sizeText, out double sizeMm, out string sizeError))
            {
                _sizeMm = null;
                _current = QrSketchResult.Fail(QrSketchErrorKind.InvalidSize, sizeError);
            }
            else if (!TryParseGrowth(_growthText, out double growthFraction, out string growthError))
            {
                _sizeMm = sizeMm;
                _growthFraction = null;
                _current = QrSketchResult.Fail(QrSketchErrorKind.InvalidGrowth, growthError);
            }
            else
            {
                _sizeMm = sizeMm;
                _growthFraction = growthFraction;
                _current = QrSketchGenerator.Generate(_content, sizeMm, growthFraction);
            }

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

        // ---------------- field parsing ----------------
        // Deterministic: invariant "40.5" always parses; a machine whose
        // culture uses a decimal comma accepts "40,5" as well. A single
        // trailing unit ("mm", "%") is tolerated — labels carry the units,
        // but a typed unit is never a different VALUE. Anything else is a
        // useful error echoing the entered text — never a substituted number.

        private static bool TryParseNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

        private static bool TryParseSize(string text, out double sizeMm, out string error)
        {
            sizeMm = 0;
            var t = (text ?? string.Empty).Trim();
            if (t.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - 2).TrimEnd();
            if (t.Length == 0)
            {
                error = "Size is empty. Enter the final grown QR outline width " +
                        "in millimetres, excluding the clear margin.";
                return false;
            }
            if (!TryParseNumber(t, out double v))
            {
                error = $"Size must be a number of millimetres — the final grown QR outline " +
                        $"width, excluding the clear margin; got '{Shorten(text)}'.";
                return false;
            }
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0.0)
            {
                error = $"Size must be a finite positive number of millimetres; got '{Shorten(text)}'.";
                return false;
            }
            sizeMm = v;
            error = string.Empty;
            return true;
        }

        private static bool TryParseGrowth(string text, out double growthFraction, out string error)
        {
            growthFraction = 0;
            var t = (text ?? string.Empty).Trim();
            if (t.EndsWith("%", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1).TrimEnd();
            if (t.Length == 0)
            {
                error = "Outline growth is empty. Enter the percentage of cell width " +
                        "grown per side.";
                return false;
            }
            if (!TryParseNumber(t, out double percent))
            {
                error = $"Outline growth must be a number — percent of cell width grown " +
                        $"per side; got '{Shorten(text)}'.";
                return false;
            }
            if (double.IsNaN(percent) || double.IsInfinity(percent) ||
                percent <= 0.0 || percent >= 50.0)
            {
                error = $"Outline growth must be a finite percentage greater than 0% and " +
                        $"less than 50% of cell width per side; got '{Shorten(text)}'.";
                return false;
            }
            growthFraction = percent / 100.0;
            error = string.Empty;
            return true;
        }

        /// <summary>Echo of the rejected input, capped so a huge paste can't flood the status line.</summary>
        private static string Shorten(string? text)
        {
            var t = text ?? string.Empty;
            return t.Length <= 40 ? t : t.Substring(0, 40) + "…";
        }

        // ---------------- create sketch ----------------

        /// <summary>
        /// Creates the QR sketch for the CURRENT inputs through the host
        /// seam. Invalid inputs fail immediately — the host is never touched,
        /// so nothing partial or stale can reach the part. Valid requests go
        /// through the command's fresh eligibility checks (active part,
        /// current selection, no sketch in edit mode).
        /// </summary>
        public CommandResult CreateSketch(ISketchHost host)
        {
            if (_current == null)
                return CommandResult.Failure(PromptText);
            if (!_current.Succeeded)
                return CommandResult.Failure(_current.Error!.Message);

            // _current.Succeeded ⇒ both fields parsed — these are exactly the
            // values the current result was generated and validated with.
            var result = _command.Execute(
                host, _content, _sizeMm!.Value, _growthFraction!.Value);
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
