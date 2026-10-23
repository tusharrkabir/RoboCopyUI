using System;
using System.Windows;
using System.Windows.Input;

namespace RoboCopyUI
{
    public partial class SettingsWindow : Window
    {
        private readonly UserSettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();

            Loaded += (s, e) => ThemeManager.ApplyThemeToWindow(this);

            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
                return;

            _settings = UserSettings.Load();
            LoadSettingsToUI();
        }

        private void LoadSettingsToUI()
        {
            if (_settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
                LightThemeRadio.IsChecked = true;
            else if (_settings.Theme.Equals("Midnight", StringComparison.OrdinalIgnoreCase))
                MidnightThemeRadio.IsChecked = true;
            else
                DarkThemeRadio.IsChecked = true;

            AutoThreadsCheck.IsChecked = _settings.AutoSelectThreads;
            ThreadsBox.Text = _settings.ThreadCount.ToString();
            ThreadsBox.IsEnabled = !_settings.AutoSelectThreads;

            UnbufferedCheck.IsChecked = _settings.UnbufferedIO;
            PreserveTimestampsCheck.IsChecked = _settings.PreserveTimestamps;

            RetryBox.Text = _settings.RetryCount.ToString();
            WaitBox.Text = _settings.WaitSeconds.ToString();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Theme_Changed(object sender, RoutedEventArgs e)
        {
            if (LightThemeRadio.IsChecked == true)
                ThemeManager.ApplyTheme("Light");
            else if (MidnightThemeRadio?.IsChecked == true)
                ThemeManager.ApplyTheme("Midnight");
            else
                ThemeManager.ApplyTheme("Dark");
        }

        private void AutoThreadsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (ThreadsBox != null)
                ThreadsBox.IsEnabled = AutoThreadsCheck.IsChecked != true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.Theme = LightThemeRadio.IsChecked == true
                ? "Light"
                : (MidnightThemeRadio?.IsChecked == true ? "Midnight" : "Dark");
            _settings.AutoSelectThreads = AutoThreadsCheck.IsChecked == true;

            if (int.TryParse(ThreadsBox.Text, out int threads) && threads >= 1 && threads <= 128)
                _settings.ThreadCount = threads;

            _settings.UnbufferedIO = UnbufferedCheck.IsChecked == true;
            _settings.PreserveTimestamps = PreserveTimestampsCheck.IsChecked == true;

            if (int.TryParse(RetryBox.Text, out int retries) && retries >= 0)
                _settings.RetryCount = retries;

            if (int.TryParse(WaitBox.Text, out int waitSecs) && waitSecs >= 0)
                _settings.WaitSeconds = waitSecs;

            _settings.Save();
            ThemeManager.ApplyTheme(_settings.Theme);

            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Revert theme preview if cancelled
            ThemeManager.ApplyTheme(_settings.Theme);
            Close();
        }
    }
}
