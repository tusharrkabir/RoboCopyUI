namespace RoboCopyUI;

/// <summary>All methods run on the owning Shell STA. One live task per cut generation.</summary>
public sealed class ClipboardTasks
{
    private readonly HashSet<uint> moving = new();
    public Lease Capture()
    {
        var snapshot=ClipboardManager.Read();
        if(snapshot.IsMove&&!moving.Add(snapshot.Sequence))
        {
            snapshot.Dispose();
            throw new InvalidOperationException(L.T("这次剪切已经有任务在运行或排队，请等待该任务结束。"));
        }
        return new Lease(this,snapshot);
    }
    public sealed class Lease : IDisposable
    {
        private readonly ClipboardTasks owner;
        private FileClipboardSnapshot? snapshot;
        private bool completed;
        internal Lease(ClipboardTasks owner,FileClipboardSnapshot snapshot){this.owner=owner;this.snapshot=snapshot;}
        public string[] Sources=>(snapshot??throw new ObjectDisposedException(nameof(Lease))).Sources;
        public bool IsMove=>(snapshot??throw new ObjectDisposedException(nameof(Lease))).IsMove;
        public string Complete(TransferAttemptResult result)
        {
            if(completed)return L.T("原剪切已处理。");
            var current=snapshot??throw new ObjectDisposedException(nameof(Lease));
            var notice=ClipboardManager.Complete(current,result);
            if(current.IsMove&&result.State==CopyState.Completed&&result.IsMove)completed=true;
            return notice;
        }
        public void Dispose()
        {
            if(snapshot==null)return;
            if(snapshot.IsMove)owner.moving.Remove(snapshot.Sequence);
            snapshot.Dispose();snapshot=null;
        }
    }
}
