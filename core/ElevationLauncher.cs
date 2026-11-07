using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RoboCopyUI;

public static class ElevationLauncher
{
    public static Process Start(StoredTaskRequest request)
    {
        var executable=Environment.ProcessPath ?? throw new IOException(L.T("无法定位当前程序。"));
        var installedAssembly=Path.Combine(ShellRegistry.InstallRoot,"App","RoboCopyUI.dll");
        if(string.Equals(Path.GetFullPath(typeof(App).Assembly.Location),installedAssembly,StringComparison.OrdinalIgnoreCase))
        {
            // The GUI-subsystem launcher waits for the worker and starts dotnet
            // with CREATE_NO_WINDOW, including when elevated through runas.
            ShellRegistry.RequireInstalled();executable=ShellRegistry.AppPath;
        }
        var start=new ProcessStartInfo(executable){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
        // Framework-dependent test launches use the same verified application assembly.
        if(Path.GetFileName(executable).Equals("dotnet.exe",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(App).Assembly.Location);
        start.ArgumentList.Add("--elevated-transfer");
        start.ArgumentList.Add(request.Path);start.ArgumentList.Add(request.Hash);
        return Process.Start(start) ?? throw new IOException(L.T("管理员任务未启动。"));
    }
    public static bool IsUserCancellation(Exception error)=>error is Win32Exception{NativeErrorCode:1223};
}

public sealed record TransferAttemptResult(CopyState State,bool FullPreservation,string[] Messages,string[] Log,bool IsMove=false);
