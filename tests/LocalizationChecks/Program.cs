using RoboCopyUI;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.IO;

var checks=new Dictionary<string,bool>();
void Check(string name,Func<bool> run){try{checks[name]=run();}catch(Exception error){Console.Error.WriteLine(name+": "+error);checks[name]=false;}}
var en=L.Catalog("en-US");var zh=L.Catalog("zh-CN");
Check("language-choices-display-human-names",()=>L.Current.Languages.Select(language=>language.ToString()).Order().SequenceEqual(new[]{"English","简体中文"}.Order()));
Check("embedded-catalogs-have-identical-keys",()=>en.Count>=300&&en.Keys.Order().SequenceEqual(zh.Keys.Order()));
Check("english-catalog-has-no-untranslated-chinese",()=>en.Where(pair=>pair.Key!="$name").All(pair=>!Regex.IsMatch(pair.Value,@"[\p{IsCJKUnifiedIdeographs}]")));
Check("format-arguments-and-formats-match",()=>en.Keys.All(key=>
{
    var expected=CompositeFormat.Parse(key);var english=CompositeFormat.Parse(en[key]);var chinese=CompositeFormat.Parse(zh[key]);
    return expected.MinimumArgumentCount==english.MinimumArgumentCount&&expected.MinimumArgumentCount==chinese.MinimumArgumentCount&&
        Regex.Matches(key,@"(?<!\{)\{\d+[^}]*\}").Select(match=>match.Value).Order().SequenceEqual(Regex.Matches(en[key],@"(?<!\{)\{\d+[^}]*\}").Select(match=>match.Value).Order())&&
        Regex.Matches(key,@"(?<!\{)\{\d+[^}]*\}").Select(match=>match.Value).Order().SequenceEqual(Regex.Matches(zh[key],@"(?<!\{)\{\d+[^}]*\}").Select(match=>match.Value).Order());
}));
Check("runtime-language-switch-and-fallback",()=>
{
    var changes=0;void Changed()=>changes++;L.Changed+=Changed;
    try
    {
        L.SetLanguage("zh-CN");if(L.T("Cancel")!="取消")return false;
        L.SetLanguage("en-US");return L.T("取消")=="Cancel"&&L.F("永久删除选中的 {0} 项？",24)=="Permanently delete the 24 selected items?"&&L.T("unknown.future.key")=="unknown.future.key"&&changes>=1;
    }
    finally{L.Changed-=Changed;}
});
Check("language-persisted-and-old-copy-bindings-removed",()=>
{
    var preferences=UserSettings.Parse("{\"SettingsVersion\":3,\"Language\":\"en-US\",\"ShortcutConflictNoticeShown\":true,\"Shortcuts\":{\"Copy\":{\"Modifiers\":3,\"Key\":67},\"Cut\":{\"Modifiers\":3,\"Key\":88}}}");
    var json=JsonSerializer.Serialize(preferences);
    return preferences.Language=="en-US"&&preferences.SettingsVersion==5&&!preferences.ShortcutConflictNoticeShown&&preferences.Shortcuts.Entries.Count()==3&&
        !JsonSerializer.Serialize(preferences.Shortcuts).Contains("Copy")&&!json.Contains("ModernMenuEnabled");
});
Check("retired-switches-removed",()=>!JsonSerializer.Serialize(new UserSettings()).Contains("ClassicMenuEnabled")&&!JsonSerializer.Serialize(new UserSettings()).Contains("FullPreservation"));
if(args.Length==1)
{
    var root=Path.GetFullPath(args[0]);var files=Directory.GetFiles(root,"*.cs").Concat(Directory.GetFiles(Path.Combine(root,"core"),"*.cs")).Concat(Directory.GetFiles(Path.Combine(root,"models"),"*.cs"));
    var used=files.SelectMany(path=>Regex.Matches(File.ReadAllText(path),"\\bL\\.[TF]\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"").Select(match=>Regex.Unescape(match.Groups[1].Value))).Distinct().ToArray();
    var missing=used.Where(key=>!en.ContainsKey(key)||!zh.ContainsKey(key)).ToArray();
    if(missing.Length>0)Console.Error.WriteLine(JsonSerializer.Serialize(missing));
    Check("source-messages-all-have-both-translations",()=>missing.Length==0);
}
Console.WriteLine(JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));return checks.All(pair=>pair.Value)?0:1;
