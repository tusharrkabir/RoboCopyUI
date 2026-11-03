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
        private void StartElevatedTransfer(string[] args)
        {
            NotificationService.Suppress=true;
            MetadataPrivilegeScope? privileges=null;
            try
            {
                if(args.Length!=3)throw new IOException(L.T("管理员任务参数无效。"));
                using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
                if(!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    throw new IOException(L.T("此单次任务未取得管理员权限，未执行传输。"));
                var request=TaskRequestStore.ReadAndClaim(args[1],args[2]);
                L.SetLanguage(request.Language);
                ThemeManager.ApplyTheme(request.Theme);
                if(request.Options.FullPreservation)privileges=new MetadataPrivilegeScope();
                var scope=privileges;
                Exit+=(_,_)=>scope?.Dispose();
                var window=new MainWindow(request.Sources.ToList(),request.Destination,request.Options.IsMove,false,request.Options,true,
                    result=>TaskRequestStore.WriteReceipt(new(request.Id,args[2],result.State,result.FullPreservation,result.Messages,DateTimeOffset.UtcNow,result.IsMove),result.Log),
                    ()=>File.Exists(TaskRequestStore.CancelPath(request.Id)));
                MainWindow=window;window.Show();
            }
            catch(Exception ex)
            {
                privileges?.Dispose();
                AppDialog.Show(L.F("管理员任务未启动：{0}", ex.Message),"RoboCopyUI");
                Shutdown(1);
            }
        }

        private DesktopInstance? _instance;
        private DesktopHost? _host;
        private Mutex? _installerGuard;

        protected override async void OnStartup(StartupEventArgs e)
        {
            _=SetCurrentProcessExplicitAppUserModelID("RoboCopyUI.Desktop");
            base.OnStartup(e);
            _installerGuard=new Mutex(false,InstallerActions.RunningMutexName);
            if(e.Args.Length>0&&e.Args[0] is "--configure-installation" or "--remove-installation-integration")
            {
                int code=1;
                try
                {
                    if(e.Args.Length>2||e.Args.Length==2&&e.Args[1]!="--purge-user-data")throw new ArgumentException(L.T("安装动作参数无效。"));
                    code=InstallerActions.Execute(e.Args[0],e.Args.Length==2);
                }
                catch(Exception error)
                {
                    try{var log=Path.Combine(ShellRegistry.InstallRoot,"integration.log");TransferSafety.RequireNoLinks(log);File.AppendAllText(log,DateTimeOffset.Now+" "+error+Environment.NewLine);}catch{}
                }
                Shutdown(code);return;
            }
            if(e.Args.Length>0 && e.Args[0]=="--elevated-transfer"){StartElevatedTransfer(e.Args);return;}
            try
            {
                var preferences=UserSettings.Load();
                L.SetLanguage(preferences.Language);
                if(UserSettings.LoadNotice!=null)AppDialog.Show(UserSettings.LoadNotice,"RoboCopyUI");
                ThemeManager.ApplyTheme(preferences.Theme);
                var request=ParseDesktopArguments(e.Args);
                _instance=new DesktopInstance();
                if(!_instance.IsPrimary)
                {
                    var response=await _instance.Send(request);
                    if(!response.Accepted)AppDialog.Show(response.Message,"RoboCopyUI");
                    Shutdown(response.Accepted?0:1);return;
                }
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                if(request.Action!="ping")ShowShortcutNotice(preferences);
                _host=new DesktopHost(this);_instance.Listen(command=>Task.FromResult(_host.Handle(command)));

                await Task.Run(()=>DesktopSettings.Reconcile(preferences));
                if(request.Action!="ping")
                {
                    var response=_host.Handle(request);
                    if(!response.Accepted)AppDialog.Show(response.Message,"RoboCopyUI");
                }
            }
            catch(Exception ex)
            {
                AppDialog.Show(L.F("无法确认桌面请求结果：{0}\n请先从托盘检查任务，避免重复启动。", ex.Message),"RoboCopyUI");Shutdown(1);
            }
        }

        internal static void ShowShortcutNotice(UserSettings preferences)
        {
                if(IntegrationReadiness.Shortcuts&&!preferences.ShortcutConflictNoticeShown)
                {
                    var keys = preferences.Shortcuts;
                    var disabled = L.T("已停用");
                    AppDialog.Show(L.F("先用系统 Ctrl+C 复制或 Ctrl+X 剪切，在目标文件夹的文件列表内按以下快捷键。\n\n普通粘贴：{0}\n完整保留复制：{1}\n快速永久删除（先确认，默认取消）：{2}\n\n完整保留仅对本次粘贴生效。/ZB 先使用可重启模式，遇到访问被拒时尝试备份模式；/COPYALL 复制文件数据、属性、时间戳、权限、所有者和审核信息。目标需支持这些信息，账户也需相应权限。剪切的项目仅在移动验证成功后清理源。\n\n可在设置中改键或停用单项，也可从托盘暂停快捷键。其他软件可能占用同一组合。",keys.Paste?.Display??disabled,keys.PreservePaste?.Display??disabled,keys.PermanentDelete?.Display??disabled),L.T("RoboCopyUI 快捷键首次说明"),MessageBoxButton.OK,MessageBoxImage.Information);
                    preferences.ShortcutConflictNoticeShown=true;preferences.Save();
                }
        }

        private static DesktopRequest ParseDesktopArguments(string[] args)
        {
            var action=args.Length==0?"settings":args[0].ToLowerInvariant();
            var paths=Array.Empty<string>();string? folder=null;
            switch(action)
            {
                case "--background": action="ping";break;
                case "--settings": action="settings";break;
                case "settings": break;
                case "--tasks": action="tasks";break;
                case "--uninstall":case "/uninstall":throw new IOException(L.T("请通过安装器按清单卸载。"));
                default:
                    throw new IOException(L.T("命令参数无效。"));
            }
            var request=new DesktopRequest(1,Guid.NewGuid(),action,paths,folder);DesktopInstance.Validate(request);return request;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _host?.Dispose();_instance?.Dispose();_installerGuard?.Dispose();base.OnExit(e);
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string identifier);
    }
}
