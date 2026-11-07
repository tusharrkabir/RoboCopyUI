using RoboCopyUI;
using System.Diagnostics;
using System.Text.Json;

var root = Path.GetFullPath(args.Single());
if (Directory.Exists(root) || File.Exists(root))
    throw new InvalidOperationException("测试根目录必须是全新目录，禁止复用已有目录。");
Directory.CreateDirectory(root);
var results = new List<object>();
var failures = 0;
string Hash(string file) { using var stream=File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }

void Check(string name, Action<string> test)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    try { test(path); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Reject(Action action)
{
    bool rejected = false;
    try { action(); } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; }
    Assert(rejected, "Expected rejection");
}
string Put(string dir, string name, string text = "payload")
{
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, name);
    File.WriteAllText(path, text);
    return path;
}
RobocopyOptions Options(bool move = false) => new() { IsMove = move, FullPreservation = false,
    RetryCount = 0, WaitSeconds = 0, AutoSelectThreadCount = false, ThreadCount = 2 };
CopyState Run(string path, List<string> sources, string target, RobocopyOptions options,
    CollisionChoice choice = CollisionChoice.Replace)
{
    object Snapshot() => Directory.EnumerateFileSystemEntries(path,"*",SearchOption.AllDirectories)
        .Where(p => !p.EndsWith(".json")).Select(p => new { path=Path.GetRelativePath(path,p),
            directory=Directory.Exists(p), bytes=File.Exists(p)?new FileInfo(p).Length:(long?)null,
            sha256=File.Exists(p)?Hash(p):null }).ToArray();
    // Files may intentionally be locked; those cases have explicit content assertions instead.
    object? before=null, after=null;
    try { before=Snapshot(); } catch(IOException) { }
    var state = CopyState.Scanning;
    using var engine = new RobocopyEngine();
    var errors = new List<string>();
    engine.StatusChanged += (_, s) => state = s;
    engine.Error += (_, e) => errors.Add(e);
    engine.CollisionsDetected += (_, _) => engine.ResolveCollisions(choice);
    engine.Start(sources, target, options);
    try { after=Snapshot(); } catch(IOException) { }
    File.WriteAllText(Path.Combine(path, "engine.json"), JsonSerializer.Serialize(new { state = state.ToString(), errors, processes = engine.Results, before, after }, new JsonSerializerOptions { WriteIndented = true }));
    return state;
}

