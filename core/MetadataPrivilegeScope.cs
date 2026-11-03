using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace RoboCopyUI;

/// <summary>Existing token privileges only. Used by the one-task elevated worker, never the tray.</summary>
public sealed class MetadataPrivilegeScope : IDisposable
{
    private readonly SafeAccessTokenHandle token;
    private readonly List<TokenPrivilege> previous = new();
    private bool disposed;
    private static readonly string[] Names=["SeBackupPrivilege","SeRestorePrivilege","SeSecurityPrivilege"];
    public List<string> Unavailable { get; } = new();
    public MetadataPrivilegeScope()
    {
        if(!OpenProcessToken(new nint(-1),0x28,out token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        foreach(var name in Names)
        {
            if(!LookupPrivilegeValue(null,name,out var luid)){Unavailable.Add(name);continue;}
            var desired=new TokenPrivilege{Count=1,Luid=luid,Attributes=2};
            if(!AdjustTokenPrivileges(token,false,ref desired,(uint)Marshal.SizeOf<TokenPrivilege>(),out var old,out _) || Marshal.GetLastWin32Error()==1300)
                Unavailable.Add(name);
            else previous.Add(old);
        }
    }
    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        foreach(var saved in previous){var state=saved;RestoreTokenPrivileges(token,false,ref state,0,nint.Zero,nint.Zero);}
        previous.Clear();token.Dispose();
    }
    public static Dictionary<string,bool> CurrentState()
    {
        if(!OpenProcessToken(new nint(-1),8,out var current))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using(current)
        {
            var result=new Dictionary<string,bool>();
            foreach(var name in Names)
            {
                if(!LookupPrivilegeValue(null,name,out var luid))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var set=new PrivilegeSet{Count=1,Control=1,Luid=luid,Attributes=2};
                if(!PrivilegeCheck(current,ref set,out var enabled))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                result[name]=enabled;
            }
            return result;
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct Luid {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct TokenPrivilege {public uint Count;public Luid Luid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct PrivilegeSet {public uint Count,Control;public Luid Luid;public uint Attributes;}
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(nint process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,bool disableAll,ref TokenPrivilege state,uint size,out TokenPrivilege previous,out uint returned);
    [DllImport("advapi32.dll",EntryPoint="AdjustTokenPrivileges",SetLastError=true)]private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token,bool disableAll,ref TokenPrivilege state,uint size,nint previous,nint returned);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool PrivilegeCheck(SafeAccessTokenHandle token,ref PrivilegeSet required,out bool enabled);
}
