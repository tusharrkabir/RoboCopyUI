using System.Drawing;
using System.Windows;
using Forms=System.Windows.Forms;

namespace RoboCopyUI;

/// <summary>Ordinary-integrity tray and serialized transfer coordinator.</summary>
public sealed class DesktopHost : IDisposable
{
    private readonly App app;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem startupMenu;
    private readonly Queue<QueuedTransfer> pending=new();
    private SettingsWindow? settings;
    private Window? tasks;
    private MainWindow? active;
    private volatile bool exiting;
    private bool deleting;
    private CancellationTokenSource? deletionCancellation;
    private DeletionTaskWindow? deletionWindow;
    private readonly ShellApartment shell=new();
    private readonly ClipboardTasks clipboardTasks=new();
    private ExplorerShortcuts? shortcuts;
    private bool shortcutsPaused;
    private ShortcutMap shortcutMap=new(new());
    private bool shortcutsEnabled;
    private Action? activeRelease;
    private sealed record QueuedTransfer(string[] Sources,string Destination,RobocopyOptions Options,Action<TransferAttemptResult>? Finished,Action? Release);
    public DesktopHost(App application)
    {
        app=application;
        menu=new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("设置"),null,(_,_)=>app.Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(L.T("任务"),null,(_,_)=>app.Dispatcher.Invoke(ShowTasks));
        var pause=new Forms.ToolStripMenuItem(L.T("暂停快捷键")){CheckOnClick=true,Enabled=IntegrationReadiness.Shortcuts};
        pause.CheckedChanged+=(_,_)=>{shortcutsPaused=pause.Checked;shortcuts?.Replace(shortcutMap,shortcutsEnabled&&!shortcutsPaused);};menu.Items.Add(pause);
        startupMenu=new Forms.ToolStripMenuItem(L.T("开机自动启动")){Enabled=DesktopSettings.Installed};
        startupMenu.Click+=(_,_)=>app.Dispatcher.Invoke(SetLogonStart);menu.Items.Add(startupMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("退出"),null,(_,_)=>app.Dispatcher.Invoke(RequestExit));
        var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/RoboCopyUI;component/Assets/app_icon.ico"))?.Stream;
        using(stream){tray=new Forms.NotifyIcon{Text=L.T("RoboCopyUI"),Icon=stream==null?SystemIcons.Application:new Icon(stream),Visible=true,ContextMenuStrip=menu};}
        tray.DoubleClick+=(_,_)=>app.Dispatcher.Invoke(ShowSettings);
        NotificationService.DesktopToast=(title,message,icon)=>app.Dispatcher.BeginInvoke((Action)(()=>tray.ShowBalloonTip(3000,title,message,icon)));
        UserSettings.SettingsSaved+=SettingsSaved;SettingsSaved(UserSettings.Load());
        L.Changed+=RefreshLanguage;
    }
    private void RefreshLanguage()
    {
        if(!app.Dispatcher.CheckAccess()){_=app.Dispatcher.BeginInvoke(RefreshLanguage);return;}
        menu.Items[0].Text=L.T("设置");menu.Items[1].Text=L.T("任务");menu.Items[2].Text=L.T("暂停快捷键");startupMenu.Text=L.T("开机自动启动");menu.Items[5].Text=L.T("退出");UpdateTray();
    }
    private void SettingsSaved(UserSettings preferences)
    {
        if(!app.Dispatcher.CheckAccess()){_=app.Dispatcher.BeginInvoke((Action)(()=>SettingsSaved(preferences)));return;}
        startupMenu.Checked=preferences.StartWithWindows;settings?.SynchronizeLogonStart(preferences.StartWithWindows);
        shortcutMap=new(preferences.Shortcuts);shortcutsEnabled=preferences.ShortcutsEnabled;
        if(!IntegrationReadiness.Shortcuts)return;
        if(shortcuts==null)
        {
            try{shortcuts=new(shell,shortcutMap,shortcutsEnabled&&!shortcutsPaused,OnShortcut,ShortcutRejected);}
            catch(Exception error){ShortcutRejected(L.T("快捷键未启用：")+error.Message);}
        }
        else shortcuts.Replace(shortcutMap,shortcutsEnabled&&!shortcutsPaused);
    }
    private void SetLogonStart()
    {
        startupMenu.Enabled=false;
        bool previous=false,applied=false;
        try
        {
            var preferences=UserSettings.Load();previous=preferences.StartWithWindows;
            ShellRegistry.SetLogonStart(!previous);applied=true;
            preferences.StartWithWindows=!previous;preferences.Save();
        }
        catch(Exception error)
        {
            if(applied)
            {
                try{ShellRegistry.SetLogonStart(previous);}
                catch(Exception rollback){error=new AggregateException(error,rollback);}
            }
            AppDialog.Show(L.F("设置未保存：{0}",error.Message),"RoboCopyUI");
        }
        finally{startupMenu.Enabled=DesktopSettings.Installed&&!exiting;}
    }
    private void ShortcutRejected(string message)
    {
        if(exiting)return;
        _=app.Dispatcher.BeginInvoke((Action)(()=>{if(!exiting)tray.ShowBalloonTip(4000,"RoboCopyUI",message,Forms.ToolTipIcon.Warning);}));
    }
    private void OnShortcut(FileAction action,ShellViewContext context)
    {
        // Called on the Shell STA immediately after the view/selection recheck.
        if(exiting)return;
        try
        {
            if(action is FileAction.Paste or FileAction.PreservePaste)
            {
                var lease=clipboardTasks.Capture();_=RunShortcutPaste(lease,context.Folder,action==FileAction.PreservePaste);
            }
            else if(action==FileAction.PermanentDelete)
            {
                if(context.Selection.Length==0)throw new InvalidOperationException(L.T("没有选中可删除项目。"));
                _=AdmitShortcutDeletion(context.Selection);
            }
        }
        catch(Exception error){ShortcutRejected(L.T("本次快捷键未执行：")+error.Message);}
    }
    private async Task AdmitShortcutDeletion(string[] selected)
    {
        await app.Dispatcher.InvokeAsync(()=>
        {
            if(exiting||deleting||active!=null||pending.Count!=0)
            {
                ShortcutRejected(L.T("另一个文件任务尚未结束，请结束该任务后再删除。"));
                return;
            }
            deleting=true;_=RunDeletion(selected);
        });
    }
    private async Task RunShortcutPaste(ClipboardTasks.Lease lease,string destination,bool fullPreservation)
    {
        try{await QueueCapturedClipboard(lease,destination,fullPreservation);}catch(Exception error){ShortcutRejected(L.T("粘贴未启动：")+error.Message);}
    }
    public void ShowSettings()
    {
        if(exiting)return;
        App.ShowShortcutNotice(UserSettings.Load());
        if(settings==null){settings=new SettingsWindow();settings.Closed+=(_,_)=>settings=null;settings.Show();}
        else{settings.Show();if(settings.WindowState==WindowState.Minimized)settings.WindowState=WindowState.Normal;settings.Activate();}
    }
    public void ShowTasks()
    {
        if(active!=null){active.Show();if(active.WindowState==WindowState.Minimized)active.WindowState=WindowState.Normal;active.Activate();return;}
        if(deletionWindow!=null){deletionWindow.Show();deletionWindow.Activate();return;}
        if(tasks==null){tasks=new ThemedWindow{Title=L.T("RoboCopyUI 任务"),Width=420,Height=160,WindowStartupLocation=WindowStartupLocation.CenterScreen,Content=new System.Windows.Controls.TextBlock{Text=L.T("当前没有运行中的任务。"),Margin=new Thickness(24)}};tasks.Closed+=(_,_)=>tasks=null;tasks.Show();}
        else tasks.Activate();
    }
    public DesktopResponse Handle(DesktopRequest request)
    {
        // Acknowledge admission without blocking Explorer on a modal or transfer.
        if(exiting)return new(request.Id,false,L.T("宿主正在退出。"));
        if(request.Action=="ping")return new(request.Id,true,"ready");
        if(request.Action is "settings" or "tasks")
        {
            _=app.Dispatcher.BeginInvoke((Action)(()=>{if(request.Action=="settings")ShowSettings();else ShowTasks();}));
            return new(request.Id,true,"accepted");
        }
        return new(request.Id,false,L.T("此桌面文件入口尚未通过对应验收，未执行。"));
    }
    private async Task RunDeletion(string[] selected)
    {
        deletionCancellation?.Dispose();deletionCancellation=new();var cancellation=deletionCancellation;
        try
        {
            deletionWindow?.Close();deletionWindow=new(()=>cancellation.Cancel());deletionWindow.Closed+=(_,_)=>deletionWindow=null;deletionWindow.Show();
            var plan=await shell.Run(()=>PermanentDeletion.Freeze(selected));cancellation.Token.ThrowIfCancellationRequested();
            var confirmed=await deletionWindow.ConfirmAsync(plan);
            if(!confirmed||cancellation.IsCancellationRequested){deletionWindow.Finish(L.T("已取消确认，没有开始删除。"),selected);return;}
            deletionWindow.Started(plan.Selected);
            var result=await shell.Run(()=>plan.ExecuteConfirmed(cancellation.Token));
            deletionWindow.Finish(result.Completed?L.T("永久删除已完成。"):result.Aborted?L.T("删除已停止；部分项目可能已经删除，请核对下方实际结果。"):L.T("删除未全部完成，请核对实际结果。"),
                result.Items.Select(item=>$"{item.Path}（Shell 0x{item.HResult:X8}）").Concat(result.Errors));
        }
        catch(OperationCanceledException){deletionWindow?.Finish(L.T("任务已取消；请核对选中项目。"),selected);}
        catch(Exception error){deletionWindow?.Finish(L.T("删除未完成：")+error.Message,selected);}
        finally
        {
            deleting=false;
            if(exiting){deletionWindow?.Close();app.Shutdown();}else StartNext();
        }
    }
    private async Task QueueCapturedClipboard(ClipboardTasks.Lease lease,string destination,bool fullPreservation)
    {
        try
        {
            // Keep this OLE object until the entire retry/window lifecycle ends.
            // A failed attempt must not release the cut to another queued move.
            var sources=await shell.Run(()=>lease.Sources);
            var move=await shell.Run(()=>lease.IsMove);
            await app.Dispatcher.InvokeAsync(()=>
            {
                var options=UserSettings.Load().TransferOptions(move,fullPreservation);
                QueueTransfer(sources,destination,options,result=>
                {
                    _=CompleteClipboard(lease,result);
                },()=>{_=shell.Run(lease.Dispose);});
            });
        }
        catch{await shell.Run(lease.Dispose);throw;}
    }
    private async Task CompleteClipboard(ClipboardTasks.Lease lease,TransferAttemptResult result)
    {
        try{await shell.Run(()=>lease.Complete(result));}
        catch(Exception error)
        {
            await app.Dispatcher.InvokeAsync(()=>AppDialog.Show(L.T("文件任务已返回结果，但剪贴板状态更新失败：")+error.Message+L.T("\n请先检查源和目标，再决定是否重新剪切。"),"RoboCopyUI"));
        }
    }
    public void QueueTransfer(IEnumerable<string> sources,string destination,RobocopyOptions options,Action<TransferAttemptResult>? finished=null,Action? release=null)
    {
        if(exiting)throw new InvalidOperationException(L.T("宿主正在退出。"));
        if(deleting)throw new InvalidOperationException(L.T("永久删除流程尚未结束，请稍后再启动传输。"));
        var paths=sources.Select(TransferSafety.Normalize).ToArray();
        TransferSafety.Validate(RobocopyBatchItem.CreateBatches(paths,TransferSafety.Normalize(destination)));
        pending.Enqueue(new(paths,destination,options.Snapshot(),finished,release));
        StartNext();
    }
    private void StartNext()
    {
        if(active!=null||exiting||pending.Count==0)return;
        var task=pending.Dequeue();
        activeRelease=task.Release;
        try
        {
            Attach(new MainWindow(task.Sources.ToList(),task.Destination,task.Options.IsMove,false,task.Options,false,task.Finished));
            active!.Show();UpdateTray();
        }
        catch{active=null;activeRelease=null;task.Release?.Invoke();throw;}
    }
    private void Attach(MainWindow window)
    {
        active=window;window.HideOnClose=true;
        window.Replaced+=replacement=>{Attach(replacement);UpdateTray();};
        window.Closed+=(_,_)=>
        {
            if(active!=window)return;
            active=null;var release=activeRelease;activeRelease=null;release?.Invoke();UpdateTray();
            if(exiting)app.Shutdown();else StartNext();
        };
    }
    private void UpdateTray()=>tray.Text=L.F("RoboCopyUI — {0}，排队 {1}", (active==null?L.T("空闲"):L.T("有任务")), pending.Count);
    public void RequestExit()
    {
        if(exiting)return;
        if(active!=null||pending.Count>0||deleting)
        {
            var answer=AppDialog.Show(L.T("退出将取消正在运行和排队的任务。等待任务停止后退出；已复制的目标内容会保留。是否退出？"),"RoboCopyUI",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel);
            if(answer!=MessageBoxResult.OK)return;
        }
        exiting=true;while(pending.TryDequeue(out var task))task.Release?.Invoke();settings?.Close();tasks?.Close();
        deletionCancellation?.Cancel();deletionWindow?.Cancel();
        if(deleting)return;
        deletionWindow?.Close();if(active!=null)active.RequestStop();else app.Shutdown();
    }
    public void Dispose(){L.Changed-=RefreshLanguage;UserSettings.SettingsSaved-=SettingsSaved;shortcuts?.Dispose();NotificationService.DesktopToast=null;tray.Visible=false;tray.Dispose();menu.Dispose();deletionCancellation?.Dispose();shell.Dispose();}
}