Check("settings-default", p => { var s = UserSettings.LoadFrom(Path.Combine(p, "settings.json")); Assert(s.RetryCount == 3 && s.WaitSeconds == 5 && !s.TransferOptions().FullPreservation); });
Check("settings-old-zero", p => { var s = UserSettings.Parse("{\"SettingsVersion\":2,\"RetryCount\":0,\"WaitSeconds\":0,\"Theme\":\"Light\"}"); Assert(s.RetryCount == 3 && s.WaitSeconds == 5 && s.Theme == "Light"); });
Check("settings-old-custom", p => { var s = UserSettings.Parse("{\"SettingsVersion\":2,\"RetryCount\":7,\"WaitSeconds\":2}"); Assert(s.RetryCount == 7 && s.WaitSeconds == 2); });
Check("settings-new-zero", p => { var s = UserSettings.Parse("{\"SettingsVersion\":3,\"RetryCount\":0,\"WaitSeconds\":0}"); Assert(s.RetryCount == 0 && s.WaitSeconds == 0); });
Check("settings-partial", p => { var s = UserSettings.Parse("{\"SettingsVersion\":2,\"RetryCount\":4}"); Assert(s.RetryCount == 4 && s.WaitSeconds == 5); });
Check("settings-invalid", p => { foreach (var json in new[]{"{\"RetryCount\":-1}", "{\"WaitSeconds\":3601}", "{\"RetryCount\":101}"}) Reject(() => UserSettings.Parse(json)); });
Check("settings-atomic", p => { var f = Path.Combine(p,"settings.json"); var s = new UserSettings(); s.SaveTo(f); s.RetryCount = 1; s.WaitSeconds = 2; s.SaveTo(f); Assert(UserSettings.LoadFrom(f).RetryCount == 1 && File.Exists(f + ".bak")); Assert(!Directory.GetFiles(p,"*.tmp").Any()); });
Check("settings-corrupt", p => { var f = Put(p,"settings.json","{broken"); var s = UserSettings.LoadFrom(f); Assert(s.RetryCount == 3 && UserSettings.LoadNotice != null && Directory.GetFiles(p,"*.corrupt-*.json").Length == 1 && File.ReadAllText(f) == "{broken"); });
Check("arguments-preservation", p => { var b = new RobocopyBatchItem { SourceDir=p, DestDir=Path.Combine(p,"out"),IsDirectoryCopy=true }; var a = RobocopyCommandBuilder.BuildArguments(b,new(){IsMove=true},false); foreach(var flag in new[]{"/E ","/ZB ","/COPYALL ","/DCOPY:DAT","/R:3","/W:5"}) Assert(a.Contains(flag),flag); foreach(var flag in new[]{"/MIR","/PURGE","/IPG","/FFT","/NODCOPY"}) Assert(!a.Contains(flag),flag); });
Check("same-source-target", p => { var file=Put(p,"a.txt"); var state=Run(p,[file],p,Options(true)); Assert(state==CopyState.Error && File.Exists(file)); });
Check("nested-target", p => { var src=Path.Combine(p,"src"); Put(src,"a.txt"); var dest=Path.Combine(src,"child"); Directory.CreateDirectory(dest); Assert(Run(p,[src],dest,Options(true))==CopyState.Error && File.Exists(Path.Combine(src,"a.txt"))); });
Check("directory-extra-and-empty", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Put(src,"zero",""); Put(src,"中文 空格.txt"); Directory.CreateDirectory(Path.Combine(src,"empty")); Put(Path.Combine(dest,"src"),"only-target.txt","keep"); Assert(Run(p,[src],dest,Options())==CopyState.Completed); Assert(File.ReadAllText(Path.Combine(dest,"src","only-target.txt"))=="keep" && File.Exists(Path.Combine(dest,"src","zero")) && Directory.Exists(Path.Combine(dest,"src","empty"))); });
Check("selected-file-only", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(dest); var file=Put(src,"chosen",""); Put(src,"sibling.txt"); Put(Path.Combine(src,"chosen-tree"),"hidden"); Assert(Run(p,[file],dest,Options())==CopyState.Completed); Assert(File.Exists(Path.Combine(dest,"chosen")) && !File.Exists(Path.Combine(dest,"sibling.txt")) && !Directory.Exists(Path.Combine(dest,"chosen-tree"))); });
Check("move-verified", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(dest); var file=Put(src,"a.txt"); Assert(Run(p,[file],dest,Options(true))==CopyState.Completed); Assert(!File.Exists(file) && File.ReadAllText(Path.Combine(dest,"a.txt"))=="payload"); });
Check("move-skip-retains", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); var file=Put(src,"a.txt","new"); Put(dest,"a.txt","old"); Assert(Run(p,[file],dest,Options(true),CollisionChoice.Skip)==CopyState.Error); Assert(File.ReadAllText(file)=="new" && File.ReadAllText(Path.Combine(dest,"a.txt"))=="old"); });
Check("rename-verified-move", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); var file=Put(src,"a.txt","new"); Put(dest,"a.txt","old"); Assert(Run(p,[file],dest,Options(true),CollisionChoice.Rename)==CopyState.Completed); Assert(!File.Exists(file) && File.ReadAllText(Path.Combine(dest,"a (1).txt"))=="new" && File.ReadAllText(Path.Combine(dest,"a.txt"))=="old"); });
Check("exit16-retains", p => { var file=Put(Path.Combine(p,"src"),"a.txt"); var dest=Put(p,"dest","not a directory"); var options=Options(true); Assert(Run(p,[file],dest,options)==CopyState.Error && File.Exists(file)); });
Check("exit8-locked-retains", p => { var file=Put(Path.Combine(p,"src"),"a.txt"); var dest=Path.Combine(p,"dest"); var target=Put(dest,"a.txt","old"); using var locked=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None); Assert(Run(p,[file],dest,Options(true))==CopyState.Error && File.Exists(file)); });
Check("manifest-source-changed", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); var file=Put(src,"a.txt"); var b=RobocopyBatchItem.CreateBatches([file],dest).Single(); var m=TransferSafety.Freeze(b); Put(dest,"a.txt"); File.WriteAllText(file,"changed"); Assert(m.CleanVerifiedSources(false,()=>false).Count>0 && File.Exists(file)); });
Check("manifest-target-corrupt", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); var file=Put(src,"a.txt"); var m=TransferSafety.Freeze(RobocopyBatchItem.CreateBatches([file],dest).Single()); Put(dest,"a.txt","corrupt"); Assert(m.CleanVerifiedSources(false,()=>false).Count>0 && File.Exists(file)); });
Check("manifest-cancel-retains", p => { var file=Put(Path.Combine(p,"src"),"a.txt"); var m=TransferSafety.Freeze(RobocopyBatchItem.CreateBatches([file],Path.Combine(p,"dest")).Single()); m.CleanVerifiedSources(false,()=>true); Assert(File.Exists(file)); });
Check("late-addition-retains", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Put(src,"old.txt"); var b=RobocopyBatchItem.CreateBatches([src],dest).Single(); var m=TransferSafety.Freeze(b); var late=Put(src,"new.txt","late"); Assert(Run(p,[src],dest,Options())==CopyState.Completed); Assert(m.CleanVerifiedSources(false,()=>false).Count>0 && File.ReadAllText(late)=="late" && File.Exists(Path.Combine(src,"old.txt"))); });
Check("legacy-delete-closed", p => { var file=Put(p,"keep"); using var e=new RobocopyEngine(); var state=CopyState.Scanning; e.StatusChanged+=(_,s)=>state=s; e.StartDelete([file]); Assert(state==CopyState.Error && File.Exists(file)); });
Check("retry-real-three-five", p => { var src=Path.Combine(p,"src"); var file=Put(src,"a.txt"); var dest=Path.Combine(p,"dest"); var target=Put(dest,"a.txt","old"); using var locked=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None); var options=Options(); options.RetryCount=3; options.WaitSeconds=5; var watch=Stopwatch.StartNew(); Assert(Run(p,[file],dest,options)==CopyState.Error); watch.Stop(); File.WriteAllText(Path.Combine(p,"duration.txt"),watch.Elapsed.ToString()); Assert(watch.Elapsed>=TimeSpan.FromSeconds(14) && watch.Elapsed<TimeSpan.FromSeconds(50),watch.Elapsed.ToString()); });

