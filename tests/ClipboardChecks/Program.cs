using RoboCopyUI;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject=System.Runtime.InteropServices.ComTypes.IDataObject;

internal static class Program
{
 [STAThread]static int Main(string[] args)
 {
  if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
  if(args.Length==2&&args[0]=="--hold-clipboard")
  {
   if(!OpenClipboard(0))return 2;
   try{File.WriteAllText(args[1],"ready");Thread.Sleep(1200);}finally{CloseClipboard();}
   return 0;
  }
  var root=Path.GetFullPath(args.Single());if(Directory.Exists(root))throw new IOException("Fresh root required");Directory.CreateDirectory(root);
  var checks=new Dictionary<string,bool>();var logs=new List<object>();
  void Check(string name,Func<bool> test){try{checks[name]=test();}catch(Exception e){checks[name]=false;logs.Add(new{name,error=e.ToString()});}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
  string Put(string directory,string name){Directory.CreateDirectory(directory);var file=Path.Combine(directory,name);File.WriteAllText(file,"clipboard fixture "+name);return file;}
  TransferAttemptResult Transfer(string[] sources,string target,bool move){using var engine=new RobocopyEngine();var state=CopyState.Scanning;var errors=new List<string>();engine.StatusChanged+=(_,s)=>state=s;engine.Error+=(_,s)=>errors.Add(s);engine.Start(sources.ToList(),target,new RobocopyOptions{IsMove=move,FullPreservation=false,AutoSelectThreadCount=false,ThreadCount=2,RetryCount=0,WaitSeconds=0});var result=new TransferAttemptResult(state,false,errors.ToArray(),engine.Results.ToArray(),move);logs.Add(result);return result;}
  var source=Path.Combine(root,"source");var first=Put(source,"中文 空格.txt");var second=Put(source,"second.txt");var empty=Path.Combine(source,"empty");Directory.CreateDirectory(empty);
  Check("native-file-list-and-copy-intent",()=>{ClipboardManager.SetCopy([first,second,empty]);using var snapshot=ClipboardManager.Read();return snapshot.Origin==ClipboardOrigin.Application&&!snapshot.IsMove&&snapshot.Sources.SequenceEqual(new[]{first,second,empty});});
  Check("copy-can-paste-twice",()=>{using var snapshot=ClipboardManager.Read();var one=Path.Combine(root,"copy-one");var two=Path.Combine(root,"copy-two");var r1=Transfer(snapshot.Sources,one,false);ClipboardManager.Complete(snapshot,r1);var r2=Transfer(snapshot.Sources,two,false);ClipboardManager.Complete(snapshot,r2);using var current=ClipboardManager.Read();return r1.State==CopyState.Completed&&r2.State==CopyState.Completed&&File.Exists(first)&&File.Exists(Path.Combine(one,"中文 空格.txt"))&&File.Exists(Path.Combine(two,"中文 空格.txt"))&&current.Sources.Length==3;});
  Check("new-selection-replaces-no-time-window",()=>{ClipboardManager.SetCopy([second]);using var snapshot=ClipboardManager.Read();return snapshot.Sources.SequenceEqual(new[]{second});});
  Check("verified-move-completes-original-cut",()=>{var file=Put(Path.Combine(root,"move-source"),"move.txt");ClipboardManager.SetCut([file]);using var snapshot=ClipboardManager.Read();var target=Path.Combine(root,"move-target");Put(target,"extra.txt");var result=Transfer(snapshot.Sources,target,true);var notice=ClipboardManager.Complete(snapshot,result);logs.Add(new{notice});bool emptyClipboard;try{using var current=ClipboardManager.Read();emptyClipboard=false;}catch{emptyClipboard=true;}return result.State==CopyState.Completed&&!File.Exists(file)&&File.Exists(Path.Combine(target,"move.txt"))&&File.Exists(Path.Combine(target,"extra.txt"))&&emptyClipboard;});
  Check("failed-cut-keeps-source-and-clipboard",()=>{var file=Put(Path.Combine(root,"failed-source"),"keep.txt");var target=Put(root,"not-directory");ClipboardManager.SetCut([file]);using var snapshot=ClipboardManager.Read();var result=Transfer(snapshot.Sources,target,true);ClipboardManager.Complete(snapshot,result);using var current=ClipboardManager.Read();return result.State==CopyState.Error&&File.Exists(file)&&current.IsMove&&current.Sources.SequenceEqual(new[]{file});});
  Check("ordinary-copy-fallback-does-not-complete-cut",()=>{using var snapshot=ClipboardManager.Read();var result=Transfer(snapshot.Sources,Path.Combine(root,"ordinary-target"),false);ClipboardManager.Complete(snapshot,result);using var current=ClipboardManager.Read();return result.State==CopyState.Completed&&File.Exists(snapshot.Sources[0])&&current.IsMove;});
  Check("later-clipboard-not-cleared",()=>{var file=Put(Path.Combine(root,"later-source"),"old.txt");ClipboardManager.SetCut([file]);using var old=ClipboardManager.Read();var result=Transfer(old.Sources,Path.Combine(root,"later-target"),true);ClipboardManager.SetCopy([second]);ClipboardManager.Complete(old,result);using var current=ClipboardManager.Read();return result.State==CopyState.Completed&&!File.Exists(file)&&!current.IsMove&&current.Sources.SequenceEqual(new[]{second});});
  Check("unknown-move-source-rejected",()=>{var data=new System.Windows.DataObject();data.SetFileDropList(new System.Collections.Specialized.StringCollection{first});data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes(2)));System.Windows.Clipboard.SetDataObject(data,true);try{using var snapshot=ClipboardManager.Read();return false;}catch(IOException){return File.Exists(first);}});
  Check("legacy-json-never-overrides-system-clipboard",()=>{var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RoboCopyUI");Directory.CreateDirectory(folder);var legacy=Path.Combine(folder,"clipboard.json");if(File.Exists(legacy))throw new IOException("Legacy fixture already exists");var json=JsonSerializer.Serialize(new{SourcePaths=new[]{first},IsCutMode=true});File.WriteAllText(legacy,json);System.Windows.Clipboard.SetText("new user clipboard");bool rejected=false;try{using var snapshot=ClipboardManager.Read();}catch{rejected=true;}return rejected&&File.ReadAllText(legacy)==json&&System.Windows.Clipboard.GetText()=="new user clipboard";});
  Check("root-cut-rejected-without-changing-clipboard",()=>{try{ClipboardManager.SetCut([@"C:\"]);return false;}catch(IOException){return System.Windows.Clipboard.GetText()=="new user clipboard";}});
  Check("non-sta-access-rejected",()=>{bool rejected=false;var thread=new Thread(()=>{try{using var snapshot=ClipboardManager.Read();}catch(InvalidOperationException){rejected=true;}});thread.SetApartmentState(ApartmentState.MTA);thread.Start();thread.Join();return rejected;});
  Check("busy-clipboard-is-bounded-and-recovers",()=>
  {
   ClipboardManager.SetCopy([first]);var signal=Path.Combine(root,"clipboard-held.ready");
   var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,ArgumentList={typeof(Program).Assembly.Location,"--hold-clipboard",signal}};
   using var child=System.Diagnostics.Process.Start(start)!;
   var ready=System.Diagnostics.Stopwatch.StartNew();while(!File.Exists(signal)&&!child.HasExited&&ready.ElapsedMilliseconds<5000)Thread.Sleep(10);
   if(!File.Exists(signal))return false;
   var elapsed=System.Diagnostics.Stopwatch.StartNew();bool rejected=false;
   try{using var snapshot=ClipboardManager.Read();}catch(COMException){rejected=true;}catch(IOException){rejected=true;}
   elapsed.Stop();if(!child.WaitForExit(5000))return false;
   using var recovered=ClipboardManager.Read();
   return rejected&&elapsed.ElapsedMilliseconds<1000&&child.ExitCode==0&&File.Exists(first)&&recovered.Sources.SequenceEqual(new[]{first});
  });
  Check("malformed-drop-blocks-rejected-without-source-change",()=>
  {
   var malformed=new List<byte[]>{new byte[4],new byte[24],new byte[24],new byte[24]};
   BitConverter.GetBytes(30).CopyTo(malformed[1],0);
   BitConverter.GetBytes(20).CopyTo(malformed[2],0);BitConverter.GetBytes(1).CopyTo(malformed[2],16);malformed[2][20]=65;malformed[2][22]=66;
   BitConverter.GetBytes(21).CopyTo(malformed[3],0);BitConverter.GetBytes(1).CopyTo(malformed[3],16);
   foreach(var bytes in malformed)
   {
    var data=new DropFixture(bytes);Marshal.ThrowExceptionForHR(OleSetClipboard(data));
    bool rejected=false;try{using var snapshot=ClipboardManager.Read();}catch(IOException){rejected=true;}
    // Do not leave a managed test object on the clipboard after this scope.
    Marshal.ThrowExceptionForHR(OleSetClipboard(null));GC.KeepAlive(data);
    if(!rejected||!File.Exists(first))return false;
   }
   return true;
  });
  try{ClipboardManager.Shutdown();}catch(Exception e){logs.Add(new{shutdown=e.ToString()});}
  Check("shell-actor-cut-lease-and-retry-lifecycle",()=>
  {
   using var shell=new ShellApartment();var tasks=new ClipboardTasks();
   return shell.Run(()=>
   {
    var file=Put(Path.Combine(root,"leased-source"),"lease.txt");ClipboardManager.SetCut([file]);
    using(var lease=tasks.Capture())
    {
     bool duplicate=false;try{using var another=tasks.Capture();}catch(InvalidOperationException){duplicate=true;}
     if(!duplicate)return false;
     lease.Complete(new(CopyState.Error,false,["fixture failure"],[],true));
     bool stillHeld=false;try{using var another=tasks.Capture();}catch(InvalidOperationException){stillHeld=true;}
     var fallback=Transfer(lease.Sources,Path.Combine(root,"leased-copy-target"),false);lease.Complete(fallback);
     if(!stillHeld||fallback.State!=CopyState.Completed||!File.Exists(file))return false;
    }
    using var retry=tasks.Capture();var moved=Transfer(retry.Sources,Path.Combine(root,"leased-move-target"),true);
    File.WriteAllText(file,"new source after verified move");retry.Complete(moved);retry.Complete(moved);
    return moved.State==CopyState.Completed&&File.ReadAllText(file)=="new source after verified move";
   }).GetAwaiter().GetResult();
  });
  File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,logs},new JsonSerializerOptions{WriteIndented=true}));
  Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");return checks.All(c=>c.Value)?0:1;
 }
 [DllImport("ole32.dll")]static extern int OleSetClipboard(ComDataObject? data);
 [DllImport("user32.dll")]static extern bool OpenClipboard(nint window);
 [DllImport("user32.dll")]static extern bool CloseClipboard();
 [ComVisible(true),ClassInterface(ClassInterfaceType.None)]sealed class DropFixture(byte[] bytes):ComDataObject
 {
  public void GetData(ref FORMATETC format,out STGMEDIUM medium)
  {
   if(format.cfFormat!=15)throw new COMException("Format not provided",unchecked((int)0x80040064));
   var memory=GlobalAlloc(0x42,(nuint)bytes.Length);if(memory==nint.Zero)throw new OutOfMemoryException();
   var pointer=GlobalLock(memory);try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}
   medium=new(){tymed=TYMED.TYMED_HGLOBAL,unionmember=memory};
  }
  public int QueryGetData(ref FORMATETC format)=>format.cfFormat==15?0:unchecked((int)0x80040064);
  public void GetDataHere(ref FORMATETC format,ref STGMEDIUM medium)=>throw new NotImplementedException();
  public int GetCanonicalFormatEtc(ref FORMATETC input,out FORMATETC output){output=input;output.ptd=0;return 0x00040130;}
  public void SetData(ref FORMATETC format,ref STGMEDIUM medium,bool release)=>throw new NotImplementedException();
  public IEnumFORMATETC EnumFormatEtc(DATADIR direction)=>direction==DATADIR.DATADIR_GET?new DropFormats():throw new NotImplementedException();
  public int DAdvise(ref FORMATETC format,ADVF flags,IAdviseSink sink,out int connection){connection=0;return unchecked((int)0x80040003);}
  public void DUnadvise(int connection)=>throw new NotImplementedException();
  public int EnumDAdvise(out IEnumSTATDATA? enumerator){enumerator=null;return unchecked((int)0x80040003);}
  [DllImport("kernel32.dll")]static extern nint GlobalAlloc(uint flags,nuint size);
  [DllImport("kernel32.dll")]static extern nint GlobalLock(nint memory);
  [DllImport("kernel32.dll")]static extern bool GlobalUnlock(nint memory);
 }
 [ComVisible(true),ClassInterface(ClassInterfaceType.None)]sealed class DropFormats:IEnumFORMATETC
 {
  private bool used;
  public int Next(int count,FORMATETC[] formats,int[]? fetched)
  {
   if(used||count==0){if(fetched?.Length>0)fetched[0]=0;return 1;}
   formats[0]=new(){cfFormat=15,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};used=true;
   if(fetched?.Length>0)fetched[0]=1;return count==1?0:1;
  }
  public int Skip(int count){if(count==0)return 0;var available=!used;used=true;return available&&count==1?0:1;}
  public int Reset(){used=false;return 0;}
  public void Clone(out IEnumFORMATETC other){other=new DropFormats{used=used};}
 }
}
