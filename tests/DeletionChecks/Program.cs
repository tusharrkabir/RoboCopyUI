using RoboCopyUI;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
 [STAThread]static int Main(string[] args)
 {
  if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
  var root=Path.GetFullPath(args.Single());
  if(!root.StartsWith(@"C:\RoboCopyUI-DeleteChecks-",StringComparison.OrdinalIgnoreCase)||Directory.Exists(root))throw new IOException("Fresh disposable root required");
  Directory.CreateDirectory(root);var checks=new Dictionary<string,bool>();var evidence=new List<object>();
  void Check(string name,Func<bool> run){try{checks[name]=run();}catch(Exception error){checks[name]=false;evidence.Add(new{name,error=error.ToString()});}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
  string Put(string dir,string name){Directory.CreateDirectory(dir);var path=Path.Combine(dir,name);File.WriteAllText(path,"dedicated deletion fixture: "+name);return path;}
  bool Rejects(Action run){try{run();return false;}catch(IOException){return true;}}
  RecycleInfo Recycle(){var info=new RecycleInfo{Size=(uint)Marshal.SizeOf<RecycleInfo>()};Marshal.ThrowExceptionForHR(SHQueryRecycleBin(@"C:\",ref info));return info;}
  var before=Recycle();
  Check("roots-and-protected-system-directories-refused",()=>Rejects(()=>PermanentDeletion.Freeze([@"C:\"]))&&Rejects(()=>PermanentDeletion.Freeze([Environment.GetFolderPath(Environment.SpecialFolder.Windows)]))&&Rejects(()=>PermanentDeletion.Freeze([@"\\unavailable\share\"])));
  Check("selected-file-only-and-no-recycle-entry",()=>
  {
   var file=Put(Path.Combine(root,"file"),"selected.txt");var sibling=Put(Path.GetDirectoryName(file)!,"sibling.txt");
   var plan=PermanentDeletion.Freeze([file]);var result=plan.ExecuteConfirmed();evidence.Add(result);
   var current=Recycle();return result.Completed&&!result.Aborted&&!File.Exists(file)&&File.Exists(sibling)&&current.Count==before.Count&&current.Bytes==before.Bytes;
  });
  Check("directory-tree-and-empty-folder-delete",()=>
  {
   var folder=Path.Combine(root,"tree");Put(Path.Combine(folder,"nested"),"child.txt");Directory.CreateDirectory(Path.Combine(folder,"empty"));
   var sibling=Put(root,"unselected.txt");var plan=PermanentDeletion.Freeze([folder]);var result=plan.ExecuteConfirmed();evidence.Add(result);
   return plan.EntryCount==4&&result.Completed&&!Directory.Exists(folder)&&File.Exists(sibling);
  });
  Check("pre-cancelled-keeps-source",()=>
  {
   var file=Put(Path.Combine(root,"cancelled"),"keep.txt");var plan=PermanentDeletion.Freeze([file]);using var cancel=new CancellationTokenSource();cancel.Cancel();
   bool cancelled=false;try{plan.ExecuteConfirmed(cancel.Token);}catch(OperationCanceledException){cancelled=true;}return cancelled&&File.Exists(file);
  });
  Check("changed-file-refused-before-any-delete",()=>
  {
   var one=Put(Path.Combine(root,"changed"),"one.txt");var two=Put(Path.GetDirectoryName(one)!,"two.txt");var plan=PermanentDeletion.Freeze([one,two]);File.AppendAllText(two," updated after confirmation snapshot");
   return Rejects(()=>plan.ExecuteConfirmed())&&File.Exists(one)&&File.Exists(two);
  });
  Check("late-child-refused-before-any-delete",()=>
  {
   var dir=Path.Combine(root,"late");var first=Put(dir,"first.txt");var plan=PermanentDeletion.Freeze([dir]);var added=Put(dir,"new.txt");
   return Rejects(()=>plan.ExecuteConfirmed())&&File.Exists(first)&&File.Exists(added);
  });
  Check("locked-file-reports-failure-and-retains-source",()=>
  {
   var file=Put(Path.Combine(root,"locked"),"keep.txt");var plan=PermanentDeletion.Freeze([file]);
   using var held=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);var result=plan.ExecuteConfirmed();evidence.Add(result);
   return !result.Completed&&File.Exists(file)&&(result.Aborted||result.Errors.Length>0||result.Items.Any(item=>item.HResult!=0));
  });
  Check("nested-and-duplicate-selection-refused",()=>
  {
   var file=Put(Path.Combine(root,"nested-selection"),"keep.txt");return Rejects(()=>PermanentDeletion.Freeze([Path.GetDirectoryName(file)!,file]))&&Rejects(()=>PermanentDeletion.Freeze([file,file]))&&File.Exists(file);
  });
  Check("junction-refused-and-target-preserved",()=>
  {
   var target=Path.Combine(root,"junction-target");var file=Put(target,"keep.txt");var junction=Path.Combine(root,"junction");
   var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,ArgumentList={"/d","/c","mklink","/J",junction,target}};
   using var child=Process.Start(start)!;if(!child.WaitForExit(5000))return false;
   evidence.Add(new{junctionSetupExit=child.ExitCode,stdout=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd()});if(child.ExitCode!=0)return false;
   return Rejects(()=>PermanentDeletion.Freeze([junction]))&&File.Exists(file)&&Directory.Exists(junction);
  });
  Check("hardlink-delete-keeps-other-name",()=>
  {
   var target=Put(Path.Combine(root,"hardlink-target"),"keep.txt");var link=Path.Combine(root,"selected-hardlink.txt");
   if(!CreateHardLink(link,target,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   var result=PermanentDeletion.Freeze([link]).ExecuteConfirmed();evidence.Add(result);return result.Completed&&!File.Exists(link)&&File.Exists(target)&&File.ReadAllText(target).Contains("keep.txt");
  });
  Check("recycle-bin-unchanged",()=>{var current=Recycle();evidence.Add(new{before,current});return before.Count==current.Count&&before.Bytes==current.Bytes;});
  File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,evidence},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
  Console.WriteLine($"{checks.Count(pair=>pair.Value)} passed, {checks.Count(pair=>!pair.Value)} failed");return checks.All(pair=>pair.Value)?0:1;
 }
 [StructLayout(LayoutKind.Sequential)]struct RecycleInfo{public uint Size;public long Bytes,Count;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHQueryRecycleBin(string root,ref RecycleInfo info);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLink(string link,string existing,nint security);
}
