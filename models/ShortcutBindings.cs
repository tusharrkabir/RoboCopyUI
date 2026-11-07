using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace RoboCopyUI;

[Flags]public enum ShortcutModifiers { None=0, Control=1, Alt=2, Shift=4 }
public enum FileAction { Paste, PermanentDelete, PreservePaste }
public sealed record ShortcutChord(ShortcutModifiers Modifiers,int Key)
{
    [JsonIgnore]public string Display=>string.Join("+",new[]{Modifiers.HasFlag(ShortcutModifiers.Control)?"Ctrl":null,Modifiers.HasFlag(ShortcutModifiers.Alt)?"Alt":null,Modifiers.HasFlag(ShortcutModifiers.Shift)?"Shift":null,Key is >=0x70 and <=0x87?"F"+(Key-0x6f):Key==0x2e?"Delete":Key==0x2d?"Insert":((char)Key).ToString()}.Where(s=>s!=null));
}
public sealed class ShortcutPreferences
{
    public ShortcutChord? Paste { get; set; }=new(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x56);
    public ShortcutChord? PreservePaste { get; set; }=new(ShortcutModifiers.Control|ShortcutModifiers.Alt|ShortcutModifiers.Shift,0x56);
    public ShortcutChord? PermanentDelete { get; set; }=new(ShortcutModifiers.Control|ShortcutModifiers.Alt,0x44);
    [JsonIgnore]public IEnumerable<(FileAction Action,ShortcutChord? Chord)> Entries=>[(FileAction.Paste,Paste),(FileAction.PreservePaste,PreservePaste),(FileAction.PermanentDelete,PermanentDelete)];
}
public sealed class ShortcutMap
{
    private readonly FrozenDictionary<(ShortcutModifiers Modifiers,int Key),FileAction> bindings;
    public ShortcutMap(ShortcutPreferences preferences)
    {
        var selected=new Dictionary<ShortcutChord,FileAction>();
        foreach(var (action,chord) in preferences.Entries)
        {
            if(chord==null)continue;Validate(chord);
            if(!selected.TryAdd(chord,action))throw new ArgumentException(L.F("快捷键重复：{0}", chord.Display));
        }
        bindings=selected.ToFrozenDictionary(pair=>(pair.Key.Modifiers,pair.Key.Key),pair=>pair.Value);
    }
    public bool TryMatch(ShortcutModifiers modifiers,int key,out FileAction action)=>bindings.TryGetValue((modifiers,key),out action);
    public static void Validate(ShortcutChord chord)
    {
        if(chord.Modifiers==ShortcutModifiers.Control&&chord.Key is 0x43 or 0x58)
            throw new ArgumentException(L.T("Ctrl+C 和 Ctrl+X 保留给系统复制、剪切，请选择其他组合。"));
        if(chord.Modifiers==ShortcutModifiers.None||((int)chord.Modifiers&~7)!=0)throw new ArgumentException(L.T("快捷键须包含 Ctrl、Alt 或 Shift；不接管普通 Delete 或 Windows 键组合。"));
        if(chord.Key is not (>=0x30 and <=0x39 or >=0x41 and <=0x5a or >=0x70 and <=0x87 or 0x2d or 0x2e))throw new ArgumentException(L.T("快捷键主键须为字母、数字、F1–F24、Insert 或 Delete。"));
        if(chord.Key==0x2e&&chord.Modifiers.HasFlag(ShortcutModifiers.Control|ShortcutModifiers.Alt)||chord.Key==0x73&&chord.Modifiers.HasFlag(ShortcutModifiers.Alt))throw new ArgumentException(L.T("此组合是系统保留操作，不能接管。"));
    }
    public static string? KnownConflict(ShortcutChord? chord)
    {
        if(chord==null)return null;
        if(chord.Modifiers==ShortcutModifiers.Control&&chord.Key is 0x43 or 0x56 or 0x58)return L.T("此键已有资源管理器复制／粘贴／剪切操作；仅在有效文件列表内会改由 RoboCopyUI 接管。");
        if(chord.Modifiers==ShortcutModifiers.Shift&&chord.Key==0x2e)return L.T("此键已有资源管理器永久删除操作；有效文件列表内会改由 RoboCopyUI 确认。");
        return null;
    }
}
