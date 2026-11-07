using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RoboCopyUI;

public sealed record VolumeCapabilities(string Root,string FileSystem,uint Serial,uint Flags)
{
    public bool PreservesAcl => (Flags&8)!=0;
    public bool NamedStreams => (Flags&0x40000)!=0;
    public TimeSpan OrdinaryTimestampTolerance => FileSystem.Contains("FAT",StringComparison.OrdinalIgnoreCase)
        ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;

    public static VolumeCapabilities Read(string path)
    {
        var existing=Path.GetFullPath(path);
        while(!File.Exists(existing)&&!Directory.Exists(existing))
            existing=Path.GetDirectoryName(existing) ?? throw new IOException(L.F("目标卷不可访问：{0}", path));
        var root=new StringBuilder(32768);
        if(!GetVolumePathName(TransferSafety.NativePath(existing),root,root.Capacity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),L.F("无法确认目标卷：{0}", path));
        var fileSystem=new StringBuilder(64);
        if(!GetVolumeInformation(root.ToString(),nint.Zero,0,out var serial,out _,out var flags,fileSystem,fileSystem.Capacity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),L.F("无法确认文件系统：{0}", root));
        return new(root.ToString(),fileSystem.ToString(),serial,flags);
    }
    [DllImport("kernel32.dll",EntryPoint="GetVolumePathNameW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool GetVolumePathName(string file,StringBuilder root,int capacity);
    [DllImport("kernel32.dll",EntryPoint="GetVolumeInformationW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool GetVolumeInformation(string root,nint volumeName,int volumeCapacity,out uint serial,out uint maximumComponent,out uint flags,StringBuilder fileSystem,int fileSystemCapacity);
}
