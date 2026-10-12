using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Core.Preview;

namespace SolidWorksQrAddIn.Core.Interaction
{
    /// <summary>
    /// The interaction state behind the one modeless QR window — the
    /// headlessly testable core. Owns three things:
    ///
    ///   1. Inputs → geometry → picture. <see cref="SetContent"/>,
    ///      <see cref="SetSizeText"/>, <see cref="SetGrowthText"/>,
    ///      <see cref="SetEccLevel"/> and <see cref="SetInvert"/> store each
    ///      edit EXACTLY as entered (no trimming or replacing rejected
    ///      values), re-validate, regenerate the QR
    ///      contour result, and re-render the picture from it. An invalid
    ///      input clears the picture — a stale preview must never look like a
    ///      valid rendering of bad input.
    ///
    ///      The size text is the QR code width in the document's length unit,
    ///      excluding the quiet zone (spec "Size and growth"); the growth
    ///      text is the percentage of cell width grown per side. Scaling is
    ///      uniform: growth changes preserve the entered outline size through
    ///      the cell-pitch recalculation p = S/(N + 2r).
    ///
    ///      The content type (spec #11, ticket #27): <see cref="SetContentType"/>
    ///      selects a <see cref="PayloadComposer"/> preset — "raw" is the
    ///      default and is today's single content box unchanged. Other presets
    ///      expose fields (<see cref="CurrentPreset"/>); every field edit
    ///      (<see cref="SetPresetField"/>) composes them through
    ///      <see cref="PayloadComposer.Compose"/> and lands the literal
    ///      payload in the content box — the same verbatim state a typed
    ///      edit writes. A field set that cannot compose fails the request
    ///      with the composer's message on the status line (and no picture);
    ///      the box keeps its last text until the fields compose again —
    ///      nothing malformed reaches the generator or the host.
    ///
    ///      THE VERBATIM RULE: a manual content edit while a preset is
    ///      selected is treated exactly like a Raw/typed edit — the content
    ///      box is authoritative free text, so the type reverts to Raw (the
    ///      alternative — fields that no longer describe the box — would be
    ///      a lie the next field edit silently wipes). Entered field values
    ///      are remembered per preset: re-picking the type restores the
    ///      fields and recomposes, overwriting the box with their payload.
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
        /// <summary>The initial physical QR code width (mm), converted to the document's length unit.</summary>
        public const double InitialSizeMm = 25.0;

        /// <summary>The per-side outline growth the field starts with, as a cell-width fraction (3%).</summary>
        public const double InitialGrowthFractionPerSide = 0.03;

        /// <summary>Preview raster resolution (pixels per side, incl. margin).</summary>
        public const int PreviewPixelsAcross = 240;

        /// <summary>
        /// The pane's static note about the inserted artifact (spec #10,
        /// amended by ADR 0004): the created QR is one block instance drawn
        /// at the requested size — resize is an edit of the block's Scale
        /// factor, not a re-create; extrusion works off the sketch or the
        /// exploded block.
        /// </summary>
        public const string ResizeNote =
            "Inserted as one sketch block instance — drag to move it, or " +
            "edit the block's Scale property to resize (a factor: 2 doubles " +
            "the size). To extrude, select the sketch and run Extrude, or " +
            "explode the block and pick regions inside cells.";

        /// <summary>
        /// The pane's static note about the quiet zone: the keep-out square
        /// marks the light margin scanners need around the code. It is
        /// construction geometry inside the block — never part of an
        /// extrusion profile.
        /// </summary>
        public const string QuietZoneNote =
            "The inner construction square marks the QR code size. The outer " +
            "keep-out square marks the overall size, including the quiet zone. " +
            "Keep this margin free of other markings so scanners can read the code. " +
            "The squares are construction geometry and never form extrusion profiles.";

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
        private readonly IHostStateWatcher? _hostWatcher;
        private readonly CreateQrSketchCommand _command = new CreateQrSketchCommand();
        private IQrSketchWindow? _window;
        private IQrSketchWindow? _attachedView;
        private string _content = string.Empty;
        // The preset side of the content state (spec #11) — selection,
        // remembered fields and a pending compose failure travel together:
        // a direct content edit resets all three at once.
        private readonly PresetState _preset = new PresetState();
        private string _sizeText = InitialSizeText;
        private string _growthText = InitialGrowthPercentText;
        private double? _sizeMm = InitialSizeMm;
        private double? _growthFraction = InitialGrowthFractionPerSide;
        private QRCodeGenerator.ECCLevel _eccLevel = QRCodeGenerator.ECCLevel.M;
        private bool _invert;
        private QrSketchResult? _current;
        private QrSketchInfo? _info;
        private IReadOnlyList<QrInfoLine> _infoLines = Array.Empty<QrInfoLine>();
        private HostLengthUnit _lengthUnit = HostLengthUnit.Millimetres;

