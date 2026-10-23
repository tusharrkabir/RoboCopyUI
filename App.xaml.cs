using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace RoboCopyUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static string CleanPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            // Strip outer quotes or spaces that get added by command-line parsing or escaping
            string cleaned = path.Trim('"', ' ', '\t');

            // If path is a drive letter without backslash (e.g. "E:" or "C:"), append trailing backslash
            if (cleaned.Length == 2 && cleaned[1] == ':')
            {
                cleaned += "\\";
            }

            try
            {
                return Path.GetFullPath(cleaned);
            }
            catch
            {
                return cleaned;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                System.Diagnostics.Debug.WriteLine($"[AppDomain Unhandled Exception] {ex?.Message}");
                try { NotificationService.ShowToast("RoboCopy UI Error", $"Fatal error: {ex?.Message}", System.Windows.Forms.ToolTipIcon.Error); } catch { }
            };

            DispatcherUnhandledException += (s, args) =>
            {
                System.Diagnostics.Debug.WriteLine($"[Dispatcher Unhandled Exception] {args.Exception.Message}");
                try { NotificationService.ShowToast("RoboCopy UI Error", $"UI Error: {args.Exception.Message}", System.Windows.Forms.ToolTipIcon.Error); } catch { }
                args.Handled = true;
            };

            var settings = UserSettings.Load();
            ThemeManager.ApplyTheme(settings.Theme);

            string[] args = e.Args;

            if (args.Length == 0)
            {
                // Default F5 test mode: launch main copy UI with test paths
                var copyWindow = new MainWindow(@"C:\Program Files\Adobe", @"d:\TestDest");
                copyWindow.Show();
                return;
            }

            string flag = args[0].ToLowerInvariant();

            switch (flag)
            {
                case "--copy":
                    if (args.Length >= 2)
                    {
                        var items = args.Skip(1)
                            .Select(CleanPath)
                            .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                            .ToList();
                        if (items.Count > 0)
                        {
                            ClipboardManager.SetCopy(items);
                        }
                    }
                    Shutdown();
                    break;

                case "--cut":
                    if (args.Length >= 2)
                    {
                        var items = args.Skip(1)
                            .Select(CleanPath)
                            .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                            .ToList();
                        if (items.Count > 0)
                        {
                            ClipboardManager.SetCut(items);
                        }
                    }
                    Shutdown();
                    break;

                case "--delete":
                    if (args.Length >= 2)
                    {
                        var rawItems = args.Skip(1).Select(CleanPath).Where(p => !string.IsNullOrEmpty(p));
                        var (isMaster, allPaths) = DeleteSessionManager.RegisterAndAccumulate(rawItems);
                        if (!isMaster)
                        {
                            Shutdown();
                            return;
                        }

                        if (allPaths != null && allPaths.Count > 0)
                        {
                            var deleteWindow = new MainWindow(allPaths, isDelete: true);
                            deleteWindow.Show();
                            return;
                        }
                    }
                    Shutdown();
                    break;

                case "--paste":
                    string rawDest = args.Length >= 2 ? args[1] : string.Empty;
                    string destDir = CleanPath(rawDest);
                    var clip = ClipboardManager.GetClipboard();

                    if (clip != null && clip.SourcePaths != null && clip.SourcePaths.Count > 0)
                    {
                        var validPaths = clip.SourcePaths
                            .Select(CleanPath)
                            .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                            .ToList();
                        if (validPaths.Count == 0)
                        {
                            NotificationService.ShowToast("RoboCopy UI", "Source files/folders no longer exist.", System.Windows.Forms.ToolTipIcon.Error);
                            Shutdown();
                            return;
                        }

                        if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
                        {
                            NotificationService.ShowToast("RoboCopy UI", "Invalid destination folder.", System.Windows.Forms.ToolTipIcon.Error);
                            Shutdown();
                            return;
                        }

                        // IMMEDIATELY CLEAR CLIPBOARD TO ENSURE CLEAN STATE AS REQUESTED
                        ClipboardManager.Clear();

                        var options = new RobocopyOptions
                        {
                            ThreadCount = settings.AutoSelectThreads ? 8 : settings.ThreadCount,
                            AutoSelectThreadCount = settings.AutoSelectThreads,
                            UnbufferedIO = settings.UnbufferedIO,
                            PreserveTimestamps = settings.PreserveTimestamps,
                            RetryCount = settings.RetryCount,
                            WaitSeconds = settings.WaitSeconds,
                            Mirror = clip.IsCutMode
                        };

                        var copyWindow = new MainWindow(validPaths, destDir, clip.IsCutMode);
                        copyWindow.Show();
                    }
                    else
                    {
                        NotificationService.ShowToast("RoboCopy UI", "Clipboard is empty. Use 'RoboCopy' or 'RoboCut' first.", System.Windows.Forms.ToolTipIcon.Warning);
                        Shutdown();
                    }
                    break;

                case "--settings":
                case "/settings":
                    var settingsWindow = new SettingsWindow();
                    settingsWindow.Show();
                    break;

                case "--register-shell":
                    ShellRegistry.Register();
                    NotificationService.ShowToast("RoboCopy UI", "Explorer context menu entries registered successfully!", System.Windows.Forms.ToolTipIcon.Info);
                    Shutdown();
                    break;

                case "--unregister-shell":
                    ShellRegistry.Unregister();
                    NotificationService.ShowToast("RoboCopy UI", "Explorer context menu entries removed.", System.Windows.Forms.ToolTipIcon.Info);
                    Shutdown();
                    break;

                case "--uninstall":
                case "/uninstall":
                    ShellRegistry.Unregister();

                    try
                    {
                        using var unkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                        unkey?.DeleteSubKeyTree("RoboCopyUI", throwOnMissingSubKey: false);
                    }
                    catch { }

                    try
                    {
                        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RoboCopy Settings.lnk");
                        if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                        string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RoboCopy Settings.lnk");
                        if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);
                    }
                    catch { }

                    NotificationService.ShowToast("RoboCopy UI", "RoboCopyUI has been successfully uninstalled.", System.Windows.Forms.ToolTipIcon.Info);

                    try
                    {
                        string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c timeout /t 2 /nobreak & rmdir /s /q \"{appDir}\"",
                            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { }

                    Shutdown();
                    break;

                default:
                    if (args.Length >= 2)
                    {
                        string source = CleanPath(args[0]);
                        string destination = CleanPath(args[1]);
                        var copyWindow = new MainWindow(source, destination);
                        copyWindow.Show();
                    }
                    else
                    {
                        var copyWindow = new MainWindow(@"C:\Program Files\Adobe", @"d:\TestDest");
                        copyWindow.Show();
                    }
                    break;
            }
        }
    }
}
