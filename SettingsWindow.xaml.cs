using System;
using System.Windows;
using System.Windows.Input;

namespace RoboCopyUI
{
    public partial class SettingsWindow : Window
    {
        private readonly UserSettings _settings = new();
        private readonly string _originalLanguage=L.Current.Language;
        private readonly string _originalTheme=ThemeManager.CurrentTheme;
        private bool _saved,_saving;

        public SettingsWindow()
        {
            InitializeComponent();

            Loaded += (s, e) => ThemeManager.ApplyThemeToWindow(this);

            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
                return;

            _settings = UserSettings.Load();
            _originalLanguage=_settings.Language;
            _originalTheme=_settings.Theme;
            Closed+=(_,_)=>{L.Changed-=RefreshLabels;if(!_saved){L.SetLanguage(_originalLanguage);ThemeManager.ApplyTheme(_originalTheme);}};
            LanguageBox.ItemsSource=L.Current.Languages;
            LanguageBox.SelectedValue=_settings.Language;
            LoadSettingsToUI();
            L.Changed+=RefreshLabels;
        }

        public void SynchronizeLogonStart(bool enabled)
        {
            _settings.StartWithWindows=enabled;LogonStartCheck.IsChecked=enabled;
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
            LogonStartCheck.IsChecked=_settings.StartWithWindows;
            ShortcutsCheck.IsChecked=_settings.ShortcutsEnabled;
            LogonStartCheck.IsEnabled=DesktopSettings.Installed;
            RefreshLabels();
        }
        private void RefreshLabels()
        {
            ShortcutSummary.Text=string.Join("   ",_settings.Shortcuts.Entries.Select(entry=>entry.Chord?.Display??L.T("停用")));
            DesktopIntegrationStatus.Text=DesktopSettings.LastError ?? (!DesktopSettings.Installed?L.T("使用完整安装版后可设置桌面集成。"):
                L.T("关闭设置后仍可从托盘重新打开。"));
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

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ThreadsBox.Text, out var threads) || threads is < 1 or > 128)
            { AppDialog.Show(this, L.T("线程数须为 1–128 的整数。")); ThreadsBox.Focus(); return; }
            if (!int.TryParse(RetryBox.Text, out var retries) || retries is < 0 or > 100)
            { AppDialog.Show(this, L.T("失败后重试次数须为 0–100 的整数。")); RetryBox.Focus(); return; }
            if (!int.TryParse(WaitBox.Text, out var waitSecs) || waitSecs is < 0 or > 3600)
            { AppDialog.Show(this, L.T("等待时间须为 0–3600 的整数秒。")); WaitBox.Focus(); return; }
            _settings.Theme = LightThemeRadio.IsChecked == true
                ? "Light"
                : (MidnightThemeRadio?.IsChecked == true ? "Midnight" : "Dark");
            _settings.AutoSelectThreads = AutoThreadsCheck.IsChecked == true;

            _settings.ThreadCount = threads;

            _settings.UnbufferedIO = UnbufferedCheck.IsChecked == true;
            _settings.PreserveTimestamps = PreserveTimestampsCheck.IsChecked == true;

            _settings.RetryCount = retries;

            _settings.WaitSeconds = waitSecs;

            _settings.StartWithWindows=LogonStartCheck.IsChecked==true;
            _settings.ShortcutsEnabled=ShortcutsCheck.IsChecked==true;
            UserSettings? previous=null;
            _saving=true;
            var button=sender as System.Windows.Controls.Button;if(button!=null)button.IsEnabled=false;
            try { previous=UserSettings.Load();await Task.Run(()=>DesktopSettings.Apply(_settings)); _settings.Save(); }
            catch (Exception ex) { if(previous!=null)await Task.Run(()=>DesktopSettings.Reconcile(previous));AppDialog.Show(this, L.F("设置未保存：{0}", ex.Message)); return; }
            finally{_saving=false;if(button!=null)button.IsEnabled=true;}
            ThemeManager.ApplyTheme(_settings.Theme);

            _saved=true;Close();
        }

        private void EditShortcuts_Click(object sender,RoutedEventArgs e)
        {
            var dialog=new ShortcutSettingsWindow(_settings.Shortcuts){Owner=this};
            if(dialog.ShowDialog()==true){_settings.Shortcuts=dialog.Preferences;ShortcutSummary.Text=string.Join("   ",_settings.Shortcuts.Entries.Select(entry=>entry.Chord?.Display??L.T("停用")));}
        }

        private void ResetTransferDefaults_Click(object sender, RoutedEventArgs e)
        {
            RetryBox.Text = "3";
            WaitBox.Text = "5";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            L.SetLanguage(_originalLanguage);
            // Revert theme preview if cancelled
            ThemeManager.ApplyTheme(_settings.Theme);
            Close();
        }
        private void LanguageBox_SelectionChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if(LanguageBox?.SelectedValue is not string code)return;
            _settings.Language=code;L.SetLanguage(code);
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if(_saving)e.Cancel=true;
            base.OnClosing(e);
        }
    }
}
