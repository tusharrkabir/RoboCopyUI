using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboCopyUI;

public sealed record DesktopRequest(int Version,Guid Id,string Action,string[] Paths,string? Folder);
public sealed record DesktopResponse(Guid Id,bool Accepted,string Message);

/// <summary>One ordinary host per user/session. Bounded, current-user-only IPC; no command execution payloads.</summary>
public sealed class DesktopInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop=new();
    private Task? listener;
    private readonly string sid=WindowsIdentity.GetCurrent().User!.Value;
    private readonly int session=Process.GetCurrentProcess().SessionId;
    public string PipeName { get; }
    public bool IsPrimary { get; }
    private static readonly JsonSerializerOptions Json=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8};
    private const int Limit=1024*1024;
    public DesktopInstance()
    {
        PipeName=$"RoboCopyUI.v1.{sid}.s{session}";
        mutex=new Mutex(false,@"Local\"+PipeName);
        try{IsPrimary=mutex.WaitOne(0);}catch(AbandonedMutexException){IsPrimary=true;}
    }
    public void Listen(Func<DesktopRequest,Task<DesktopResponse>> dispatch)
    {
        if(!IsPrimary||listener!=null)throw new InvalidOperationException("Only the primary host listens once");
        listener=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,4096,4096);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid)||Process.GetProcessById(checked((int)pid)).SessionId!=session)
                        throw new IOException("Client session mismatch");
                    string? caller=null;pipe.RunAsClient(()=>caller=WindowsIdentity.GetCurrent().User?.Value);
                    if(caller!=sid)throw new IOException("Client user mismatch");
                    var request=JsonSerializer.Deserialize<DesktopRequest>(await ReadPacket(pipe,timeout.Token),Json) ?? throw new IOException("Empty request");
                    Validate(request);
                    var response=await dispatch(request);
                    await WritePacket(pipe,JsonSerializer.SerializeToUtf8Bytes(response,Json),timeout.Token);
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
                catch(Exception ex){System.Diagnostics.Trace.WriteLine("Desktop IPC rejected: "+ex.Message);}
            }
        });
    }
    public static void Validate(DesktopRequest request)
    {
        if(request.Version!=1||request.Id==Guid.Empty||request.Paths==null||request.Paths.Length>10000)
            throw new IOException(L.T("桌面请求无效。"));
        if(request.Action is not ("ping" or "settings" or "tasks"))
            throw new IOException(L.T("未知桌面动作。"));
        if(request.Paths.Length!=0||request.Folder!=null)throw new IOException(L.T("桌面请求无效。"));
    }
    public async Task<DesktopResponse> Send(DesktopRequest request,CancellationToken cancellation=default)
    {
        Validate(request);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        await WritePacket(pipe,JsonSerializer.SerializeToUtf8Bytes(request,Json),timeout.Token);
        var response=JsonSerializer.Deserialize<DesktopResponse>(await ReadPacket(pipe,timeout.Token),Json) ?? throw new IOException(L.T("桌面宿主无回执。"));
        if(response.Id!=request.Id)throw new IOException(L.T("桌面宿主回执不匹配。"));
        return response;
    }
    private static async Task<byte[]> ReadPacket(Stream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token);
        var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <=0 or >Limit)throw new IOException("IPC packet too large");
        var data=new byte[length];await stream.ReadExactlyAsync(data,token);return data;
    }
    private static async Task WritePacket(Stream stream,byte[] data,CancellationToken token)
    {
        if(data.Length is <=0 or >Limit)throw new IOException("IPC packet too large");
        var header=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header,data.Length);
        await stream.WriteAsync(header,token);await stream.WriteAsync(data,token);await stream.FlushAsync(token);
    }
    public void Dispose(){stop.Cancel();if(IsPrimary)mutex.ReleaseMutex();mutex.Dispose();}
    [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint pid);
}