Check("move-empty-directory", p => { var src=Path.Combine(p,"empty"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(src); Directory.CreateDirectory(dest); Assert(Run(p,[src],dest,Options(true))==CopyState.Completed); Assert(!Directory.Exists(src) && Directory.Exists(Path.Combine(dest,"empty"))); });
Check("manifest-missing-empty-target", p => { var src=Path.Combine(p,"empty"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(src); var m=TransferSafety.Freeze(RobocopyBatchItem.CreateBatches([src],dest).Single()); Assert(m.CleanVerifiedSources(false,()=>false).Count>0 && Directory.Exists(src)); });
Check("named-stream-retains", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(dest); var file=Put(src,"a.txt"); File.WriteAllText(file+":note","extra"); Assert(Run(p,[file],dest,Options(true))==CopyState.Error && File.ReadAllText(file+":note")=="extra"); });

Check("skip-directory-keeps-target", p => { var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); Put(src,"a.txt","new"); Put(Path.Combine(dest,"src"),"a.txt","old"); Assert(Run(p,[src],dest,Options(true),CollisionChoice.Skip)==CopyState.Error); Assert(File.ReadAllText(Path.Combine(src,"a.txt"))=="new" && File.ReadAllText(Path.Combine(dest,"src","a.txt"))=="old"); });
Check("cancel-live-transfer", p => {
    var src=Path.Combine(p,"src"); var dest=Path.Combine(p,"dest"); var file=Put(src,"a.txt"); var target=Put(dest,"a.txt","old");
    using var locked=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    using var engine=new RobocopyEngine(); var state=CopyState.Scanning; Task? cancel=null;
    engine.CollisionsDetected+=(_,_)=>engine.ResolveCollisions(CollisionChoice.Replace);
    engine.StatusChanged+=(_,s)=>{state=s; if(s==CopyState.Copying) cancel=Task.Run(()=>{Thread.Sleep(500); engine.Cancel();});};
    var options=Options(true); options.RetryCount=100; options.WaitSeconds=1;
    var task=Task.Run(()=>engine.Start([file],dest,options)); Assert(task.Wait(TimeSpan.FromSeconds(10)),"取消没有及时结束子进程"); cancel?.Wait();
    Assert(state==CopyState.Cancelled && File.ReadAllText(file)=="payload");
});
Check("full-preservation-privilege-failure", p => {
    var file=Put(Path.Combine(p,"src"),"a.txt"); var dest=Path.Combine(p,"dest"); Directory.CreateDirectory(dest); var options=Options(); options.FullPreservation=true;
    var state=Run(p,[file],dest,options);
    Assert(File.Exists(file));
    if(state==CopyState.Error) Assert(File.ReadAllText(Path.Combine(p,"engine.json")).Contains("exit=16") || File.ReadAllText(Path.Combine(p,"engine.json")).Contains("exit=8"));
    else Assert(state==CopyState.Completed && File.ReadAllText(Path.Combine(dest,"a.txt"))=="payload");
});

