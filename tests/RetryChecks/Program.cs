using RoboCopyUI;
using System.Text;
using System.Text.Json;

if(Environment.UserName!="WDAGUtilityAccount")throw new InvalidOperationException("Sandbox only");
var root=Path.GetFullPath(args.Single());if(Directory.Exists(root))throw new IOException("Fresh root required");Directory.CreateDirectory(root);
var checks=new Dictionary<string,bool>();var errors=new List<string>();
void Check(string name,Func<bool> check){try{checks[name]=check();}catch(Exception e){checks[name]=false;errors.Add(name+": "+e);}Console.WriteLine((checks[name]?"PASS ":"FAIL ")+name);}
bool Reject(Action action){try{action();return false;}catch{return true;}}
var options=new RobocopyOptions{FullPreservation=true,IsMove=true,RetryCount=3,WaitSeconds=5};
var request=TaskRetryRequest.Create([Path.Combine(root,"源 文件.txt")],Path.Combine(root,"目标"),options,"Dark");
options.RetryCount=99;var bytes=request.Encode();var hash=TaskRetryRequest.Digest(bytes);
TaskRetryRequest Decode(byte[] data,string? sid=null,int? session=null,DateTimeOffset? now=null,string? digest=null)=>TaskRetryRequest.Decode(data,digest??TaskRetryRequest.Digest(data),sid??request.CallerSid,session??request.SessionId,now??DateTimeOffset.UtcNow);
Check("snapshot-does-not-follow-global-changes",()=>Decode(bytes).Options.RetryCount==3);
Check("unicode-and-move-round-trip",()=>Decode(bytes).Sources[0]==request.Sources[0]&&Decode(bytes).Options.IsMove);
Check("language-round-trip",()=>Decode(bytes).Language==request.Language);
Check("changed-content-rejected",()=>Reject(()=>Decode([..bytes,(byte)' '],digest:hash)));
Check("wrong-user-rejected",()=>Reject(()=>Decode(bytes,sid:"S-1-5-18")));
Check("wrong-session-rejected",()=>Reject(()=>Decode(bytes,session:request.SessionId+1)));
Check("expired-request-rejected",()=>Reject(()=>Decode(bytes,now:request.CreatedUtc.AddMinutes(6))));
Check("future-request-rejected",()=>Reject(()=>Decode(bytes,now:request.CreatedUtc.AddMinutes(-1))));
var json=Encoding.UTF8.GetString(bytes);
Check("unsupported-language-rejected",()=>Reject(()=>Decode(Encoding.UTF8.GetBytes(json.Replace("\"Language\":\""+request.Language+"\"","\"Language\":\"invalid-language\"")))));
Check("language-change-with-original-hash-rejected",()=>Reject(()=>Decode(Encoding.UTF8.GetBytes(json.Replace("\"Language\":\""+request.Language+"\"","\"Language\":\""+(request.Language=="en-US"?"zh-CN":"en-US")+"\"")),digest:hash)));
Check("unknown-fields-rejected",()=>Reject(()=>Decode(Encoding.UTF8.GetBytes(json[..^1]+",\"Executable\":\"cmd.exe\"}"))));
Check("invalid-settings-rejected",()=>Reject(()=>Decode(Encoding.UTF8.GetBytes(json.Replace("\"ThreadCount\":8","\"ThreadCount\":129")))));
var stored=TaskRequestStore.Save(request);
Check("store-claim-valid",()=>TaskRequestStore.ReadAndClaim(stored.Path,stored.Hash).Id==request.Id);
Check("claim-replay-rejected",()=>Reject(()=>TaskRequestStore.ReadAndClaim(stored.Path,stored.Hash)));
Check("outside-request-directory-rejected",()=>Reject(()=>TaskRequestStore.ReadAndClaim(Path.Combine(root,"request.json"),hash)));
Check("missing-receipt-is-not-success",()=>TaskRequestStore.ReadReceipt(stored)==null);
TaskRequestStore.WriteReceipt(new(request.Id,hash,CopyState.Error,true,["failed; source retained"],DateTimeOffset.UtcNow),["isolated worker log"]);
Check("receipt-bound-to-request",()=>TaskRequestStore.ReadReceipt(stored)?.State==CopyState.Error);
Check("wrong-receipt-hash-rejected",()=>Reject(()=>TaskRequestStore.ReadReceipt(stored with{Hash=new string('0',64)})));
TaskRequestStore.Cancel(request.Id);
Check("cancel-marker-is-task-specific",()=>File.Exists(TaskRequestStore.CancelPath(request.Id)));
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,errors},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{checks.Count(c=>c.Value)} passed, {checks.Count(c=>!c.Value)} failed");return checks.All(c=>c.Value)?0:1;