        /// <param name="windowFactory">Builds the surface the session drives.</param>
        /// <param name="hostProvider">
        /// Optional fresh-host factory consulted for the document's length
        /// unit when interpreting size input and formatting details. Creates
        /// still take a separate host per call, never a cached insertion
        /// route. Without a readable host the initial unit is millimetres;
        /// a later read failure retains the last known unit.
        /// </param>
        /// <param name="hostWatcher">
        /// Optional host-state change source (active-document switch, length
        /// unit edit). Each <see cref="IHostStateWatcher.Changed"/> event
        /// re-formats the unit-dependent readouts — the document's units
        /// land live without any field edit and without polling.
        /// </param>
        public QrSketchSession(
            Func<IQrSketchWindow> windowFactory, Func<ISketchHost>? hostProvider = null,
            IHostStateWatcher? hostWatcher = null)
        {
            _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
            _hostProvider = hostProvider;
            _lengthUnit = ReadLengthUnit();
            _sizeText = FormatSizeText(InitialSizeMm, _lengthUnit);
            _hostWatcher = hostWatcher;
            if (_hostWatcher != null)
                _hostWatcher.Changed += OnHostStateChanged;
            StatusText = PromptText;
            CellSizeReadout = string.Empty;
        }

        /// <summary>Host state changed behind the UI's back — refresh the unit-dependent display.</summary>
        private void OnHostStateChanged() => RefreshInfo();

        public void AttachView(IQrSketchWindow view)
        {
            _attachedView = view ?? throw new ArgumentNullException(nameof(view));
            NotifyWindow();
        }

        // ---------------- inputs → geometry → picture ----------------

        /// <summary>The payload exactly as entered — meaningful whitespace included.</summary>
        public string Content => _content;

        /// <summary>The size field in document units; converted when the document's unit changes.</summary>
        public string SizeText => _sizeText;

        public HostLengthUnit LengthUnit => _lengthUnit;

        public string SizeLabel => "QR code size (" + _lengthUnit.Symbol + "):";

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

        /// <summary>
        /// The error-correction level in force — L/M/Q/H, default M. A
        /// discrete selector value rather than a parsed text field, so it is
        /// always usable: there is no invalid state.
        /// </summary>
        public QRCodeGenerator.ECCLevel EccLevel => _eccLevel;

        /// <summary>
        /// The output polarity in force (ADR 0002): false emits the dark
        /// cells — black code on a light field; true emits the light cells —
        /// a white-on-black field for marking/engraving. Default off.
        /// </summary>
        public bool Invert => _invert;

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
        /// regeneration and <see cref="RefreshInfo"/> through the injected
        /// host provider.
        /// </summary>
        public string CellSizeReadout { get; private set; }

        /// <summary>
        /// The pane's info section — label/value rows describing the current
        /// request (size, matrix, quiet zone, encoding) with dimensions in
        /// the document's length unit. Empty whenever the current inputs are
        /// invalid: stale facts must never describe an unusable request.
        /// Rebuilt on every regeneration and by <see cref="RefreshInfo"/>.
        /// </summary>
        public IReadOnlyList<QrInfoLine> InfoLines => _infoLines;

        /// <summary>True when the current inputs produced insertable geometry.</summary>
        public bool HasValidRequest => _current?.Succeeded == true;

