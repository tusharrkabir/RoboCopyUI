using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;

namespace RoboCopyUI;

public static class AppDialog
{
    public static MessageBoxResult Show(string text,string caption="RoboCopyUI",MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)=>ShowCore(null,text,caption,buttons,defaultResult);
    public static MessageBoxResult Show(Window owner,string text,string caption="RoboCopyUI",MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,MessageBoxResult defaultResult=MessageBoxResult.None)=>ShowCore(owner,text,caption,buttons,defaultResult);
    private static MessageBoxResult ShowCore(Window? owner,string text,string caption,MessageBoxButton buttons,MessageBoxResult defaultResult)
    {
        var window=new ThemedWindow{Title=caption.StartsWith("RoboCopyUI",StringComparison.Ordinal)?caption:"RoboCopyUI — "+caption,Width=580,MinWidth=380,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        if(owner?.IsVisible==true){window.Owner=owner;window.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        var panel=new DockPanel{Margin=new Thickness(24,14,24,24)};
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};DockPanel.SetDock(actions,Dock.Bottom);panel.Children.Add(actions);
        var message=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap};panel.Children.Add(new ScrollViewer{Content=message,MaxHeight=440,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});window.Content=panel;
        var result=MessageBoxResult.Cancel;
        var choices=buttons switch
        {
            MessageBoxButton.OKCancel=>new[]{MessageBoxResult.OK,MessageBoxResult.Cancel},
            MessageBoxButton.YesNo=>new[]{MessageBoxResult.Yes,MessageBoxResult.No},
            MessageBoxButton.YesNoCancel=>new[]{MessageBoxResult.Yes,MessageBoxResult.No,MessageBoxResult.Cancel},
            _=>new[]{MessageBoxResult.OK}
        };
        if(defaultResult==MessageBoxResult.None)defaultResult=choices.Contains(MessageBoxResult.Cancel)?MessageBoxResult.Cancel:choices.Contains(MessageBoxResult.No)?MessageBoxResult.No:MessageBoxResult.OK;
        foreach(var choice in choices)
        {
            var button=new Button{MinWidth=88,Margin=new Thickness(8,0,0,0),IsDefault=choice==defaultResult,IsCancel=choice is MessageBoxResult.Cancel or MessageBoxResult.No};
            L.Bind(button,ContentControl.ContentProperty,choice.ToString());actions.Children.Add(button);
            button.Click+=(_,_)=>{result=choice;window.Close();};
            if(choice==defaultResult)window.Loaded+=(_,_)=>button.Focus();
        }
        window.ShowDialog();return result;
    }
}
