using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RoboCopyUI
{
    public static class ShellRegistry
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        private static readonly string AppPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName 
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RoboCopyUI.exe");

        public static string GetIconPath()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string directIco = Path.Combine(baseDir, "app_icon.ico");
                if (File.Exists(directIco))
                {
                    return directIco;
                }

                string assetsIco = Path.Combine(baseDir, "Assets", "app_icon.ico");
                if (File.Exists(assetsIco))
                {
                    return assetsIco;
                }
            }
            catch { }

            return $"{AppPath},0";
        }

        public static void Register()
        {
            string iconPath = GetIconPath();

            try
            {
                // Prevent context menu item disappearance when >15 items are selected in Explorer
                using (var expKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer", writable: true))
                {
                    expKey?.SetValue("MultipleInvokePromptMinimum", 65535, RegistryValueKind.DWord);
                }

                try
                {
                    using (var expHklmKey = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer", writable: true))
                    {
                        expHklmKey?.SetValue("MultipleInvokePromptMinimum", 65535, RegistryValueKind.DWord);
                    }
                }
                catch { }

                // Register entries in both CurrentUser (HKCU) and LocalMachine (HKLM if elevated)
                RegisterAllEntries(Registry.CurrentUser, iconPath);
                try
                {
                    RegisterAllEntries(Registry.LocalMachine, iconPath);
                }
                catch { }

                // Notify Windows Explorer to refresh its icon cache and shell associations immediately
                NotifyShell();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Shell registry error: {ex.Message}");
            }
        }

        public static void Unregister()
        {
            try
            {
                UnregisterAllEntries(Registry.CurrentUser);
                try
                {
                    UnregisterAllEntries(Registry.LocalMachine);
                }
                catch { }

                NotifyShell();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Shell unregistry error: {ex.Message}");
            }
        }

        public static void NotifyShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private static void RegisterAllEntries(RegistryKey rootKey, string iconPath)
        {
            // 1. Files (*) - RoboCopy, RoboCut, Robocopy Delete
            RegisterKey(rootKey, @"Software\Classes\*\shell\RoboCopy", "RoboCopy", $"\"{AppPath}\" --copy \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\*\shell\RoboCut", "RoboCut", $"\"{AppPath}\" --cut \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\*\shell\RoboCopyDelete", "Robocopy Delete", $"\"{AppPath}\" --delete \"%1\"", iconPath);

            // 2. Directories (Folders) - RoboCopy, RoboCut, RoboPaste, Robocopy Delete
            RegisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCopy", "RoboCopy", $"\"{AppPath}\" --copy \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCut", "RoboCut", $"\"{AppPath}\" --cut \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Directory\shell\RoboPaste", "RoboPaste", $"\"{AppPath}\" --paste \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCopyDelete", "Robocopy Delete", $"\"{AppPath}\" --delete \"%1\"", iconPath);

            // 3. Directory Background (Right-click inside folder) - RoboPaste & Settings
            RegisterKey(rootKey, @"Software\Classes\Directory\Background\shell\RoboPaste", "RoboPaste", $"\"{AppPath}\" --paste \"%V\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Directory\Background\shell\RoboCopySettings", "RoboCopy Settings", $"\"{AppPath}\" --settings", iconPath);

            // 4. Drives - RoboCopy, RoboCut, RoboPaste
            RegisterKey(rootKey, @"Software\Classes\Drive\shell\RoboCopy", "RoboCopy", $"\"{AppPath}\" --copy \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Drive\shell\RoboCut", "RoboCut", $"\"{AppPath}\" --cut \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Drive\shell\RoboPaste", "RoboPaste", $"\"{AppPath}\" --paste \"%1\"", iconPath);

            // 5. Drive Background (Right-click inside root drive) - RoboPaste & Settings
            RegisterKey(rootKey, @"Software\Classes\Drive\Background\shell\RoboPaste", "RoboPaste", $"\"{AppPath}\" --paste \"%V\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\Drive\Background\shell\RoboCopySettings", "RoboCopy Settings", $"\"{AppPath}\" --settings", iconPath);

            // 6. AllFilesystemObjects (Mixed selection of files AND folders together) - RoboCopy, RoboCut, Robocopy Delete
            RegisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCopy", "RoboCopy", $"\"{AppPath}\" --copy \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCut", "RoboCut", $"\"{AppPath}\" --cut \"%1\"", iconPath);
            RegisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCopyDelete", "Robocopy Delete", $"\"{AppPath}\" --delete \"%1\"", iconPath);
        }

        private static void UnregisterAllEntries(RegistryKey rootKey)
        {
            UnregisterKey(rootKey, @"Software\Classes\*\shell\RoboCopy");
            UnregisterKey(rootKey, @"Software\Classes\*\shell\RoboCut");
            UnregisterKey(rootKey, @"Software\Classes\*\shell\RoboCopyDelete");
            UnregisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCopy");
            UnregisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCut");
            UnregisterKey(rootKey, @"Software\Classes\Directory\shell\RoboPaste");
            UnregisterKey(rootKey, @"Software\Classes\Directory\shell\RoboCopyDelete");
            UnregisterKey(rootKey, @"Software\Classes\Directory\Background\shell\RoboPaste");
            UnregisterKey(rootKey, @"Software\Classes\Directory\Background\shell\RoboCopySettings");
            UnregisterKey(rootKey, @"Software\Classes\Drive\shell\RoboCopy");
            UnregisterKey(rootKey, @"Software\Classes\Drive\shell\RoboCut");
            UnregisterKey(rootKey, @"Software\Classes\Drive\shell\RoboPaste");
            UnregisterKey(rootKey, @"Software\Classes\Drive\Background\shell\RoboPaste");
            UnregisterKey(rootKey, @"Software\Classes\Drive\Background\shell\RoboCopySettings");
            UnregisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCopy");
            UnregisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCut");
            UnregisterKey(rootKey, @"Software\Classes\AllFilesystemObjects\shell\RoboCopyDelete");
        }

        private static void RegisterKey(RegistryKey rootKey, string subKeyPath, string menuText, string commandLine, string iconPath)
        {
            try
            {
                using (var key = rootKey.CreateSubKey(subKeyPath))
                {
                    if (key != null)
                    {
                        key.SetValue("", menuText);
                        key.SetValue("Icon", iconPath);
                        key.SetValue("MultiSelectModel", "Document");

                        using (var cmdKey = key.CreateSubKey("command"))
                        {
                            cmdKey?.SetValue("", commandLine);
                        }
                    }
                }
            }
            catch { }
        }

        private static void UnregisterKey(RegistryKey rootKey, string subKeyPath)
        {
            try
            {
                rootKey.DeleteSubKeyTree(subKeyPath, false);
            }
            catch { }
        }
    }
}
