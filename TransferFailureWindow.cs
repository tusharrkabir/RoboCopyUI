using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using Orientation=System.Windows.Controls.Orientation;

namespace RoboCopyUI;

public enum TransferRetryChoice { Stop, OrdinaryCopy, Administrator }

/// <summary>Only shown after the engine has stopped. No automatic mode downgrade.</summary>
public sealed class TransferFailureWindow : ThemedWindow
{
    public TransferRetryChoice Choice { get; private set; }
    public TransferFailureWindow(IEnumerable<string> sources,string destination,TransferAttemptResult result,bool elevatedWorker)
    {
        Title=L.T("任务未完成");Width=720;Height=540;MinWidth=600;MinHeight=440;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel{Margin=new Thickness(20)};
        var intro=new TextBlock{Text=L.T("任务已经停下。未通过验证的源项目会保留；目标可能已有部分副本。"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        DockPanel.SetDock(intro,Dock.Top);layout.Children.Add(intro);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        void Add(string text,TransferRetryChoice choice,bool cancel=false){var button=new Button{Content=text,Padding=new Thickness(12,8,12,8),Margin=new Thickness(6,0,0,0),IsCancel=cancel};button.Click+=(_,_)=>{Choice=choice;DialogResult=true;};actions.Children.Add(button);}
        if(result.FullPreservation)Add(L.T("仅本次普通复制"),TransferRetryChoice.OrdinaryCopy);
        if(!elevatedWorker)Add(L.T("请求管理员权限重试…"),TransferRetryChoice.Administrator);
        Add(L.T("停止并保留现状"),TransferRetryChoice.Stop,true);
        DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
        var explanation=new TextBlock{Text=L.T("普通复制不复制 Windows 权限、所有者和审核信息，并保留源项目。两种重试都会重新检查当前文件、重新生成计划并再次确认冲突；不会修改默认设置。管理员权限也无法让 exFAT 保留 Windows 权限。"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
        DockPanel.SetDock(explanation,Dock.Bottom);layout.Children.Add(explanation);
        layout.Children.Add(new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=L.T("源：\n")+string.Join("\n",sources)+L.T("\n\n目标：\n")+destination+L.T("\n\n原因：\n")+string.Join("\n",result.Messages)+L.T("\n\n执行记录（含已创建的副本）：\n")+string.Join("\n",result.Log)});
        Content=layout;
    }
}
