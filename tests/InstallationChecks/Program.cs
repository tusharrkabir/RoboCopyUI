using Microsoft.Win32;
using RoboCopyUI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
const string root=@"C:\RoboCopyUI-UninstallChecks";
var roaming=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RoboCopyUI");
var settings=Path.Combine(roaming,"settings.json");
var requests=TaskRequestStore.DirectoryPath;
var log=Path.Combine(requests,"ae782a9d1f7d4ad98b15ce56096ac9ff.log.txt");
var source=Path.Combine(root,"source.txt");var target=Path.Combine(root,"target.txt");
var unknownSettings=Path.Combine(roaming,"user-notes.txt");
var unknownRequests=Path.Combine(requests,"unknown.log.txt");
var unknownNested=Path.Combine(requests,"UserFiles","keep.txt");
var unknownInstall=Path.Combine(ShellRegistry.InstallRoot,"user-file.txt");
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Put(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write);using var writer=new StreamWriter(file);writer.Write(text);}
if(args.Single()=="seed")
{
    if(Directory.Exists(root))throw new IOException("Fresh uninstall fixture required");
    ShellRegistry.RequireInstalled();
    Put(source,"Copy source outside installation and app-data");Put(target,"Copied target outside installation and app-data");
    Put(log,"Known one-task log to retain by default");Put(unknownSettings,"Unknown settings-directory file must remain");
    Put(unknownRequests,"Unknown task log name must remain");Put(unknownNested,"Unknown nested data must remain");
    Put(unknownInstall,"Unknown installation-directory file must remain");
    File.WriteAllText(Path.Combine(root,"before.json"),JsonSerializer.Serialize(new[]{source,target,settings,log,unknownSettings,unknownRequests,unknownNested,unknownInstall}.ToDictionary(path=>path,Hash)));
    return 0;
}
if(args.Single()=="migration")
{
    const string known=@"Software\Classes\*\shell\RoboCopy";
    const string unknown=@"Software\Classes\Directory\shell\RoboCut";
    foreach(var keyName in new[]{known,unknown}){using var existing=Registry.CurrentUser.OpenSubKey(keyName);if(existing!=null)throw new IOException("Migration fixture key already exists");}
    var original=File.ReadAllBytes(settings);
    try
    {
        using(var key=Registry.CurrentUser.CreateSubKey(known))
        {
            key.SetValue("MultiSelectModel","Document");
            using var command=key.CreateSubKey("command");command.SetValue("",$"\"{ShellRegistry.AppPath}\" --copy \"%1\"");
        }
        using(var key=Registry.CurrentUser.CreateSubKey(unknown)){key.SetValue("fixture","Unknown contents must remain");}
        File.WriteAllText(settings,"{\"SettingsVersion\":2,\"Theme\":\"Light\",\"RetryCount\":0,\"WaitSeconds\":0,\"ThreadCount\":6,\"StartWithWindows\":false}");
        var preferences=UserSettings.Load();preferences.SaveTo(settings);ShellRegistry.MigrateLegacyCurrentUser();
        using var knownAfter=Registry.CurrentUser.OpenSubKey(known);using var unknownAfter=Registry.CurrentUser.OpenSubKey(unknown);
        var checks=new Dictionary<string,bool>
        {
            ["classic-preference-removed"]=!File.ReadAllText(settings).Contains("ClassicMenuEnabled"),
            ["old-zero-migrates-and-other-preferences-remain"]=preferences.RetryCount==3&&preferences.WaitSeconds==5&&preferences.Theme=="Light"&&preferences.ThreadCount==6&&!preferences.StartWithWindows,
            ["recognized-own-command-removed"]=knownAfter==null,
            ["unknown-key-retained"]=unknownAfter?.GetValue("fixture") as string=="Unknown contents must remain"
        };
        File.WriteAllText(@"C:\RoboCopyUI-Results\installer-migration-checks.json",JsonSerializer.Serialize(checks));
        return checks.All(pair=>pair.Value)?0:1;
    }
    finally
    {
        File.WriteAllBytes(settings,original);
        foreach(var keyName in new[]{known,unknown})Registry.CurrentUser.DeleteSubKeyTree(keyName,false);
    }
}
var before=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(root,"before.json")))!;
var mode=args.Single();
if(mode=="purge")InstallerActions.PurgeUserData();
else if(mode!="retained")throw new ArgumentException("Unknown fixture phase");
var preserved=new[]{source,target,unknownSettings,unknownRequests,unknownNested,unknownInstall};
var results=new Dictionary<string,bool>
{
    ["source-target-and-unknown-files-unchanged"]=preserved.All(path=>File.Exists(path)&&Hash(path)==before[path]),
    ["settings-default-retained-or-explicitly-purged"]=mode=="purge"?!File.Exists(settings):File.Exists(settings)&&Hash(settings)==before[settings],
    ["log-default-retained-or-explicitly-purged"]=mode=="purge"?!File.Exists(log):File.Exists(log)&&Hash(log)==before[log],
    ["installed-launcher-removed"]=!File.Exists(ShellRegistry.AppPath),
    ["classic-menu-removed"]=!ShellRegistry.IsRegistered
};
File.WriteAllText(@"C:\RoboCopyUI-Results\uninstall-"+mode+"-checks.json",JsonSerializer.Serialize(results));
Console.WriteLine(JsonSerializer.Serialize(results));
return results.All(pair=>pair.Value)?0:1;
