using System.Windows.Threading;

namespace RoboCopyUI;

/// <summary>Owns Shell/OLE objects and their message pump independently of progress UI.</summary>
public sealed class ShellApartment : IDisposable
{
    private readonly Thread thread;
    private readonly Dispatcher dispatcher;
    public ShellApartment()
    {
        var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread=new Thread(()=>{try{ready.SetResult(Dispatcher.CurrentDispatcher);Dispatcher.Run();}catch(Exception error){ready.TrySetException(error);}}){IsBackground=true,Name="RoboCopyUI Shell STA"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();dispatcher=ready.Task.GetAwaiter().GetResult();
    }
    public Task<T> Run<T>(Func<T> operation)=>dispatcher.InvokeAsync(operation,DispatcherPriority.Normal).Task;
    public Task Run(Action operation)=>dispatcher.InvokeAsync(operation,DispatcherPriority.Normal).Task;
    public void Dispose()
    {
        if(dispatcher.HasShutdownStarted)return;
        var finish=Run(()=>{try{ClipboardManager.Shutdown();}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
        try{if(!finish.Wait(TimeSpan.FromSeconds(3)))System.Diagnostics.Trace.WriteLine("Shell STA did not stop within the bounded shutdown wait");}
        catch(Exception error){System.Diagnostics.Trace.WriteLine("Shell shutdown: "+error.Message);}
        thread.Join(TimeSpan.FromSeconds(1));
    }
}
