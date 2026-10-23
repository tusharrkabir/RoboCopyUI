using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RoboCopyUI
{
    public static class WindowBlurHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public static void EnableBlur(Window window, bool isDarkTheme)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
                if (hwnd == IntPtr.Zero) return;

                int darkMode = isDarkTheme ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Blur enable failed: {ex.Message}");
            }
        }
    }
}