Check("cancel-before-start",p=>{var file=Put(Path.Combine(p,"src"),"a.txt");var dest=Path.Combine(p,"dest");using var e=new RobocopyEngine();var state=CopyState.Scanning;e.StatusChanged+=(_,s)=>state=s;e.Cancel();e.Start([file],dest,Options(true));Assert(state==CopyState.Cancelled&&File.Exists(file)&&!Directory.Exists(dest));});
Check("cancel-during-scanning",p=>{var file=Put(Path.Combine(p,"src"),"a.txt");var dest=Path.Combine(p,"dest");using var e=new RobocopyEngine();var state=CopyState.Scanning;e.StatusChanged+=(_,s)=>{state=s;if(s==CopyState.Scanning)e.Cancel();};e.Start([file],dest,Options(true));Assert(state==CopyState.Cancelled&&File.Exists(file)&&!Directory.Exists(dest));});
Check("cancel-after-completion",p=>{var file=Put(Path.Combine(p,"src"),"a.txt");var dest=Path.Combine(p,"dest");Directory.CreateDirectory(dest);using var e=new RobocopyEngine();var states=new List<CopyState>();e.StatusChanged+=(_,s)=>states.Add(s);e.Start([file],dest,Options());e.Cancel();Assert(states.Last()==CopyState.Completed&&states.Count(s=>s==CopyState.Completed)==1&&states.All(s=>s!=CopyState.Cancelled));});
Check("rename-directory-verified-move",p=>{var src=Path.Combine(p,"photos.v1");var dest=Path.Combine(p,"dest");Put(src,"a.txt","new");Put(Path.Combine(dest,"photos.v1"),"a.txt","old");Assert(Run(p,[src],dest,Options(true),CollisionChoice.Rename)==CopyState.Completed);Assert(!Directory.Exists(src)&&File.ReadAllText(Path.Combine(dest,"photos.v1 (1)","a.txt"))=="new"&&File.ReadAllText(Path.Combine(dest,"photos.v1","a.txt"))=="old");});
Check("unrelated-parent-name-not-collision",p=>{var src=Path.Combine(p,"src");var dest=Path.Combine(p,"dest");Put(src,"a.txt","new");Put(dest,"a.txt","unrelated");Assert(CollisionScanner.FindCollisions(RobocopyBatchItem.CreateBatches([src],dest).Single()).Count==0);Assert(Run(p,[src],dest,Options(),CollisionChoice.Rename)==CopyState.Completed);Assert(File.ReadAllText(Path.Combine(dest,"src","a.txt"))=="new"&&File.ReadAllText(Path.Combine(dest,"a.txt"))=="unrelated");});
Check("overlapping-source-selection",p=>{var src=Path.Combine(p,"src");var file=Put(src,"a.txt");var dest=Path.Combine(p,"dest");Directory.CreateDirectory(dest);Assert(Run(p,[src,file],dest,Options(true))==CopyState.Error&&File.Exists(file)&&Directory.GetFileSystemEntries(dest).Length==0);});
Check("target-hardlink-retains",p=>{var file=Put(Path.Combine(p,"src"),"a.txt","new");var dest=Path.Combine(p,"dest");Directory.CreateDirectory(dest);var unrelated=Put(p,"unrelated","old");Assert(TestNative.CreateHardLink(Path.Combine(dest,"a.txt"),unrelated,nint.Zero),$"Cannot create isolated hardlink (Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");Assert(Run(p,[file],dest,Options(true))==CopyState.Error&&File.ReadAllText(file)=="new"&&File.ReadAllText(unrelated)=="old");});
Check("source-change-at-collision-retains",p=>{var file=Put(Path.Combine(p,"src"),"a.txt","before");var dest=Path.Combine(p,"dest");Put(dest,"a.txt","old");using var e=new RobocopyEngine();var state=CopyState.Scanning;e.StatusChanged+=(_,s)=>state=s;e.CollisionsDetected+=(_,_)=>{File.WriteAllText(file,"after");e.ResolveCollisions(CollisionChoice.Replace);};e.Start([file],dest,Options(true));Assert(state==CopyState.Error&&File.ReadAllText(file)=="after"&&File.ReadAllText(Path.Combine(dest,"a.txt"))=="after");});

