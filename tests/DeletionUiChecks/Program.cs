using RoboCopyUI;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
 [STAThread]static int Main(string[] args)
 {
  try{return Run(args);}catch(Exception error){if(Environment.UserName=="WDAGUtilityAccount")File.WriteAllText(@"C:\RoboCopyUI-Results\delete-ui-error.log",error.ToString());return 1;}
 }
 static int Run(string[] args)
 {
  if(Environment.UserName!="WDAGUtilityAccount"||args.Length!=1||args[0] is not ("Cancel" or "Confirm"))throw new InvalidOperationException("Sandbox fixture only");
  var root=@"C:\RoboCopyUI-DeleteUiMerged"+args[0];var output=@"C:\RoboCopyUI-Results\delete-ui-merged-"+args[0].ToLowerInvariant()+".json";
  if(Directory.Exists(root)||File.Exists(output))throw new IOException("Fresh fixture required");Directory.CreateDirectory(root);
  var file=Path.Combine(root,"selected.txt");var folder=Path.Combine(root,"selected-folder");var sibling=Path.Combine(root,"unselected.txt");
  File.WriteAllText(file,"Dedicated selected deletion fixture");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"child.txt"),"Dedicated folder child");File.WriteAllText(sibling,"Must remain untouched");
  string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
  var before=new{FileHash=Hash(file),ChildHash=Hash(Path.Combine(folder,"child.txt")),SiblingHash=Hash(sibling)};
  using var shell=new ShellApartment();var plan=shell.Run(()=>PermanentDeletion.Freeze([file,folder])).GetAwaiter().GetResult();
  var app=new System.Windows.Application();using var cancellation=new CancellationTokenSource();var dialog=new DeletionTaskWindow(()=>cancellation.Cancel());
  File.WriteAllText(output+".ready",JsonSerializer.Serialize(new{Pid=Environment.ProcessId,Selected=plan.Selected,plan.EntryCount,before}));
  bool confirmed=false;DeletionResult? result=null;Exception? failure=null;
  dialog.Loaded+=async(_,_)=>
  {
   try
   {
    confirmed=await dialog.ConfirmAsync(plan);
    if(confirmed&&!cancellation.IsCancellationRequested){dialog.Started(plan.Selected);result=await shell.Run(()=>plan.ExecuteConfirmed(cancellation.Token));}
   }
   catch(Exception error){failure=error;}
   finally{dialog.Finish("Acceptance fixture complete",[]);dialog.Close();}
  };
  app.Run(dialog);if(failure!=null)throw failure;
  var after=new{FileExists=File.Exists(file),FolderExists=Directory.Exists(folder),SiblingExists=File.Exists(sibling),FileHash=File.Exists(file)?Hash(file):null,ChildHash=File.Exists(Path.Combine(folder,"child.txt"))?Hash(Path.Combine(folder,"child.txt")):null,SiblingHash=Hash(sibling)};
  File.WriteAllText(output,JsonSerializer.Serialize(new{confirmed,before,after,result},new JsonSerializerOptions{WriteIndented=true}));
  return confirmed?result?.Completed==true?0:1:0;
 }
}
