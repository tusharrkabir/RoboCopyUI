using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace RoboCopyUI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Single source of truth for what the UI should look like right now.
        // Every button just changes this value and calls UpdateUI() — no
        // scattered if/else per control. Driven by RobocopyEngine's
        // StatusChanged event, not set directly by buttons — buttons only
        // ask the engine to do something; the engine's own event is what
        // confirms the state actually changed.
        private CopyState _state = CopyState.Copying;

        // Set true the instant the Popup auto-closes (StaysOpen="False") so the
        // same mouse click that closed it doesn't immediately reopen it.
        private bool _popupJustClosed = false;

        // The real copy engine and how many rows are currently in the
        // Skipped popup.
        private readonly RobocopyEngine _engine = new RobocopyEngine();
        private int _skippedCount = 0;

        // Guards every UI-touching handler below against running after the
        // window has started closing — engine events arrive on background
        // threads and can land slightly after Close() was called.
        private bool _isShuttingDown = false;

        private readonly List<string> _sourcePaths;
        private readonly string _source;
        private readonly string _destination;
        private readonly bool _isCutMode;
        private readonly bool _isDeleteMode;

        // One-shot timer that auto-closes the window a few seconds after a
        // successful completion — stopped/disposed in OnClosed so it can't
        // fire against an already-closed window.
        private DispatcherTimer? _autoCloseTimer;
        private DispatcherTimer? _scanningTimer;
        private int _scanningDotCount = 0;

        /// <summary>
        /// Design-time / quick-test convenience constructor.
        /// </summary>
        public MainWindow() : this(new List<string> { @"C:\Source" }, @"C:\Destination", false, false)
        {
        }

        public MainWindow(string source, string destination) : this(new List<string> { source }, destination, false, false)
        {
        }

        public MainWindow(List<string> deletePaths, bool isDelete) : this(deletePaths, string.Empty, false, isDelete)
        {
        }

        public MainWindow(List<string> sourcePaths, string destination, bool isCut = false, bool isDelete = false)
        {
            _sourcePaths = sourcePaths ?? new List<string>();
            _source = _sourcePaths.FirstOrDefault() ?? string.Empty;
            _destination = destination;
            _isCutMode = isCut;
            _isDeleteMode = isDelete;

            InitializeComponent();

            Loaded += (s, e) => ThemeManager.ApplyThemeToWindow(this);

            // The XAML's ProgressFill Width is a leftover design-time mockup
            // value — reset here so it doesn't flash before the first real
            // ProgressChanged tick arrives.
            ProgressFill.Width = 0;
            ProgressPercentText.Text = "0%";

            if (_isDeleteMode)
            {
                FromLabel.Text = "Target: ";
                DestGroupPanel.Visibility = Visibility.Collapsed;
                CopiedLabelRun.Text = "Deleted : ";

                string titleAction = "Robocopy Delete";
                if (_sourcePaths.Count > 1)
                {
                    JobTitleText.Text = $"{titleAction} ({_sourcePaths.Count} items)";
                    SourcePathText.Text = $"{_sourcePaths.Count} items selected";
                    SourcePathText.ToolTip = string.Join(Environment.NewLine, _sourcePaths);
                }
                else
                {
                    JobTitleText.Text = titleAction;
                    SourcePathText.Text = PathFormatter.FormatConcisePath(_source, 35);
                    SourcePathText.ToolTip = _source;
                }
            }
            else
            {
                string titleAction = isCut ? "RoboCut" : "RoboCopy";
                if (_sourcePaths.Count > 1)
                {
                    JobTitleText.Text = $"{titleAction} ({_sourcePaths.Count} items)";
                    SourcePathText.Text = $"{_sourcePaths.Count} items selected";
                    SourcePathText.ToolTip = string.Join(Environment.NewLine, _sourcePaths);
                }
                else
                {
                    JobTitleText.Text = titleAction;
                    SourcePathText.Text = PathFormatter.FormatConcisePath(_source, 26);
                    SourcePathText.ToolTip = _source;
                }

                DestPathText.Text = PathFormatter.FormatConcisePath(destination, 26);
                DestPathText.ToolTip = destination;
            }

            _engine.ProgressChanged += Engine_ProgressChanged;
            _engine.StatusChanged += Engine_StatusChanged;
            _engine.SkippedFileFound += Engine_SkippedFileFound;
            _engine.Error += Engine_Error;
            _engine.CollisionsDetected += Engine_CollisionsDetected;
            _engine.ThreadCountChanged += (s, threads) =>
            {
                Dispatcher.Invoke(() => MtLevelText.Text = $"MT: {threads}");
            };

            UpdateUI();

            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
                return;

            var userSettings = UserSettings.Load();
            if (_isDeleteMode && userSettings.AutoSelectThreads)
            {
                int deleteThreads = DriveTypeDetector.GetOptimizedDeleteThreads(_sourcePaths);
                MtLevelText.Text = $"MT: {deleteThreads}";
            }
            else
            {
                MtLevelText.Text = $"MT: {userSettings.ThreadCount}";
            }

            var options = new RobocopyOptions
            {
                ThreadCount = userSettings.ThreadCount,
                AutoSelectThreadCount = userSettings.AutoSelectThreads,
                UnbufferedIO = userSettings.UnbufferedIO,
                PreserveTimestamps = userSettings.PreserveTimestamps,
                RetryCount = userSettings.RetryCount,
                WaitSeconds = userSettings.WaitSeconds,
                Mirror = isCut
            };

            if (_isDeleteMode)
            {
                Task.Run(() => _engine.StartDelete(_sourcePaths, options)).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.Flatten().InnerException;
                        System.Diagnostics.Debug.WriteLine($"[RobocopyEngine Delete Task Error] {ex?.Message}");
                        Dispatcher.Invoke(() => NotificationService.ShowToast("Robocopy Delete Error", $"Delete failed: {ex?.Message}", System.Windows.Forms.ToolTipIcon.Error));
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                // Start() blocks (pre-scan + collision-detection wait), so this
                // must not run on the UI thread.
                Task.Run(() => _engine.Start(_sourcePaths, _destination, options)).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception?.Flatten().InnerException;
                        System.Diagnostics.Debug.WriteLine($"[RobocopyEngine Task Error] {ex?.Message}");
                        Dispatcher.Invoke(() => NotificationService.ShowToast("Robocopy Error", $"Task failed: {ex?.Message}", System.Windows.Forms.ToolTipIcon.Error));
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isShuttingDown = true;
            _scanningTimer?.Stop();
            _autoCloseTimer?.Stop();
            _engine.Cancel();
            _engine.Dispose();
            base.OnClosed(e);
        }

        // ==========================================================
        // SECTION 11.1 : WINDOW DRAG
        // ==========================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        // ==========================================================
        // SECTION 11.2 : TITLE BAR BUTTONS
        // ==========================================================

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.ShowDialog();

            var userSettings = UserSettings.Load();
            MtLevelText.Text = $"MT: {userSettings.ThreadCount}";
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ==========================================================
        // SECTION 11.3 : PAUSE / RESUME
        // ==========================================================

        // Buttons no longer set _state themselves — they just ask the engine
        // to do something. The engine's StatusChanged event (below) is what
        // actually updates _state and calls UpdateUI(), once the engine
        // confirms the change really happened.
        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_state == CopyState.Copying)
                _engine.Pause();
            else if (_state == CopyState.Paused)
                _engine.Resume();
        }

        // ==========================================================
        // SECTION 11.4 : CANCEL
        // ==========================================================

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            // Once the job is done (or already stopped), this button just
            // reads "Close" — clicking it shouldn't run a real cancel.
            if (_state == CopyState.Completed || _state == CopyState.Error || _state == CopyState.Cancelled)
            {
                Close();
                return;
            }

            _engine.Cancel();
            _state = CopyState.Cancelled;
            UpdateUI();
            Close();
        }

        // Call this any time _state changes. It is the ONLY place that touches
        // button content/enabled-state/colors for the copy lifecycle.
        private void UpdateUI()
        {
            if (TaskbarProgress != null)
            {
                switch (_state)
                {
                    case CopyState.Scanning:
                        TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
                        break;
                    case CopyState.Copying:
                        TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
                        break;
                    case CopyState.Paused:
                        TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Paused;
                        break;
                    case CopyState.Error:
                        TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
                        break;
                    case CopyState.Completed:
                    case CopyState.Cancelled:
                        TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                        break;
                }
            }

            _scanningTimer?.Stop();

            switch (_state)
            {
                case CopyState.Scanning:
                    ScanningStatusText.Visibility = Visibility.Visible;
                    BtnPause.Visibility = Visibility.Visible;
                    BtnPause.IsEnabled = false;
                    BtnCancel.Content = "Cancel";
                    BtnCancel.IsEnabled = true;
                    ProgressPercentText.Text = "0%";
                    
                    if (_scanningTimer == null)
                    {
                        _scanningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    }
                    _scanningTimer.Tick -= DeletingTimer_Tick;
                    _scanningTimer.Tick -= ScanningTimer_Tick;
                    _scanningTimer.Tick += ScanningTimer_Tick;
                    _scanningTimer.Start();
                    break;

                case CopyState.Copying:
                    if (_isDeleteMode)
                    {
                        ScanningStatusText.Visibility = Visibility.Visible;
                        if (_scanningTimer == null)
                        {
                            _scanningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                        }
                        _scanningTimer.Tick -= ScanningTimer_Tick;
                        _scanningTimer.Tick -= DeletingTimer_Tick;
                        _scanningTimer.Tick += DeletingTimer_Tick;
                        _scanningTimer.Start();
                    }
                    else
                    {
                        ScanningStatusText.Visibility = Visibility.Collapsed;
                    }
                    BtnPause.Visibility = Visibility.Visible;
                    BtnPause.Content = "Pause";
                    BtnPause.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0xFF));
                    BtnPause.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0x93, 0xFF));
                    BtnPause.IsEnabled = true;
                    BtnCancel.Content = "Cancel";
                    BtnCancel.IsEnabled = true;
                    break;

                case CopyState.Paused:
                    ScanningStatusText.Visibility = Visibility.Collapsed;
                    BtnPause.Visibility = Visibility.Visible;
                    BtnPause.Content = "Resume";
                    BtnPause.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0x93, 0xFF));
                    BtnPause.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0x93, 0xFF));
                    BtnPause.IsEnabled = true;
                    BtnCancel.Content = "Cancel";
                    BtnCancel.IsEnabled = true;
                    break;

                case CopyState.Completed:
                    ScanningStatusText.Visibility = Visibility.Collapsed;
                    AnimateToCompleted();
                    break;

                case CopyState.Cancelled:
                    ScanningStatusText.Visibility = Visibility.Collapsed;
                    BtnPause.IsEnabled = false;
                    BtnCancel.Content = "Cancelled";
                    BtnCancel.IsEnabled = false;
                    break;

                case CopyState.Error:
                    ScanningStatusText.Visibility = Visibility.Collapsed;
                    BtnPause.IsEnabled = false;
                    BtnCancel.Content = "Close";
                    BtnCancel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x5C, 0x8C));
                    BtnCancel.IsEnabled = true;
                    break;
            }
        }

        // ==========================================================
        // SECTION 11.5 : ENGINE EVENT HANDLERS
        // ==========================================================
        // These fire on background threads, so every one marshals to the UI
        // thread via Dispatcher.Invoke before touching any control. Each also
        // bails out early if the window is shutting down.

        private void Engine_ProgressChanged(object sender, CopyProgress progress)
        {
            if (_isShuttingDown) return;
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown) return;
                RenderProgress(progress);
            });
        }

        private void RenderProgress(CopyProgress progress)
        {
            // Once we've moved on to Completed/Cancelled, those states own the
            // visuals — a late-arriving tick shouldn't fight with that.
            if (_state == CopyState.Completed || _state == CopyState.Cancelled)
                return;

            double trackWidth = ProgressTrack.ActualWidth > 2 ? ProgressTrack.ActualWidth - 2 : 0;
            ProgressFill.HorizontalAlignment = HorizontalAlignment.Left;
            ProgressFill.Width = trackWidth * (progress.PercentComplete / 100.0);
            ProgressPercentText.Text = $"{progress.PercentComplete}%";

            if (TaskbarProgress != null)
            {
                TaskbarProgress.ProgressValue = Math.Min(1.0, Math.Max(0.0, progress.PercentComplete / 100.0));
            }

            CopiedAmountRun.Text = progress.CopiedDisplay;
            TotalAmountRun.Text = progress.TotalDisplay;
            RemainingLineValueRun.Text = progress.RemainingBytesDisplay;

            // Simplified: size only, no file count — matches the pure
            // bytes-based telemetry model (no per-file tracking).
            RemainingCardValue.Text = progress.RemainingBytesDisplay;
            SpeedCardValue.Text = progress.SpeedDisplay;
            TimeCardValue.Text = progress.RemainingTimeDisplay;
        }

        private void Engine_StatusChanged(object sender, CopyState newState)
        {
            if (_isShuttingDown) return;
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown) return;
                _state = newState;
                UpdateUI();
            });
        }

        private void Engine_SkippedFileFound(object sender, SkippedFileEntry entry)
        {
            if (_isShuttingDown) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (_isShuttingDown) return;
                AddSkippedRow(entry);
            }));
        }

        private void Engine_Error(object sender, string message)
        {
            // No dedicated error-message UI element exists — StatusChanged(Error)
            // already drives the button state via UpdateUI(). This is here so
            // the message itself isn't silently lost.
            System.Diagnostics.Debug.WriteLine($"[RobocopyEngine error] {message}");
        }

        // ==========================================================
        // SECTION 11.5B : COLLISION HANDLING (Replace / Skip / Rename)
        // ==========================================================
        // Fired once, before the main copy starts, if any top-level source
        // items already exist at the destination. Blocks the engine's
        // background thread until ResolveCollisions() is called.

        private void Engine_CollisionsDetected(object sender, System.Collections.Generic.List<CollisionEntry> collisions)
        {
            if (_isShuttingDown) return;
            Dispatcher.Invoke(() =>
            {
                if (_isShuttingDown) return;

                CollisionMessageText.Text = collisions.Count == 1
                    ? $"\"{collisions[0].Name}\" already exists in the destination folder."
                    : $"{collisions.Count} items already exist in the destination folder.";

                CollisionOverlay.Visibility = Visibility.Visible;
            });
        }

        private void BtnCollisionReplace_Click(object sender, RoutedEventArgs e)
        {
            CollisionOverlay.Visibility = Visibility.Collapsed;
            _engine.ResolveCollisions(CollisionChoice.Replace);
        }

        private void BtnCollisionSkip_Click(object sender, RoutedEventArgs e)
        {
            CollisionOverlay.Visibility = Visibility.Collapsed;
            _engine.ResolveCollisions(CollisionChoice.Skip);
        }

        private void BtnCollisionRename_Click(object sender, RoutedEventArgs e)
        {
            CollisionOverlay.Visibility = Visibility.Collapsed;
            _engine.ResolveCollisions(CollisionChoice.Rename);
        }

        // ==========================================================
        // SECTION 11.6 : COMPLETED TRANSITION ANIMATION
        // ==========================================================

        // Each stage takes about this long — matches the "~300ms feels premium" target.
        private const int StepMs = 300;

        // Runs the whole Copying -> Completed transition as a short staged sequence:
        //   1) bar fills to 100% and turns green (300ms)
        //   2) Pause fades out, Cancel crossfades to "Close" (300ms)
        //   3) all the labels/values crossfade to their completed text (300ms)
        // Total ~900ms, each individual step ~300ms as requested.
        private void AnimateToCompleted()
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var stepDuration = TimeSpan.FromMilliseconds(StepMs);

            // --- Stage 1: bar -> 100% and blue -> green -------------------------

            double targetWidth = ProgressTrack.ActualWidth > 0
                ? ProgressTrack.ActualWidth - 2
                : ProgressFill.ActualWidth;

            var widthAnim = new DoubleAnimation(ProgressFill.ActualWidth, targetWidth, stepDuration)
            {
                EasingFunction = ease
            };

            var stop0Anim = new ColorAnimation(Color.FromRgb(0x2F, 0xB8, 0x6E), stepDuration);
            var stop1Anim = new ColorAnimation(Color.FromRgb(0x34, 0xD1, 0x7E), stepDuration);

            var percentBrush = new SolidColorBrush(((SolidColorBrush)ProgressPercentText.Foreground).Color);
            ProgressPercentText.Foreground = percentBrush;
            var percentColorAnim = new ColorAnimation(Color.FromRgb(0x34, 0xD1, 0x7E), stepDuration);

            widthAnim.Completed += (s, e) =>
            {
                ProgressFill.Width = double.NaN;
                ProgressFill.HorizontalAlignment = HorizontalAlignment.Stretch;

                AnimateButtonsOut();
            };

            ProgressFill.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            ProgressFillBrush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, stop0Anim);
            ProgressFillBrush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, stop1Anim);
            percentBrush.BeginAnimation(SolidColorBrush.ColorProperty, percentColorAnim);
            CrossFadeElement(ProgressPercentText, () => ProgressPercentText.Text = "100%");
        }

        // --- Stage 2: Pause fades away, Cancel crossfades to "Close" -----------
        private void AnimateButtonsOut()
        {
            var stepDuration = TimeSpan.FromMilliseconds(StepMs);

            var pauseFade = new DoubleAnimation(1, 0, stepDuration);
            pauseFade.Completed += (s, e) =>
            {
                BtnPause.Visibility = Visibility.Collapsed;
                BtnPause.IsEnabled = false;

                AnimateLabelsIn();
            };
            BtnPause.BeginAnimation(UIElement.OpacityProperty, pauseFade);

            CrossFadeElement(BtnCancel, () => BtnCancel.Content = "Close");
        }

        // --- Stage 3: all the labels/values crossfade to their "completed" text,
        // using the engine's real final numbers.
        private void AnimateLabelsIn()
        {
            var progress = _engine.Progress;

            CrossFadeElement(CopiedInfoText, () => CopiedAmountRun.Text = progress.CopiedDisplay);
            CrossFadeElement(JobTitleText, () => JobTitleText.Text = _isDeleteMode ? "Robocopy Delete" : (_isCutMode ? "RoboCut" : "RoboCopy"));

            CrossFadeElement(RemainingInfoText, () =>
            {
                RemainingLineLabelRun.Text = "";
                RemainingLineValueRun.Text = _isDeleteMode ? "Deleted" : "Completed";
                RemainingLineValueRun.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD1, 0x7E));
            });

            // Simplified: skip summary shows size only (no count) — consistent
            // with dropping file-count tracking everywhere else.
            // Removed RemainingCard modifications so it stays as "Remaining 0 GB"

            CrossFadeElement(SpeedCardLabel, () => SpeedCardLabel.Text = "Average Speed");
            CrossFadeElement(SpeedCardValue, () => SpeedCardValue.Text = progress.AverageSpeedDisplay);

            CrossFadeElement(TimeCardLabel, () => TimeCardLabel.Text = "Total Time");
            CrossFadeElement(TimeCardValue, () => TimeCardValue.Text = progress.ElapsedTimeDisplay);

            if (_isDeleteMode)
            {
                NotificationService.ShowToast("Robocopy Delete", $"Successfully deleted {progress.CopiedDisplay} in {progress.ElapsedTimeDisplay}.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                NotificationService.ShowCompletionToast(progress.CopiedDisplay, progress.AverageSpeedDisplay, progress.ElapsedTimeDisplay);
            }

            // If anything was skipped, surface it immediately rather than making
            // the person remember to click "Skipped" themselves. The window also
            // must NOT auto-close in this case.
            if (progress.SkippedFiles.Count > 0)
            {
                SkippedPopup.IsOpen = true;
                SkippedChevronRotate.Angle = 180;
                return;
            }

            // Nothing was skipped — auto-close a few seconds after a clean finish.
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoCloseTimer.Tick += (s, e) =>
            {
                _autoCloseTimer.Stop();
                if (!_isShuttingDown)
                    Close();
            };
            _autoCloseTimer.Start();
        }

        private void ScanningTimer_Tick(object? sender, EventArgs e)
        {
            _scanningDotCount = (_scanningDotCount % 3) + 1;
            ScanningStatusText.Text = $"scanning{new string('.', _scanningDotCount)}";
        }

        private void DeletingTimer_Tick(object? sender, EventArgs e)
        {
            _scanningDotCount = (_scanningDotCount % 3) + 1;
            ScanningStatusText.Text = $"deleting{new string('.', _scanningDotCount)}";
        }

        // Fades an element out, runs updateContent while invisible, then fades it
        // back in — a simple, reusable crossfade for any UIElement whose text or
        // content is changing.
        private void CrossFadeElement(UIElement element, Action updateContent, int totalMs = StepMs)
        {
            var half = TimeSpan.FromMilliseconds(totalMs / 2.0);

            var fadeOut = new DoubleAnimation(1, 0, half);
            fadeOut.Completed += (s, e) =>
            {
                updateContent();

                var fadeIn = new DoubleAnimation(0, 1, half);
                element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            element.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // ==========================================================
        // SECTION 11.7 : SKIPPED TABLE POPUP
        // ==========================================================

        // First click opens the popup and rotates the chevron up.
        // Second click: because StaysOpen="False", the Popup already closes itself
        // the moment you press down outside it — which for click #2 is this same
        // click, and happens *before* MouseUp fires here. We use
        // MouseLeftButtonUp (not MouseLeftButtonDown) specifically so opening
        // never races with that press-triggered dismissal.
        private void SkippedToggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_popupJustClosed)
            {
                _popupJustClosed = false;
                return;
            }

            SkippedPopup.IsOpen = true;
            SkippedChevronRotate.Angle = 180;
        }

        private void SkippedPopup_Closed(object sender, System.EventArgs e)
        {
            _popupJustClosed = true;
            SkippedChevronRotate.Angle = 0;
        }

        private void SkippedScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var scv = (System.Windows.Controls.ScrollViewer)sender;
            scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // Maps a skip reason to a display color. Deliberately kept here, not in
        // CopyProgress/SkippedFileEntry — the model just describes *what*
        // happened, and presentation (what color that gets) is the UI's call.
        private static System.Windows.Media.Color GetReasonColor(string reason)
        {
            switch (reason)
            {
                case "Access Denied":
                case "Corrupted file":
                    return System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B);

                case "Skipped by filter":
                    return System.Windows.Media.Color.FromRgb(0xFF, 0xC2, 0x4B);

                case "File In Use":
                    return System.Windows.Media.Color.FromRgb(0xB9, 0x8C, 0xFF);

                default:
                    return System.Windows.Media.Color.FromRgb(0x9B, 0x9B, 0xA8);
            }
        }

        // Appends ONE row to the Skipped popup and bumps the count — called
        // live as SkippedFileFound events arrive from the engine.
        private void AddSkippedRow(SkippedFileEntry file)
        {
            // Divider BEFORE this row, but only if a row already exists —
            // avoids a leading divider above the very first entry.
            if (SkippedRowsPanel.Children.Count > 0)
            {
                var divider = new Border { Height = 1 };
                divider.SetResourceReference(Border.BackgroundProperty, "BorderColor");
                SkippedRowsPanel.Children.Add(divider);
            }

            var row = new Grid
            {
                Margin = new Thickness(0, 8, 0, 8)
            };
            // Must match the popup header grid exactly. The previous, wider
            // row columns pushed size/reason out of alignment and beyond the
            // popup boundary.
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });

            var fileNameText = new TextBlock
            {
                Text = file.FileName,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            fileNameText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
            row.Children.Add(fileNameText);

            var pathText = new TextBlock
            {
                Text = file.OriginalPath,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            pathText.SetResourceReference(TextBlock.ForegroundProperty, "Purple");
            Grid.SetColumn(pathText, 1);
            row.Children.Add(pathText);

            var sizeText = new TextBlock
            {
                Text = file.SizeDisplay,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            sizeText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            Grid.SetColumn(sizeText, 2);
            row.Children.Add(sizeText);

            var reasonText = new TextBlock
            {
                Text = file.Reason,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(GetReasonColor(file.Reason)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(reasonText, 3);
            row.Children.Add(reasonText);

            SkippedRowsPanel.Children.Add(row);

            _skippedCount++;
            SkippedCount.Text = $" ({_skippedCount})";
        }
    }
}