        /// <summary>
        /// Re-formats the cell-size readout and info lines against the
        /// document's CURRENT length unit — no regeneration, only display.
        /// Host notifications and showing the pane call this, so a
        /// document-unit change or document switch lands live even when the
        /// engineer has not edited a field. Best-effort: a host that cannot
        /// be read leaves the last formatting in place.
        /// </summary>
        public void RefreshInfo()
        {
            var unit = ReadLengthUnit();
            bool unitChanged = !SameUnit(unit, _lengthUnit);
            if (unitChanged)
            {
                if (_sizeMm.HasValue)
                    _sizeText = FormatSizeText(_sizeMm.Value, unit);
                _lengthUnit = unit;
                if (_current?.Error?.Kind == QrSketchErrorKind.InvalidSize)
                {
                    if (TryParseSize(_sizeText, _lengthUnit, out _, out string error))
                    {
                        Regenerate();
                        return;
                    }
                    _current = QrSketchResult.Fail(QrSketchErrorKind.InvalidSize, error);
                    StatusText = error;
                }
            }
            if (_info != null)
            {
                var lines = BuildInfoLines(_info, _content, out string cellSize);
                if (!unitChanged && cellSize == CellSizeReadout && LinesEqual(lines, _infoLines))
                    return;
                CellSizeReadout = cellSize;
                _infoLines = lines;
            }
            else if (!unitChanged)
                return;
            NotifyWindow();
        }

        private static bool SameUnit(HostLengthUnit a, HostLengthUnit b) =>
            a.MetresPerUnit == b.MetresPerUnit && a.Symbol == b.Symbol;

        private static string FormatSizeText(double mm, HostLengthUnit unit) =>
            (mm / (unit.MetresPerUnit * 1000.0)).ToString("G12", CultureInfo.InvariantCulture);

        /// <summary>
        /// Records <paramref name="content"/> verbatim and re-validates the
        /// whole request — current content, size and growth — so the picture
        /// and pending request always reflect the CURRENT inputs.
        ///
        /// A direct content edit is exactly a Raw/typed edit (spec #11's
        /// "editable verbatim" rule): the content box is authoritative free
        /// text, so the content type reverts to Raw and a pending preset-field
        /// failure drops away. The edited text stays — it is the literal
        /// payload now.
        /// </summary>
        public QrSketchResult SetContent(string content)
        {
            _preset.ResetToRaw();
            _content = content ?? string.Empty;
            return Regenerate();
        }

        // ---------------- content type + preset fields ----------------

        /// <summary>
        /// The selected content-type preset id — <see cref="PayloadComposer.DefaultPresetId"/>
        /// ("raw") initially and whenever the content box is edited directly.
        /// </summary>
        public string ContentPresetId => _preset.Id;

        /// <summary>
        /// The selected preset's descriptor — the pane renders
        /// <see cref="PayloadPresetDescriptor.Fields"/> as its per-preset
        /// inputs from this, never hard-coding per-preset UI.
        /// </summary>
        public PayloadPresetDescriptor CurrentPreset =>
            FindPreset(_preset.Id) ?? PayloadComposer.Presets[0];

        /// <summary>
        /// The selected preset's entered field values, keyed by field
        /// <see cref="PayloadFieldDescriptor.Key"/> — the pane prefills its
        /// inputs from this (absent keys take the descriptor's
        /// <see cref="PayloadFieldDescriptor.DefaultValue"/>). For "raw" the
        /// map is synthesized: its only field IS the content box.
        /// </summary>
        public IReadOnlyDictionary<string, string> PresetFields
        {
            get
            {
                if (_preset.Id == PayloadComposer.DefaultPresetId)
                    return new Dictionary<string, string> { ["content"] = _content };
                return new Dictionary<string, string>(_preset.FieldsFor(_preset.Id));
            }
        }

        /// <summary>
        /// The pending preset-composition failure — which field is wrong and
        /// why (<see cref="PayloadComposeError.FieldKey"/> names the bad
        /// input so the pane can flag it) — or null while the fields compose.
        /// </summary>
        public PayloadComposeError? PresetFieldError => _preset.ComposeError;

