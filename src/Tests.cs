using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DroidSwitcher;
internal static partial class Tests
{
    static Dictionary<string,byte[]> Fixture(string subject,string organization="org-test",string refresh="test-refresh") {
        var segment=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {sub=subject,email=subject+"@example.test"})).TrimEnd('=').Replace('+','-').Replace('/','_');
        var plain=JsonSerializer.SerializeToUtf8Bytes(new {access_token="e30."+segment+".fake-signature",refresh_token=refresh,active_organization_id=organization});
        var key=RandomNumberGenerator.GetBytes(32); var nonce=RandomNumberGenerator.GetBytes(16); var tag=new byte[16]; var cipher=NativeCrypto.Gcm(key,nonce,tag,plain,true);
        return new() { ["auth.v2.file"]=Encoding.UTF8.GetBytes(string.Join(':',Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher))),["auth.v2.key"]=Encoding.UTF8.GetBytes(Convert.ToBase64String(key)) };
    }
    static void Write(string home,Dictionary<string,byte[]> files) { Directory.CreateDirectory(home); foreach(var pair in files) File.WriteAllBytes(Path.Combine(home,pair.Key),pair.Value); }
    static void Assert(bool condition) { if(!condition) throw new Exception("Assertion failed"); }
    static void MustFail(Action action) { bool failed=false; try {action();} catch {failed=true;} Assert(failed); }
    public static int Run(string resultsDir) {
        resultsDir=Path.GetFullPath(resultsDir); Directory.CreateDirectory(resultsDir);
        string root=Path.Combine(resultsDir,"synthetic-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var results=new List<object>(); bool failed=false;
        void Test(string name,Action action) { try {action(); results.Add(new {name,passed=true});} catch(Exception ex) {failed=true;results.Add(new {name,passed=false,error=ex.GetType().Name});} }
        Test("AES-GCM 16-byte nonce cross-check against Python cryptography",()=> {
            var key=Enumerable.Range(0,32).Select(x=>(byte)x).ToArray(); var nonce=Enumerable.Range(0,16).Select(x=>(byte)x).ToArray();
            var plain=NativeCrypto.Gcm(key,nonce,Convert.FromHexString("6c93fbbb764c546446e7ab38fca36e44"),Convert.FromHexString("010dc30256963305f98db44031895bda3ad704d614a0a726374a"));
            Assert(Encoding.UTF8.GetString(plain)=="factory-droid-windows-test");
        });
        Test("DPAPI round-trip and plaintext absent on disk",()=> { var plain=Encoding.UTF8.GetBytes("fake-sensitive-credential"); var encrypted=NativeCrypto.Protect(plain); Assert(encrypted.Length>plain.Length); Assert(!Encoding.UTF8.GetString(encrypted).Contains("fake-sensitive")); Assert(NativeCrypto.Protect(encrypted,true).AsSpan().SequenceEqual(plain)); });
        Test("Authentication tampering rejected",()=> { var files=Fixture("tampered"); var value=Encoding.UTF8.GetString(files["auth.v2.file"]).Split(':'); var tag=Convert.FromBase64String(value[1]); tag[0]^=1; value[1]=Convert.ToBase64String(tag); files["auth.v2.file"]=Encoding.UTF8.GetBytes(string.Join(':',value)); MustFail(()=>Store.Validate(files)); });
        Test("Unknown secure storage rejected",()=> { var files=Fixture("unknown"); files["auth.v2.keyring"]=[1,2,3]; MustFail(()=>Store.Validate(files)); });
        Test("Incomplete login rejected",()=>MustFail(()=>Store.Validate(Fixture("incomplete",refresh:""))));
        var live=Path.Combine(root,"live"); var memoryKeyring=new MemoryKeyring(); var store=new Store(Path.Combine(root,"store"),live,memoryKeyring); var loginA=Fixture("account-a"); var loginB=Fixture("account-b"); Account? a=null,b=null;
        Test("Capture and account deduplication",()=> {Write(live,loginA); a=store.SaveCurrent("账号 A",true); var again=store.SaveCurrent("A 新名字",true); Assert(a.Id==again.Id && store.Accounts().Count==1); a=again; var isolated=Path.Combine(root,"isolated"); Write(isolated,loginB); b=store.SaveFrom(isolated,"账号 B",test:true); Assert(store.Accounts().Count==2); });
        Test("Switch preserves settings and session organization markers",()=> { File.WriteAllText(Path.Combine(live,"settings.json"),"keep-settings"); Directory.CreateDirectory(Path.Combine(live,"sessions")); File.WriteAllText(Path.Combine(live,"sessions","session.jsonl"),"keep-org-binding"); store.Switch(b!.Id,true); Assert(Store.Capture(live).Subject=="account-b" && store.Active==b.Id); Assert(File.ReadAllText(Path.Combine(live,"settings.json"))=="keep-settings" && File.ReadAllText(Path.Combine(live,"sessions","session.jsonl"))=="keep-org-binding"); });
        Test("Failure between credential writes rolls back",()=> {MustFail(()=>store.Switch(a!.Id,true,()=>throw new IOException("Injected fault"))); Assert(Store.Capture(live).Subject=="account-b" && store.Active==b!.Id && !store.HasRecovery); });
        Test("Crash journal survives restart and restores previous auth",()=> { store.FakeJournalForTest(); Write(live,loginA); var restarted=new Store(store.Root,live,memoryKeyring); Assert(restarted.HasRecovery); MustFail(()=>restarted.Switch(a!.Id,true)); restarted.Recover(true); Assert(Store.Capture(live).Subject=="account-b" && !restarted.HasRecovery && restarted.Active==b!.Id); });
        Test("Externally changed login cannot overwrite saved account",()=> {Write(live,loginA); MustFail(()=>store.Switch(a!.Id,true)); Assert(Store.Capture(live).Subject=="account-a" && !store.HasRecovery); Write(live,loginB); });
        Test("Refreshed current auth is synchronized before switching",()=> {var refreshed=Fixture("account-b",refresh:"new-test-refresh"); Write(live,refreshed); store.Switch(a!.Id,true); store.Switch(b!.Id,true); Assert(Store.Equal(Store.Capture(live).Files,refreshed)); });
        Test("Store lock blocks concurrent mutations",()=> { using var held=new FileStream(Path.Combine(store.Root,"store.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); MustFail(()=>store.Switch(a!.Id,true)); });
        Test("Droid write lock blocks credential reads",()=> { File.WriteAllText(Path.Combine(live,"auth.v2.write.lock"),"synthetic"); MustFail(()=>Store.Capture(live)); File.Delete(Path.Combine(live,"auth.v2.write.lock")); });
        Test("Removing a snapshot leaves live auth unchanged",()=> {var before=Store.Capture(live).Files; store.Remove(a!.Id); Assert(store.Accounts().Count==1 && Store.Equal(before,Store.Capture(live).Files)); });
        Test("New login persists across restart and cancellation preserves saved accounts",()=> {var pending=store.CreateLogin("测试登录",true); Write(store.LoginHome(pending),Fixture("new-login")); var reopened=new Store(store.Root,live,memoryKeyring); Assert(reopened.Pending()?.Id==pending.Id); var before=Store.Capture(live).Files; reopened.ClearPending(true); Assert(reopened.Pending()==null && !File.Exists(Path.Combine(reopened.LoginHome(pending),"auth.v2.key"))); Assert(reopened.Accounts().Count==1 && Store.Equal(before,Store.Capture(live).Files)); });
        Test("Reauthentication cannot be overwritten by old active login",()=> {var renewed=Fixture("account-b",refresh:"renewed-oauth"); var isolated=Path.Combine(root,"reauth"); Write(isolated,renewed); var updated=store.SaveFrom(isolated,"B 重新登录",test:true); Assert(updated.Id==b!.Id && store.Active==""); store.Switch(updated.Id,true); Assert(Store.Equal(Store.Capture(live).Files,renewed)); });
        Test("Windows credential API round-trip using a disposable test entry",()=> {
            var native=new WindowsKeyring("DroidSwitcher.Test/"+Guid.NewGuid().ToString("N"));
            try {Assert(native.Read()==null); var fakeKey=Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); native.Write(fakeKey); Assert(native.Read()!.AsSpan().SequenceEqual(fakeKey));}
            finally {native.Delete();}
        });
        Test("Secure snapshots switch keys and rollback restores the original system key",()=> {
            var mem=new MemoryKeyring(); var secureLive=Path.Combine(root,"secure-live"); var s=new Store(Path.Combine(root,"secure-store"),secureLive,mem);
            var x=Fixture("secure-a"); Write(secureLive,new() {["auth.v2.keyring"]=x["auth.v2.file"]}); mem.Write(x["auth.v2.key"]); var savedA=s.SaveCurrent("secure-a",true);
            var y=Fixture("secure-b"); var isolated=Path.Combine(root,"secure-isolated"); Write(isolated,new() {["auth.v2.keyring"]=y["auth.v2.file"]}); mem.Write(y["auth.v2.key"]); var savedB=s.SaveFrom(isolated,"secure-b",test:true);
            mem.Write(x["auth.v2.key"]); s.Switch(savedB.Id,true); Assert(Store.Capture(secureLive,mem).Subject=="secure-b"); Assert(mem.Read()!.AsSpan().SequenceEqual(y["auth.v2.key"]));
            Assert(!File.Exists(Path.Combine(secureLive,"auth.v2.keyring.key")) && !File.Exists(Path.Combine(secureLive,"auth.v2.key")));
            MustFail(()=>s.Switch(savedA.Id,true,()=>throw new IOException("Synthetic failure"))); Assert(Store.Capture(secureLive,mem).Subject=="secure-b" && mem.Read()!.AsSpan().SequenceEqual(y["auth.v2.key"]));
            s.FakeJournalForTest(); mem.Write(x["auth.v2.key"]); Write(secureLive,new() {["auth.v2.keyring"]=x["auth.v2.file"]}); var reopened=new Store(s.Root,secureLive,mem); reopened.Recover(true); Assert(Store.Capture(secureLive,mem).Subject=="secure-b");
        });
        Test("Switching from empty auth restores the original absent system credential on failure",()=> {
            var mem=new MemoryKeyring(); var blank=Path.Combine(root,"blank"); var s=new Store(Path.Combine(root,"blank-store"),blank,mem); var x=Fixture("secure-first"); var isolated=Path.Combine(root,"first-login"); Write(isolated,new() {["auth.v2.keyring"]=x["auth.v2.file"]}); mem.Write(x["auth.v2.key"]); var saved=s.SaveFrom(isolated,"first",test:true); mem.Delete();
            MustFail(()=>s.Switch(saved.Id,true,()=>throw new IOException("Synthetic failure"))); Assert(mem.Read()==null && !File.Exists(Path.Combine(blank,"auth.v2.keyring")));
        });
        Test("Background Droid blocks switching but allows isolated login and snapshot saving",()=> {
            var executable=Environment.ProcessPath!; if(Path.GetFileName(executable).Equals("dotnet.exe",StringComparison.OrdinalIgnoreCase)) throw new Exception("Run tests using the published executable");
            var fake=Path.Combine(root,"droid.exe"); File.Copy(executable,fake);
            using var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake,"--test-wait") {UseShellExecute=false,CreateNoWindow=true})!;
            try {Assert(!child.HasExited); MustFail(Store.RequireIdle); store.CreateLogin("后台运行时添加"); store.SaveCurrent("后台运行时保存"); store.ClearPending(); Assert(store.Pending()==null);}
            finally {if(!child.HasExited) {child.Kill(); child.WaitForExit();}}
        });
        FeatureTests(Test,root);
        File.WriteAllText(Path.Combine(resultsDir,"self-test.json"),JsonSerializer.Serialize(new {passed=!failed,tests=results},new JsonSerializerOptions {WriteIndented=true}));
        // Synthetic files only; no real Factory directory or credentials are read in self-tests.
        if(Path.GetFullPath(root).StartsWith(resultsDir+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true);
        return failed?1:0;
    }
}
