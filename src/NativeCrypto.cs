using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace DroidSwitcher;

// Droid uses a 16-byte GCM nonce. Windows CNG and .NET AesGcm require 12;
// use the maintained Bouncy Castle implementation for format compatibility.
internal static class NativeCrypto
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32", SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr ptr);
    public static byte[] Protect(byte[] bytes, bool decrypt=false) {
        var input = new Blob { Size=bytes.Length, Data=Marshal.AllocHGlobal(bytes.Length) }; Blob output=default;
        try {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            bool ok=decrypt ? CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output) : CryptProtectData(ref input,"Droid Switcher account",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok) throw new CryptographicException("无法解密此账号快照；请使用创建它的 Windows 用户。");
            byte[] result=new byte[output.Size]; Marshal.Copy(output.Data,result,0,result.Length); return result;
        } finally { for(int i=0;i<input.Size;i++) Marshal.WriteByte(input.Data,i,0); Marshal.FreeHGlobal(input.Data); if(output.Data!=IntPtr.Zero) { for(int i=0;i<output.Size;i++) Marshal.WriteByte(output.Data,i,0); LocalFree(output.Data); } }
    }
    public static byte[] Gcm(byte[] key, byte[] nonce, byte[] tag, byte[] input, bool encrypt=false) {
        var cipher=new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt,new AeadParameters(new KeyParameter(key),128,nonce));
        byte[] combined=encrypt ? input : [..input,..tag];
        byte[] output=new byte[cipher.GetOutputSize(combined.Length)];
        try {
            int count=cipher.ProcessBytes(combined,0,combined.Length,output,0); count+=cipher.DoFinal(output,count);
            if(encrypt) { Array.Copy(output,count-16,tag,0,16); return output[..(count-16)]; }
            return output;
        } catch { CryptographicOperations.ZeroMemory(output); throw new CryptographicException("Droid 凭据加密校验失败。"); }
        finally { if(!encrypt) CryptographicOperations.ZeroMemory(combined); }
    }
}