        /// <summary>
        /// Selects the content-type preset and composes immediately from its
        /// remembered fields (defaults on first pick): a composed payload
        /// lands in the content box verbatim; a field set that cannot compose
        /// fails the request openly — the composer's message on the status
        /// line and no picture — while the box keeps its LAST text until the
        /// fields compose again (the INVALID status is the signal that the
        /// box is not what the fields describe). An unknown id is refused
        /// with the composer's UnknownPreset error — the current selection
        /// stays in force.
        /// </summary>
        public QrSketchResult SetContentType(string? presetId)
        {
            var preset = FindPreset(presetId);
            if (preset == null)
            {
                // The composer produces the same refusal; only the error is
                // kept (Compose of an unknown id can never succeed).
                _preset.ComposeError =
                    PayloadComposer.Compose(presetId ?? string.Empty, null).Error;
                return Regenerate();
            }
            _preset.Id = preset.Id;
            return ComposePreset();
        }

        /// <summary>
        /// Records one preset-field edit — keyed by the descriptor's
        /// <see cref="PayloadFieldDescriptor.Key"/>, the literal text for
        /// text kinds, the option Value for Choice, "true"/"false" for
        /// Boolean — then recomposes. A successful compose lands in the
        /// content box verbatim; a failure names the bad field on the status
        /// line and the request stays invalid until it is fixed. A key the
        /// current preset does not declare is refused — a caller bug the
        /// status line should name, never silently drop.
        /// </summary>
        public QrSketchResult SetPresetField(string key, string? value)
        {
            var preset = CurrentPreset;
            if (!preset.Fields.Any(f => f.Key == key))
            {
                _preset.ComposeError = new PayloadComposeError(
                    PayloadComposeErrorKind.InvalidField, key,
                    preset.DisplayName + ": '" + (key ?? string.Empty) +
                    "' is not one of its fields (" +
                    string.Join(", ", preset.Fields.Select(f => f.Key)) + ").");
                return Regenerate();
            }
            if (_preset.Id == PayloadComposer.DefaultPresetId)
                return SetContent(value ?? string.Empty);   // raw's only field IS the content box
            _preset.FieldsFor(_preset.Id)[key] = value ?? string.Empty;
            return ComposePreset();
        }

        /// <summary>
        /// Composes the current preset's fields. Success lands the literal
        /// payload in <see cref="_content"/> — written directly, NOT through
        /// <see cref="SetContent"/>, which is the manual-edit path that
        /// reverts to Raw — and clears the compose error. Failure keeps the
        /// error pending (and the box's last text) for <see cref="Regenerate"/>
        /// to report.
        /// </summary>
        private QrSketchResult ComposePreset()
        {
            var compose = PayloadComposer.Compose(_preset.Id, PresetFields);
            _preset.ComposeError = compose.Error;
            if (compose.Succeeded)
                _content = compose.Content!;
            return Regenerate();
        }