Check("case-and-trailing-separator",p=>{var src=Path.Combine(p,"Source");var file=Put(src,"A.txt");var dest=Path.Combine(p,"Dest");Directory.CreateDirectory(dest);Assert(Run(p,[src.ToUpperInvariant()+Path.DirectorySeparatorChar],dest+Path.DirectorySeparatorChar,Options(true))==CopyState.Completed&&!File.Exists(file)&&File.Exists(Path.Combine(dest,"SOURCE","A.txt")));});
Check("long-chinese-space-path",p=>{var src=Path.Combine(p,"中文 空格");for(int i=0;i<10;i++)src=Path.Combine(src,"长目录名字 abcdefghijklmnop"+i);var file=Put(src,"文件 name.txt");var dest=Path.Combine(p,"目标 space");Directory.CreateDirectory(dest);Assert(file.Length>260&&Run(p,[file],dest,Options(true))==CopyState.Completed&&!File.Exists(file)&&File.ReadAllText(Path.Combine(dest,"文件 name.txt"))=="payload");});
Check("directory-link-does-not-traverse",p=>{var src=Path.Combine(p,"src");var file=Put(src,"a.txt");var outside=Path.Combine(p,"outside");var keep=Put(outside,"keep.txt","outside");TestNative.CreateDirectoryLink(Path.Combine(src,"link"),outside);var dest=Path.Combine(p,"dest");Directory.CreateDirectory(dest);Assert(Run(p,[src],dest,Options(true))==CopyState.Error&&File.Exists(file)&&File.ReadAllText(keep)=="outside"&&!File.Exists(Path.Combine(dest,"src","link","keep.txt")));});
Check("unavailable-unc-stops-retains-source",p=>{var file=Put(Path.Combine(p,"src"),"keep.txt");Assert(Run(p,[file],@"\\127.0.0.1\RoboCopyUIUnavailableShare",Options(true))==CopyState.Error&&File.Exists(file));});

File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new { passed=results.Count-failures,failed=failures,results },new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"{results.Count-failures} passed, {failures} failed. Evidence: {root}");
return failures==0?0:1;

internal static class TestNative
{
    internal static void CreateDirectoryLink(string link,string target)
    {
        try { Directory.CreateSymbolicLink(link,target);return; }
        catch(IOException error) when((error.HResult&0xffff)==1314) { }
        // A junction exercises the same no-traversal boundary without granting
        // the ordinary test user the symbolic-link creation privilege.
        if((link+target).IndexOfAny(['&','|','<','>','^','%','!','"','\r','\n'])>=0)
            throw new IOException("Unsupported characters in junction fixture path");
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"))
        {
            UseShellExecute=false,CreateNoWindow=true,
            Arguments=$"/d /c mklink /J \"{link}\" \"{target}\"",
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEnd();var errorText=process.StandardError.ReadToEnd();
        if(!process.WaitForExit(5000)||process.ExitCode!=0)
            throw new IOException("Cannot create isolated junction: "+output+errorText);
        Console.WriteLine("Fixture uses a junction; ordinary user has no symbolic-link privilege.");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    internal static extern bool CreateHardLink(string link,string target,nint security);
}
