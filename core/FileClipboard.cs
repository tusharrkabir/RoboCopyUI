using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject=System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RoboCopyUI;

public enum ClipboardOrigin { Application, Explorer, Other }
public sealed class FileClipboardSnapshot : IDisposable
{
    private readonly string[] paths;
    internal ComDataObject? Data { get; private set; }
    public string[] Sources=>paths.ToArray();
    public bool IsMove { get; }
    public uint Sequence { get; }
    public ClipboardOrigin Origin { get; }
    public uint PreferredEffect { get; }
    internal FileClipboardSnapshot(string[] sources,bool move,uint sequence,ClipboardOrigin origin,ComDataObject data,uint preferredEffect)
    {paths=sources;IsMove=move;Sequence=sequence;Origin=origin;Data=data;PreferredEffect=preferredEffect;}
    public void Dispose(){if(Data!=null){Marshal.ReleaseComObject(Data);Data=null;}}
}

/// <summary>Real OLE file clipboard. Only known Shell/owned sources may request verified moves.</summary>
public static class ClipboardManager
{
    private static bool initialized;
    private static int ownerThread;
    private static ComDataObject? owned;
    private static Guid ownedToken;
    private const string Marker="RoboCopyUI.FileIntent.v1";
    private static readonly Guid DataObjectId=new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid DataHandler=new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static void EnsureSta()
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException(L.T("剪贴板必须在 STA 线程中操作。"));
        if(initialized&&ownerThread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException(L.T("文件剪贴板由固定的 STA 线程处理。"));
        if(!initialized){Marshal.ThrowExceptionForHR(OleInitialize(nint.Zero));initialized=true;ownerThread=Environment.CurrentManagedThreadId;}
    }
    private static FORMATETC Format(string name)=>Format(unchecked((short)RegisterClipboardFormat(name)));
    private static FORMATETC Format(short id)=>new(){cfFormat=id,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
    private static void Retry(Action action)
    {
        for(int i=0;;i++)try{action();return;}catch(COMException e)when(i<2&&e.HResult is unchecked((int)0x800401D0) or unchecked((int)0x80010001)){Thread.Sleep(30);}
    }
    public static void SetCopy(IEnumerable<string> paths)=>Set(paths,false);
    public static void SetCut(IEnumerable<string> paths)=>Set(paths,true);
    private static void Set(IEnumerable<string> selected,bool move)
    {
        EnsureSta();var paths=selected.Select(TransferSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(paths.Length is 0 or >10000||paths.Sum(p=>(long)p.Length)>512*1024)throw new IOException(L.T("文件剪贴板选区为空或过大。"));
        if(paths.Any(p=>!File.Exists(p)&&!Directory.Exists(p)))throw new IOException(L.T("选中的部分项目已不存在，未改动剪贴板。"));
        if(paths.Any(p=>p.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(p)!),StringComparison.OrdinalIgnoreCase)))throw new IOException(L.T("整盘或共享根只能作为粘贴目标。"));
        foreach(var path in paths)TransferSafety.RequireNoLinks(path);
        if(paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)throw new IOException(L.T("一次复制或剪切须来自同一文件夹，请分别选择。"));
        var pidls=new List<nint>();IShellItemArrayContext? array=null;ComDataObject? data=null;
        try
        {
            foreach(var path in paths){Marshal.ThrowExceptionForHR(SHParseDisplayName(path,nint.Zero,out var pidl,0,out _));pidls.Add(pidl);}
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Count,pidls.ToArray(),out array));
            var handler=DataHandler;var id=DataObjectId;
            Marshal.ThrowExceptionForHR(array.BindToHandler(nint.Zero,ref handler,ref id,out var pointer));
            try{data=(ComDataObject)Marshal.GetObjectForIUnknown(pointer);}finally{Marshal.Release(pointer);}
            SetDword(data,"Preferred DropEffect",move?2u:1u);
            var token=Guid.NewGuid();SetBytes(data,Marker,token.ToByteArray());
            Retry(()=>Marshal.ThrowExceptionForHR(OleSetClipboard(data)));
            var previous=owned;owned=data;data=null;ownedToken=token;
            if(previous!=null)Marshal.ReleaseComObject(previous);
        }
        finally{if(data!=null)Marshal.ReleaseComObject(data);if(array!=null)Marshal.ReleaseComObject(array);foreach(var pidl in pidls)Marshal.FreeCoTaskMem(pidl);}
    }
    public static FileClipboardSnapshot Read()
    {
        EnsureSta();
        for(int attempt=0;attempt<3;attempt++)
        {
            var sequence=GetClipboardSequenceNumber();var owner=GetClipboardOwner();ComDataObject? data=null;
            try
            {
                Retry(()=>Marshal.ThrowExceptionForHR(OleGetClipboard(out data)));
                var paths=ReadPaths(data!);var preferred=ReadDword(data!,"Preferred DropEffect")??1;
                // Explorer's Copy can offer COPY|LINK (5). Choose the offered copy;
                // never infer a move from an ambiguous COPY|MOVE combination.
                if(preferred is not (1 or 2 or 5))throw new IOException(L.F("不支持链接或不明确的文件剪贴板意图（0x{0:X}）。", preferred));
                var origin=ClipboardOrigin.Other;
                if(owned!=null&&OleIsCurrentClipboard(owned)==0&&ReadBytes(data!,Marker,16) is byte[] bytes&&new Guid(bytes)==ownedToken)origin=ClipboardOrigin.Application;
                else if(IsExplorer(owner)&&HasFormat(data!,"Shell IDList Array"))origin=ClipboardOrigin.Explorer;
                if(sequence!=GetClipboardSequenceNumber()||owner!=GetClipboardOwner())continue;
                if(preferred==2&&origin==ClipboardOrigin.Other)throw new IOException(L.T("无法确认此剪切的清理责任。请重新在资源管理器剪切，或改用复制。"));
                var snapshot=new FileClipboardSnapshot(paths,preferred==2,sequence,origin,data!,preferred);data=null;return snapshot;
            }
            catch(COMException error)when(attempt<2&&error.HResult is unchecked((int)0x800401D0) or unchecked((int)0x80010001))
            {
                // OLE can return a proxy successfully while GetData itself is busy.
                // Re-read a coherent generation; never keep a half-read selection.
                Thread.Sleep(30);
            }
            finally{if(data!=null)Marshal.ReleaseComObject(data);}
        }
        throw new IOException(L.T("剪贴板正在变化，请重试粘贴。"));
    }
    private static bool IsExplorer(nint window)
    {
        if(window==nint.Zero)return false;
        try{GetWindowThreadProcessId(window,out var pid);using var process=Process.GetProcessById((int)pid);return process.SessionId==Process.GetCurrentProcess().SessionId&&string.Equals(process.MainModule?.FileName,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),StringComparison.OrdinalIgnoreCase);}catch{return false;}
    }
    public static string Complete(FileClipboardSnapshot snapshot,TransferAttemptResult result)
    {
        EnsureSta();
        if(!snapshot.IsMove||result.State!=CopyState.Completed||!result.IsMove)return L.T("剪贴板保持原状。");
        if(snapshot.Data==null)throw new ObjectDisposedException(nameof(snapshot));
        if(snapshot.Sequence!=GetClipboardSequenceNumber())return L.T("任务已完成；剪贴板已更换，未修改新内容。");
        // Target has already removed only verified sources. Never request source-side deletion.
        // SetData's successful HRESULT is the Shell protocol acknowledgement. OLE's
        // clipboard wrapper may cache GetData formats and cannot be used for read-back.
        SetDword(snapshot.Data,"Performed DropEffect",0);
        SetDword(snapshot.Data,"Logical Performed DropEffect",2);
        // Explorer's clipboard bridge can trigger delete-on-paste on MOVE even
        // after accepting Performed=NONE. Never grant that second deletion.
        // Logical=MOVE records the completed task; PasteSucceeded=NONE releases
        // the source's cut feedback without authorizing source-side cleanup.
        SetDword(snapshot.Data,"Paste Succeeded",0);
        if(!OpenClipboard(nint.Zero))return L.T("任务已完成；剪贴板占用，未清空。");
        try
        {
            if(snapshot.Sequence!=GetClipboardSequenceNumber())return L.T("任务已完成；剪贴板已变化，未清空。");
            if(!EmptyClipboard())throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{CloseClipboard();}
        return L.T("已完成验证移动并更新原剪切状态。");
    }
    public static void Shutdown()
    {
        if(!initialized)return;EnsureSta();
        try{if(owned!=null&&OleIsCurrentClipboard(owned)==0)Marshal.ThrowExceptionForHR(OleFlushClipboard());}
        finally{if(owned!=null){Marshal.ReleaseComObject(owned);owned=null;}OleUninitialize();initialized=false;ownerThread=0;}
    }
    private static string[] ReadPaths(ComDataObject data)
    {
        var format=Format(15);data.GetData(ref format,out var medium);
        try
        {
            if(medium.tymed!=TYMED.TYMED_HGLOBAL)throw new IOException(L.T("不支持此文件剪贴板格式。"));
            ValidateDropBlock(medium.unionmember);
            var count=DragQueryFile(medium.unionmember,uint.MaxValue,null!,0);
            if(count is 0 or >10000)throw new IOException(L.T("文件剪贴板选区为空或过大。"));
            var paths=new List<string>();long total=0;
            for(uint i=0;i<count;i++){var length=DragQueryFile(medium.unionmember,i,null!,0);total+=length;if(length is 0 or >32767||total>512*1024)throw new IOException(L.T("文件剪贴板路径过大。"));var text=new StringBuilder((int)length+1);if(DragQueryFile(medium.unionmember,i,text,length+1)!=length)throw new IOException(L.T("文件剪贴板读取不完整。"));paths.Add(TransferSafety.Normalize(text.ToString()));}
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally{ReleaseStgMedium(ref medium);}
    }
    private static bool HasFormat(ComDataObject data,string name){var format=Format(name);return data.QueryGetData(ref format)==0;}
    private static void ValidateDropBlock(nint memory)
    {
        var size=GlobalSize(memory);if(size<20||size>4*1024*1024)throw new IOException(L.T("文件剪贴板数据大小无效。"));
        var pointer=GlobalLock(memory);if(pointer==nint.Zero)throw new IOException(L.T("无法读取文件剪贴板数据。"));
        try
        {
            var offset=Marshal.ReadInt32(pointer);var wide=Marshal.ReadInt32(pointer,16);
            if(offset<20||offset>checked((int)size)-2||wide is not (0 or 1)||wide==1&&offset%2!=0)throw new IOException(L.T("文件剪贴板数据头无效。"));
            var step=wide==1?2:1;bool terminated=false;
            for(int index=offset;index+step*2<=(int)size;index+=step)
                if((wide==1?Marshal.ReadInt16(pointer,index)==0:Marshal.ReadByte(pointer,index)==0)&&
                   (wide==1?Marshal.ReadInt16(pointer,index+step)==0:Marshal.ReadByte(pointer,index+step)==0)){terminated=true;break;}
            if(!terminated)throw new IOException(L.T("文件剪贴板缺少结束标记。"));
        }
        finally{GlobalUnlock(memory);}
    }
    private static uint? ReadDword(ComDataObject data,string name){var bytes=ReadBytes(data,name,4);return bytes==null?null:System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);}
    private static byte[]? ReadBytes(ComDataObject data,string name,int length)
    {
        var format=Format(name);if(data.QueryGetData(ref format)!=0)return null;data.GetData(ref format,out var medium);
        try{if(medium.tymed!=TYMED.TYMED_HGLOBAL||GlobalSize(medium.unionmember)<(nuint)length)return null;var pointer=GlobalLock(medium.unionmember);if(pointer==nint.Zero)throw new IOException(L.T("无法读取文件剪贴板。"));try{var bytes=new byte[length];Marshal.Copy(pointer,bytes,0,length);return bytes;}finally{GlobalUnlock(medium.unionmember);}}
        finally{ReleaseStgMedium(ref medium);}
    }
    private static void SetDword(ComDataObject data,string name,uint value){var bytes=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes,value);SetBytes(data,name,bytes);}
    private static void SetBytes(ComDataObject data,string name,byte[] bytes)
    {
        var memory=GlobalAlloc(0x42,(nuint)bytes.Length);if(memory==nint.Zero)throw new OutOfMemoryException();
        var medium=new STGMEDIUM{tymed=TYMED.TYMED_HGLOBAL,unionmember=memory};
        try{var pointer=GlobalLock(memory);if(pointer==nint.Zero)throw new IOException(L.T("无法写入文件剪贴板。"));try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}var format=Format(name);data.SetData(ref format,ref medium,true);}
        catch{ReleaseStgMedium(ref medium);throw;}
    }
    [DllImport("ole32.dll")]private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]private static extern void OleUninitialize();
    [DllImport("ole32.dll")]private static extern int OleSetClipboard(ComDataObject? data);
    [DllImport("ole32.dll")]private static extern int OleGetClipboard(out ComDataObject? data);
    [DllImport("ole32.dll")]private static extern int OleIsCurrentClipboard(ComDataObject data);
    [DllImport("ole32.dll")]private static extern int OleFlushClipboard();
    [DllImport("ole32.dll")]private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string path,nint bind,out nint pidl,uint attributes,out uint result);
    [DllImport("shell32.dll")]private static extern int SHCreateShellItemArrayFromIDLists(uint count,nint[] pidls,[MarshalAs(UnmanagedType.Interface)]out IShellItemArrayContext array);
    [DllImport("shell32.dll",EntryPoint="DragQueryFileW",CharSet=CharSet.Unicode)]private static extern uint DragQueryFile(nint drop,uint index,StringBuilder text,uint size);
    [DllImport("user32.dll",EntryPoint="RegisterClipboardFormatW",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EmptyClipboard();
    [DllImport("kernel32.dll")]private static extern nint GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll")]private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")]private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(nint memory);
}
