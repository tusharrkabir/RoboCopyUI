using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboCopyUI;

public sealed class TaskRetryRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public Guid Id { get; init; }
    public string CallerSid { get; init; } = "";
    public int SessionId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string[] Sources { get; init; } = [];
    public string Destination { get; init; } = "";
    public RobocopyOptions Options { get; init; } = new();
    public string Theme { get; init; } = "Dark";
    public string Language { get; init; } = "en-US";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=12 };
    public const int MaximumBytes=1024*1024;

    public static TaskRetryRequest Create(IEnumerable<string> sources,string destination,RobocopyOptions options,string theme) => new()
    {
        Id=Guid.NewGuid(),CallerSid=WindowsIdentity.GetCurrent().User!.Value,
        SessionId=Process.GetCurrentProcess().SessionId,CreatedUtc=DateTimeOffset.UtcNow,
        Sources=sources.Select(TransferSafety.Normalize).ToArray(),Destination=TransferSafety.Normalize(destination),
        Options=options.Snapshot(),Theme=theme,Language=L.Current.Language
    };
    public byte[] Encode()
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(this,Json);
        if(bytes.Length>MaximumBytes)throw new IOException(L.T("单次提权请求过大，请减少选择的项目。"));
        return bytes;
    }
    public static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static TaskRetryRequest Decode(byte[] bytes,string expectedHash,string expectedSid,int expectedSession,DateTimeOffset now)
    {
        if(bytes.Length is 0 or > MaximumBytes)throw new IOException(L.T("提权请求大小无效。"));
        if(expectedHash.Length!=64 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),Convert.FromHexString(expectedHash)))
            throw new IOException(L.T("提权请求内容已变化；未执行任务。"));
        var request=JsonSerializer.Deserialize<TaskRetryRequest>(bytes,Json) ?? throw new IOException(L.T("提权请求为空。"));
        if(request.ProtocolVersion!=1||request.Id==Guid.Empty)throw new IOException(L.T("提权请求协议或标识无效。"));
        if(request.CallerSid!=expectedSid||request.SessionId!=expectedSession)throw new IOException(L.T("提权账户或会话与原任务不同；未执行任务。"));
        if(request.CreatedUtc>now.AddSeconds(30)||request.CreatedUtc<now.AddMinutes(-5))throw new IOException(L.T("提权请求已过期，请从原任务重新发起。"));
        if(request.Sources==null||request.Sources.Length is 0 or >10000||request.Options==null)throw new IOException(L.T("提权任务选区无效。"));
        foreach(var source in request.Sources)TransferSafety.Normalize(source);
        TransferSafety.Normalize(request.Destination);
        var options=request.Options;
        if(options.FileMask!="*.*"||options.ThreadCount is <1 or >128||options.RetryCount is <0 or >100||options.WaitSeconds is <0 or >3600)
            throw new IOException(L.T("提权任务设置无效。"));
        if(request.Theme is not ("Dark" or "Light" or "Midnight"))throw new IOException(L.T("提权任务主题无效。"));
        if(!L.Supports(request.Language))throw new IOException(L.T("提权任务语言无效。"));
        return request;
    }
}

public sealed record StoredTaskRequest(Guid Id,string Path,string Hash);
public sealed record WorkerReceipt(Guid Id,string RequestHash,CopyState State,bool FullPreservation,string[] Messages,DateTimeOffset FinishedUtc,bool IsMove=false);

public static class TaskRequestStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RoboCopyUI","TaskRequests");
    public static string CancelPath(Guid id)=>Path.Combine(DirectoryPath,id.ToString("N")+".cancel");
    private static string ResultPath(Guid id)=>Path.Combine(DirectoryPath,id.ToString("N")+".result.json");
    public static StoredTaskRequest Save(TaskRetryRequest request)
    {
        TransferSafety.RequireNoLinks(DirectoryPath);
        var sid=WindowsIdentity.GetCurrent().User!;
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(sid);
        foreach(var identity in new[]{sid,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
            security.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        var directory=new DirectoryInfo(DirectoryPath);
        if(!directory.Exists)directory.Create(security);
        TransferSafety.RequireNoLinks(DirectoryPath);
        var owner=directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if(!sid.Equals(owner))throw new IOException(L.T("提权请求目录不属于当前用户，未写入任务。"));
        directory.SetAccessControl(security);
        var bytes=request.Encode();
        var path=Path.Combine(DirectoryPath,request.Id.ToString("N")+".json");
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
        return new(request.Id,path,TaskRetryRequest.Digest(bytes));
    }
    public static TaskRetryRequest ReadAndClaim(string path,string hash)
    {
        var canonical=TransferSafety.Normalize(path);
        if(!string.Equals(Path.GetDirectoryName(canonical),DirectoryPath,StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("提权请求不在当前用户的任务目录内。"));
        TransferSafety.RequireNoLinks(canonical);
        byte[] bytes;
        using(var file=new FileStream(canonical,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            if(file.Length is <=0 or >TaskRetryRequest.MaximumBytes)throw new IOException(L.T("提权请求大小无效。"));
            using var buffer=new MemoryStream();file.CopyTo(buffer);bytes=buffer.ToArray();
        }
        var request=TaskRetryRequest.Decode(bytes,hash,WindowsIdentity.GetCurrent().User!.Value,Process.GetCurrentProcess().SessionId,DateTimeOffset.UtcNow);
        if(!Path.GetFileName(canonical).Equals(request.Id.ToString("N")+".json",StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("提权请求文件名与标识不符。"));
        using(var claim=new FileStream(Path.Combine(DirectoryPath,request.Id.ToString("N")+".claim"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {var data=System.Text.Encoding.ASCII.GetBytes(hash);claim.Write(data);claim.Flush(true);}
        return request;
    }
    public static void Cancel(Guid id)=>File.WriteAllText(CancelPath(id),"cancel");
    public static void WriteReceipt(WorkerReceipt receipt,IEnumerable<string> fullLog)
    {
        var path=ResultPath(receipt.Id);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        var bytes=JsonSerializer.SerializeToUtf8Bytes(receipt);
        using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}
        File.Move(temporary,path,overwrite:true);
        File.WriteAllLines(Path.Combine(DirectoryPath,receipt.Id.ToString("N")+".log.txt"),fullLog);
    }
    public static WorkerReceipt? ReadReceipt(StoredTaskRequest request)
    {
        var path=ResultPath(request.Id);if(!File.Exists(path))return null;
        TransferSafety.RequireNoLinks(path);
        byte[] bytes;
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {if(file.Length>TaskRetryRequest.MaximumBytes)throw new IOException(L.T("任务回执过大。"));using var buffer=new MemoryStream();file.CopyTo(buffer);bytes=buffer.ToArray();}
        var receipt=JsonSerializer.Deserialize<WorkerReceipt>(bytes) ?? throw new IOException(L.T("任务回执为空。"));
        if(receipt.Id!=request.Id||receipt.RequestHash!=request.Hash||
            receipt.State is not (CopyState.Completed or CopyState.Cancelled or CopyState.Error)||
            receipt.Messages==null||receipt.Messages.Length>10000||receipt.Messages.Any(m=>m==null||m.Length>128*1024)||
            receipt.FinishedUtc>DateTimeOffset.UtcNow.AddSeconds(30))throw new IOException(L.T("任务回执与本次请求不符。"));
        return receipt;
    }
}
