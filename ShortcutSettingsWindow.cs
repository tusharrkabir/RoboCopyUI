using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox=System.Windows.Controls.TextBox;
using Button=System.Windows.Controls.Button;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Orientation=System.Windows.Controls.Orientation;

namespace RoboCopyUI;

public sealed class ShortcutSettingsWindow : ThemedWindow
{
    private readonly Dictionary<FileAction,ShortcutChord?> choices;
    private readonly Dictionary<FileAction,TextBox> editors=new();
    private readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)};
    public ShortcutPreferences Preferences { get; private set; }
    public ShortcutSettingsWindow(ShortcutPreferences preferences)
    {
        Preferences=preferences;choices=preferences.Entries.ToDictionary(entry=>entry.Action,entry=>entry.Chord);
        Title=L.T("RoboCopyUI 快捷键");Width=660;Height=430;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock{Text=L.T("仅在已确认焦点的资源管理器文件列表内接管；地址栏、搜索、重命名、导航树和其他应用保留系统行为。桌面及虚拟文件夹暂不支持。"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        foreach(var (action,label) in new[]{(FileAction.Paste,L.T("粘贴")),(FileAction.PreservePaste,L.T("完整保留复制")),(FileAction.PermanentDelete,L.T("快速删除（总是确认）"))})
        {
            var row=new Grid{Margin=new Thickness(0,4,0,4)};
            row.ColumnDefinitions.Add(new(){Width=new GridLength(185)});row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=new GridLength(75)});
            row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center});
            var editor=new TextBox{IsReadOnly=true,Padding=new Thickness(8),Margin=new Thickness(0,0,12,0),ToolTip=L.T("点击这里，然后按组合键；清除后该项停用。")};
            editors[action]=editor;Grid.SetColumn(editor,1);row.Children.Add(editor);
            editor.PreviewKeyDown+=(_,e)=>
            {
                e.Handled=true;var key=e.Key==Key.System?e.SystemKey:e.Key;
                if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)return;
                if((Keyboard.Modifiers&ModifierKeys.Windows)!=0){notice.Text=L.T("不接管 Windows 键组合。");return;}
                var mods=ShortcutModifiers.None;
                if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))mods|=ShortcutModifiers.Control;
                if(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))mods|=ShortcutModifiers.Alt;
                if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))mods|=ShortcutModifiers.Shift;
                var chord=new ShortcutChord(mods,KeyInterop.VirtualKeyFromKey(key));
                try{ShortcutMap.Validate(chord);choices[action]=chord;Refresh();}catch(ArgumentException error){notice.Text=error.Message;}
            };
            var clear=new Button{Content=L.T("清除"),Padding=new Thickness(8)};Grid.SetColumn(clear,2);row.Children.Add(clear);clear.Click+=(_,_)=>{choices[action]=null;Refresh();};
            panel.Children.Add(row);
        }
        panel.Children.Add(notice);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var reset=new Button{Content=L.T("恢复默认"),Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,12,0)};buttons.Children.Add(reset);
        reset.Click+=(_,_)=>{foreach(var (action,chord) in new ShortcutPreferences().Entries)choices[action]=chord;Refresh();};
        var cancel=new Button{Content=L.T("取消"),IsCancel=true,Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,12,0)};buttons.Children.Add(cancel);
        var apply=new Button{Content=L.T("采用这些组合"),Padding=new Thickness(12,7,12,7)};buttons.Children.Add(apply);
        apply.Click+=(_,_)=>{var selected=Build();try{_=new ShortcutMap(selected);Preferences=selected;DialogResult=true;}catch(ArgumentException error){notice.Text=error.Message;}};
        Refresh();
    }
    private ShortcutPreferences Build()=>new(){Paste=choices[FileAction.Paste],PreservePaste=choices[FileAction.PreservePaste],PermanentDelete=choices[FileAction.PermanentDelete]};
    private void Refresh()
    {
        foreach(var (action,editor) in editors)editor.Text=choices[action]?.Display??L.T("已停用");
        try{_=new ShortcutMap(Build());notice.Text=string.Join("\n",choices.Values.Select(ShortcutMap.KnownConflict).Where(text=>text!=null).Distinct());if(notice.Text.Length==0)notice.Text=L.T("第三方软件仍可能占用组合键；保存后请在文件列表现场试按。普通 Delete 保持系统行为。");}
        catch(ArgumentException error){notice.Text=error.Message;}
    }
}
