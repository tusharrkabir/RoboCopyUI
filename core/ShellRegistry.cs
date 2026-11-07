using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;

namespace RoboCopyUI;

public static class ShellRegistry
{
    public const string ClassId="{2F652125-D6CD-4B90-932C-A6A57D552D74}";
    public const string VerbName="RoboCopyUI.SafeDesktop";
    private const string Owner="RoboCopyUI.SafeDesktop.v1";
    public static string InstallRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","RoboCopyUI");
    public static string AppPath=>Path.Combine(InstallRoot,"RoboCopyUI.exe");
    private static readonly string[] Scopes=["AllFilesystemObjects","Directory\\Background","Drive"];
    public static string GetIconPath()=>AppPath+",0";
    private static string KeyPath(string scope)=>$@"Software\Classes\{scope}\shellex\ContextMenuHandlers\{VerbName}";
    public static bool IsRegistered=>Scopes.All(scope=>{using var key=Registry.CurrentUser.OpenSubKey(KeyPath(scope));return key?.GetValue("RoboCopyUIOwner") as string==Owner;});
    public static bool LegacyCurrentUserPresent=>LegacyKeys.Any(path=>{using var key=Registry.CurrentUser.OpenSubKey(path);return key!=null;});
    public static bool LegacyMachinePresent=>LegacyKeys.Any(path=>{using var key=Registry.LocalMachine.OpenSubKey(path);return key!=null;});
    private static IEnumerable<string> LegacyKeys=>
        from scope in new[]{"*","Directory","Directory\\Background","Drive","Drive\\Background","AllFilesystemObjects"}
        from verb in new[]{"RoboCopy","RoboCut","RoboPaste","RoboCopyDelete","RoboCopySettings"}
        select $@"Software\Classes\{scope}\shell\{verb}";
    public static void RequireInstalled()
    {
        TransferSafety.RequireNoLinks(InstallRoot);
        if(!File.Exists(AppPath)||!File.Exists(Path.Combine(InstallRoot,"App","RoboCopyUI.dll")))throw new IOException(L.T("桌面集成需要完整安装版；当前安装目录缺少程序文件。"));
    }
    public static void MigrateLegacyCurrentUser()
    {
        foreach(var path in LegacyKeys)
        {
            using(var key=Registry.CurrentUser.OpenSubKey(path))
            {
                if(key==null||key.GetSubKeyNames().Length!=1||key.GetSubKeyNames()[0]!="command"||key.GetValueNames().Any(name=>name is not ("" or "Icon" or "MultiSelectModel")))continue;
                using var command=key.OpenSubKey("command");
                if(command==null||command.GetValueNames().Any(name=>name!="")||command.GetSubKeyNames().Length!=0)continue;
                var value=command.GetValue("") as string;
                var action=path.EndsWith("RoboCopySettings",StringComparison.Ordinal)?"--settings":path.EndsWith("RoboCopyDelete",StringComparison.Ordinal)?"--delete":path.EndsWith("RoboCopy",StringComparison.Ordinal)?"--copy":path.EndsWith("RoboCut",StringComparison.Ordinal)?"--cut":"--paste";
                var expected=$"\"{AppPath}\" {action}";
                if(action!="--settings")expected+=path.Contains("Background",StringComparison.Ordinal)?" \"%V\"":" \"%1\"";
                if(!string.Equals(value,expected,StringComparison.OrdinalIgnoreCase)||key.GetValue("MultiSelectModel") as string!="Document")continue;
            }
            Registry.CurrentUser.DeleteSubKeyTree(path,false);
        }
        NotifyShell();
    }
    public static void RemoveOwnedMenus()
    {
        var paths = Scopes.Select(KeyPath)
            .Concat(Scopes.Select(scope => $@"Software\Classes\{scope}\shell\{VerbName}"))
            .Append($@"Software\Classes\CLSID\{ClassId}");
        foreach (var path in paths)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path))
            {
                if (key?.GetValue("RoboCopyUIOwner") as string != Owner) continue;
            }
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
        NotifyShell();
    }
    public static void SetLogonStart(bool enabled)
    {
        if(enabled)RequireInstalled();
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var existing=key.GetValue("RoboCopyUI") as string;
        var command=$"\"{AppPath}\" --background";
        if(existing!=null&&!string.Equals(existing,command,StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("RoboCopyUI 登录启动项已有不同路径，未覆盖；请核对旧安装。"));
        if(enabled)key.SetValue("RoboCopyUI",command);else key.DeleteValue("RoboCopyUI",false);
    }
    public static void NotifyShell()=>SHChangeNotify(0x08000000,0,nint.Zero,nint.Zero);
    [DllImport("shell32.dll")]private static extern void SHChangeNotify(uint eventId,uint flags,nint item1,nint item2);
}
