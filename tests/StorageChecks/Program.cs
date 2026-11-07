using RoboCopyUI;
using System.Text.Json;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
var root=Path.GetFullPath(args[0]);
if(Directory.Exists(root))throw new IOException("Fresh root required");
Directory.CreateDirectory(root);
var checks=new Dictionary<string,bool>();var logs=new List<object>();
var suffix=Guid.NewGuid().ToString("N");
var fat=Path.Combine(@"R:\",suffix);var smb=Path.Combine(@"\\127.0.0.1\RoboCopyUIFixture",suffix);
Directory.CreateDirectory(fat);
void Check(string name,Func<bool> test){try{checks[name]=test();}catch(Exception e){checks[name]=false;logs.Add(new{name,error=e.ToString()});}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
(CopyState State,List<string> Errors,int Processes) Run(string source,string target,bool full,bool move=false){
 using var e=new RobocopyEngine();var state=CopyState.Scanning;var errors=new List<string>();
 e.StatusChanged+=(_,s)=>state=s;e.Error+=(_,s)=>errors.Add(s);
 e.Start([source],target,new RobocopyOptions{FullPreservation=full,IsMove=move,AutoSelectThreadCount=false,ThreadCount=2,RetryCount=0,WaitSeconds=0});
 logs.Add(new{source,target,full,move,state=state.ToString(),errors,processes=e.Results.ToArray()});return(state,errors,e.Results.Count);
}
string Source(string name){var path=Path.Combine(root,name);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"中文 空格.txt"),"storage fixture "+name);Directory.CreateDirectory(Path.Combine(path,"empty"));return path;}
Check("exfat-capabilities",()=>VolumeCapabilities.Read(fat).FileSystem.Equals("exFAT",StringComparison.OrdinalIgnoreCase)&&!VolumeCapabilities.Read(fat).PreservesAcl);
var fullSource=Source("full-source");
Check("full-exfat-stops-before-copy",()=>{var result=Run(fullSource,fat,true);return result.State==CopyState.Error&&result.Processes==0&&Directory.Exists(fullSource)&&!Directory.Exists(Path.Combine(fat,"full-source"));});
var ordinary=Source("ordinary-source");
Check("ordinary-exfat-copy",()=>Run(ordinary,fat,false).State==CopyState.Completed&&File.ReadAllText(Path.Combine(fat,"ordinary-source","中文 空格.txt"))=="storage fixture ordinary-source");
var move=Source("move-source");
Check("ordinary-exfat-verified-move",()=>Run(move,fat,false,true).State==CopyState.Completed&&!Directory.Exists(move)&&File.Exists(Path.Combine(fat,"move-source","中文 空格.txt")));
var fromFat=Path.Combine(fat,"from-fat");Directory.CreateDirectory(fromFat);File.WriteAllText(Path.Combine(fromFat,"文件.txt"),"back from FAT");
Check("ordinary-exfat-source-move",()=>Run(fromFat,root,false,true).State==CopyState.Completed&&!Directory.Exists(fromFat)&&File.ReadAllText(Path.Combine(root,"from-fat","文件.txt"))=="back from FAT");
// SUBST exercises filesystem aliases without requiring a network adapter.
using(var subst=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"subst.exe")){ArgumentList={"S:",root},UseShellExecute=false,CreateNoWindow=true})!){subst.WaitForExit();if(subst.ExitCode!=0)throw new IOException("SUBST fixture failed");}
try{
 var aliasSource=Source("alias-source");
 Check("subst-alias-same-target-rejected",()=>{var r=Run(aliasSource,@"S:\",false,true);return r.State==CopyState.Error&&r.Processes==0&&File.Exists(Path.Combine(aliasSource,"中文 空格.txt"));});
 Check("subst-alias-nested-target-rejected",()=>{var r=Run(aliasSource,@"S:\alias-source\nested",false,true);return r.State==CopyState.Error&&r.Processes==0&&!Directory.Exists(Path.Combine(aliasSource,"nested"));});
}finally{using var subst=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"subst.exe")){ArgumentList={"S:","/D"},UseShellExecute=false,CreateNoWindow=true})!;subst.WaitForExit();}
if(args.Length>1 && args[1]=="--smb"){Directory.CreateDirectory(smb);
var smbSource=Source("smb-source");var existing=Path.Combine(smb,"smb-source");Directory.CreateDirectory(existing);File.WriteAllText(Path.Combine(existing,"extra.txt"),"retain");
Check("ordinary-smb-copy-retains-extra",()=>Run(smbSource,smb,false).State==CopyState.Completed&&File.Exists(Path.Combine(existing,"extra.txt"))&&File.Exists(Path.Combine(existing,"中文 空格.txt")));
var smbMove=Source("smb-move");
Check("ordinary-smb-verified-move",()=>Run(smbMove,smb,false,true).State==CopyState.Completed&&!Directory.Exists(smbMove)&&File.Exists(Path.Combine(smb,"smb-move","中文 空格.txt")));
var alias=Path.Combine(@"C:\RoboCopyUI-SmbFixture",suffix,"alias-source");Directory.CreateDirectory(alias);File.WriteAllText(Path.Combine(alias,"keep.txt"),"retain source");
Check("unc-alias-same-target-rejected",()=>{var r=Run(alias,smb,false,true);return r.State==CopyState.Error&&r.Processes==0&&File.Exists(Path.Combine(alias,"keep.txt"));});
Check("unc-alias-nested-target-rejected",()=>{var r=Run(alias,Path.Combine(smb,"alias-source","nested"),false,true);return r.State==CopyState.Error&&r.Processes==0&&!Directory.Exists(Path.Combine(alias,"nested"))&&File.Exists(Path.Combine(alias,"keep.txt"));});
using(var scope=new MetadataPrivilegeScope()){
 var fullSmb=Source("full-smb");
 Check("full-smb-copy",()=>scope.Unavailable.Count==0&&Run(fullSmb,smb,true).State==CopyState.Completed&&File.Exists(Path.Combine(smb,"full-smb","中文 空格.txt")));
}
}
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,logs},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");
return checks.All(c=>c.Value)?0:1;
