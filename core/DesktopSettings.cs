using System.IO;

namespace RoboCopyUI;

public static class IntegrationReadiness
{
    // Open each operational entry only after its corresponding isolated acceptance.
    public const bool ClipboardTransfers=true;
    public const bool Shortcuts=true;
    public const bool PermanentDelete=true;
}

public static class DesktopSettings
{
    private static readonly object applyGate=new();
    public static bool Installed=>File.Exists(ShellRegistry.AppPath)&&File.Exists(Path.Combine(ShellRegistry.InstallRoot,"App","RoboCopyUI.dll"));
    public static string? LastError { get; private set; }
    public static void Apply(UserSettings settings,bool repair=false)
    {
        lock(applyGate)
        {
        if(!Installed)return;
        var errors=new List<Exception>();
        try{ShellRegistry.SetLogonStart(settings.StartWithWindows);}catch(Exception error){errors.Add(error);}
        if(errors.Count>0)throw new AggregateException(L.T("部分桌面集成未完成。"),errors);
        LastError=null;
        }
    }
    public static void Reconcile(UserSettings settings)
    {
        try{Apply(settings);}catch(Exception ex){LastError=ex.Message;}
    }
}
