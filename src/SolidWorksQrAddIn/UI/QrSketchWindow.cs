using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Core.Registration;

namespace SolidWorksQrAddIn.UI
{
    /// <summary>
    /// The one modeless "Create QR Sketch" window — a thin WPF view over
    /// <see cref="QrSketchSession"/>. All content→picture→create behaviour
    /// lives in the session (headlessly tested); this class only:
    ///
    ///   - pushes the three field texts verbatim into the session
    ///     (<see cref="QrSketchSession.SetContent"/>, <see cref="QrSketchSession.SetSizeText"/>,
    ///     <see cref="QrSketchSession.SetGrowthText"/>) — DEBOUNCED, so a
    ///     burst of keystrokes coalesces into one regeneration instead of
    ///     regenerating a dense QR synchronously per keystroke,
    ///   - blits <see cref="QrSketchSession.Picture"/> (a Gray8 raster) into an
    ///     Image via BitmapSource.Create — the same grown contour result that
    ///     sketch insertion consumes,
    ///   - calls <see cref="QrSketchSession.CreateSketch"/> with a freshly
    ///     created host adapter (fresh host state every press — no cached
    ///     support) — only AFTER flushing every pending field edit, so a
    ///     create can never run on a stale, not-yet-regenerated request,
    ///   - shows <see cref="QrSketchSession.StatusText"/>,
    ///   - re-pulls everything on <see cref="SyncFromSession"/> — the session
    ///     calls it whenever state changed without the window driving (a
    ///     probe edit, a create outcome), so the shown fields and picture
    ///     can never diverge from the session.
    ///
    /// Owned by the SolidWorks main window via WindowInteropHelper so it
    /// floats with the application; shown modeless so the engineer keeps
    /// interacting with SolidWorks (select supports, edit the open sketch).
    /// </summary>
    public sealed class QrSketchWindow : Window, IQrSketchWindow
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
        private readonly TextBox _growthBox;
        private readonly Image _picture;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _editDebounce;
        private bool _isOpen;
        private bool _contentDirty;
        private bool _sizeDirty;
        private bool _growthDirty;
        private bool _suppressFieldEvents;

        /// <param name="session">Session state the window renders and drives.</param>
        /// <param name="hostProvider">Builds a fresh host adapter per Create — never cached.</param>
        /// <param name="ownerHandle">SolidWorks main-window handle (may be IntPtr.Zero).</param>
        public QrSketchWindow(QrSketchSession session, Func<ISketchHost> hostProvider, IntPtr ownerHandle)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _hostProvider = hostProvider ?? throw new ArgumentNullException(nameof(hostProvider));

            Title = AddinIdentity.Title;
            Width = 320;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = false;

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

            // Spec window budget: content, size, growth, picture, Create.
            panel.Children.Add(new TextBlock
            {
                Text = "Final grown QR outline size (mm) — the grown QR outline " +
                       "width, excluding the clear margin:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 4),
            });
            _sizeBox = new TextBox
            {
                ToolTip = "The finished QR outline width in millimetres after outline " +
                          "growth — the 4-cell white margin around it is not included.",
            };
            panel.Children.Add(_sizeBox);

            panel.Children.Add(new TextBlock
            {
                Text = "Outline growth (% of cell width per side):",
                Margin = new Thickness(0, 8, 0, 4),
            });
            _growthBox = new TextBox
            {
                ToolTip = "How far each dark cell grows past its own outline on every " +
                          "side, as a percentage of one cell's width. Resolves " +
                          "corner-only contacts between diagonal cells.",
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

            _status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(_status);
            Content = panel;

            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(this).Owner = ownerHandle;

            _editDebounce = new DispatcherTimer { Interval = EditDebounce };
            _editDebounce.Tick += (_, __) => FlushPendingEdits();
            _contentBox.TextChanged += (_, __) => MarkEdited(ref _contentDirty);
            _sizeBox.TextChanged += (_, __) => MarkEdited(ref _sizeDirty);
            _growthBox.TextChanged += (_, __) => MarkEdited(ref _growthDirty);
            Closed += (_, __) =>
            {
                _isOpen = false;
                // Keep the session truthful: an edit typed just before the
                // close still lands — the next window opens on the real inputs.
                FlushPendingEdits();
            };

            // A recreated window picks up the session's existing inputs.
            // Suppressed: the session already holds these exact values —
            // assigning them is a pull, not an edit to push back.
            _suppressFieldEvents = true;
            try
            {
                _contentBox.Text = _session.Content;
                _sizeBox.Text = _session.SizeText;
                _growthBox.Text = _session.GrowthText;
            }
            finally { _suppressFieldEvents = false; }
            RefreshView();
        }

        // ---------------- IQrSketchWindow ----------------
        // Callers arrive on arbitrary threads (menu callback, COM probe), so
        // every member marshals to the window's own dispatcher thread.

        public bool IsOpen => _isOpen;

        public void ShowModeless()
        {
            MarshalToUi(() =>
            {
                Show();          // modeless — SolidWorks stays interactive
                _isOpen = true;
                Activate();
            });
        }

        public void FocusWindow()
        {
            MarshalToUi(() =>
            {
                if (!_isOpen)
                    throw new InvalidOperationException("A closed QR window cannot be focused.");
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
            });
        }

        /// <summary>Interface close — marshalled; Window.Close stays public for WPF itself.</summary>
        void IQrSketchWindow.Close() => MarshalToUi(base.Close);

        /// <summary>
        /// The session changed without this window driving it (a probe edit,
        /// a create outcome): re-pull the field texts, picture and status so
        /// the shown view can never diverge from the session. A field with a
        /// pending debounced edit keeps the engineer's in-progress text —
        /// its queued flush will push the newer text back into the session.
        /// </summary>
        public void SyncFromSession() => MarshalToUi(() =>
        {
            _suppressFieldEvents = true;
            try
            {
                if (!_contentDirty && _contentBox.Text != _session.Content)
                    _contentBox.Text = _session.Content;
                if (!_sizeDirty && _sizeBox.Text != _session.SizeText)
                    _sizeBox.Text = _session.SizeText;
                if (!_growthDirty && _growthBox.Text != _session.GrowthText)
                    _growthBox.Text = _session.GrowthText;
            }
            finally { _suppressFieldEvents = false; }
            RefreshView();
        });

        private void MarshalToUi(Action action)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.Invoke(action);
        }

        // ---------------- view internals ----------------

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
        /// revalidated yet.
        /// </summary>
        private void FlushPendingEdits()
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
                _session.SetSizeText(_sizeBox.Text);
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
            _picture.Source = _session.Picture == null ? null : ToBitmapSource(_session.Picture);
            ShowStatus(_session.StatusText, isError: !_session.HasValidRequest);
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