        private static PayloadPresetDescriptor? FindPreset(string? presetId) =>
            PayloadComposer.Presets.FirstOrDefault(
                p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The content-type state (spec #11): which preset is selected, the
        /// field values remembered per preset, and a pending compose
        /// failure. One type because the three move together — a direct
        /// content edit (<see cref="SetContent"/>) resets all of them.
        /// </summary>
        private sealed class PresetState
        {
            /// <summary>The selected preset id — "raw" initially and after any direct content edit.</summary>
            public string Id = PayloadComposer.DefaultPresetId;

            /// <summary>
            /// Entered field values, remembered per preset — switching types
            /// and back restores them. "raw" never appears here: its single
            /// "content" field IS the box's text, so storing it would
            /// duplicate the truth.
            /// </summary>
            public readonly Dictionary<string, Dictionary<string, string>> FieldsByPreset =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// A pending composition failure — set by the last
            /// SetContentType / SetPresetField that could not produce a
            /// payload, cleared by the next successful compose or any direct
            /// content edit. While set the request is invalid no matter what
            /// other fields do.
            /// </summary>
            public PayloadComposeError? ComposeError;

            /// <summary>A direct content edit reverts the selection to Raw; the failure drops with it.</summary>
            public void ResetToRaw()
            {
                Id = PayloadComposer.DefaultPresetId;
                ComposeError = null;
            }

            /// <summary>The remembered field map for a non-raw preset, created on first use.</summary>
            public Dictionary<string, string> FieldsFor(string presetId)
            {
                if (!FieldsByPreset.TryGetValue(presetId, out var fields))
                {
                    fields = new Dictionary<string, string>();
                    FieldsByPreset[presetId] = fields;
                }
                return fields;
            }
        }

        /// <summary>
        /// Records the QR code size field in document units, excluding the
        /// quiet zone, and re-validates the
        /// whole request.
        /// </summary>
        public QrSketchResult SetSizeText(string sizeText) => SetSizeText(sizeText, _lengthUnit);

        public QrSketchResult SetSizeText(string sizeText, HostLengthUnit inputUnit)
        {
            _sizeText = sizeText ?? string.Empty;
            if (!SameUnit(inputUnit, _lengthUnit) && TryParseSize(_sizeText, inputUnit, out double mm, out _))
                _sizeText = FormatSizeText(mm, _lengthUnit);
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

        /// <summary>
        /// Records the error-correction level and re-validates the whole
        /// request — the level is part of the encoding, so a denser choice
        /// rebuilds the matrix, the picture, and the info lines just like a
        /// content edit.
        /// </summary>
        public QrSketchResult SetEccLevel(QRCodeGenerator.ECCLevel eccLevel)
        {
            _eccLevel = eccLevel;
            return Regenerate();
        }

        /// <summary>
        /// Flips the output polarity and re-validates the whole request: with
        /// <paramref name="invert"/> the emitted contours are the working
        /// matrix's light cells — a dark field with the code left unmarked —
        /// and the picture renders that same result (what you see is what
        /// gets sketched). Applies to future creates only; existing sketches
        /// are never mutated.
        /// </summary>
        public QrSketchResult SetInvert(bool invert)
        {
            _invert = invert;
            return Regenerate();
        }

        /// <summary>
        /// The request the CURRENT inputs describe — the one the generator
        /// and the create command both consume, so a create can never encode
        /// different settings than the preview showed.
        /// </summary>
        private QrSketchRequest CurrentRequest(double sizeMm, double growthFraction) =>
            new QrSketchRequest(_content, sizeMm, growthFraction)
            {
                EccLevel = _eccLevel,
                Invert = _invert,
            };

        private QrSketchResult Regenerate()
        {
            // Field-level validity first so the error names the field that is
            // actually wrong; the generator still re-checks everything itself.
            if (!TryParseSize(_sizeText, _lengthUnit, out double sizeMm, out string sizeError))
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
            else if (_preset.ComposeError != null)
            {
                // A pending preset-field failure is a content-level problem:
                // it wins over generation and nothing malformed reaches the
                // generator. The numeric fields stay recorded — they are not
                // what is wrong.
                _sizeMm = sizeMm;
                _growthFraction = growthFraction;
                _current = QrSketchResult.Fail(
                    QrSketchErrorKind.InvalidPayload, _preset.ComposeError.Message);
            }
            else
            {
                _sizeMm = sizeMm;
                _growthFraction = growthFraction;
                _current = QrSketchGenerator.Generate(CurrentRequest(sizeMm, growthFraction));
            }

            if (_current.Succeeded)
            {
                var sketch = _current.Sketch!;
                _info = sketch.Info;
                Picture = QrPictureRenderer.Render(sketch, PreviewPixelsAcross);
                // The status stays a one-liner — facts about the QR live in
                // the info section (InfoLines), not in status text.
                StatusText = "Ready — click Create Sketch to insert.";
                _infoLines = BuildInfoLines(_info, _content, out string cellSize);
                CellSizeReadout = cellSize;
            }
            else
            {
                _info = null;
                Picture = null;
                StatusText = _current.Error!.Message;
                CellSizeReadout = string.Empty;
                _infoLines = Array.Empty<QrInfoLine>();
            }
            NotifyWindow();
            return _current;
        }

        /// <summary>
        /// The info-section rows for the current request. Dimensions are
        /// shown only in the document's current length unit, matching the
        /// size field's unit without any secondary-unit display.
        /// </summary>
        private IReadOnlyList<QrInfoLine> BuildInfoLines(
            QrSketchInfo info, string content, out string cellSize)
        {
            var unit = _lengthUnit;
            string FormatMm(double mm) =>
                (mm / 1000.0 / unit.MetresPerUnit).ToString("G4", CultureInfo.InvariantCulture)
                + " " + unit.Symbol;

            cellSize = FormatMm(info.PitchMm);
            var lines = new List<QrInfoLine>
            {
                new QrInfoLine("QR code size", FormatMm(info.FinalSizeMm)),
                new QrInfoLine(
                    "Overall size",
                    FormatMm(info.MatrixCellCount * info.PitchMm)),
                new QrInfoLine("QR grid", info.CellCount + " × " + info.CellCount + " cells"),
                new QrInfoLine("With quiet zone", info.MatrixCellCount + " × " + info.MatrixCellCount + " cells"),
                new QrInfoLine("Cell size", cellSize),
                new QrInfoLine(
                    "Quiet zone",
                    QrSketchInfo.QuietZoneCellsPerSide + " cells (" +
                    FormatMm(QrSketchInfo.QuietZoneCellsPerSide * info.PitchMm) + ") per side"),
                new QrInfoLine(
                    "Encoding",
                    "Version " + info.Version + " · ECC " + _eccLevel + " · " + content.Length +
                    (content.Length == 1 ? " character" : " characters")),
                new QrInfoLine(
                    "Polarity",
                    info.Inverted
                        ? "Inverted — light code on a dark field"
                        : "Dark code on a light field"),
            };
            return lines;
        }

        private static bool LinesEqual(IReadOnlyList<QrInfoLine> a, IReadOnlyList<QrInfoLine> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Label != b[i].Label || a[i].Value != b[i].Value)
                    return false;
            return true;
        }

        private HostLengthUnit ReadLengthUnit()
        {
            try
            {
                var state = _hostProvider?.Invoke().CaptureState();
                return state?.LengthUnit ?? _lengthUnit;
            }
            catch
            {
                return _lengthUnit;
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
            try { (_attachedView ?? _window)?.SyncFromSession(); }
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

        private static bool TryParseSize(string text, HostLengthUnit unit, out double sizeMm, out string error)
        {
            var result = ParseNumericField(text, unit.Symbol,
                v => v > 0 && v * (unit.MetresPerUnit * 1000.0) > 0 &&
                     !double.IsInfinity(v * (unit.MetresPerUnit * 1000.0)), out double value);
            sizeMm = value * (unit.MetresPerUnit * 1000.0);
            switch (result)
            {
                case NumericFieldError.Empty:
                    error = $"Size is empty. Enter the QR code width in {unit.Symbol}, excluding the quiet zone.";
                    return false;
                case NumericFieldError.NotANumber:
                    error = $"Size must be a number in {unit.Symbol}, excluding the quiet zone; got '{Shorten(text)}'.";
                    return false;
                case NumericFieldError.OutOfRange:
                    error = $"Size must be a finite positive number in {unit.Symbol}; got '{Shorten(text)}'.";
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
                    error = "Cell growth is empty. Enter the percentage of cell width " +
                            "grown per side.";
                    return false;
                case NumericFieldError.NotANumber:
                    error = $"Cell growth must be a number — percent of cell width grown " +
                            $"per side; got '{Shorten(text)}'.";
                    return false;
                case NumericFieldError.OutOfRange:
                    error = $"Cell growth must be a finite percentage greater than 0% and " +
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

            // _current.Succeeded ⇒ both fields parsed — the request is exactly
            // the one the current result was generated and validated with.
            // The window/command boundary must never let a host exception
            // escape toward SolidWorks — a failure result is the contract.
            CommandResult result;
            try
            {
                result = _command.Execute(
                    host, CurrentRequest(_sizeMm!.Value, _growthFraction!.Value));
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
            if (_hostWatcher != null)
                try { _hostWatcher.Changed -= OnHostStateChanged; } catch { }
            var window = _window ?? _attachedView;
            _window = null;
            _attachedView = null;
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
