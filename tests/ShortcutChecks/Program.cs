using RoboCopyUI;
using System.Text.Json;

var checks=new Dictionary<string,bool>();
void Check(string name,Func<bool> run){try{checks[name]=run();}catch(Exception error){Console.Error.WriteLine(name+": "+error);checks[name]=false;}}
bool Refuses(ShortcutPreferences preferences){try{_=new ShortcutMap(preferences);return false;}catch(ArgumentException){return true;}}
Check("defaults-normal-and-full-paste-and-delete",()=>
{
    var preferences=new ShortcutPreferences();var map=new ShortcutMap(preferences);var mods=ShortcutModifiers.Control|ShortcutModifiers.Alt;
    var json=JsonSerializer.Serialize(preferences);
    return preferences.Entries.Count()==3&&!json.Contains("Copy")&&!json.Contains("Cut")&&
        map.TryMatch(mods|ShortcutModifiers.Shift,0x56,out var preserve)&&preserve==FileAction.PreservePaste&&
        map.TryMatch(mods,0x56,out var paste)&&paste==FileAction.Paste&&map.TryMatch(mods,0x44,out var delete)&&delete==FileAction.PermanentDelete&&
        !map.TryMatch(mods,0x43,out _)&&!map.TryMatch(mods,0x58,out _)&&!map.TryMatch(ShortcutModifiers.None,0x2e,out _);
});
Check("duplicates-system-copy-cut-and-reserved-refused",()=>
    Refuses(new(){Paste=new(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x44)})&&
    Refuses(new(){Paste=new(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x2e)})&&
    Refuses(new(){Paste=new(ShortcutModifiers.Alt,0x73)})&&Refuses(new(){Paste=new(ShortcutModifiers.None,0x2e)})&&
    Refuses(new(){Paste=new(ShortcutModifiers.Control,0x43)})&&Refuses(new(){Paste=new(ShortcutModifiers.Control,0x58)}));
Check("clear-and-known-paste-conflict-warning",()=>
    !new ShortcutMap(new(){Paste=null}).TryMatch(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x56,out _)&&
    ShortcutMap.KnownConflict(new(ShortcutModifiers.Control,0x56))!=null);
Check("hold-runs-once-and-consumes-release",()=>
{
    var state=new ShortcutKeyState(new(new()));state.Seed(0xa2,true);state.Seed(0xa4,true);
    return state.Process(0x56,false,false,true,out var action)&&action==FileAction.Paste&&
        state.Process(0x56,false,false,true,out action)&&action==null&&state.Process(0x56,true,false,false,out action)&&action==null&&
        state.Process(0x56,false,false,true,out action)&&action==FileAction.Paste;
});
Check("unverified-focus-and-injected-keys-pass-through",()=>
{
    var state=new ShortcutKeyState(new(new()));state.Seed(0xa2,true);state.Seed(0xa4,true);
    return !state.Process(0x44,false,true,true,out _)&&!state.Process(0x44,false,false,false,out _)&&
        !state.Process(0x44,true,false,false,out _)&&state.Process(0x44,false,false,true,out var action)&&action==FileAction.PermanentDelete;
});
Check("ordinary-delete-and-extra-modifiers-pass-through",()=>
{
    var state=new ShortcutKeyState(new(new()));if(state.Process(0x2e,false,false,true,out _))return false;
    state.Seed(0xa2,true);state.Seed(0xa4,true);state.Seed(0xa0,true);if(state.Process(0x44,false,false,true,out _))return false;
    state.Process(0x44,true,false,true,out _);state.Seed(0xa0,false);state.Seed(0x5b,true);return !state.Process(0x44,false,false,true,out _);
});
Check("atomic-rebind-removes-old-and-keeps-held-release",()=>
{
    var state=new ShortcutKeyState(new(new()));state.Seed(0xa2,true);state.Seed(0xa4,true);if(!state.Process(0x56,false,false,true,out _))return false;
    state.Replace(new(new(){Paste=new(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x42)}));
    return state.Process(0x56,true,false,false,out _)&&!state.Process(0x56,false,false,true,out _)&&
        state.Process(0x42,false,false,true,out var action)&&action==FileAction.Paste;
});
Check("existing-full-setting-cannot-enable-normal-paste",()=>
{
    var settings=UserSettings.Parse("""{"SettingsVersion":4,"FullPreservation":true,"ShortcutConflictNoticeShown":true}""");
    var normal=settings.TransferOptions();
    var full=settings.TransferOptions(fullPreservation:true);
    var batch=new RobocopyBatchItem { IsDirectoryCopy=true, SourceDir=Environment.CurrentDirectory, DestDir=System.IO.Path.GetTempPath() };
    var argsNormal=RobocopyCommandBuilder.BuildArgumentList(batch,normal,false);
    var argsFull=RobocopyCommandBuilder.BuildArgumentList(batch,full,false);
    return !settings.ShortcutConflictNoticeShown && !normal.FullPreservation && full.FullPreservation &&
           new[]{"/ZB","/COPYALL","/DCOPY:DAT"}.All(flag=>argsFull.Contains(flag)&&!argsNormal.Contains(flag));
});
Check("migration-keeps-previous-binding-on-conflict",()=>
{
    var settings=UserSettings.Parse("""{"SettingsVersion":4,"Shortcuts":{"Paste":{"Modifiers":7,"Key":86}}}""");
    return settings.Shortcuts.PreservePaste==null && settings.Shortcuts.Paste?.Modifiers==(ShortcutModifiers)7;
});
Console.WriteLine(JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));return checks.All(pair=>pair.Value)?0:1;
