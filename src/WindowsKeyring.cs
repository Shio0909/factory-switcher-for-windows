using System.Runtime.InteropServices;

namespace DroidSwitcher;
internal interface IKeyring {byte[]? Read(); void Write(byte[] raw); void Delete();}
internal sealed class WindowsKeyring : IKeyring
{
    readonly string Target;
    internal WindowsKeyring(string? testTarget=null) {Target=testTarget ?? "Factory CLI/auth-encryption-key";}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Credential {
        public uint Flags,Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize; public IntPtr Blob; public uint Persist,AttributeCount;
        public IntPtr Attributes; public string? TargetAlias; public string UserName;
    }
    [DllImport("advapi32",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CredRead(string target,uint type,int flags,out IntPtr value);
    [DllImport("advapi32",EntryPoint="CredWriteW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CredWrite(ref Credential value,uint flags);
    [DllImport("advapi32",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CredDelete(string target,uint type,int flags);
    [DllImport("advapi32")] static extern void CredFree(IntPtr value);
    public byte[]? Read() {
        if(!CredRead(Target,1,0,out var pointer)) {if(Marshal.GetLastWin32Error()==1168) return null; throw new IOException("无法读取 Factory 的 Windows 凭据。请以完成 Droid 登录的同一 Windows 用户运行工具。");}
        try {var credential=Marshal.PtrToStructure<Credential>(pointer); if(credential.BlobSize>8192) throw new IOException("Factory 系统凭据格式不兼容。"); var raw=new byte[credential.BlobSize]; Marshal.Copy(credential.Blob,raw,0,raw.Length); return raw;}
        finally {CredFree(pointer);}
    }
    public void Write(byte[] raw) {
        var memory=Marshal.AllocHGlobal(raw.Length);
        try {Marshal.Copy(raw,0,memory,raw.Length); var credential=new Credential {Type=1,TargetName=Target,UserName="auth-encryption-key",BlobSize=(uint)raw.Length,Blob=memory,Persist=3};
            if(!CredWrite(ref credential,0)) throw new IOException("写入 Factory 系统密钥失败，恢复记录已保留。");
            var verified=Read(); if(verified==null || !raw.AsSpan().SequenceEqual(verified)) throw new IOException("系统密钥写入校验失败。");
        } finally {for(int i=0;i<raw.Length;i++) Marshal.WriteByte(memory,i,0); Marshal.FreeHGlobal(memory);}
    }
    public void Delete() {if(!CredDelete(Target,1,0) && Marshal.GetLastWin32Error()!=1168) throw new IOException("恢复原系统密钥状态失败。");}
}
internal sealed class MemoryKeyring : IKeyring {
    byte[]? raw; public byte[]? Read()=>raw?.ToArray(); public void Write(byte[] value)=>raw=value.ToArray(); public void Delete()=>raw=null;
}
