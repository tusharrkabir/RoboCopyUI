using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RoboCopyUI;

public sealed record DeletionItemResult(string Path,int HResult);
public sealed record DeletionResult(bool Completed,bool Aborted,DeletionItemResult[] Items,string[] Errors);

/// <summary>Separate Shell deletion plan. No robocopy, mirror or recycle flags.</summary>
public sealed class PermanentDeletion
{
    private sealed record Stamp(string Path,uint Volume,ulong Id,long Length,long Written,bool Directory);
    private readonly string[] selected;
    private readonly Dictionary<string,Stamp> entries=new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] protectedPaths;
    public string[] Selected=>selected.ToArray();
    public int EntryCount=>entries.Count;
    private PermanentDeletion(string[] selected)
    {
        this.selected=selected;
        protectedPaths=new[]{Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Path.GetDirectoryName(ShellRegistry.AppPath)!}
            .Where(path=>!string.IsNullOrWhiteSpace(path)).Select(TransferSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static PermanentDeletion Freeze(IEnumerable<string> paths)
    {
        var selected=paths.Select(TransferSafety.Normalize).ToArray();
        if(selected.Length is 0 or >10000||selected.Sum(path=>(long)path.Length)>512*1024)throw new IOException(L.T("删除选区为空或过大。"));
        var plan=new PermanentDeletion(selected);
        foreach(var path in selected)
        {
            if(selected.Any(other=>other!=path&&Contains(path,other)))throw new IOException(L.T("删除选区互相包含，请只选顶层项目。"));
            plan.Capture(path);
        }
        return plan;
    }
    private static bool Contains(string parent,string child)=>child.Equals(parent,StringComparison.OrdinalIgnoreCase)||child.StartsWith(Path.TrimEndingDirectorySeparator(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private void Guard(string path)
    {
        if(path.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("不能永久删除整盘或共享根。"));
        TransferSafety.RequireNoLinks(path);
        if(protectedPaths.Any(protectedPath=>Contains(protectedPath,path)||Contains(path,protectedPath)))throw new IOException(L.T("不能永久删除受保护的系统或程序目录：")+path);
    }
    private Stamp Read(string path)
    {
        Guard(path);
        using var handle=CreateFile(TransferSafety.NativePath(path),0,7,0,3,0x02200000,0);
        if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var info))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),L.T("无法核实待删除项目：")+path);
        var text=new System.Text.StringBuilder(32768);var count=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);
        if(count==0||count>=text.Capacity)throw new IOException(L.T("无法解析待删除项目的实际位置：")+path);
        var physical=text.ToString();
        physical=physical.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)?@"\\"+physical[8..]:physical.StartsWith(@"\\?\",StringComparison.Ordinal)?physical[4..]:physical;
        if(protectedPaths.Any(protectedPath=>Contains(protectedPath,physical)||Contains(physical,protectedPath)))throw new IOException(L.T("路径别名指向受保护目录：")+path);
        var id=((ulong)info.HighId<<32)|info.LowId;
        if(id==0||(info.Attributes&(uint)FileAttributes.ReparsePoint)!=0)throw new IOException(L.T("无法验证链接自身的删除范围，未删除：")+path);
        bool directory=(info.Attributes&(uint)FileAttributes.Directory)!=0;
        if(directory&&GetFileInformationByHandleEx(handle,23,out var sensitive,4)&&(sensitive&1)!=0)throw new IOException(L.T("暂不支持区分大小写目录的永久删除：")+path);
        return new(path,info.Volume,id,directory?0:((long)info.HighSize<<32)|info.LowSize,((long)info.Write.dwHighDateTime<<32)|(uint)info.Write.dwLowDateTime,directory);
    }
    private void Capture(string path)
    {
        if(entries.Count>=100000)throw new IOException(L.T("本次删除超过 100000 个项目，请拆分选区。"));
        var stamp=Read(path);
        if(!entries.TryAdd(path,stamp))throw new IOException(L.T("删除选区重复或存在大小写歧义。"));
        if(stamp.Directory)foreach(var child in Directory.EnumerateFileSystemEntries(path))Capture(child);
    }
    private void ValidateFrozen()
    {
        var children=entries.Keys.GroupBy(Path.GetDirectoryName,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key!,group=>group.ToHashSet(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase);
        foreach(var stamp in entries.Values)
        {
            if(Read(stamp.Path)!=stamp)throw new IOException(L.T("确认期间项目已变化，未开始删除：")+stamp.Path);
            if(stamp.Directory)
            {
                var actual=Directory.EnumerateFileSystemEntries(stamp.Path).ToArray();
                var frozen=children.GetValueOrDefault(stamp.Path)??new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if(!frozen.SetEquals(actual))throw new IOException(L.T("确认期间目录内容已变化，未开始删除：")+stamp.Path);
            }
        }
    }
    public DeletionResult ExecuteConfirmed(CancellationToken cancellation=default)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException(L.T("Shell 删除须在 STA 线程执行。"));
        ValidateFrozen();cancellation.ThrowIfCancellationRequested();
        var operation=(IFileOperationDelete)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
        var sink=new Sink(this,cancellation);var items=new List<IShellItemContext>();uint cookie=0;bool advised=false;
        try
        {
            // Explicitly excludes FOF_ALLOWUNDO, FOFX_ADDUNDORECORD and RECYCLEONDELETE.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x00102414));
            Marshal.ThrowExceptionForHR(operation.Advise(sink,out cookie));advised=true;
            foreach(var path in selected)
            {
                var iid=typeof(IShellItemContext).GUID;Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path,0,ref iid,out var item));
                items.Add(item);Marshal.ThrowExceptionForHR(operation.DeleteItem(item,null));
            }
            var result=operation.PerformOperations();var abortedResult=operation.GetAnyOperationsAborted(out var aborted);
            if(result<0)sink.Errors.Add(L.F("Shell 删除失败：0x{0:X8}", result));
            if(abortedResult<0)sink.Errors.Add(L.F("无法取得取消状态：0x{0:X8}", abortedResult));
            if(sink.Finished<0)sink.Errors.Add(L.F("Shell 结束状态：0x{0:X8}", sink.Finished));
            bool complete=result>=0&&abortedResult>=0&&!aborted&&!cancellation.IsCancellationRequested&&sink.Errors.Count==0&&
                selected.All(path=>sink.Results.Any(item=>item.Path.Equals(path,StringComparison.OrdinalIgnoreCase)&&IsFinishedStatus(item.HResult)))&&
                entries.Keys.All(path=>VerifiedAbsent(path,sink.Errors));
            return new(complete,aborted||cancellation.IsCancellationRequested,sink.Results.ToArray(),sink.Errors.ToArray());
        }
        finally{if(advised)operation.Unadvise(cookie);foreach(var item in items)Marshal.ReleaseComObject(item);Marshal.ReleaseComObject(operation);GC.KeepAlive(sink);}
    }
    private static string ItemPath(IShellItemContext item)
    {
        Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000,out var text));
        try{return TransferSafety.Normalize(Marshal.PtrToStringUni(text)??throw new IOException(L.T("Shell 项目没有文件系统路径。")));}finally{Marshal.FreeCoTaskMem(text);}
    }
    // SDK sherrors.h: recursive Shell processing reports these informational
    // statuses. They are not sufficient by themselves; every frozen path must
    // also be absent after PerformOperations, with no error or abort.
    private static bool IsFinishedStatus(int result)=>result is 0 or 0x00270003 or 0x00270008 or 0x0027000a;
    private static bool VerifiedAbsent(string path,List<string> errors)
    {
        if(GetFileAttributes(TransferSafety.NativePath(path))!=uint.MaxValue)return false;
        var code=Marshal.GetLastWin32Error();if(code is 2 or 3)return true;
        errors.Add(L.F("无法核实删除后状态：{0}（Win32 {1}），不能视为完成。", path, code));return false;
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]private sealed class Sink(PermanentDeletion plan,CancellationToken cancellation):IFileOperationSink
    {
        public readonly List<DeletionItemResult> Results=new();public readonly List<string> Errors=new();public int Finished;
        public int StartOperations()=>cancellation.IsCancellationRequested?unchecked((int)0x800704c7):0;
        public int FinishOperations(int result){Finished=result;return 0;}
        public int PreDeleteItem(uint flags,IShellItemContext item)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();var path=ItemPath(item);
                if(!plan.entries.TryGetValue(path,out var frozen))throw new IOException(L.T("删除项目超出确认范围：")+path);
                var current=plan.Read(path);
                if(current.Volume!=frozen.Volume||current.Id!=frozen.Id||current.Directory!=frozen.Directory||!frozen.Directory&&current!=frozen)
                    throw new IOException(L.T("删除项目已变化：")+path);
                return 0;
            }
            catch(Exception error){Errors.Add(error.Message);return unchecked((int)0x800704c7);}
        }
        public int PostDeleteItem(uint flags,IShellItemContext item,int result,IShellItemContext? newlyCreated)
        {try{var path=ItemPath(item);Results.Add(new(path,result));if(!IsFinishedStatus(result))Errors.Add(L.F("未删除：{0}（0x{1:X8}）", path, result));}catch(Exception error){Errors.Add(error.Message);}return 0;}
        public int PreRenameItem(uint f,IShellItemContext i,string n)=>unchecked((int)0x80004001);
        public int PostRenameItem(uint f,IShellItemContext i,string n,int r,IShellItemContext? c)=>0;
        public int PreMoveItem(uint f,IShellItemContext i,IShellItemContext d,string n)=>unchecked((int)0x80004001);
        public int PostMoveItem(uint f,IShellItemContext i,IShellItemContext d,string n,int r,IShellItemContext? c)=>0;
        public int PreCopyItem(uint f,IShellItemContext i,IShellItemContext d,string n)=>unchecked((int)0x80004001);
        public int PostCopyItem(uint f,IShellItemContext i,IShellItemContext d,string n,int r,IShellItemContext? c)=>0;
        public int PreNewItem(uint f,IShellItemContext d,string n)=>unchecked((int)0x80004001);
        public int PostNewItem(uint f,IShellItemContext d,string n,string t,uint a,int r,IShellItemContext? c)=>0;
        public int UpdateProgress(uint total,uint done)=>cancellation.IsCancellationRequested?unchecked((int)0x800704c7):0;
        public int ResetTimer()=>0;public int PauseTimer()=>0;public int ResumeTimer()=>0;
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInfo{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;public uint Volume,HighSize,LowSize,Links,HighId,LowId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint mode,uint flags,nint template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out uint info,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,System.Text.StringBuilder text,uint size,uint flags);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string name,nint context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IShellItemContext item);
}

