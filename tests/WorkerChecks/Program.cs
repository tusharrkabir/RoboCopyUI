using RoboCopyUI;
using System.Diagnostics;
using System.Text.Json;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
var root=Path.GetFullPath(args[0]);var ordinaryUi=args.Length==2&&args[1]=="--ordinary-ui";
var shellElevation=args.Length==2&&args[1]=="--shell-elevation";
var cancelBefore=args.Length==2&&args[1]=="--cancel-before";
if(Directory.Exists(root))throw new IOException("Fresh root required");Directory.CreateDirectory(root);
var source=Path.Combine(root,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"fixture.txt"),"worker fixture");
var target=ordinaryUi ? Path.Combine(@"R:\","RoboCopyUI-Ordinary-"+Guid.NewGuid().ToString("N")) : Path.Combine(root,"target");Directory.CreateDirectory(target);
var settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"RoboCopyUI","settings.json");
byte[]? before=File.Exists(settingsPath)?File.ReadAllBytes(settingsPath):null;
var request=TaskRetryRequest.Create([source],target,new RobocopyOptions{FullPreservation=true,IsMove=ordinaryUi,RetryCount=0,WaitSeconds=0,AutoSelectThreadCount=false,ThreadCount=2},"Dark");
var stored=TaskRequestStore.Save(request);
if(cancelBefore)TaskRequestStore.Cancel(request.Id);
var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
start.ArgumentList.Add(typeof(App).Assembly.Location);start.ArgumentList.Add("--elevated-transfer");start.ArgumentList.Add(stored.Path);start.ArgumentList.Add(stored.Hash);
using var process=shellElevation ? ElevationLauncher.Start(stored) : Process.Start(start)!;
if(!process.WaitForExit(ordinaryUi?300000:30000)){TaskRequestStore.Cancel(request.Id);throw new TimeoutException("Worker did not exit after completion");}
var receipt=TaskRequestStore.ReadReceipt(stored);
var checks=new Dictionary<string,bool>{["worker-exit-zero"]=process.ExitCode==0,["bound-terminal-receipt"]=receipt?.State==(cancelBefore?CopyState.Cancelled:CopyState.Completed)&&receipt.FullPreservation==!ordinaryUi,["copied-content-or-cancelled-before-copy"]=cancelBefore?!File.Exists(Path.Combine(target,"source","fixture.txt")):File.Exists(Path.Combine(target,"source","fixture.txt"))&&File.ReadAllText(Path.Combine(target,"source","fixture.txt"))=="worker fixture",["copy-keeps-source"]=File.Exists(Path.Combine(source,"fixture.txt")),["default-settings-unchanged"]=before==null?!File.Exists(settingsPath):File.Exists(settingsPath)&&before.SequenceEqual(File.ReadAllBytes(settingsPath))};
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,receipt},new JsonSerializerOptions{WriteIndented=true}));
foreach(var c in checks)Console.WriteLine((c.Value?"PASS ":"FAIL ")+c.Key);
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");return checks.All(c=>c.Value)?0:1;
