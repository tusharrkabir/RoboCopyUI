using System.IO;
using System.Text.RegularExpressions;

namespace RoboCopyUI;

public static class InstallerActions
{
    public static string RunningMutexName=>@"Global\RoboCopyUI.Running."+Environment.UserDomainName+"."+Environment.UserName;
    public static int Execute(string action,bool purge)
    {
        using var instance=new DesktopInstance();
        if(!instance.IsPrimary)throw new IOException(L.T("请先从托盘退出 RoboCopyUI，等待文件任务停止后再安装或卸载。"));
        TransferSafety.RequireNoLinks(ShellRegistry.InstallRoot);
        if(action=="--configure-installation")
        {
            ShellRegistry.RequireInstalled();var preferences=UserSettings.Load();L.SetLanguage(preferences.Language);preferences.Save();
            ShellRegistry.MigrateLegacyCurrentUser();ShellRegistry.RemoveOwnedMenus();DesktopSettings.Apply(preferences);return 0;
        }
        if(action!="--remove-installation-integration")throw new ArgumentException(L.T("未知安装动作。"));
        ShellRegistry.RemoveOwnedMenus();ShellRegistry.SetLogonStart(false);
        if(purge)PurgeUserData();return 0;
    }
    public static void PurgeUserData()
    {
        var settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RoboCopyUI");
        var requests=TaskRequestStore.DirectoryPath;
        RemoveKnown(settings,name=>name is "settings.json" or "settings.json.bak" or "clipboard.json"||Regex.IsMatch(name,@"^settings\.json\.corrupt-[0-9]{17}\.json$")||Regex.IsMatch(name,@"^settings\.json\.[0-9a-f]{32}\.tmp$"));
        RemoveKnown(requests,name=>Regex.IsMatch(name,@"^[0-9a-f]{32}(\.json|\.claim|\.cancel|\.result\.json|\.log\.txt|\.result\.json\.[0-9a-f]{32}\.tmp)$"));
        var parent=Path.GetDirectoryName(requests)!;if(Directory.Exists(parent)){TransferSafety.RequireNoLinks(parent);if(!Directory.EnumerateFileSystemEntries(parent).Any())Directory.Delete(parent);}
    }
    private static void RemoveKnown(string folder,Func<string,bool> known)
    {
        if(!Directory.Exists(folder))return;TransferSafety.RequireNoLinks(folder);
        foreach(var path in Directory.EnumerateFiles(folder))
        {
            if(!known(Path.GetFileName(path)))continue;
            TransferSafety.RequireNoLinks(path);File.Delete(path);
        }
        if(!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
    }
}
