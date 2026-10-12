using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DroidSwitcher;

internal record Account(string Id, string Label, string Subject, string Email, string Organization, DateTime SavedUtc);
internal record LoginState(Dictionary<string,byte[]> Files, string Subject, string Email, string Organization);
internal record Journal(Dictionary<string,byte[]> Files, string Active, byte[]? SystemKey=null,bool HasSystemKeySnapshot=false);
internal record PendingLogin(string Id,string Label,int ProcessId=0,long StartTicks=0);

internal sealed partial class Store
{
    public static string DroidPath=>AppPaths.Droid;
    public static string DesktopPath=>AppPaths.Desktop;
    public static readonly string[] AuthNames=["auth.v2.file","auth.v2.key","auth.v2.keyring","auth.v2.loginkeychain","auth.encrypted"];
    public string Root { get; }
    public string Live { get; }
    readonly IKeyring keyring;
    string AccountsDir=>Path.Combine(Root,"accounts");
    string JournalPath=>Path.Combine(Root,"pending.dpapi");
    string ActivePath=>Path.Combine(Root,"active.txt");
    string PendingLoginPath=>Path.Combine(Root,"pending-login.dpapi");
    public bool HasRecovery=>File.Exists(JournalPath);
    public string Active=>File.Exists(ActivePath) ? File.ReadAllText(ActivePath).Trim() : "";
    public Store(string? root=null,string? live=null,IKeyring? keyring=null) {
        this.keyring=keyring ?? new WindowsKeyring();
        Root=root ?? AppPaths.Data;
        Live=live ?? AppPaths.FactoryHome;
        Directory.CreateDirectory(AccountsDir); Restrict(Root);
    }
    public static void Restrict(string path) {
        var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void Atomic(string path,byte[] bytes) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) { f.Write(bytes); f.Flush(true); } File.Move(temp,path,true); }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    void SaveEncrypted<T>(string path,T value) { byte[] plain=JsonSerializer.SerializeToUtf8Bytes(value); try { Atomic(path,NativeCrypto.Protect(plain)); } finally { CryptographicOperations.ZeroMemory(plain); } }
    T ReadEncrypted<T>(string path) { byte[] plain=NativeCrypto.Protect(File.ReadAllBytes(path),true); try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("账号快照为空。"); } finally { CryptographicOperations.ZeroMemory(plain); } }
    public List<Account> Accounts() {
        var result=new List<Account>(); foreach(var file in Directory.GetFiles(AccountsDir,"*.dpapi")) {
            try { result.Add(ReadEncrypted<AccountBundle>(file).Account); }
            catch { throw new InvalidDataException("存在无法读取的账号快照。请保留 data 目录，使用原 Windows 用户打开工具。"); }
        } return result.OrderBy(x=>x.Label,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    internal record AccountBundle(Account Account,LoginState Login);
    string AccountPath(string id) { if(!Guid.TryParseExact(id,"N",out _)) throw new InvalidDataException("账号 ID 无效。"); return Path.Combine(AccountsDir,id+".dpapi"); }
    static Dictionary<string,byte[]> RawFiles(string home) {
        var files=new Dictionary<string,byte[]>(); foreach(var name in AuthNames) {
            var path=Path.Combine(home,name); if(!File.Exists(path)) continue;
            if(new FileInfo(path).Length>1024*1024) throw new InvalidDataException("凭据文件超过预期大小。");
            files[name]=File.ReadAllBytes(path);
        } return files;
    }
    static Dictionary<string,byte[]> SnapshotFiles(string home,IKeyring keyring) {
        var files=RawFiles(home);
        if(files.ContainsKey("auth.v2.keyring")) files["auth.v2.keyring.key"]=keyring.Read() ?? throw new IOException("Windows 凭据管理器中没有 Factory 加密密钥。请使用原 Windows 用户，保留登录目录并重新登录该账号。");
        return files;
    }
    public static LoginState Capture(string home,IKeyring? keyring=null) {
        keyring ??=new WindowsKeyring();
        if(File.Exists(Path.Combine(home,"auth.v2.write.lock"))) throw new IOException("Droid 正在更新凭据。请退出 Droid 后重试。");
        var files=SnapshotFiles(home,keyring); var state=Validate(files); var again=SnapshotFiles(home,keyring);
        if(!Equal(files,again)) throw new IOException("凭据正在变化，请退出 Droid 后重试。"); return state;
    }
    public static LoginState Validate(Dictionary<string,byte[]> files) {
        bool secure=files.Count==2 && files.ContainsKey("auth.v2.keyring") && files.ContainsKey("auth.v2.keyring.key");
        bool keyfile=files.Count==2 && files.ContainsKey("auth.v2.file") && files.ContainsKey("auth.v2.key");
        if(!secure && !keyfile) throw new InvalidDataException(files.Count==0?"还没有可保存的 Droid 登录，请先完成官方登录。":"凭据格式不兼容或存在多套认证文件。支持 Windows keyring 和 v2 密钥文件格式；原登录未修改。");
        byte[] key=[]; byte[] plain=[];
        try {
            key=Convert.FromBase64String(Encoding.UTF8.GetString(files[secure?"auth.v2.keyring.key":"auth.v2.key"]).Trim());
            var parts=Encoding.UTF8.GetString(files[secure?"auth.v2.keyring":"auth.v2.file"]).Trim().Split(':');
            if(key.Length!=32 || parts.Length!=3) throw new InvalidDataException("Droid 凭据格式不兼容。");
            byte[] nonce=Convert.FromBase64String(parts[0]), tag=Convert.FromBase64String(parts[1]), cipher=Convert.FromBase64String(parts[2]);
            if(nonce.Length!=16 || tag.Length!=16) throw new InvalidDataException("Droid 凭据格式不兼容。");
            plain=NativeCrypto.Gcm(key,nonce,tag,cipher);
            using var json=JsonDocument.Parse(plain); var obj=json.RootElement;
            var access=Get(obj,"access_token"); if(access.Length==0 || Get(obj,"refresh_token").Length==0) throw new InvalidDataException("登录未完成或缺少刷新令牌。");
            var jwtParts=access.Split('.'); if(jwtParts.Length!=3) throw new InvalidDataException("无法确认登录账号身份。");
            var segment=jwtParts[1].Replace('-','+').Replace('_','/'); segment=segment.PadRight((segment.Length+3)/4*4,'=');
            using var jwt=JsonDocument.Parse(Convert.FromBase64String(segment)); var subject=Get(jwt.RootElement,"sub");
            if(subject.Length==0) throw new InvalidDataException("登录令牌中缺少账号身份。");
            return new LoginState(files,subject,Get(jwt.RootElement,"email"),Get(obj,"active_organization_id"));
        } catch(FormatException) { throw new InvalidDataException("Droid 凭据编码不兼容。"); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    static string Get(JsonElement e,string name)=>e.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static bool Equal(Dictionary<string,byte[]> a,Dictionary<string,byte[]> b)=>a.Count==b.Count && a.All(p=>b.TryGetValue(p.Key,out var bytes) && p.Value.AsSpan().SequenceEqual(bytes));
    FileStream Lock()=>new(Path.Combine(Root,"store.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    public static void RequireIdle() {
        var blockers=new List<string>();
        foreach(var name in new[]{"droid","factory-desktop"}) {
            var processes=Process.GetProcessesByName(name); try { blockers.AddRange(processes.Select(p=>name+" (PID "+p.Id+")")); }
            finally { foreach(var p in processes) p.Dispose(); }
        }
        if(blockers.Count>0) throw new IOException("切换账号需要退出正在使用当前登录态的 Droid / Factory。检测到后台进程：\n"+string.Join("、",blockers)+"\n添加和保存账号无需退出其他客户端；切换时不会强制结束任务。");
    }
    void Ready(bool test,bool requireIdle=true) { if(!test && requireIdle) RequireIdle(); if(HasRecovery) throw new IOException("有一次未完成的切换，请先点击恢复。"); }
    public Account SaveCurrent(string label,bool test=false)=>SaveFrom(Live,label,true,test);
    public Account SaveFrom(string home,string label,bool current=false,bool test=false) {
        using var guard=Lock(); Ready(test,false); var login=Capture(home,keyring); label=label.Trim();
        if(label.Length==0 || label.Length>80) throw new ArgumentException("请输入 1–80 个字符的账号名称。");
        var existing=Accounts().FirstOrDefault(x=>x.Subject==login.Subject && x.Organization==login.Organization);
        var account=new Account(existing?.Id ?? Guid.NewGuid().ToString("N"),label,login.Subject,login.Email,login.Organization,DateTime.UtcNow);
        SaveEncrypted(AccountPath(account.Id),new AccountBundle(account,login));
        // A fresh OAuth login supersedes the saved copy. Do not later sync the older
        // live session over it; the next explicit switch will back up live auth first.
        if(!current && existing?.Id==Active) Atomic(ActivePath,[]);
        if(current) Atomic(ActivePath,Encoding.UTF8.GetBytes(account.Id)); return account;
    }
    void SyncActive() {
        if(Active.Length==0) return;
        if(!File.Exists(Path.Combine(Live,"auth.v2.file")) && !File.Exists(Path.Combine(Live,"auth.v2.keyring"))) return; // Explicit logout must not erase the saved login.
        var bundle=ReadEncrypted<AccountBundle>(AccountPath(Active)); var current=Capture(Live,keyring);
        if(current.Subject!=bundle.Account.Subject || current.Organization!=bundle.Account.Organization) throw new IOException("当前 Droid 登录与工具记录不一致。请用“保存当前登录”先导入，避免覆盖其他账号。");
        SaveEncrypted(AccountPath(Active),new AccountBundle(bundle.Account with {SavedUtc=DateTime.UtcNow},current));
    }
    void Apply(Dictionary<string,byte[]> files,Action? afterFirst=null) {
        if(files.Keys.Any(x=>!AuthNames.Contains(x) && x!="auth.v2.keyring.key")) throw new InvalidDataException("恢复数据包含未知文件。");
        if(files.TryGetValue("auth.v2.keyring.key",out var rawKey)) keyring.Write(rawKey);
        Directory.CreateDirectory(Live); var names=files.Keys.Where(x=>x!="auth.v2.keyring.key").OrderBy(x=>x=="auth.v2.key" ? 0 : 1).ToList();
        for(int i=0;i<names.Count;i++) { Atomic(Path.Combine(Live,names[i]),files[names[i]]); if(i==0) afterFirst?.Invoke(); }
        foreach(var name in AuthNames) if(!files.ContainsKey(name) && File.Exists(Path.Combine(Live,name))) File.Delete(Path.Combine(Live,name));
    }
    public void Switch(string id,bool test=false,Action? injectFailure=null) {
        using var guard=Lock(); Ready(test);
        if(Pending()!=null) throw new IOException("请先保存或放弃待保存的新登录，再切换账号。");
        if(SessionBackups().Any(b=>b.State=="pending")) throw new IOException("会话共享中断，请先在“会话共享”页恢复未完成的备份，再切换账号。");
        var bundle=ReadEncrypted<AccountBundle>(AccountPath(id)); var target=Validate(bundle.Login.Files);
        if(target.Subject!=bundle.Account.Subject || target.Organization!=bundle.Account.Organization) throw new InvalidDataException("账号快照身份不一致。");
        SyncActive(); var raw=SnapshotFiles(Live,keyring); if(raw.Count>0) Validate(raw);
        var journal=new Journal(raw,Active,keyring.Read(),true); SaveEncrypted(JournalPath,journal);
        try {
            if(!test) RequireIdle(); if(File.Exists(Path.Combine(Live,"auth.v2.write.lock"))) throw new IOException("Droid 凭据被锁定。");
            Apply(target.Files,injectFailure);
            if(!test) RequireIdle();
            if(!Equal(Capture(Live,keyring).Files,target.Files)) throw new IOException("切换后的凭据校验失败。");
            Atomic(ActivePath,Encoding.UTF8.GetBytes(id)); ArchiveJournal();
        } catch {
            // Refuse rollback if a Droid process started during the transaction. Keep the journal for recovery.
            if(!test) RequireIdle(); RestoreSystemKey(journal); Apply(journal.Files); Atomic(ActivePath,Encoding.UTF8.GetBytes(journal.Active));
            if(!Equal(SnapshotFiles(Live,keyring),journal.Files)) throw new IOException("回滚未完成，请保留数据并点击恢复。");
            ArchiveJournal(); throw;
        }
    }
    void ArchiveJournal() {
        var backups=Path.Combine(Root,"backups"); Directory.CreateDirectory(backups);
        File.Move(JournalPath,Path.Combine(backups,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".dpapi"));
    }
    public void Recover(bool test=false) {
        using var guard=Lock(); if(!test) RequireIdle(); if(!HasRecovery) return;
        var journal=ReadEncrypted<Journal>(JournalPath); if(journal.Files.Count>0) Validate(journal.Files);
        RestoreSystemKey(journal); Apply(journal.Files); if(!Equal(SnapshotFiles(Live,keyring),journal.Files)) throw new IOException("恢复校验失败。");
        Atomic(ActivePath,Encoding.UTF8.GetBytes(journal.Active)); ArchiveJournal();
    }
    public void Remove(string id) {
        using var guard=Lock(); if(HasRecovery) throw new IOException("请先恢复未完成的切换。");
        File.Delete(AccountPath(id)); if(Active==id) Atomic(ActivePath,[]);
    }
    public PendingLogin? Pending()=>File.Exists(PendingLoginPath)?ReadEncrypted<PendingLogin>(PendingLoginPath):null;
    public string LoginHome(PendingLogin pending) {
        if(!Guid.TryParseExact(pending.Id,"N",out _)) throw new InvalidDataException("登录目录标识无效。");
        return Path.Combine(Root,"logins",pending.Id,".factory");
    }
    public PendingLogin CreateLogin(string label,bool test=false) {
        using var guard=Lock(); Ready(test,false); if(Pending()!=null) throw new IOException("请先保存或放弃上次新登录。");
        if(string.IsNullOrWhiteSpace(label) || label.Length>80) throw new ArgumentException("请输入 1–80 个字符的账号名称。");
        var pending=new PendingLogin(Guid.NewGuid().ToString("N"),label.Trim()); var home=LoginHome(pending);
        Directory.CreateDirectory(home); Restrict(Path.GetDirectoryName(home)!); SaveEncrypted(PendingLoginPath,pending); return pending;
    }
    public void ClearPending(bool test=false) {
        using var guard=Lock(); var pending=Pending(); if(pending==null) return;
        if(!test) EnsureLoginExited(pending);
        if(File.Exists(Path.Combine(LoginHome(pending),"auth.v2.write.lock"))) throw new IOException("新登录正在更新凭据，请先退出它的登录终端。");
        foreach(var name in AuthNames) {var p=Path.Combine(LoginHome(pending),name); if(File.Exists(p)) File.Delete(p);}
        File.Delete(PendingLoginPath);
    }
    public void TrackLogin(Process process) {
        using var guard=Lock(); var pending=Pending() ?? throw new IOException("没有待保存的新登录。");
        SaveEncrypted(PendingLoginPath,pending with {ProcessId=process.Id,StartTicks=process.StartTime.ToUniversalTime().Ticks});
    }
    public static void EnsureLoginExited(PendingLogin pending) {
        if(pending.ProcessId==0) return;
        try {using var p=Process.GetProcessById(pending.ProcessId); if(!p.HasExited && p.StartTime.ToUniversalTime().Ticks==pending.StartTicks) throw new IOException("请先 Ctrl+C 退出这个新账号的登录终端，其他客户端可以保持打开。");}
        catch(ArgumentException) { /* The original login shell has exited. */ }
    }
    void RestoreSystemKey(Journal journal) {if(!journal.HasSystemKeySnapshot) return; if(journal.SystemKey==null) keyring.Delete(); else keyring.Write(journal.SystemKey);}
    internal void FakeJournalForTest() { SaveEncrypted(JournalPath,new Journal(SnapshotFiles(Live,keyring),Active,keyring.Read(),true)); }
}
