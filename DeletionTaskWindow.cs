using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button=System.Windows.Controls.Button;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Orientation=System.Windows.Controls.Orientation;

namespace RoboCopyUI;

/// <summary>One window owns planning, explicit confirmation, execution and results.</summary>
public sealed class DeletionTaskWindow:ThemedWindow
{
    private enum Phase { Planning,Confirming,Running,Finished }
    private Phase phase=Phase.Planning;
    private readonly Action requestStop;
    private TaskCompletionSource<bool>? confirmation;
    private readonly TextBlock status=new(){Text=L.T("正在核对删除范围…"),FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
    private readonly TextBlock explanation=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12),Visibility=Visibility.Collapsed};
    private readonly System.Windows.Controls.TextBox details=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Button cancelButton=new(){Content=L.T("取消"),IsDefault=true,MinWidth=100,Padding=new Thickness(18,8,18,8)};
    private readonly Button confirmButton=new(){Content=L.T("确认永久删除"),Visibility=Visibility.Collapsed,Margin=new Thickness(12,0,0,0),Padding=new Thickness(18,8,18,8)};

    public DeletionTaskWindow(Action cancel)
    {
        requestStop=cancel;
        L.Bind(this,TitleProperty,"RoboCopyUI — 永久删除任务");
        Width=720;Height=500;MinWidth=600;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        explanation.SetResourceReference(ForegroundProperty,"SecondaryText");
        var panel=new DockPanel{Margin=new Thickness(24,14,24,24)};Content=panel;
        DockPanel.SetDock(status,Dock.Top);panel.Children.Add(status);
        DockPanel.SetDock(explanation,Dock.Top);panel.Children.Add(explanation);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        buttons.Children.Add(cancelButton);buttons.Children.Add(confirmButton);DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);panel.Children.Add(details);
        cancelButton.Click+=(_,_)=>Cancel();
        confirmButton.Click+=(_,_)=>
        {
            if(phase!=Phase.Confirming)return;
            phase=Phase.Running;confirmButton.IsEnabled=false;confirmButton.Visibility=Visibility.Collapsed;
            confirmation!.TrySetResult(true);
        };
        Loaded+=(_,_)=>cancelButton.Focus();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Cancel();}};
    }

    public Task<bool> ConfirmAsync(PermanentDeletion plan)
    {
        if(phase!=Phase.Planning||confirmation!=null)throw new InvalidOperationException("Deletion confirmation is already active.");
        phase=Phase.Confirming;confirmation=new(TaskCreationOptions.RunContinuationsAsynchronously);
        status.Text=L.F("永久删除选中的 {0} 项？",plan.Selected.Length);
        explanation.Text=L.F("包括目录内容，共冻结 {0} 个文件或文件夹。不会放入回收站，无法通过撤销恢复。请逐项核对路径；确认前取消不会改动文件。",plan.EntryCount);
        explanation.Visibility=Visibility.Visible;details.Text=string.Join(Environment.NewLine,plan.Selected);
        confirmButton.Visibility=Visibility.Visible;confirmButton.IsEnabled=true;cancelButton.IsEnabled=true;
        if(IsLoaded)_=Dispatcher.BeginInvoke((Action)(()=>cancelButton.Focus()));
        return confirmation.Task;
    }

    public void Cancel()
    {
        if(phase==Phase.Finished){Close();return;}
        if(phase==Phase.Confirming)
        {
            phase=Phase.Running;confirmButton.IsEnabled=false;confirmButton.Visibility=Visibility.Collapsed;
            confirmation!.TrySetResult(false);cancelButton.IsEnabled=false;return;
        }
        requestStop();status.Text=L.T("正在请求停止；已经删除的项目无法恢复，等待 Shell 返回实际结果…");cancelButton.IsEnabled=false;
    }

    public void Started(string[] selected)
    {
        phase=Phase.Running;explanation.Visibility=Visibility.Collapsed;confirmButton.Visibility=Visibility.Collapsed;
        status.Text=L.T("正在永久删除；仅处理确认的选区。关闭窗口会隐藏任务，可从托盘重新打开。");details.Text=string.Join(Environment.NewLine,selected);cancelButton.IsEnabled=true;
    }

    public void Finish(string summary,IEnumerable<string> lines)
    {
        confirmation?.TrySetResult(false);phase=Phase.Finished;status.Text=summary;
        explanation.Visibility=Visibility.Collapsed;confirmButton.Visibility=Visibility.Collapsed;
        details.Text=string.Join(Environment.NewLine,lines);cancelButton.Content=L.T("关闭");cancelButton.IsEnabled=true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if(phase!=Phase.Finished)
        {
            if(phase is Phase.Planning or Phase.Confirming)Cancel();
            e.Cancel=true;Hide();
        }
        base.OnClosing(e);
    }
}
