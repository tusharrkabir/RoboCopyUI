using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RoboCopyUI;

/// <summary>Dedicated LL hook thread; Shell/UIA work stays on the Shell STA.</summary>
public sealed class ExplorerShortcuts : IDisposable
{
    private readonly ShellApartment shell;
    private readonly Action<FileAction,ShellViewContext> admitted;
    private readonly Action<string> rejected;
    private readonly ShortcutKeyState keys;
    private readonly Thread thread;
    private readonly Dispatcher dispatcher;
    private readonly HookProc keyboardCallback;
    private readonly EventProc eventCallback;
    private readonly DispatcherTimer timer;
    private ShellViewContextReader? reader;
    private Cache? cache;
    private long generation;
    private int refreshing;
    private volatile bool enabled;
    private volatile bool disposed;
    private nint keyboardHook,foregroundHook,focusHook;
    private long events,injectedEvents,eligibleEvents,matchedEvents,candidates,eligibleCandidates;
    private int candidateModifiers;
    public (long Events,long Injected,long Eligible,long Matched,long Candidates,long EligibleCandidates,int CandidateModifiers) Diagnostics=>(Interlocked.Read(ref events),Interlocked.Read(ref injectedEvents),Interlocked.Read(ref eligibleEvents),Interlocked.Read(ref matchedEvents),Interlocked.Read(ref candidates),Interlocked.Read(ref eligibleCandidates),Volatile.Read(ref candidateModifiers));
    private sealed record Cache(ShellViewContext Context,long Generation,long At);
    public ExplorerShortcuts(ShellApartment shell,ShortcutMap map,bool enabled,
        Action<FileAction,ShellViewContext> admitted,Action<string> rejected)
    {
        this.shell=shell;keys=new(map);this.enabled=enabled;this.admitted=admitted;this.rejected=rejected;
        keyboardCallback=OnKeyboard;eventCallback=OnEvent;
        var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread=new Thread(()=>
        {
            try
            {
                for(int i=0;i<256;i++)if(i is not (0x10 or 0x11 or 0x12))keys.Seed(i,(GetAsyncKeyState(i)&0x8000)!=0);
                keyboardHook=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
                foregroundHook=SetWinEventHook(3,3,0,eventCallback,0,0,0);
                focusHook=SetWinEventHook(0x8005,0x800c,0,eventCallback,0,0,0);
                if(keyboardHook==0||foregroundHook==0||focusHook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.SetResult(Dispatcher.CurrentDispatcher);Dispatcher.Run();
            }
            catch(Exception error){ready.TrySetException(error);}
            finally{if(keyboardHook!=0)UnhookWindowsHookEx(keyboardHook);if(foregroundHook!=0)UnhookWinEvent(foregroundHook);if(focusHook!=0)UnhookWinEvent(focusHook);}
        }){IsBackground=true,Name="RoboCopyUI keyboard hook"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();dispatcher=ready.Task.GetAwaiter().GetResult();
        // Timer executes only a quick admission check; at most one COM refresh is queued.
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(200),DispatcherPriority.Background,(_,_)=>Refresh(),dispatcher);
        timer.Start();Refresh();
    }
    public void Replace(ShortcutMap map,bool enabled)
    {
        keys.Replace(map);this.enabled=enabled;Invalidate();
    }
    private void Invalidate(){Interlocked.Increment(ref generation);Volatile.Write(ref cache,null);}
    private void OnEvent(nint hook,uint eventId,nint window,int objectId,int child,uint eventThread,uint milliseconds)
    {
        Invalidate();
        // Key releases on a secure desktop are not delivered to this hook.
        // Reconcile modifiers on a foreground transition, outside the LL callback.
        if(eventId==3)foreach(var key in new[]{0xa0,0xa1,0xa2,0xa3,0xa4,0xa5,0x5b,0x5c})keys.Seed(key,(GetAsyncKeyState(key)&0x8000)!=0);
    }
    private void Refresh()
    {
        if(disposed||!enabled||Interlocked.Exchange(ref refreshing,1)!=0)return;
        _=RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        var expected=Interlocked.Read(ref generation);
        try
        {
            var context=await shell.Run(()=>{if(reader?.ConnectionLost==true){reader.Dispose();reader=null;}reader??=new();return reader.TryRead(out var current,out _)?current:null;});
            if(!disposed&&enabled&&context!=null&&expected==Interlocked.Read(ref generation))
                Volatile.Write(ref cache,new(context,expected,Stopwatch.GetTimestamp()));
            else Volatile.Write(ref cache,null);
        }
        catch{Volatile.Write(ref cache,null);}
        finally{Interlocked.Exchange(ref refreshing,0);}
    }
    private nint OnKeyboard(int code,nint message,nint pointer)
    {
        if(code<0)return CallNextHookEx(0,code,message,pointer);
        try
        {
            var data=Marshal.PtrToStructure<KeyboardData>(pointer);
            Interlocked.Increment(ref events);if((data.Flags&0x12)!=0)Interlocked.Increment(ref injectedEvents);
            var key=data.Key switch{0x10=>data.Scan==0x36?0xa1u:0xa0u,0x11=>(data.Flags&1)!=0?0xa3u:0xa2u,0x12=>(data.Flags&1)!=0?0xa5u:0xa4u,_=>data.Key};
            bool up=message==0x101||message==0x105;
            if(!up&&message!=0x100&&message!=0x104)return CallNextHookEx(0,code,message,pointer);
            var current=Volatile.Read(ref cache);
            bool eligible=!disposed&&enabled&&current!=null&&current.Generation==Interlocked.Read(ref generation)&&
                Stopwatch.GetElapsedTime(current.At)<TimeSpan.FromMilliseconds(500)&&GetForegroundWindow()==current.Context.Window;
            if(eligible)Interlocked.Increment(ref eligibleEvents);
            if(!up&&key is 0x56 or 0x44){Interlocked.Increment(ref candidates);if(eligible)Interlocked.Increment(ref eligibleCandidates);Volatile.Write(ref candidateModifiers,(int)keys.Modifiers);}
            bool swallow=keys.Process((int)key,up,(data.Flags&0x12)!=0,eligible,out var action);
            if(action.HasValue&&current!=null)
            {
                Interlocked.Increment(ref matchedEvents);
                var operation=action.Value;var captured=current;
                // Scheduling only: no Shell, UIA, IPC, locks, waits or IO in this callback.
                ThreadPool.QueueUserWorkItem(state=>{_=Dispatch(operation,captured);});
            }
            else if(!up&&(data.Flags&0x12)==0&&key is not (>=0xa0 and <=0xa5))Invalidate();
            return swallow?1:CallNextHookEx(0,code,message,pointer);
        }
        catch{return CallNextHookEx(0,code,message,pointer);}
    }
    private async Task Dispatch(FileAction action,Cache captured)
    {
        try
        {
            var accepted=await shell.Run(()=>
            {
                if(disposed||!enabled||captured.Generation!=Interlocked.Read(ref generation))return false;
                reader??=new();if(!reader.TryRead(out var current,out _)||current==null)return false;
                var old=captured.Context;
                if(current.Window!=old.Window||current.View!=old.View||!string.Equals(current.Folder,old.Folder,StringComparison.Ordinal)||
                    !current.Selection.SequenceEqual(old.Selection,StringComparer.Ordinal)||GetForegroundWindow()!=old.Window||captured.Generation!=Interlocked.Read(ref generation))return false;
                admitted(action,current);return true;
            });
            if(!accepted)rejected(L.T("文件列表或选区已变化，本次快捷键未执行；请重新按键。"));
        }
        catch(Exception error){rejected(L.T("本次快捷键未执行：")+error.Message);}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;Invalidate();
        dispatcher.BeginInvoke((Action)(()=>{timer.Stop();dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}));
        thread.Join(TimeSpan.FromSeconds(2));
        _=shell.Run(()=>{reader?.Dispose();reader=null;});
        GC.KeepAlive(keyboardCallback);GC.KeepAlive(eventCallback);
    }
    [StructLayout(LayoutKind.Sequential)]private struct KeyboardData{public uint Key,Scan,Flags,Time;public nuint Extra;}
    private delegate nint HookProc(int code,nint message,nint pointer);
    private delegate void EventProc(nint hook,uint eventId,nint window,int objectId,int child,uint eventThread,uint milliseconds);
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nint message,nint pointer);
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWinEventHook(uint first,uint last,nint module,EventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")]private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
}
