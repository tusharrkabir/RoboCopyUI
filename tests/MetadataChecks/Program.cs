using RoboCopyUI;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

if(!Environment.UserName.Equals("WDAGUtilityAccount",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only run these ACL fixtures inside Windows Sandbox");
var root=Path.GetFullPath(args.Single());
if(Directory.Exists(root))throw new InvalidOperationException("Fresh test root required");
Directory.CreateDirectory(root);
var privilegesBefore=MetadataPrivilegeScope.CurrentState();
using var privileges=new MetadataPrivilegeScope();
if(privileges.Unavailable.Count>0)throw new InvalidOperationException("Missing test privileges: "+string.Join(",",privileges.Unavailable));
var source=Path.Combine(root,"source");var target=Path.Combine(root,"target");
Directory.CreateDirectory(source);Directory.CreateDirectory(target);
var empty=Path.Combine(source,"empty");Directory.CreateDirectory(empty);
var file=Path.Combine(source,"fixture.txt");File.WriteAllText(file,"ACL owner audit fixture");
var user=WindowsIdentity.GetCurrent().User!;
var owner=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
var group=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
var fileSecurity=new FileSecurity();
fileSecurity.SetAccessRuleProtection(true,false);
fileSecurity.SetOwner(owner);fileSecurity.SetGroup(group);
fileSecurity.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,AccessControlType.Allow));
fileSecurity.AddAuditRule(new FileSystemAuditRule(user,FileSystemRights.WriteData,AuditFlags.Success|AuditFlags.Failure));
new FileInfo(file).SetAccessControl(fileSecurity);
var directorySecurity=new DirectorySecurity();
directorySecurity.SetAccessRuleProtection(true,false);directorySecurity.SetOwner(owner);directorySecurity.SetGroup(group);
directorySecurity.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
directorySecurity.AddAuditRule(new FileSystemAuditRule(user,FileSystemRights.CreateFiles,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AuditFlags.Success|AuditFlags.Failure));
new DirectoryInfo(source).SetAccessControl(directorySecurity);
File.SetAttributes(file,FileAttributes.Archive|FileAttributes.Hidden);
Directory.SetCreationTimeUtc(source,new DateTime(2020,1,2,3,4,5,DateTimeKind.Utc));
Directory.SetLastWriteTimeUtc(source,new DateTime(2021,2,3,4,5,6,DateTimeKind.Utc));
Directory.SetCreationTimeUtc(empty,new DateTime(2020,3,4,5,6,7,DateTimeKind.Utc));
Directory.SetLastWriteTimeUtc(empty,new DateTime(2021,4,5,6,7,8,DateTimeKind.Utc));
var state=CopyState.Scanning;var errors=new List<string>();
using var engine=new RobocopyEngine();engine.StatusChanged+=(_,s)=>state=s;engine.Error+=(_,e)=>errors.Add(e);
var options=new RobocopyOptions{FullPreservation=true,AutoSelectThreadCount=false,ThreadCount=2,RetryCount=0,WaitSeconds=0};
engine.Start([source],target,options);
var copiedRoot=Path.Combine(target,"source");var copiedFile=Path.Combine(copiedRoot,"fixture.txt");var copiedEmpty=Path.Combine(copiedRoot,"empty");
string FileSddl(string path)=>new FileInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorSddlForm(AccessControlSections.All);
string DirectorySddl(string path)=>new DirectoryInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorSddlForm(AccessControlSections.All);
var checks=new Dictionary<string,bool>{
 ["completed"]=state==CopyState.Completed,
 ["content"]=File.Exists(copiedFile)&&File.ReadAllText(copiedFile)==File.ReadAllText(file),
 ["file-DACL-owner-group-audit"]=File.Exists(copiedFile)&&FileSddl(file)==FileSddl(copiedFile),
 ["root-DACL-owner-group-audit"]=Directory.Exists(copiedRoot)&&DirectorySddl(source)==DirectorySddl(copiedRoot),
 ["empty-directory"]=Directory.Exists(copiedEmpty),
 ["file-attributes"]=File.Exists(copiedFile)&&File.GetAttributes(file)==File.GetAttributes(copiedFile),
 ["root-created"]=Directory.GetCreationTimeUtc(source)==Directory.GetCreationTimeUtc(copiedRoot),
 ["root-written"]=Directory.GetLastWriteTimeUtc(source)==Directory.GetLastWriteTimeUtc(copiedRoot),
 ["empty-created"]=Directory.GetCreationTimeUtc(empty)==Directory.GetCreationTimeUtc(copiedEmpty),
 ["empty-written"]=Directory.GetLastWriteTimeUtc(empty)==Directory.GetLastWriteTimeUtc(copiedEmpty)
};
var sourceSddl=FileSddl(file);var targetSddl=File.Exists(copiedFile)?FileSddl(copiedFile):null;
var sourceDirectorySddl=DirectorySddl(source);
var moveTarget=Path.Combine(root,"move-target");Directory.CreateDirectory(moveTarget);
using var move=new RobocopyEngine();var moveState=CopyState.Scanning;move.StatusChanged+=(_,s)=>moveState=s;move.Error+=(_,e)=>errors.Add(e);
var moveOptions=options.Snapshot();moveOptions.IsMove=true;
move.Start([source],moveTarget,moveOptions);
var movedRoot=Path.Combine(moveTarget,"source");var movedFile=Path.Combine(movedRoot,"fixture.txt");
checks["full-move-content-and-source-cleanup"]=moveState==CopyState.Completed&&!Directory.Exists(source)&&File.Exists(movedFile)&&File.ReadAllText(movedFile)=="ACL owner audit fixture";
checks["full-move-file-security"]=File.Exists(movedFile)&&FileSddl(movedFile)==sourceSddl;
checks["full-move-directory-security"]=Directory.Exists(movedRoot)&&DirectorySddl(movedRoot)==sourceDirectorySddl;
privileges.Dispose();
var privilegesAfter=MetadataPrivilegeScope.CurrentState();
checks["privileges-restored"]=privilegesBefore.All(p=>privilegesAfter[p.Key]==p.Value);
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{state=state.ToString(),moveState=moveState.ToString(),checks,errors,sourceSddl,targetSddl,privilegesBefore,privilegesAfter,processes=engine.Results,moveProcesses=move.Results},new JsonSerializerOptions{WriteIndented=true}));
foreach(var check in checks)Console.WriteLine((check.Value?"PASS ":"FAIL ")+check.Key);
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");
return checks.All(c=>c.Value)?0:1;
