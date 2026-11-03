using RoboCopyUI;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
if(args[0]=="--native-sink")
{
 using var sink=new DesktopInstance();if(!sink.IsPrimary)throw new IOException("Existing host; do not probe against it");
 sink.Listen(request=>{lock(typeof(DesktopRequest)){File.AppendAllText(args[1],JsonSerializer.Serialize(request)+Environment.NewLine);}return Task.FromResult(new DesktopResponse(request.Id,true,"harmless selection probe; no file operations"));});
 File.WriteAllText(args[1]+".ready",Process.GetCurrentProcess().Id.ToString());
 Thread.Sleep(TimeSpan.FromMinutes(15));return 0;
}
if(args[0]=="--client")
{
 using var client=new DesktopInstance();
 if(client.IsPrimary)throw new Exception("Duplicate primary host");
 var request=JsonSerializer.Deserialize<DesktopRequest>(File.ReadAllText(args[1]))!;
 var response=client.Send(request).GetAwaiter().GetResult();
 if(args.Length==3)File.WriteAllText(args[2],JsonSerializer.Serialize(response));
 return response.Accepted&&response.Id==request.Id?0:1;
}
var root=Path.GetFullPath(args.Single());if(Directory.Exists(root))throw new IOException("Fresh root required");Directory.CreateDirectory(root);
using var primary=new DesktopInstance();if(!primary.IsPrimary)throw new IOException("Existing host; do not test against it");
var received=new ConcurrentQueue<DesktopRequest>();
primary.Listen(r=>{received.Enqueue(r);return Task.FromResult(new DesktopResponse(r.Id,true,"test admission only; no file actions"));});
var checks=new Dictionary<string,bool>();var errors=new List<string>();
void Check(string name,Func<bool> action){try{checks[name]=action();}catch(Exception e){checks[name]=false;errors.Add(name+": "+e);}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
bool Send(DesktopRequest request)
{
 var file=Path.Combine(root,request.Id+".json");File.WriteAllText(file,JsonSerializer.Serialize(request));
 var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,ArgumentList={typeof(Program).Assembly.Location,"--client",file}};
 using var child=Process.Start(start)!;return child.WaitForExit(10000)&&child.ExitCode==0;
}
Check("second-process-forwards-single-instance",()=>Send(new(1,Guid.NewGuid(),"tasks",[],null)));
Check("file-actions-cannot-be-invoked-over-ipc",()=>new[]{"paste","transfer","delete"}.All(action=>{
 try{DesktopInstance.Validate(new(1,Guid.NewGuid(),action,[Path.Combine(root,"selection.txt")],root));return false;}
 catch(IOException){return true;}
}));
Check("settings-without-traditional-menu",()=>Send(new(1,Guid.NewGuid(),"settings",[],null)));
Check("unknown-action-rejected",()=>{try{DesktopInstance.Validate(new(1,Guid.NewGuid(),"cmd.exe",[],null));return false;}catch(IOException){return true;}});
Check("missing-paste-target-rejected",()=>{try{DesktopInstance.Validate(new(1,Guid.NewGuid(),"paste",[],null));return false;}catch(IOException){return true;}});
Check("oversized-packet-rejected-and-listener-recovers",()=>
{
 using(var pipe=new NamedPipeClientStream(".",primary.PipeName,PipeDirection.InOut,PipeOptions.None,TokenImpersonationLevel.Identification))
 {pipe.Connect(3000);pipe.Write(BitConverter.GetBytes(int.MaxValue));pipe.Flush();var read=Task.Run(()=>pipe.ReadByte());if(!read.Wait(7000)||read.Result!=-1)return false;}
 return Send(new(1,Guid.NewGuid(),"ping",[],null));
});
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,errors,received=received.ToArray()},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");return checks.All(c=>c.Value)?0:1;
