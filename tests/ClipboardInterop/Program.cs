using RoboCopyUI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

internal static class Program
{
 [STAThread]static int Main(string[] args)
 {
  if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
  var mode=args[0];var path=TransferSafety.Normalize(args[1]);var output=Path.GetFullPath(args[2]);
  const string root=@"C:\RoboCopyUI-ClipboardInterop";
  if(!path.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Isolated interop fixture only");
  try
  {
   if(mode.StartsWith("publish-",StringComparison.Ordinal))
   {
    if(mode.Contains("cut"))ClipboardManager.SetCut([path]);else ClipboardManager.SetCopy([path]);
    using(var snapshot=ClipboardManager.Read())Write(new{mode,pid=Environment.ProcessId,ready=true,snapshot.IsMove,snapshot.Origin,snapshot.PreferredEffect,sources=snapshot.Sources});
    if(!mode.EndsWith("-exit",StringComparison.Ordinal))
    {
     var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
     timer.Tick+=(_,_)=>{if(File.Exists(output+".stop")){timer.Stop();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);}};
     timer.Start();Dispatcher.Run();
    }
    ClipboardManager.Shutdown();return 0;
   }
   if(mode=="inspect")
   {
    using var snapshot=ClipboardManager.Read();
    Write(new{mode,snapshot.IsMove,snapshot.Origin,snapshot.PreferredEffect,sources=snapshot.Sources});
    return 0;
   }
   if(mode is not ("consume" or "consume-recreate"))throw new IOException("Unknown test mode");
   using var source=ClipboardManager.Read();
   if(source.Sources.Any(p=>!p.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase)))throw new IOException("Clipboard outside interop fixture");
   var originals=source.Sources.ToDictionary(p=>p,p=>File.Exists(p)?Hash(p):null);
   using var engine=new RobocopyEngine();var state=CopyState.Scanning;var errors=new List<string>();
   engine.StatusChanged+=(_,s)=>state=s;engine.Error+=(_,s)=>errors.Add(s);
   engine.Start(source.Sources.ToList(),path,new RobocopyOptions{IsMove=source.IsMove,FullPreservation=false,AutoSelectThreadCount=false,ThreadCount=2,RetryCount=0,WaitSeconds=0});
   var result=new TransferAttemptResult(state,false,errors.ToArray(),engine.Results.ToArray(),source.IsMove);
   var recreated=new List<string>();
   if(mode=="consume-recreate"&&state==CopyState.Completed&&source.IsMove)
    foreach(var original in originals.Where(p=>p.Value!=null)){using var file=new FileStream(original.Key,FileMode.CreateNew,FileAccess.Write);using var writer=new StreamWriter(file);writer.Write("New source created AFTER verified target cleanup");recreated.Add(original.Key);}
   var notice=ClipboardManager.Complete(source,result);
   var contentMatches=originals.Where(p=>p.Value!=null).All(p=>File.Exists(Path.Combine(path,Path.GetFileName(p.Key)))&&Hash(Path.Combine(path,Path.GetFileName(p.Key)))==p.Value);
   var recreatedPreserved=recreated.All(p=>File.Exists(p)&&File.ReadAllText(p)=="New source created AFTER verified target cleanup");
   Write(new{mode,state=state.ToString(),source.IsMove,source.PreferredEffect,origin=source.Origin.ToString(),sources=source.Sources,sourceAfter=source.Sources.Select(p=>new{path=p,exists=File.Exists(p)||Directory.Exists(p)}),target=path,contentMatches,recreated,recreatedPreserved,notice,errors,processes=engine.Results});
   source.Dispose();ClipboardManager.Shutdown();return state==CopyState.Completed&&contentMatches&&recreatedPreserved?0:1;
  }
  catch(Exception error){Write(new{mode,error=error.ToString()});return 1;}
  void Write(object value)=>File.WriteAllText(output,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
 }
 private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
}
