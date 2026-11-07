using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Button=System.Windows.Controls.Button;
using Brushes=System.Windows.Media.Brushes;
using FontFamily=System.Windows.Media.FontFamily;
using Image=System.Windows.Controls.Image;

namespace RoboCopyUI;

/// <summary>Shared application chrome for confirmations, errors and task windows.</summary>
public class ThemedWindow:Window
{
    private bool wrapped;
    public ThemedWindow()
    {
        Icon=BitmapFrame.Create(new Uri("pack://application:,,,/RoboCopyUI;component/Assets/app_icon.ico"));
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;UseLayoutRounding=true;
        SetResourceReference(ForegroundProperty,"PrimaryText");
        Resources.MergedDictionaries.Add(new(){Source=new Uri("/RoboCopyUI;component/Themes/DialogStyles.xaml",UriKind.Relative)});
        WindowChrome.SetWindowChrome(this,new(){CaptionHeight=0,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0)});
        Loaded+=(_,_)=>ThemeManager.ApplyThemeToWindow(this);
    }
    protected override void OnContentChanged(object oldContent,object newContent)
    {
        base.OnContentChanged(oldContent,newContent);
        if(wrapped||newContent==null)return;
        wrapped=true;
        Content=null;
        var layout=new Grid();layout.RowDefinitions.Add(new(){Height=new GridLength(44)});layout.RowDefinitions.Add(new());
        var caption=new Grid{Margin=new Thickness(18,8,12,0)};caption.ColumnDefinitions.Add(new(){Width=new GridLength(24)});caption.ColumnDefinitions.Add(new());caption.ColumnDefinitions.Add(new(){Width=new GridLength(32)});
        caption.MouseLeftButtonDown+=(_,e)=>{if(e.ButtonState==MouseButtonState.Pressed)DragMove();};
        caption.Children.Add(new Image{Source=Icon,Width=18,Height=18,VerticalAlignment=VerticalAlignment.Center});
        var title=new TextBlock{VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};
        title.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(Title)){Source=this});Grid.SetColumn(title,1);caption.Children.Add(title);
        var close=new Button{Content="×",Padding=new Thickness(0),MinHeight=28,FontSize=20};Grid.SetColumn(close,2);caption.Children.Add(close);close.Click+=(_,_)=>Close();
        L.Bind(close,ToolTipProperty,"Close");layout.Children.Add(caption);
        var presenter=new ContentPresenter{Content=newContent};Grid.SetRow(presenter,1);layout.Children.Add(presenter);
        var border=new Border{Child=layout,Margin=new Thickness(4),CornerRadius=new CornerRadius(15),BorderThickness=new Thickness(1)};
        border.SetResourceReference(Border.BackgroundProperty,"WindowBackground");border.SetResourceReference(Border.BorderBrushProperty,"BorderColor");Content=border;
    }
}
