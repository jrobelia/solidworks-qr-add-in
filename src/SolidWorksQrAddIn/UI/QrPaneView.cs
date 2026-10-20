using System;
using System.Collections.Generic;
using System.Linq;
using QRCoder;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Core.Preview;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// The QR add-in's pane content — a code-built WPF <see cref="UserControl"/>
    /// hosted inside the SolidWorks task-pane tab via
    /// <see cref="QrTaskPaneControl"/> (WinForms + ElementHost). It is the same
    /// thin view over <see cref="QrSketchSession"/> the retired modeless window
    /// was: all content→picture→create behaviour lives in the session
    /// (headlessly tested); this class only:
    ///
    ///   - pushes the field texts verbatim into the session
    ///     (<see cref="QrSketchSession.SetContent"/>, <see cref="QrSketchSession.SetSizeText"/>,
    ///     <see cref="QrSketchSession.SetGrowthText"/>) — DEBOUNCED, so a
    ///     burst of keystrokes coalesces into one regeneration instead of
    ///     regenerating a dense QR synchronously per keystroke — plus the
    ///     discrete controls: the error-correction selector pushes
    ///     <see cref="QrSketchSession.SetEccLevel"/> and the invert checkbox
    ///     pushes <see cref="QrSketchSession.SetInvert"/> immediately (a
    ///     committed pick is never debounced),
    ///   - drives the content-type picker (spec #11): a dropdown of
    ///     <see cref="PayloadComposer.Presets"/> selection-changed into
    ///     <see cref="QrSketchSession.SetContentType"/>, plus per-preset
    ///     inputs built from <see cref="QrSketchSession.CurrentPreset"/>'s
    ///     field DESCRIPTORS (kind → control; never per-preset UI) feeding
    ///     <see cref="QrSketchSession.SetPresetField"/>. Raw renders no
    ///     extra inputs — its single "content" field IS the payload box
    ///     below, which stays editable verbatim under every preset,
    ///   - blits <see cref="QrSketchSession.Picture"/> (a Gray8 raster) into an
    ///     Image via BitmapSource.Create — the same grown contour result that
    ///     sketch insertion consumes,
    ///   - calls <see cref="QrSketchSession.CreateSketch"/> with a freshly
    ///     created host adapter (fresh host state every press — no cached
    ///     support) — only AFTER flushing every pending field edit, so a
    ///     create can never run on a stale, not-yet-regenerated request,
    ///   - lays out <see cref="QrSketchSession.InfoLines"/> as label/value
    ///     text rows — plain facts, deliberately not text boxes — and
    ///     <see cref="QrSketchSession.StatusText"/>,
    ///   - re-pulls everything on <see cref="SyncFromSession"/> — the session
    ///     calls it (through the task-pane adapter, already on this
    ///     dispatcher) whenever state changed without the view driving, so
    ///     the shown fields and picture can never diverge from the session.
    /// </summary>
    public sealed class QrPaneView : System.Windows.Controls.UserControl
    {
        /// <summary>
        /// How long after the last keystroke pending field edits are pushed
        /// into the session. Dense payloads take seconds to regenerate —
        /// regenerating per keystroke would make typing unbearable.
        /// </summary>
        private static readonly TimeSpan EditDebounce = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// One ECC dropdown entry — the level code the session parser and
        /// probes speak (<see cref="Code"/>, what SelectedValue returns) plus
        /// the label shown in the list: the code first, then what the level
        /// means (<see cref="Label"/>).
        /// </summary>
        private sealed class EccOption
        {
            public string Code { get; }
            public string Label { get; }
            public EccOption(string code, string label) { Code = code; Label = label; }
            public override string ToString() => Label;
        }

        private static readonly EccOption[] EccOptions =
        {
            new EccOption("L", "L — low, ~7% damage recovery"),
            new EccOption("M", "M — medium, ~15% damage recovery"),
            new EccOption("Q", "Q — quartile, ~25% damage recovery"),
            new EccOption("H", "H — high, ~30% damage recovery"),
        };

        /// <summary>
        /// One cell-shape dropdown entry — the canonical code the session
        /// parser and probes speak (<see cref="QrCellShapes"/>) plus the
        /// label shown in the list.
        /// </summary>
        private sealed class ShapeOption
        {
            public string Code { get; }
            public string Label { get; }
            public ShapeOption(string code, string label) { Code = code; Label = label; }
            public override string ToString() => Label;
        }

        private static readonly ShapeOption[] ShapeOptions =
        {
            new ShapeOption(QrCellShapes.SquareCode,
                "Square — grown cell outlines (block)"),
            new ShapeOption(QrCellShapes.CircleCode,
                "Circle — real circle dots (linear pattern)"),
        };

        private readonly QrSketchSession _session;
        private readonly Func<ISketchHost> _hostProvider;
        private readonly ComboBox _presetCombo;
        private readonly StackPanel _presetFieldsPanel;
        private readonly TextBlock _contentLabel;
        private readonly TextBox _contentBox;
        private readonly TextBox _sizeBox;
        private readonly TextBlock _sizeLabel;
        private HostLengthUnit _sizeInputUnit;
        private readonly TextBox _growthBox;
        private readonly TextBlock _growthLabel;
        private readonly ComboBox _shapeCombo;
        private readonly TextBlock _shapeParamLabel;
        private readonly TextBox _shapeParamBox;
        private readonly ComboBox _eccBox;
        private readonly CheckBox _invertBox;
        private readonly Image _picture;
        private readonly Border _infoSection;
        private readonly Grid _infoGrid;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _editDebounce;
        private bool _contentDirty;
        private bool _sizeDirty;
        private bool _growthDirty;
        private bool _shapeParamDirty;
        private bool _suppressFieldEvents;
        // Preset-field inputs keyed by the descriptors' contract Key, and the
        // keys with a pending debounced text edit. Discrete controls
        // (dropdown, checkbox) apply on change — they are never dirty.
        private readonly Dictionary<string, Control> _presetInputs =
            new Dictionary<string, Control>();
        private readonly HashSet<string> _dirtyPresetFields = new HashSet<string>();
        private string? _shownPresetId;

        /// <param name="session">Session state the view renders and drives.</param>
        /// <param name="hostProvider">Builds a fresh host adapter per Create — never cached.</param>
        public QrPaneView(QrSketchSession session, Func<ISketchHost> hostProvider)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _hostProvider = hostProvider ?? throw new ArgumentNullException(nameof(hostProvider));

            var panel = new StackPanel { Margin = new Thickness(10) };

            // Content type (ticket #27): the dropdown is the composer preset
            // list verbatim — id is the compose key, display name is shown.
            panel.Children.Add(new TextBlock
            {
                Text = "Content type:",
                Margin = new Thickness(0, 0, 0, 4),
            });
            _presetCombo = new ComboBox
            {
                ItemsSource = PayloadComposer.Presets,
                DisplayMemberPath = nameof(PayloadPresetDescriptor.DisplayName),
                SelectedValuePath = nameof(PayloadPresetDescriptor.Id),
                ToolTip = "What the QR payload describes — Raw is free text, " +
                          "the other types format their fields into the " +
                          "literal encoded payload shown below.",
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 160,
            };
            panel.Children.Add(_presetCombo);

            // Per-preset inputs, rebuilt from the selected preset's field
            // descriptors whenever the type changes. Empty under Raw: its
            // only field is the content box below (today's workflow).
            _presetFieldsPanel = new StackPanel { Visibility = Visibility.Collapsed };
            panel.Children.Add(_presetFieldsPanel);

            _contentLabel = new TextBlock { Margin = new Thickness(0, 8, 0, 4) };
            panel.Children.Add(_contentLabel);
            _contentBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinLines = 3,
                MaxLines = 6,
                Height = 64,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(_contentBox);

            // Spec budget: content, size, growth, picture, Create.
            _sizeInputUnit = _session.LengthUnit;
            _sizeLabel = new TextBlock
            {
                Text = _session.SizeLabel,
                Margin = new Thickness(0, 8, 0, 4),
            };
            panel.Children.Add(_sizeLabel);
            _sizeBox = new TextBox
            {
                ToolTip = "Width and height of the QR code in the document's length unit. " +
                          "The quiet zone is not included.",
            };
            panel.Children.Add(_sizeBox);

            // The cell-shape selector (spec #13, ticket #38): a discrete
            // two-option dropdown — a committed pick pushes into the
            // session immediately like the ECC selector. The growth and
            // dot-size fields below trade which one is shown — growth is
            // inert under Circle, dot size under Square — so the selector
            // and everything above it never moves when the shape flips.
            // Both fields keep their entered text verbatim either way.
            panel.Children.Add(new TextBlock
            {
                Text = "Cell shape:",
                Margin = new Thickness(0, 8, 0, 4),
            });
            _shapeCombo = new ComboBox
            {
                ItemsSource = ShapeOptions,
                DisplayMemberPath = nameof(ShapeOption.Label),
                SelectedValuePath = nameof(ShapeOption.Code),
                ToolTip = "The primitive each emitted QR cell becomes. " +
                          "Square — today's grown outline contours, created as " +
                          "one block. Circle — one true circle dot per cell, " +
                          "sized by the dot-size field (a native sketch pattern " +
                          "on hosts that emit it).",
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 220,
            };
            panel.Children.Add(_shapeCombo);

            _shapeParamLabel = new TextBlock
            {
                Text = _session.ShapeParameterLabel,
                Margin = new Thickness(0, 8, 0, 4),
            };
            panel.Children.Add(_shapeParamLabel);
            _shapeParamBox = new TextBox
            {
                ToolTip = "The dot diameter as a percentage of the cell pitch — " +
                          "higher = larger dots. Dots must stay disjoint: the " +
                          "value is capped per request so the dot-to-dot gap " +
                          "never falls below the insertion floor.",
            };
            panel.Children.Add(_shapeParamBox);

            // Growth lives under the selector with dot size: the two
            // shape-conditional fields share one slot, so hiding/reshowing
            // never shifts the selector's position (QA note on PR #43 —
            // growth used to sit ABOVE the dropdown and the layout jumped).
            _growthLabel = new TextBlock
            {
                Text = "Cell growth per side (% of cell width):",
                Margin = new Thickness(0, 8, 0, 4),
            };
            panel.Children.Add(_growthLabel);
            _growthBox = new TextBox
            {
                ToolTip = "Widens each dark cell on all sides by this percentage of " +
                          "the cell spacing, helping diagonally touching cells connect. " +
                          "The QR code size stays fixed.",
            };
            panel.Children.Add(_growthBox);

            panel.Children.Add(new TextBlock
            {
                Text = "Error correction:",
                Margin = new Thickness(0, 8, 0, 4),
            });
            _eccBox = new ComboBox
            {
                ItemsSource = EccOptions,
                DisplayMemberPath = nameof(EccOption.Label),
                SelectedValuePath = nameof(EccOption.Code),
                ToolTip = "QR error-correction level — how much damage the code " +
                          "survives. A higher level recovers more but packs more " +
                          "cells into the same size — H is densest, L lightest. " +
                          "M is the default.",
            };
            panel.Children.Add(_eccBox);

            // The invert toggle (spec #11, ADR 0002): a checkbox is a
            // committed edit the moment it is clicked — it pushes straight
            // into the session like a flushed field, never through the
            // keystroke debounce. Applies to future creates only.
            _invertBox = new CheckBox
            {
                Content = "Invert output (light code on a dark field)",
                ToolTip = "Emits the code's light cells instead of the dark ones: " +
                          "the sketch becomes a dark field with the code left " +
                          "unmarked, for marking/engraving workflows. " +
                          "Applies to the next sketch created — existing " +
                          "sketches are never changed.",
                Margin = new Thickness(0, 8, 0, 0),
            };
            _invertBox.Checked += OnInvertToggled;
            _invertBox.Unchecked += OnInvertToggled;
            panel.Children.Add(_invertBox);

            var frame = new Border
            {
                BorderBrush = SystemColors.ControlDarkBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 8, 0, 0),
                Width = QrSketchSession.PreviewPixelsAcross,
                Height = QrSketchSession.PreviewPixelsAcross,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _picture = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.NearestNeighbor);
            frame.Child = _picture;
            panel.Children.Add(frame);

            var create = new Button
            {
                Content = "Create Sketch",
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            create.Click += OnCreateClicked;
            panel.Children.Add(create);

            // Info section (spec #10 follow-up): read-only FACTS about the
            // request the fields currently describe — label/value text rows,
            // deliberately NOT text boxes so nothing looks editable. The
            // session formats every value (headlessly testable); this view
            // only lays the rows out. Hidden while inputs are invalid —
            // stale facts must never describe an unusable request.
            var infoPanel = new StackPanel { Margin = new Thickness(8) };
            infoPanel.Children.Add(new TextBlock
            {
                Text = "QR details",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            _infoGrid = new Grid();
            _infoGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto });
            _infoGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoPanel.Children.Add(_infoGrid);
            _infoSection = new Border
            {
                BorderBrush = SystemColors.ControlDarkBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 8, 0, 0),
            };
            _infoSection.Child = infoPanel;
            panel.Children.Add(_infoSection);

            panel.Children.Add(new TextBlock
            {
                Text = QrSketchSession.ResizeNote,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 8, 0, 0),
            });

            panel.Children.Add(new TextBlock
            {
                Text = QrSketchSession.QuietZoneNote,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 6, 0, 0),
            });

            _status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(_status);

            // The pane is taller than the task-pane host once preset fields
            // and the info section are shown — scroll vertically rather than
            // clip. Horizontal is disabled so text wraps to the pane width.
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            _editDebounce = new DispatcherTimer { Interval = EditDebounce };
            _editDebounce.Tick += (_, __) => FlushPendingEdits();
            _contentBox.TextChanged += (_, __) => MarkEdited(ref _contentDirty);
            _sizeBox.TextChanged += (_, __) => MarkEdited(ref _sizeDirty);
            _growthBox.TextChanged += (_, __) => MarkEdited(ref _growthDirty);
            _shapeParamBox.TextChanged += (_, __) => MarkEdited(ref _shapeParamDirty);
            // The cell-shape dropdown is discrete — a committed pick pushes
            // into the session immediately, exactly like the ECC selector;
            // an unrecognised code is refused by the shared parser, never
            // coerced.
            _shapeCombo.SelectionChanged += (_, __) =>
            {
                if (_suppressFieldEvents || _shapeCombo.SelectedValue == null)
                    return;
                if (QrCellShapes.TryParse(_shapeCombo.SelectedValue as string,
                        out QrCellShape cellShape))
                    _session.SetCellShape(cellShape);
            };
            // A dropdown pick is discrete — there is nothing to debounce, so
            // the level lands in the session immediately.
            _eccBox.SelectionChanged += (_, __) =>
            {
                if (_suppressFieldEvents || _eccBox.SelectedValue == null)
                    return;
                // The shared parser seam — an unrecognized level is refused
                // rather than coerced, exactly like the probe surface.
                if (QrEccLevels.TryParse(_eccBox.SelectedValue as string,
                        out QRCodeGenerator.ECCLevel ecc))
                    _session.SetEccLevel(ecc);
            };
            _presetCombo.SelectionChanged += (_, __) => OnPresetTypeChanged();

            // Unit-change refresh is event-driven through the session's
            // host-state watcher (ActiveDocChangeNotify + UnitsChangeNotify);
            // showing the tab is one more honest moment to re-read, in case
            // the pane missed an event while hidden.
            IsVisibleChanged += (_, __) => { if (IsVisible) _session.RefreshInfo(); };

            // The pane outlives any one "open" — pull the session's existing
            // inputs so a session that outlives a hidden tab still shows the
            // real values. Suppressed: assigning them is a pull, not an edit.
            _suppressFieldEvents = true;
            try
            {
                _presetCombo.SelectedValue = _session.ContentPresetId;
                _contentBox.Text = _session.Content;
                _sizeBox.Text = _session.SizeText;
                _growthBox.Text = _session.GrowthText;
                _shapeCombo.SelectedValue = QrCellShapes.CodeFor(_session.CellShape);
                _shapeParamBox.Text = _session.ShapeParameterText;
                _eccBox.SelectedValue = _session.EccLevel.ToString();
                _invertBox.IsChecked = _session.Invert;
            }
            finally { _suppressFieldEvents = false; }
            RebuildPresetFields();
            RefreshView();
        }

        /// <summary>
        /// The session changed without this view driving it (a probe edit,
        /// a create outcome): re-pull the field texts, picture and status so
        /// the shown view can never diverge from the session. A field with a
        /// pending debounced edit keeps the engineer's in-progress text —
        /// its queued flush will push the newer text back into the session.
        /// Callers must already be on this view's dispatcher (the task-pane
        /// adapter marshals).
        /// </summary>
        public void SyncFromSession()
        {
            if (_sizeDirty && (_sizeInputUnit.MetresPerUnit != _session.LengthUnit.MetresPerUnit ||
                               _sizeInputUnit.Symbol != _session.LengthUnit.Symbol))
                FlushPendingEdits();
            _suppressFieldEvents = true;
            try
            {
                if (!string.Equals(_presetCombo.SelectedValue as string,
                        _session.ContentPresetId, StringComparison.Ordinal))
                    _presetCombo.SelectedValue = _session.ContentPresetId;
                // A preset switch — from the dropdown or a probe-driven
                // manual content edit reverting to Raw — rebuilds the field
                // inputs from the new preset's descriptors.
                if (_shownPresetId != _session.ContentPresetId)
                    RebuildPresetFields();
                PullPresetFieldValues();
                if (!_contentDirty && _contentBox.Text != _session.Content)
                    _contentBox.Text = _session.Content;
                if (!_sizeDirty && _sizeBox.Text != _session.SizeText)
                    _sizeBox.Text = _session.SizeText;
                if (!_growthDirty && _growthBox.Text != _session.GrowthText)
                    _growthBox.Text = _session.GrowthText;
                // A selector pick carries no in-progress state — always
                // re-pull. The shape-parameter text is a debounced field:
                // an in-progress edit keeps the engineer's text like the
                // other text boxes.
                var shapeCode = QrCellShapes.CodeFor(_session.CellShape);
                if (!Equals(_shapeCombo.SelectedValue, shapeCode))
                    _shapeCombo.SelectedValue = shapeCode;
                if (!_shapeParamDirty && _shapeParamBox.Text != _session.ShapeParameterText)
                    _shapeParamBox.Text = _session.ShapeParameterText;
                if (!Equals(_eccBox.SelectedValue, _session.EccLevel.ToString()))
                    _eccBox.SelectedValue = _session.EccLevel.ToString();
                // A toggle carries no in-progress state — always re-pull.
                if (_invertBox.IsChecked != _session.Invert)
                    _invertBox.IsChecked = _session.Invert;
            }
            finally { _suppressFieldEvents = false; }
            RefreshView();
        }

        // ---------------- content type + preset fields ----------------
        // The dropdown picks the composer preset; the panel below it renders
        // an input per field DESCRIPTOR (kind → control). Raw renders
        // nothing — its single "content" field is the payload box, which is
        // always visible and editable verbatim under every preset.

        /// <summary>Dropdown pick → the session's content type, then a rebuild via sync.</summary>
        private void OnPresetTypeChanged()
        {
            if (_suppressFieldEvents)
                return;
            // Pending edits belong to the OLD preset — they flush under it
            // before the new selection composes.
            FlushPendingEdits();
            _session.SetContentType(_presetCombo.SelectedValue as string);
        }

        /// <summary>
        /// Re-lays the preset-field inputs for the session's CURRENT preset
        /// — called on construction and whenever a sync sees the type
        /// change. Controls are built from <see cref="PayloadFieldDescriptor.Kind"/>
        /// alone: Text/MultilineText → a box, Choice → a dropdown of the
        /// declared options, Boolean → a checkbox.
        /// </summary>
        private void RebuildPresetFields()
        {
            _presetFieldsPanel.Children.Clear();
            _presetInputs.Clear();
            _dirtyPresetFields.Clear();
            _shownPresetId = _session.ContentPresetId;
            var preset = _session.CurrentPreset;
            bool wasSuppressing = _suppressFieldEvents;
            _suppressFieldEvents = true;
            try
            {
                // Raw's only declared field IS the content box — rendering it
                // again would show the same state twice. The panel collapses:
                // today's single-content-box workflow is unchanged.
                if (preset.Id == PayloadComposer.DefaultPresetId)
                {
                    _presetFieldsPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                var stored = _session.PresetFields;
                foreach (var field in preset.Fields)
                    _presetFieldsPanel.Children.Add(BuildPresetField(field, stored));
                _presetFieldsPanel.Visibility = Visibility.Visible;
            }
            finally { _suppressFieldEvents = wasSuppressing; }
        }

        /// <summary>Builds one labelled input row for a field descriptor and registers it.</summary>
        private FrameworkElement BuildPresetField(
            PayloadFieldDescriptor field, IReadOnlyDictionary<string, string> stored)
        {
            var value = stored.TryGetValue(field.Key, out var entered)
                ? entered : field.DefaultValue;

            Control input;
            switch (field.Kind)
            {
                case PayloadFieldKind.Choice:
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = field.Options,
                        DisplayMemberPath = nameof(PayloadFieldOption.Label),
                        SelectedValuePath = nameof(PayloadFieldOption.Value),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        MinWidth = 160,
                    };
                    combo.SelectedValue = value;
                    combo.SelectionChanged += (_, __) => OnPresetInputPicked(
                        field.Key, combo.SelectedValue as string ?? string.Empty);
                    input = combo;
                    break;
                }
                case PayloadFieldKind.Boolean:
                {
                    var check = new CheckBox();
                    check.IsChecked = string.Equals(
                        value, "true", StringComparison.OrdinalIgnoreCase);
                    check.Click += (_, __) => OnPresetInputPicked(
                        field.Key, check.IsChecked == true ? "true" : "false");
                    input = check;
                    break;
                }
                default:
                {
                    var box = new TextBox { ToolTip = field.Hint };
                    if (field.Kind == PayloadFieldKind.MultilineText)
                    {
                        box.AcceptsReturn = true;
                        box.TextWrapping = TextWrapping.Wrap;
                        box.MinLines = 2;
                        box.MaxLines = 4;
                        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    }
                    box.Text = value;
                    box.TextChanged += (_, __) => MarkPresetFieldEdited(field.Key);
                    input = box;
                    break;
                }
            }
            _presetInputs[field.Key] = input;

            var row = new StackPanel();
            row.Children.Add(new TextBlock
            {
                Text = field.Label + (field.Required ? " *" : ""),
                ToolTip = field.Required ? "Required" : null,
                Margin = new Thickness(0, 8, 0, 4),
            });
            row.Children.Add(input);
            return row;
        }

        /// <summary>
        /// A text-kind preset input keystroke — marks the field dirty and
        /// restarts the shared debounce, exactly like the fixed fields.
        /// </summary>
        private void MarkPresetFieldEdited(string key)
        {
            if (_suppressFieldEvents)
                return;
            _dirtyPresetFields.Add(key);
            _editDebounce.Stop();
            _editDebounce.Start();
        }

        /// <summary>
        /// A discrete preset input (dropdown pick, checkbox toggle) — no
        /// keystroke burst to debounce, so it applies at once, over the
        /// freshest text values: pending edits flush first.
        /// </summary>
        private void OnPresetInputPicked(string key, string value)
        {
            if (_suppressFieldEvents)
                return;
            FlushPendingEdits();
            _session.SetPresetField(key, value);
        }

        /// <summary>The field-map value an input currently holds.</summary>
        private static string ReadPresetValue(Control input)
        {
            switch (input)
            {
                case TextBox box: return box.Text;
                case CheckBox check: return check.IsChecked == true ? "true" : "false";
                case ComboBox combo: return combo.SelectedValue as string ?? string.Empty;
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Session→view pull for the preset inputs — the same convention the
        /// fixed fields follow: a field with a pending debounced edit keeps
        /// the engineer's in-progress text; everything else shows the
        /// session's stored value (descriptor default when never entered).
        /// </summary>
        private void PullPresetFieldValues()
        {
            var stored = _session.PresetFields;
            foreach (var field in _session.CurrentPreset.Fields)
            {
                if (!_presetInputs.TryGetValue(field.Key, out var input))
                    continue;
                var value = stored.TryGetValue(field.Key, out var v)
                    ? v : field.DefaultValue;
                switch (input)
                {
                    case TextBox box when !_dirtyPresetFields.Contains(field.Key):
                        if (box.Text != value)
                            box.Text = value;
                        break;
                    case ComboBox combo
                        when !string.Equals(combo.SelectedValue as string, value,
                            StringComparison.Ordinal):
                        combo.SelectedValue = value;
                        break;
                    case CheckBox check:
                        check.IsChecked = string.Equals(
                            value, "true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }

        // ---------------- debounced field edits ----------------
        // A keystroke only MARKS the field dirty and restarts the timer; the
        // expensive session regeneration runs once per quiet interval, not
        // once per keystroke. The dirty flag is also the no-stale-request
        // guard: Create flushes every pending edit before asking the session
        // to create, so the request it validates is always the shown one.

        private void MarkEdited(ref bool dirtyFlag)
        {
            if (_suppressFieldEvents)
                return;   // a session→view text pull is not an edit
            dirtyFlag = true;
            _editDebounce.Stop();
            _editDebounce.Start();
        }

        /// <summary>
        /// Push every pending field edit into the session now, regenerating
        /// once per dirty field. The debounce tick and Create both funnel
        /// here — a create can never run on inputs that have not been
        /// revalidated yet. Also called by the task-pane adapter when the tab
        /// is hidden, so a queued edit still lands in the session.
        /// </summary>
        public void FlushPendingEdits()
        {
            _editDebounce.Stop();
            if (_sizeDirty)
            {
                _sizeDirty = false;
                _session.SetSizeText(_sizeBox.Text, _sizeInputUnit);
            }
            if (_growthDirty)
            {
                _growthDirty = false;
                _session.SetGrowthText(_growthBox.Text);
            }
            if (_shapeParamDirty)
            {
                _shapeParamDirty = false;
                _session.SetShapeParameterText(_shapeParamBox.Text);
            }
            // Preset-field edits flush BEFORE the content box: a direct
            // content edit under a preset is the Raw-reverting manual edit —
            // it must land last so the typed text, not a recomposition, wins.
            // A key stays in the dirty set until ITS push — the same
            // convention the fixed fields use — so a sync triggered by an
            // earlier push can never re-pull (wipe) not-yet-flushed inputs.
            foreach (var key in _dirtyPresetFields.ToArray())
            {
                _dirtyPresetFields.Remove(key);
                if (_presetInputs.TryGetValue(key, out var input))
                    _session.SetPresetField(key, ReadPresetValue(input));
            }
            if (_contentDirty)
            {
                _contentDirty = false;
                _session.SetContent(_contentBox.Text);
            }
            RefreshView();
        }

        /// <summary>
        /// The invert toggle is a committed edit on click — no debounce, no
        /// dirty flag: it pushes the new polarity into the session at once,
        /// exactly like a flushed field edit. Suppressed while the view is
        /// pulling session state (a pull is not an edit).
        /// </summary>
        private void OnInvertToggled(object? sender, RoutedEventArgs e)
        {
            if (_suppressFieldEvents)
                return;
            _session.SetInvert(_invertBox.IsChecked == true);
        }

        private void OnCreateClicked(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Flush BEFORE create: the request being created must be the
                // one the engineer sees, never an earlier still-valid state.
                FlushPendingEdits();
                var result = _session.CreateSketch(_hostProvider());
                ShowStatus(result.Message, isError: !result.Succeeded);
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, isError: true);
            }
        }

        private void RefreshView()
        {
            _sizeInputUnit = _session.LengthUnit;
            _sizeLabel.Text = _session.SizeLabel;
            // The payload box label says what the box holds: Raw is today's
            // free text; a preset's box is its composed literal payload —
            // still editable verbatim either way.
            _contentLabel.Text = _session.ContentPresetId == PayloadComposer.DefaultPresetId
                ? "Text or URL (encoded exactly as entered):"
                : _session.CurrentPreset.DisplayName + " payload (editable verbatim):";
            // Inert-field hiding (spec #13, ticket #38): under Circle the
            // growth field is gone; under Square the dot-size field is.
            // A hidden field keeps its session text verbatim — re-picking
            // the shape restores the field with the value as entered.
            var growthShown = _session.GrowthApplies
                ? Visibility.Visible : Visibility.Collapsed;
            _growthLabel.Visibility = growthShown;
            _growthBox.Visibility = growthShown;
            _shapeParamLabel.Text = _session.ShapeParameterLabel;
            var paramShown = _session.ShapeParameterApplies
                ? Visibility.Visible : Visibility.Collapsed;
            _shapeParamLabel.Visibility = paramShown;
            _shapeParamBox.Visibility = paramShown;
            FlagPresetFieldError();
            _picture.Source = _session.Picture == null ? null : ToBitmapSource(_session.Picture);
            RebuildInfoRows();
            ShowStatus(_session.StatusText, isError: !_session.HasValidRequest);
        }

        /// <summary>
        /// Red-borders the input a failed compose names (FieldKey) — the
        /// status line already carries the message; this just points at the
        /// field to fix. No failing field → every input clears.
        /// </summary>
        private void FlagPresetFieldError()
        {
            var badKey = _session.PresetFieldError?.FieldKey;
            foreach (var pair in _presetInputs)
            {
                bool isBad = badKey != null &&
                    string.Equals(pair.Key, badKey, StringComparison.Ordinal);
                pair.Value.BorderBrush = isBad ? Brushes.DarkRed : null;
            }
        }

        /// <summary>
        /// Re-lay the info section from <see cref="QrSketchSession.InfoLines"/>
        /// — label/value TextBlocks, facts not fields. The whole section
        /// collapses while the inputs are invalid so no stale facts show.
        /// </summary>
        private void RebuildInfoRows()
        {
            var lines = _session.InfoLines;
            _infoGrid.Children.Clear();
            _infoGrid.RowDefinitions.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                _infoGrid.RowDefinitions.Add(
                    new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = lines[i].Label,
                    Foreground = SystemColors.GrayTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 10, 3),
                };
                var value = new TextBlock
                {
                    Text = lines[i].Value,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                    ToolTip = lines[i].Label == "Cell size"
                        ? "Spacing from one cell to the next, before cell growth."
                        : lines[i].Label == "Overall size"
                            ? "Width and height including the quiet zone."
                            : null,
                };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                _infoGrid.Children.Add(label);
                _infoGrid.Children.Add(value);
            }
            _infoSection.Visibility = lines.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public string InfoSnapshot
        {
            get
            {
                var labels = _infoGrid.Children.OfType<TextBlock>();
                var cell = labels.FirstOrDefault(x => Grid.GetColumn(x) == 0 && x.Text == "Cell size");
                string cellText = cell == null ? string.Empty : labels.First(
                    x => Grid.GetColumn(x) == 1 && Grid.GetRow(x) == Grid.GetRow(cell)).Text;
                return $"viewSize={_sizeBox.Text} viewSizeLabel={_sizeLabel.Text} viewCell={cellText} " +
                       $"viewEcc={_eccBox.SelectedValue} viewInvert={_invertBox.IsChecked == true} " +
                       $"viewShape={_shapeCombo.SelectedValue} viewParam={_shapeParamBox.Text} " +
                       $"viewGrowthVisible={_growthBox.Visibility == Visibility.Visible} " +
                       $"viewParamVisible={_shapeParamBox.Visibility == Visibility.Visible}";
            }
        }

        private void ShowStatus(string text, bool isError)
        {
            _status.Text = text;
            _status.Foreground = isError ? Brushes.DarkRed : Brushes.Black;
        }

        /// <summary>Gray8 raster → BitmapSource, byte-for-byte.</summary>
        private static BitmapSource ToBitmapSource(QrPicture pic) =>
            BitmapSource.Create(
                pic.WidthPixels, pic.HeightPixels, 96, 96,
                PixelFormats.Gray8, null, pic.Pixels, pic.WidthPixels);
    }
}
