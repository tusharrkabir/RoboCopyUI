using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace RoboCopyUI;

public sealed record ShellViewContext(nint Window, nint View, string Folder, string[] Selection);

/// <summary>STA-only, fail-closed association of foreground frame, visible Shell view and UIA focus.</summary>
public sealed class ShellViewContextReader : IDisposable
{
    private readonly object shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(
        new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid BrowserId = typeof(IShellBrowserContext).GUID;
    private static readonly Guid FolderId = typeof(IPersistFolderContext).GUID;
    private static readonly Guid ArrayId = typeof(IShellItemArrayContext).GUID;

    public bool ConnectionLost { get; private set; }
    private void ObserveConnection(Exception error)
    {
        if(error is InvalidComObjectException||error is COMException com&&com.HResult is unchecked((int)0x80010108) or unchecked((int)0x800706ba) or unchecked((int)0x80010007))ConnectionLost=true;
    }
    public bool TryRead(out ShellViewContext? context,out string reason)
    {
        try{return ReadCore(out context,out reason);}
        catch(Exception error){ObserveConnection(error);context=null;reason="Focus or Shell unavailable: "+error.GetType().Name;return false;}
    }
    private bool ReadCore(out ShellViewContext? context, out string reason)
    {
        context=null;
        reason="No file-list focus";
        var foreground=GetForegroundWindow();
        if (ClassName(foreground)!="CabinetWClass") {reason=$"Frame {ClassName(foreground)} hwnd={foreground} session={Process.GetCurrentProcess().SessionId}";return false;}
        GetWindowThreadProcessId(foreground,out var process);
        if (Process.GetProcessById((int)process).ProcessName!="explorer") return false;
        var focus=AutomationElement.FocusedElement;
        if (focus==null) {reason="UIA has no focused element";return false;}
        // Win11's virtualized UIItem provider can report PID 0. Its native ancestor must
        // still belong to the foreground Explorer process AND the visible Shell view.
        if (focus.Current.ProcessId!=0 && focus.Current.ProcessId!=process) {reason="UIA belongs to another process";return false;}
        if (focus.Current.ControlType==ControlType.Edit) {reason="Edit focus"; return false;}
        var listFound=false;
        nint focusWindow=0;
        for(var current=focus;current!=null;current=TreeWalker.ControlViewWalker.GetParent(current))
        {
            var info=current.Current;
            if (info.ControlType==ControlType.Edit || info.ControlType==ControlType.TreeItem || info.ControlType==ControlType.Tree)
            {reason="Editing or navigation-tree focus";return false;}
            if (info.ClassName is "UIItemsView" or "SysListView32" || info.AutomationId=="ItemsView") listFound=true;
            if (listFound && info.NativeWindowHandle!=0) {focusWindow=info.NativeWindowHandle;break;}
            if(info.NativeWindowHandle==foreground)break;
        }
        if(!listFound || focusWindow==0) {reason="Unrecognized file-list focus: "+focus.Current.ClassName;return false;}
        GetWindowThreadProcessId(focusWindow,out var focusProcess);
        if(focusProcess!=process) {reason="Native focus belongs to another process";return false;}
        var candidates=new List<ShellViewContext>();
        var release=new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            dynamic windows=shellWindows;
            for(int i=0;i<(int)windows.Count;i++)
            {
                object? window=null;
                try
                {
                    window=windows.Item(i); if(window==null)continue; release.Add(window);
                    if((long)((dynamic)window).HWND!=foreground.ToInt64())continue;
                    var provider=(IShellServiceProvider)window;
                    var service=TopLevelBrowser;var browserId=BrowserId;
                    Marshal.ThrowExceptionForHR(provider.QueryService(ref service,ref browserId,out var browserObject));
                    release.Add(browserObject);
                    var browser=(IShellBrowserContext)browserObject;
                    Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out var viewObject));
                    release.Add(viewObject);
                    var view=(IShellViewContext)viewObject;
                    Marshal.ThrowExceptionForHR(view.GetWindow(out var viewWindow));
                    if(!IsWindowVisible(viewWindow) || (focusWindow!=viewWindow && !IsChild(viewWindow,focusWindow)))continue;
                    var folderView=(IFolderViewContext)viewObject;
                    var folderId=FolderId;
                    Marshal.ThrowExceptionForHR(folderView.GetFolder(ref folderId,out var folderObject));
                    release.Add(folderObject);
                    Marshal.ThrowExceptionForHR(((IPersistFolderContext)folderObject).GetCurFolder(out var pidl));
                    string folder;
                    try
                    {
                        var itemId=typeof(IShellItemContext).GUID;
                        Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(pidl,ref itemId,out var item));
                        release.Add(item); folder=FileSystemPath(item);
                    }
                    finally {Marshal.FreeCoTaskMem(pidl);}
                    var arrayId=ArrayId;
                    var selection=new List<string>();
                    var selectionResult=folderView.Items(1,ref arrayId,out var arrayObject); // SVGIO_SELECTION
                    if(selectionResult>=0 && arrayObject!=null)
                    {
                        release.Add(arrayObject);
                        var array=(IShellItemArrayContext)arrayObject;
                        Marshal.ThrowExceptionForHR(array.GetCount(out var count));
                        if(count>10000)throw new InvalidOperationException("Selection exceeds task limit");
                        for(uint j=0;j<count;j++)
                        {
                            Marshal.ThrowExceptionForHR(array.GetItemAt(j,out var item));
                            release.Add(item); selection.Add(FileSystemPath(item));
                        }
                    }
                    candidates.Add(new(foreground,viewWindow,TransferSafety.Normalize(folder),selection.ToArray()));
                }
                catch(COMException ex) {ObserveConnection(ex);reason=$"Shell unavailable: 0x{ex.HResult:X8}";}
            }
            // Some ShellWindows entries alias the same visible view. Coalesce only exact matches.
            var unique=candidates.DistinctBy(c=>(c.View,c.Folder)).ToArray();
            if(unique.Length!=1 || GetForegroundWindow()!=foreground)
            {reason=$"Ambiguous or changed view ({unique.Length})";return false;}
            context=unique[0];reason="Verified foreground file list";return true;
        }
        catch(Exception ex) {ObserveConnection(ex);reason=$"Context rejected: {ex.GetType().Name}: {ex.Message}";return false;}
        finally {foreach(var value in release) if(Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
    }

    private static string FileSystemPath(IShellItemContext item)
    {
        Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000,out var path));
        try{return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException("No filesystem path");}
        finally{Marshal.FreeCoTaskMem(path);}
    }
    private static string ClassName(nint window) {var text=new System.Text.StringBuilder(256);GetClassName(window,text,256);return text.ToString();}
    public void Dispose(){if(Marshal.IsComObject(shellWindows))Marshal.ReleaseComObject(shellWindows);}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,System.Text.StringBuilder name,int size);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsChild(nint parent,nint child);
    [DllImport("shell32.dll")]private static extern int SHCreateItemFromIDList(nint pidl,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemContext item);
}

[ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellServiceProvider { [PreserveSig]int QueryService(ref Guid service,ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object value); }
[ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowserContext
{
    [PreserveSig]int GetWindow(out nint window); [PreserveSig]int ContextSensitiveHelp(bool enter);
    [PreserveSig]int InsertMenusSB(nint menu,nint widths); [PreserveSig]int SetMenuSB(nint menu,nint holemenu,nint window);
    [PreserveSig]int RemoveMenusSB(nint menu); [PreserveSig]int SetStatusTextSB(nint text);
    [PreserveSig]int EnableModelessSB(bool enable); [PreserveSig]int TranslateAcceleratorSB(nint msg,ushort id);
    [PreserveSig]int BrowseObject(nint pidl,uint flags); [PreserveSig]int GetViewStateStream(uint mode,out nint stream);
    [PreserveSig]int GetControlWindow(uint id,out nint window); [PreserveSig]int SendControlMsg(uint id,uint msg,nint w,nint l,out nint result);
    [PreserveSig]int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)]out object view);
}
[ComImport,Guid("000214E3-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellViewContext { [PreserveSig]int GetWindow(out nint window); }
[ComImport,Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderViewContext
{
    [PreserveSig]int GetCurrentViewMode(out uint mode); [PreserveSig]int SetCurrentViewMode(uint mode);
    [PreserveSig]int GetFolder(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object folder);
    [PreserveSig]int Item(int index,out nint pidl); [PreserveSig]int ItemCount(uint flags,out int count);
    [PreserveSig]int Items(uint flags,ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object array);
}
[ComImport,Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFolderContext
{
    [PreserveSig]int GetClassID(out Guid id); [PreserveSig]int Initialize(nint pidl); [PreserveSig]int GetCurFolder(out nint pidl);
}
[ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemContext
{
    [PreserveSig]int BindToHandler(nint context,ref Guid handler,ref Guid iid,out nint value);
    [PreserveSig]int GetParent(out nint parent); [PreserveSig]int GetDisplayName(uint kind,out nint text);
}
[ComImport,Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArrayContext
{
    [PreserveSig]int BindToHandler(nint context,ref Guid handler,ref Guid iid,out nint value);
    [PreserveSig]int GetPropertyStore(uint flags,ref Guid iid,out nint store);
    [PreserveSig]int GetPropertyDescriptionList(nint key,ref Guid iid,out nint list);
    [PreserveSig]int GetAttributes(uint flags,uint mask,out uint attributes);
    [PreserveSig]int GetCount(out uint count); [PreserveSig]int GetItemAt(uint index,[MarshalAs(UnmanagedType.Interface)]out IShellItemContext item);
}
