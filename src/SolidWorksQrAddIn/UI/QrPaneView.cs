using System;
using System.Linq;
using QRCoder;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
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
    ///   - pushes the three field texts verbatim into the session
    ///     (<see cref="QrSketchSession.SetContent"/>, <see cref="QrSketchSession.SetSizeText"/>,
    ///     <see cref="QrSketchSession.SetGrowthText"/>) — DEBOUNCED, so a
    ///     burst of keystrokes coalesces into one regeneration instead of
    ///     regenerating a dense QR synchronously per keystroke — and the
    ///     error-correction selector straight into
    ///     <see cref="QrSketchSession.SetEccLevel"/>, a discrete pick that
    ///     needs no debounce,
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
        private readonly TextBox _contentBox;
        private readonly TextBox _sizeBox;
        private readonly TextBlock _sizeLabel;
        private HostLengthUnit _sizeInputUnit;
        private readonly TextBox _growthBox;
        private readonly ComboBox _eccBox;
        private readonly Image _picture;
        private readonly Border _infoSection;
        private readonly Grid _infoGrid;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _editDebounce;
        private bool _contentDirty;
        private bool _sizeDirty;
        private bool _growthDirty;
        private bool _suppressFieldEvents;

        /// <param name="session">Session state the view renders and drives.</param>
        /// <param name="hostProvider">Builds a fresh host adapter per Create — never cached.</param>
        public QrPaneView(QrSketchSession session, Func<ISketchHost> hostProvider)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _hostProvider = hostProvider ?? throw new ArgumentNullException(nameof(hostProvider));

            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock
            {
                Text = "Text or URL (encoded exactly as entered):",
                Margin = new Thickness(0, 0, 0, 4),
            });
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

            panel.Children.Add(new TextBlock
            {
                Text = "Error correction:",
                Margin = new Thickness(0, 8, 0, 4),
            });
            _eccBox = new ComboBox
            {
                ItemsSource = new[] { "L", "M", "Q", "H" },
                ToolTip = "QR error-correction level. A higher level survives more " +
                          "damage but packs more cells into the same size — " +
                          "H is densest, L lightest. M is the default.",
            };
            panel.Children.Add(_eccBox);

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
            // A dropdown pick is discrete — there is nothing to debounce, so
            // the level lands in the session immediately.
            _eccBox.SelectionChanged += (_, __) =>
            {
                if (_suppressFieldEvents || _eccBox.SelectedItem == null)
                    return;
                _session.SetEccLevel(
                    (QRCodeGenerator.ECCLevel)Enum.Parse(
                        typeof(QRCodeGenerator.ECCLevel),
                        (string)_eccBox.SelectedItem));
            };

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
                _contentBox.Text = _session.Content;
                _sizeBox.Text = _session.SizeText;
                _growthBox.Text = _session.GrowthText;
                _eccBox.SelectedItem = _session.EccLevel.ToString();
            }
            finally { _suppressFieldEvents = false; }
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
                if (!_contentDirty && _contentBox.Text != _session.Content)
                    _contentBox.Text = _session.Content;
                if (!_sizeDirty && _sizeBox.Text != _session.SizeText)
                    _sizeBox.Text = _session.SizeText;
                if (!_growthDirty && _growthBox.Text != _session.GrowthText)
                    _growthBox.Text = _session.GrowthText;
                if (!Equals(_eccBox.SelectedItem, _session.EccLevel.ToString()))
                    _eccBox.SelectedItem = _session.EccLevel.ToString();
            }
            finally { _suppressFieldEvents = false; }
            RefreshView();
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
            if (_contentDirty)
            {
                _contentDirty = false;
                _session.SetContent(_contentBox.Text);
            }
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
            _picture.Source = _session.Picture == null ? null : ToBitmapSource(_session.Picture);
            RebuildInfoRows();
            ShowStatus(_session.StatusText, isError: !_session.HasValidRequest);
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
                return $"viewSize={_sizeBox.Text} viewSizeLabel={_sizeLabel.Text} " +
                       $"viewCell={cellText} viewEcc={_eccBox.SelectedItem}";
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
