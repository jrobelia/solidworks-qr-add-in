using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    ///     regenerating a dense QR synchronously per keystroke,
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
        private readonly Image _picture;
        private readonly Border _infoSection;
        private readonly Grid _infoGrid;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _editDebounce;
        private bool _contentDirty;
        private bool _sizeDirty;
        private bool _growthDirty;
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

            panel.Children.Add(new TextBlock
            {
                Text = "Cell growth per side (% of cell width):",
                Margin = new Thickness(0, 8, 0, 4),
            });
            _growthBox = new TextBox
            {
                ToolTip = "Widens each dark cell on all sides by this percentage of " +
                          "the cell spacing, helping diagonally touching cells connect. " +
                          "The QR code size stays fixed.",
            };
            panel.Children.Add(_growthBox);

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
            Content = panel;

            _editDebounce = new DispatcherTimer { Interval = EditDebounce };
            _editDebounce.Tick += (_, __) => FlushPendingEdits();
            _contentBox.TextChanged += (_, __) => MarkEdited(ref _contentDirty);
            _sizeBox.TextChanged += (_, __) => MarkEdited(ref _sizeDirty);
            _growthBox.TextChanged += (_, __) => MarkEdited(ref _growthDirty);
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
                return $"viewSize={_sizeBox.Text} viewSizeLabel={_sizeLabel.Text} viewCell={cellText}";
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
