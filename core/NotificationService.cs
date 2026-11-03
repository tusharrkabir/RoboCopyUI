using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RoboCopyUI
{
    public static class NotificationService
    {
        public static bool Suppress { get; set; }
        public static Action<string,string,ToolTipIcon>? DesktopToast { get; set; }
        private static System.Windows.Media.MediaPlayer? completionPlayer;

        public static void PlayCompletionSound()
        {
            if(Suppress)return;
            var dispatcher=System.Windows.Application.Current?.Dispatcher;
            if(dispatcher==null||dispatcher.HasShutdownStarted)return;
            if(!dispatcher.CheckAccess()){_=dispatcher.BeginInvoke((Action)PlayCompletionSound);return;}
            try
            {
                completionPlayer?.Close();
                var player=new System.Windows.Media.MediaPlayer();completionPlayer=player;
                void Release(){player.Close();if(ReferenceEquals(completionPlayer,player))completionPlayer=null;}
                player.MediaEnded+=(_,_)=>Release();
                player.MediaFailed+=(_,e)=>{System.Diagnostics.Debug.WriteLine(e.ErrorException);Release();};
                player.Volume=1;
                player.Open(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","ringtone-sms-notification.mp3")));
                player.Play();
            }
            catch(Exception error)
            {
                completionPlayer?.Close();completionPlayer=null;
                System.Diagnostics.Debug.WriteLine($"Completion sound unavailable: {error.Message}");
            }
        }
        public static void ShowToast(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if(Suppress)return;
            if(DesktopToast!=null){DesktopToast(title,message,icon);return;}
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
