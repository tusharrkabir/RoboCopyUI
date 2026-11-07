using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Binding=System.Windows.Data.Binding;

namespace RoboCopyUI;

public sealed record UiLanguage(string Code,string Name)
{
    public override string ToString()=>Name;
}

/// <summary>Embedded gettext-style catalogs. Add a locales/{code}.json file to extend languages.</summary>
public sealed class L:INotifyPropertyChanged
{
    public static L Current { get; }=new();
    private readonly Dictionary<string,Dictionary<string,string>> catalogs=new(StringComparer.OrdinalIgnoreCase);
    private volatile string language=CultureInfo.CurrentUICulture.Name.StartsWith("zh",StringComparison.OrdinalIgnoreCase)?"zh-CN":"en-US";
    public IReadOnlyList<UiLanguage> Languages { get; }
    public string Language=>language;
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? Changed;
    private L()
    {
        var assembly=typeof(L).Assembly;
        const string prefix="RoboCopyUI.locales.";
        foreach(var resource in assembly.GetManifestResourceNames().Where(name=>name.StartsWith(prefix,StringComparison.Ordinal)&&name.EndsWith(".json",StringComparison.Ordinal)))
        {
            using var stream=assembly.GetManifestResourceStream(resource)!;
            catalogs.Add(resource[prefix.Length..^5],JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??throw new InvalidDataException("Empty language catalog"));
        }
        if(!catalogs.ContainsKey("en-US")||!catalogs.ContainsKey("zh-CN"))throw new InvalidDataException("Built-in languages are missing");
        Languages=catalogs.Select(pair=>new UiLanguage(pair.Key,pair.Value.GetValueOrDefault("$name",pair.Key))).OrderBy(item=>item.Code,StringComparer.Ordinal).ToArray();
    }
    public static bool Supports(string code)=>code!=null&&Current.catalogs.ContainsKey(code);
    public static string DefaultLanguage=>CultureInfo.CurrentUICulture.Name.StartsWith("zh",StringComparison.OrdinalIgnoreCase)?"zh-CN":"en-US";
    public static void SetLanguage(string code)
    {
        if(!Supports(code))throw new ArgumentException("Unsupported language: "+code);
        if(Current.language==code)return;
        Current.language=code;
        Current.PropertyChanged?.Invoke(Current,new(nameof(Language)));
        Changed?.Invoke();
    }
    public static string T(string key)=>In(Current.language,key);
    public static string In(string language,string key)
    {
        if(Current.catalogs[language].TryGetValue(key,out var text))return text;
        return Current.catalogs["en-US"].GetValueOrDefault(key,key);
    }
    public static string F(string key,params object?[] values)=>string.Format(CultureInfo.GetCultureInfo(Current.language),T(key),values);
    public static void Bind(DependencyObject target,DependencyProperty property,string key)=>BindingOperations.SetBinding(target,property,Binding(key));
    internal static Binding Binding(string key)=>new(nameof(Language)){Source=Current,Mode=BindingMode.OneWay,Converter=new TextConverter(key)};
    private sealed class TextConverter(string key):IValueConverter
    {
        public object Convert(object value,Type type,object parameter,CultureInfo culture)=>T(key);
        public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>throw new NotSupportedException();
    }
    public static IReadOnlyDictionary<string,string> Catalog(string code)=>Current.catalogs[code];
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension:MarkupExtension
{
    public string Key { get; set; }="";
    public LocExtension(){}
    public LocExtension(string key)=>Key=key;
    public override object ProvideValue(IServiceProvider serviceProvider)=>L.Binding(Key).ProvideValue(serviceProvider);
}
