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
    ///   3. The single surface — the task-pane tab. The command entry is
    ///      <see cref="OpenOrFocusWindow"/>: bring the live pane forward or
    ///      attach one through the injected view factory. <see cref="Release"/>
    ///      (add-in disconnect) hides and drops it. The view itself is thin;
    ///      all behaviour lives here. Because session state can also change
    ///      WITHOUT the view driving — a probe-driven edit or create — every
    ///      mutation notifies the open view
    ///      (<see cref="IQrSketchWindow.SyncFromSession"/>) so the shown
    ///      fields, picture and status can never diverge from the session.
    /// </summary>
    public sealed class QrSketchSession
    {
        /// <summary>The size the field starts with (mm): the final grown QR outline width.</summary>
        public const double InitialSizeMm = 25.0;

        /// <summary>The per-side outline growth the field starts with, as a cell-width fraction (3%).</summary>
        public const double InitialGrowthFractionPerSide = 0.03;

        /// <summary>Preview raster resolution (pixels per side, incl. margin).</summary>
        public const int PreviewPixelsAcross = 240;

        /// <summary>
        /// The pane's static note (spec #10): the created QR is one block
        /// instance whose PropertyManager Scale field IS the size — resize is
        /// an edit of that field, not a re-create.
        /// </summary>
        public const string ResizeNote =
            "The QR is created as one sketch block instance — to resize it, " +
            "edit the block's Scale property (it reads as the QR size in " +
            "document units).";

        private const string PromptText = "Enter the text or URL to encode.";

        // The fields' initial text is DERIVED from the value constants —
        // one source of truth, so the shown defaults can never drift from
        // the parsed defaults they produce.
        private static readonly string InitialSizeText =
            InitialSizeMm.ToString(CultureInfo.InvariantCulture);
        private static readonly string InitialGrowthPercentText =
            (InitialGrowthFractionPerSide * 100.0).ToString(CultureInfo.InvariantCulture);

        private readonly Func<IQrSketchWindow> _windowFactory;
        private readonly Func<ISketchHost>? _hostProvider;
        private readonly CreateQrSketchCommand _command = new CreateQrSketchCommand();
        private IQrSketchWindow? _window;
        private string _content = string.Empty;
        private string _sizeText = InitialSizeText;
        private string _growthText = InitialGrowthPercentText;
        private double? _sizeMm = InitialSizeMm;
        private double? _growthFraction = InitialGrowthFractionPerSide;
        private QrSketchResult? _current;

        /// <param name="windowFactory">Builds the surface the session drives.</param>
        /// <param name="hostProvider">
        /// Optional fresh-host factory the session consults ONLY for the
        /// document's length unit when it formats the cell-size readout —
        /// display state, never an insertion route (creates still take the
        /// host per call). Null or a host that cannot be read leaves the
        /// readout in millimetres, honestly labelled.
        /// </param>
        public QrSketchSession(
            Func<IQrSketchWindow> windowFactory, Func<ISketchHost>? hostProvider = null)
        {
            _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
            _hostProvider = hostProvider;
            StatusText = PromptText;
            CellSizeReadout = string.Empty;
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

        /// <summary>
        /// The read-only cell (module) size readout in document units — the
        /// module pitch p = S/(N + 2r′), the same number the insertion guard
        /// enforces — e.g. "1.187 mm". Empty whenever the current inputs are
        /// invalid: a stale readout must never describe an unusable request.
        /// The unit is the host document's own length unit, re-read on every
        /// regeneration through the injected host provider.
        /// </summary>
        public string CellSizeReadout { get; private set; }

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
                // Plain status only — the window budget is
                // content/size/growth/picture/Create; no version, cell-count
                // or footprint readouts belong in the UI (spec). The cell
                // size is a separate read-only readout, not status text.
                StatusText = "Ready — select Create Sketch.";
                CellSizeReadout = FormatCellSize(sketch.Info.PitchMm);
            }
            else
            {
                Picture = null;
                StatusText = _current.Error!.Message;
                CellSizeReadout = string.Empty;
            }
            NotifyWindow();
            return _current;
        }

        /// <summary>
        /// The module pitch expressed in the document's length unit. The unit
        /// is host state — the injected provider is re-asked on every
        /// regeneration so a document switch lands in the readout live. A
        /// missing/failed provider shows millimetres: the pitch genuinely IS
        /// that many millimetres, so the label stays honest.
        /// </summary>
        private string FormatCellSize(double pitchMm)
        {
            var unit = ReadLengthUnit();
            double pitchDocUnits = pitchMm / 1000.0 / unit.MetresPerUnit;
            return pitchDocUnits.ToString("G4", CultureInfo.InvariantCulture) + " " + unit.Symbol;
        }

        private HostLengthUnit ReadLengthUnit()
        {
            try
            {
                var state = _hostProvider?.Invoke().CaptureState();
                return state?.LengthUnit ?? HostLengthUnit.Millimetres;
            }
            catch
            {
                return HostLengthUnit.Millimetres;
            }
        }

        /// <summary>
        /// Tells the open window to re-pull field texts, picture and status.
        /// Session state can change with no keystroke — a probe-driven edit —
        /// and the shown window must never diverge from it. Best-effort: a
        /// stuck view must not trap the caller.
        /// </summary>
        private void NotifyWindow()
        {
            try { _window?.SyncFromSession(); }
            catch { /* a dead dispatcher or stuck view must not trap the session */ }
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

        /// <summary>How the shared numeric-field parse can fail — each field names it differently.</summary>
        private enum NumericFieldError { None, Empty, NotANumber, OutOfRange }

        /// <summary>
        /// The pipeline both numeric fields share: trim, strip one optional
        /// trailing unit, reject empty, parse a number, reject non-finite and
        /// out-of-range values. Never substitutes a value — the caller owns
        /// the field-specific error text.
        /// </summary>
        private static NumericFieldError ParseNumericField(
            string? text, string unitSuffix, Func<double, bool> inRange, out double value)
        {
            value = 0;
            var t = (text ?? string.Empty).Trim();
            if (t.EndsWith(unitSuffix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - unitSuffix.Length).TrimEnd();
            if (t.Length == 0)
                return NumericFieldError.Empty;
            if (!TryParseNumber(t, out double v))
                return NumericFieldError.NotANumber;
            if (double.IsNaN(v) || double.IsInfinity(v) || !inRange(v))
                return NumericFieldError.OutOfRange;
            value = v;
            return NumericFieldError.None;
        }

        private static bool TryParseSize(string text, out double sizeMm, out string error)
        {
            switch (ParseNumericField(text, "mm", v => v > 0.0, out sizeMm))
            {
                case NumericFieldError.Empty:
                    error = "Size is empty. Enter the final grown QR outline width " +
                            "in millimetres, excluding the clear margin.";
                    return false;
                case NumericFieldError.NotANumber:
                    error = $"Size must be a number of millimetres — the final grown QR outline " +
                            $"width, excluding the clear margin; got '{Shorten(text)}'.";
                    return false;
                case NumericFieldError.OutOfRange:
                    error = $"Size must be a finite positive number of millimetres; got '{Shorten(text)}'.";
                    return false;
                default:
                    error = string.Empty;
                    return true;
            }
        }

        private static bool TryParseGrowth(string text, out double growthFraction, out string error)
        {
            growthFraction = 0;
            switch (ParseNumericField(text, "%", p => p > 0.0 && p < 50.0, out double percent))
            {
                case NumericFieldError.Empty:
                    error = "Outline growth is empty. Enter the percentage of cell width " +
                            "grown per side.";
                    return false;
                case NumericFieldError.NotANumber:
                    error = $"Outline growth must be a number — percent of cell width grown " +
                            $"per side; got '{Shorten(text)}'.";
                    return false;
                case NumericFieldError.OutOfRange:
                    error = $"Outline growth must be a finite percentage greater than 0% and " +
                            $"less than 50% of cell width per side; got '{Shorten(text)}'.";
                    return false;
                default:
                    growthFraction = percent / 100.0;
                    error = string.Empty;
                    return true;
            }
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
            // The window/command boundary must never let a host exception
            // escape toward SolidWorks — a failure result is the contract.
            CommandResult result;
            try
            {
                result = _command.Execute(
                    host, _content, _sizeMm!.Value, _growthFraction!.Value);
            }
            catch (Exception ex)
            {
                result = CommandResult.Failure(
                    $"The sketch could not be created: {ex.Message}");
            }
            StatusText = result.Message;
            // The create outcome is session state too — a probe-driven create
            // must land in the shown status exactly like a button click does.
            NotifyWindow();
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
