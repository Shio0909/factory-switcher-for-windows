using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DroidSwitcher;
internal record LocalSession(string Id,string Relative,string Title,string Organization,string Cwd,DateTime Updated);
internal record SessionEdit(string Relative,string Id,string Digest,byte[] OriginalHeader,byte[] NewHeader,string BackupFile);
internal record SessionBackup(string Id,DateTime CreatedUtc,List<SessionEdit> Edits,string State);
internal sealed partial class Store
{
    string SessionsRoot=>Path.Combine(Live,"sessions");
    string SessionBackupsRoot=>Path.Combine(Root,"session-backups");
    internal static string Scoped(string root,string relative) {
        if(string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("会话路径无效。");
        var parts=relative.Replace('\\','/').Split('/'); if(parts.Any(p=>p is "" or "." or "..")) throw new IOException("会话路径越出允许目录。");
        var full=Path.GetFullPath(Path.Combine(root,relative)); var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new IOException("会话路径越出允许目录。");
        var current=Path.GetFullPath(root); if(Directory.Exists(current) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("会话根目录不能是符号链接。");
        foreach(var part in parts) {current=Path.Combine(current,part); if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("会话路径中存在符号链接，已跳过。");} return full;
    }
    static byte[] Header(string path) {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite); using var output=new MemoryStream();
        int value; while((value=input.ReadByte())>=0) {output.WriteByte((byte)value); if(output.Length>256*1024) throw new IOException("会话头部过大。"); if(value==10) break;} return output.ToArray();
    }
    static JsonObject HeaderJson(byte[] bytes) {var data=bytes.AsSpan(); if(data.StartsWith(new byte[]{239,187,191})) data=data[3..]; return JsonNode.Parse(data)?.AsObject() ?? throw new IOException("会话头部无效。");}
    static string Field(JsonObject obj,string key)=>obj[key] is JsonValue value && value.TryGetValue<string>(out var text)?text:"";
    static string SessionId(JsonObject obj,string relative)=>Field(obj,"id") is string id && id.Length>0 ? id : Field(obj,"sessionId") is string sid && sid.Length>0 ? sid : Path.GetFileNameWithoutExtension(relative);
    static byte[] EncodeHeader(JsonObject obj,byte[] before) {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(obj); if(before.AsSpan().StartsWith(new byte[]{239,187,191})) bytes=[239,187,191,..bytes];
        if(before.Length>0 && before[^1]==10) return before.Length>1 && before[^2]==13 ? [..bytes,13,10] : [..bytes,10]; return bytes;
    }
    public List<LocalSession> Sessions() {
        var result=new List<LocalSession>(); if(!Directory.Exists(SessionsRoot)) return result;
        var options=new EnumerationOptions {RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.Hidden,IgnoreInaccessible=true};
        foreach(var path in Directory.EnumerateFiles(SessionsRoot,"*.jsonl",options)) {
            try {var relative=Path.GetRelativePath(SessionsRoot,path); _=Scoped(SessionsRoot,relative); var obj=HeaderJson(Header(path)); if(Field(obj,"type")!="session_start") continue;
                var id=SessionId(obj,relative); if(!Guid.TryParse(id,out _)) continue; result.Add(new LocalSession(id,relative,Field(obj,"title") is string title && title.Length>0?title:id,Field(obj,"organizationId"),Field(obj,"cwd"),File.GetLastWriteTime(path)));
            } catch(IOException) {} catch(JsonException) {} catch(InvalidOperationException) {}
        } return result.OrderByDescending(x=>x.Updated).ToList();
    }
    static byte[] SessionBytes(string path) {if(new FileInfo(path).Length>128*1024*1024) throw new IOException("此会话超过 128MB，请单独处理，当前文件未修改。"); return File.ReadAllBytes(path);}
    static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    static byte[] Rewrite(byte[] bytes,byte[] expected,byte[] replacement) {if(!bytes.AsSpan().StartsWith(expected)) throw new IOException("会话头部发生变化，已停止操作。"); return [..replacement,..bytes.AsSpan(expected.Length).ToArray()];}
    public List<SessionBackup> SessionBackups() {
        if(!Directory.Exists(SessionBackupsRoot)) return []; var result=new List<SessionBackup>();
        foreach(var directory in Directory.EnumerateDirectories(SessionBackupsRoot)) {var id=Path.GetFileName(directory); if(!Guid.TryParseExact(id,"N",out _)) continue; var path=Scoped(SessionBackupsRoot,id+"/manifest.dpapi"); if(File.Exists(path)) result.Add(ReadEncrypted<SessionBackup>(path));}
        return result.OrderByDescending(x=>x.CreatedUtc).ToList();
    }
    public SessionBackup ShareSessions(IEnumerable<string> relatives,bool test=false,Action? injectFailure=null) {
        using var guard=Lock(); Ready(test); if(SessionBackups().Any(b=>b.State=="pending")) throw new IOException("上次会话共享中断，请先在共享页恢复该备份。");
        var selected=relatives.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); if(selected.Count==0) throw new IOException("请勾选要共享的会话。");
        var id=Guid.NewGuid().ToString("N"); var root=Scoped(SessionBackupsRoot,id); Directory.CreateDirectory(root); Restrict(root); var edits=new List<SessionEdit>();
        foreach(var relative in selected) {
            var path=Scoped(SessionsRoot,relative); var bytes=SessionBytes(path); var header=Header(path); if(!bytes.AsSpan().StartsWith(header)) throw new IOException("会话读取期间发生变化。"); var obj=HeaderJson(header);
            if(Field(obj,"type")!="session_start" || !Guid.TryParse(SessionId(obj,relative),out _)) throw new IOException("会话格式无法识别。"); if(Field(obj,"organizationId").Length==0) continue;
            var name=edits.Count+".dpapi"; var digest=Digest(bytes); var encrypted=NativeCrypto.Protect(bytes); Atomic(Path.Combine(root,name),encrypted);
            if(Digest(NativeCrypto.Protect(File.ReadAllBytes(Path.Combine(root,name)),true))!=digest) throw new IOException("会话备份校验失败，尚未修改原文件。");
            obj.Remove("organizationId"); edits.Add(new SessionEdit(relative,SessionId(obj,relative),digest,header,EncodeHeader(obj,header),name));
        }
        var backup=new SessionBackup(id,DateTime.UtcNow,edits,"pending"); var manifest=Path.Combine(root,"manifest.dpapi"); SaveEncrypted(manifest,backup);
        try {
            // Validate the complete batch before replacing any session.
            foreach(var edit in edits) if(Digest(SessionBytes(Scoped(SessionsRoot,edit.Relative)))!=edit.Digest) throw new IOException("会话在备份后发生变化，已停止共享。");
            for(int index=0;index<edits.Count;index++) {if(!test) RequireIdle(); var edit=edits[index]; var path=Scoped(SessionsRoot,edit.Relative); var bytes=SessionBytes(path); if(Digest(bytes)!=edit.Digest) throw new IOException("会话已被其他程序修改。"); Atomic(path,Rewrite(bytes,edit.OriginalHeader,edit.NewHeader)); if(index==0) injectFailure?.Invoke();}
            ClearSessionIndex(); backup=backup with {State="shared"}; SaveEncrypted(manifest,backup); return backup;
        } catch {if(!test) RequireIdle(); RestoreSessionCore(backup); SaveEncrypted(manifest,backup with {State="restored"}); throw;}
    }
    void ClearSessionIndex() {var path=Path.Combine(Live,"sessions-index.json"); if(File.Exists(path)) File.Delete(path);}
    void RestoreSessionCore(SessionBackup backup) {
        var verified=new List<(SessionEdit Edit,byte[] Bytes,byte[] Header,JsonObject Current,JsonObject Original)>();
        foreach(var edit in backup.Edits) {
            var saved=NativeCrypto.Protect(File.ReadAllBytes(Scoped(SessionBackupsRoot,backup.Id+"/"+edit.BackupFile)),true); if(Digest(saved)!=edit.Digest || !saved.AsSpan().StartsWith(edit.OriginalHeader)) throw new IOException("会话备份校验失败。");
            var path=Scoped(SessionsRoot,edit.Relative); var bytes=SessionBytes(path); var header=Header(path); var current=HeaderJson(header); var before=HeaderJson(edit.OriginalHeader);
            if(Field(current,"type")!="session_start" || SessionId(current,edit.Relative)!=edit.Id || (Field(current,"organizationId").Length>0 && Field(current,"organizationId")!=Field(before,"organizationId"))) throw new IOException("会话已被绑定到其他组织，不能自动恢复。");
            verified.Add((edit,bytes,header,current,before));
        }
        foreach(var (edit,bytes,header,current,before) in verified) {
            var path=Scoped(SessionsRoot,edit.Relative); if(!Digest(SessionBytes(path)).Equals(Digest(bytes),StringComparison.Ordinal)) throw new IOException("恢复过程中会话发生变化，已停止。");
            if(Field(current,"organizationId")==Field(before,"organizationId")) continue;
            if(before.TryGetPropertyValue("organizationId",out var org)) current["organizationId"]=org?.DeepClone(); else current.Remove("organizationId");
            Atomic(path,Rewrite(bytes,header,EncodeHeader(current,header)));
        }
        ClearSessionIndex();
    }
    public void RestoreSessions(string id,bool test=false) {
        using var guard=Lock(); Ready(test); if(!Guid.TryParseExact(id,"N",out _)) throw new IOException("备份 ID 无效。");
        var manifest=Scoped(SessionBackupsRoot,id+"/manifest.dpapi"); var backup=ReadEncrypted<SessionBackup>(manifest); if(backup.Id!=id) throw new IOException("备份身份不匹配。");
        RestoreSessionCore(backup); SaveEncrypted(manifest,backup with {State="restored"});
    }
}