[ComImport,Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationDelete
{
    [PreserveSig]int Advise(IFileOperationSink sink,out uint cookie);[PreserveSig]int Unadvise(uint cookie);
    [PreserveSig]int SetOperationFlags(uint flags);[PreserveSig]int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)]string message);
    [PreserveSig]int SetProgressDialog(nint dialog);[PreserveSig]int SetProperties(nint properties);[PreserveSig]int SetOwnerWindow(nint owner);
    [PreserveSig]int ApplyPropertiesToItem(IShellItemContext item);[PreserveSig]int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)]object items);
    [PreserveSig]int RenameItem(IShellItemContext item,[MarshalAs(UnmanagedType.LPWStr)]string name,IFileOperationSink? sink);
    [PreserveSig]int RenameItems([MarshalAs(UnmanagedType.IUnknown)]object items,[MarshalAs(UnmanagedType.LPWStr)]string name);
    [PreserveSig]int MoveItem(IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string? name,IFileOperationSink? sink);
    [PreserveSig]int MoveItems([MarshalAs(UnmanagedType.IUnknown)]object items,IShellItemContext destination);
    [PreserveSig]int CopyItem(IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string? name,IFileOperationSink? sink);
    [PreserveSig]int CopyItems([MarshalAs(UnmanagedType.IUnknown)]object items,IShellItemContext destination);
    [PreserveSig]int DeleteItem(IShellItemContext item,IFileOperationSink? sink);[PreserveSig]int DeleteItems([MarshalAs(UnmanagedType.IUnknown)]object items);
    [PreserveSig]int NewItem(IShellItemContext destination,uint attributes,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string? template,IFileOperationSink? sink);
    [PreserveSig]int PerformOperations();[PreserveSig]int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
}
[ComVisible(true),Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFileOperationSink
{
    [PreserveSig]int StartOperations();[PreserveSig]int FinishOperations(int result);
    [PreserveSig]int PreRenameItem(uint flags,IShellItemContext item,[MarshalAs(UnmanagedType.LPWStr)]string name);
    [PreserveSig]int PostRenameItem(uint flags,IShellItemContext item,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItemContext? created);
    [PreserveSig]int PreMoveItem(uint flags,IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name);
    [PreserveSig]int PostMoveItem(uint flags,IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItemContext? created);
    [PreserveSig]int PreCopyItem(uint flags,IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name);
    [PreserveSig]int PostCopyItem(uint flags,IShellItemContext item,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItemContext? created);
    [PreserveSig]int PreDeleteItem(uint flags,IShellItemContext item);[PreserveSig]int PostDeleteItem(uint flags,IShellItemContext item,int result,IShellItemContext? created);
    [PreserveSig]int PreNewItem(uint flags,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name);
    [PreserveSig]int PostNewItem(uint flags,IShellItemContext destination,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string template,uint attributes,int result,IShellItemContext? created);
    [PreserveSig]int UpdateProgress(uint total,uint done);[PreserveSig]int ResetTimer();[PreserveSig]int PauseTimer();[PreserveSig]int ResumeTimer();
}
