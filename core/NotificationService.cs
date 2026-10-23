using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RoboCopyUI
{
    public static class NotificationService
    {
        public static void ShowToast(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                var notifyIcon = new NotifyIcon
                {
                    Icon = SystemIcons.Information,
                    Visible = true,
                    BalloonTipTitle = title,
                    BalloonTipText = message,
                    BalloonTipIcon = icon
                };

                notifyIcon.ShowBalloonTip(3000);

                // Auto-dispose after display
                var timer = new System.Threading.Timer(_ =>
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                }, null, 4000, System.Threading.Timeout.Infinite);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toast notification failed: {ex.Message}");
            }
        }

        public static void ShowCopiedToast(string sourcePath, bool isCut)
        {
            ShowCopiedToast(new[] { sourcePath }, isCut);
        }

        public static void ShowCopiedToast(IEnumerable<string> sourcePaths, bool isCut)
        {
            var list = sourcePaths.ToList();
            string action = isCut ? "Cut" : "Copied";
            string text;

            if (list.Count == 1)
            {
                string name = System.IO.Path.GetFileName(list[0]);
                if (string.IsNullOrEmpty(name)) name = list[0];
                text = $"{action}: {name}\nReady to RoboPaste into destination folder.";
            }
            else
            {
                text = $"{action}: {list.Count} items selected\nReady to RoboPaste into destination folder.";
            }

            ShowToast("RoboCopy UI", text, ToolTipIcon.Info);
        }

        public static void ShowCompletionToast(string totalSizeDisplay, string averageSpeedDisplay, string timeDisplay)
        {
            ShowToast("Transfer Complete!", $"Transferred {totalSizeDisplay} in {timeDisplay} ({averageSpeedDisplay})", ToolTipIcon.Info);
        }
    }
}
