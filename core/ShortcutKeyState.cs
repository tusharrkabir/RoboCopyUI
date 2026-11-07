namespace RoboCopyUI;

/// <summary>No Shell, UIA, filesystem or logging calls on the low-level keyboard path.</summary>
public sealed class ShortcutKeyState
{
    private ShortcutMap map;
    private readonly bool[] down=new bool[256];
    private readonly bool[] consumed=new bool[256];
    public ShortcutKeyState(ShortcutMap map){this.map=map;}
    public void Replace(ShortcutMap replacement)=>Volatile.Write(ref map,replacement);
    public void Seed(int key,bool pressed){if((uint)key<256)down[key]=pressed;}
    public ShortcutModifiers Modifiers=>
        (down[0x11]||down[0xa2]||down[0xa3]?ShortcutModifiers.Control:0)|
        (down[0x12]||down[0xa4]||down[0xa5]?ShortcutModifiers.Alt:0)|
        (down[0x10]||down[0xa0]||down[0xa1]?ShortcutModifiers.Shift:0);
    public bool Process(int key,bool keyUp,bool injected,bool eligible,out FileAction? action)
    {
        action=null;
        if(injected||(uint)key>=256)return false;
        if(keyUp){down[key]=false;var swallow=consumed[key];consumed[key]=false;return swallow;}
        if(down[key])return consumed[key];
        down[key]=true;
        if(!eligible||down[0x5b]||down[0x5c])return false;
        if(!Volatile.Read(ref map).TryMatch(Modifiers,key,out var matched))return false;
        consumed[key]=true;action=matched;return true;
    }
}
