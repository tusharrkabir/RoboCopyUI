using Microsoft.Win32;
using RoboCopyUI;
using System.Text.Json;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
var root=Path.GetFullPath(args.Single());if(Directory.Exists(root))throw new IOException("Fresh root required");Directory.CreateDirectory(root);
var checks=new Dictionary<string,bool>();var errors=new List<string>();
void Check(string name,Func<bool> check){try{checks[name]=check();}catch(Exception e){checks[name]=false;errors.Add(name+": "+e);}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
string? Threshold(RegistryKey hive){using var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");return key?.GetValue("MultipleInvokePromptMinimum")?.ToString();}
var userThreshold=Threshold(Registry.CurrentUser);var machineThreshold=Threshold(Registry.LocalMachine);
Check("cleanup-owned-menu-twice",()=>{
 var path=$@"Software\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers\{ShellRegistry.VerbName}";
 using(var existing=Registry.CurrentUser.OpenSubKey(path)){if(existing!=null)throw new IOException("Fixture already exists");}
 using(var key=Registry.CurrentUser.CreateSubKey(path)){key.SetValue("RoboCopyUIOwner","RoboCopyUI.SafeDesktop.v1");}
 ShellRegistry.RemoveOwnedMenus();ShellRegistry.RemoveOwnedMenus();
 using var after=Registry.CurrentUser.OpenSubKey(path);return after==null;
});
Check("own-logon-enable-disable-twice",()=>{ShellRegistry.SetLogonStart(true);ShellRegistry.SetLogonStart(true);using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");var command=key?.GetValue("RoboCopyUI") as string;ShellRegistry.SetLogonStart(false);ShellRegistry.SetLogonStart(false);return command==$"\"{ShellRegistry.AppPath}\" --background"&&key?.GetValue("RoboCopyUI")==null;});
var unknown=$@"Software\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers\{ShellRegistry.VerbName}";
Check("unknown-registration-not-overwritten",()=>
{
 using(var key=Registry.CurrentUser.CreateSubKey(unknown)){key.SetValue("sentinel","retain");}
 try{ShellRegistry.RemoveOwnedMenus();using var key=Registry.CurrentUser.OpenSubKey(unknown);return key?.GetValue("sentinel") as string=="retain"&&key.GetValue("RoboCopyUIOwner")==null;}
 finally{Registry.CurrentUser.DeleteSubKeyTree(unknown);}
});
const string legacy=@"Software\Classes\*\shell\RoboCopy";
using(var existing=Registry.LocalMachine.OpenSubKey(legacy)){if(existing!=null)throw new IOException("Legacy fixture key already exists");}
try{
 using(var key=Registry.LocalMachine.CreateSubKey(legacy)){key.SetValue("fixture","Sandbox only");using var command=key.CreateSubKey("command");command.SetValue("",@"""C:\Legacy\RoboCopyUI.exe"" --copy ""%1""");}
 Check("machine-legacy-detected-and-untouched",()=>{ShellRegistry.RemoveOwnedMenus();using var key=Registry.LocalMachine.OpenSubKey(legacy);return ShellRegistry.LegacyMachinePresent&&key?.GetValue("fixture") as string=="Sandbox only";});
}finally{Registry.LocalMachine.DeleteSubKeyTree(legacy);}
Check("explorer-global-thresholds-untouched",()=>Threshold(Registry.CurrentUser)==userThreshold&&Threshold(Registry.LocalMachine)==machineThreshold);
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,errors},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");return checks.All(c=>c.Value)?0:1;
