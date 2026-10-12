using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DroidSwitcher;
internal record QuotaWindow(string Name,double? Used,DateTimeOffset? Reset) {
    public string Text(DateTimeOffset now) {
        if(Used is not double used || !double.IsFinite(used) || used<0) return "未知";
        if(Reset is DateTimeOffset end && end<=now) return "待开启（上次已用 "+Math.Min(100,used).ToString("0.#")+"%）";
        return "剩余 "+Math.Clamp(100-used,0,100).ToString("0.#")+"%";
    }
}
internal record QuotaGroup(string Name,List<QuotaWindow> Windows);
internal record QuotaReport(List<QuotaGroup> Groups,double? ExtraCents,DateTimeOffset FetchedAt) {
    public string Column(string name)=>Groups.FirstOrDefault()?.Windows.FirstOrDefault(x=>x.Name==name)?.Text(DateTimeOffset.UtcNow) ?? "未提供";
    public string Details() {
        var lines=new List<string>(); foreach(var g in Groups) {lines.Add(g.Name); foreach(var w in g.Windows) lines.Add("  "+w.Name+"："+w.Text(DateTimeOffset.UtcNow)+(w.Reset.HasValue?"；重置 "+w.Reset.Value.ToLocalTime().ToString("MM-dd HH:mm"):""));}
        if(Groups.Count==0) lines.Add("此套餐未提供可识别的窗口额度。");
        if(ExtraCents.HasValue) lines.Add("额外用量余额：$"+(ExtraCents.Value/100).ToString("0.00"));
        lines.Add("查询时间："+FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")); return string.Join(Environment.NewLine,lines);
    }
    internal static QuotaReport Parse(byte[] bytes,DateTimeOffset now) {
        using var document=JsonDocument.Parse(bytes); var root=document.RootElement; var groups=new List<QuotaGroup>();
        if(root.ValueKind!=JsonValueKind.Object) throw new IOException("额度响应格式无法识别。");
        if(root.TryGetProperty("limits",out var limits) && limits.ValueKind==JsonValueKind.Object) foreach(var group in limits.EnumerateObject()) {
            if(group.Value.ValueKind!=JsonValueKind.Object) continue; var windows=new List<QuotaWindow>();
            foreach(var key in new[]{"fiveHour","weekly","monthly"}.Concat(group.Value.EnumerateObject().Select(x=>x.Name).Where(x=>x!="fiveHour" && x!="weekly" && x!="monthly").Order())) {
                if(!group.Value.TryGetProperty(key,out var window) || window.ValueKind!=JsonValueKind.Object) continue;
                double? used=Number(window,"usedPercent"); DateTimeOffset? reset=null;
                if(window.TryGetProperty("windowEnd",out var end) && end.ValueKind==JsonValueKind.String && DateTimeOffset.TryParse(end.GetString(),out var time)) reset=time;
                if(reset==null && Number(window,"secondsRemaining") is double seconds && seconds>=0 && seconds<=366*86400) reset=now.AddSeconds(seconds);
                windows.Add(new QuotaWindow(key switch {"fiveHour"=>"5小时","weekly"=>"周","monthly"=>"月",_=>key},used,reset));
            }
            if(windows.Count>0) groups.Add(new QuotaGroup(group.Name,windows));
        }
        return new QuotaReport(groups.OrderBy(x=>x.Name=="standard"?0:1).ThenBy(x=>x.Name).ToList(),Number(root,"extraUsageBalanceCents"),now);
    }
    static double? Number(JsonElement element,string key)=>element.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)?number:null;
}
internal sealed class QuotaClient
{
    readonly HttpClient client;
    public QuotaClient(HttpClient? testClient=null) {client=testClient ?? new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false}) {Timeout=TimeSpan.FromSeconds(20)};}
    async Task<(HttpStatusCode,byte[])> Send(HttpRequestMessage request,CancellationToken cancel) {
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel); using var input=await response.Content.ReadAsStreamAsync(cancel); using var output=new MemoryStream();
        byte[] buffer=new byte[8192]; int n; while((n=await input.ReadAsync(buffer,cancel))>0) {if(output.Length+n>2*1024*1024) throw new IOException("额度接口响应过大。"); output.Write(buffer,0,n);} return (response.StatusCode,output.ToArray());
    }
    public async Task<QuotaReport> Fetch(string access,string org,CancellationToken cancel) {
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.factory.ai/api/billing/limits"); request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json")); request.Headers.Add("X-Factory-Client","cli"); request.Headers.Add("X-Client-Version","0.238.0"); if(org.Length>0) request.Headers.Add("X-Factory-Org-Id",org);
        var (code,bytes)=await Send(request,cancel);
        if(code==HttpStatusCode.Unauthorized) throw new IOException("登录已过期。请打开 Factory 续期后重新保存当前登录，或重新添加该账号。");
        if(code==HttpStatusCode.Forbidden) throw new IOException("此账号无权查看所选组织的额度。");
        if((int)code==429) throw new IOException("查询过于频繁，请稍后再试。");
        if((int)code<200 || (int)code>=300) throw new IOException("额度服务返回 HTTP "+(int)code+"，保留上次结果。");
        return QuotaReport.Parse(bytes,DateTimeOffset.UtcNow);
    }
    public async Task<(string Access,string Refresh)> Refresh(string refresh,CancellationToken cancel) {
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.workos.com/user_management/authenticate");
        request.Content=new FormUrlEncodedContent(new Dictionary<string,string> {{"grant_type","refresh_token"},{"client_id","client_01HNM792M5G5G1A2THWPXKFMXB"},{"refresh_token",refresh}});
        var (code,bytes)=await Send(request,cancel); // Never retry a rotating refresh token.
        if((int)code<200 || (int)code>=300) throw new IOException(code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized?"保存的登录已失效，请通过官方 Droid 重新登录该账号。":"登录续期失败（HTTP "+(int)code+"），没有重试刷新令牌。");
        using var json=JsonDocument.Parse(bytes); var access=json.RootElement.GetProperty("access_token").GetString(); var next=json.RootElement.GetProperty("refresh_token").GetString();
        if(string.IsNullOrEmpty(access) || string.IsNullOrEmpty(next)) throw new IOException("登录续期响应不完整。"); return (access,next);
    }
}
internal sealed partial class Store
{
    internal static JsonObject Credentials(LoginState login) {
        bool secure=login.Files.ContainsKey("auth.v2.keyring"); var key=Convert.FromBase64String(Encoding.UTF8.GetString(login.Files[secure?"auth.v2.keyring.key":"auth.v2.key"]).Trim());
        byte[] plain=[]; try {var parts=Encoding.UTF8.GetString(login.Files[secure?"auth.v2.keyring":"auth.v2.file"]).Trim().Split(':'); plain=NativeCrypto.Gcm(key,Convert.FromBase64String(parts[0]),Convert.FromBase64String(parts[1]),Convert.FromBase64String(parts[2])); return JsonNode.Parse(plain)?.AsObject() ?? throw new IOException("凭据内容无效。");}
        finally {CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain);}
    }
    internal static bool Expired(string token,DateTimeOffset now) {
        try {var part=token.Split('.')[1].Replace('-','+').Replace('_','/'); part=part.PadRight((part.Length+3)/4*4,'='); using var jwt=JsonDocument.Parse(Convert.FromBase64String(part)); return !jwt.RootElement.TryGetProperty("exp",out var exp) || !exp.TryGetInt64(out var seconds) || seconds<=now.AddSeconds(60).ToUnixTimeSeconds();} catch{return true;}
    }
    internal static LoginState ReplaceTokens(LoginState login,string access,string refresh) {
        var fields=Credentials(login); fields["access_token"]=access; fields["refresh_token"]=refresh;
        bool secure=login.Files.ContainsKey("auth.v2.keyring"); var key=Convert.FromBase64String(Encoding.UTF8.GetString(login.Files[secure?"auth.v2.keyring.key":"auth.v2.key"]).Trim()); var nonce=RandomNumberGenerator.GetBytes(16); var tag=new byte[16]; var plain=JsonSerializer.SerializeToUtf8Bytes(fields);
        try {var cipher=NativeCrypto.Gcm(key,nonce,tag,plain,true); var files=login.Files.ToDictionary(p=>p.Key,p=>p.Value.ToArray()); files[secure?"auth.v2.keyring":"auth.v2.file"]=Encoding.UTF8.GetBytes(string.Join(':',Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher))); var updated=Validate(files); if(updated.Subject!=login.Subject || updated.Organization!=login.Organization) throw new IOException("续期后的账号身份不匹配。"); return updated;}
        finally {CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain);}
    }
    public QuotaReport? CachedQuota(string id) {var path=Path.Combine(Root,"quota",id+".dpapi"); _=AccountPath(id); return File.Exists(path)?ReadEncrypted<QuotaReport>(path):null;}
    public async Task<QuotaReport> FetchQuota(string id,QuotaClient? quotaClient=null,CancellationToken cancel=default,bool test=false) {
        using var guard=Lock(); Ready(false,false); if(Pending()!=null) throw new IOException("正在添加账号，请先保存或放弃新登录再查询额度。");
        var bundle=ReadEncrypted<AccountBundle>(AccountPath(id)); var login=bundle.Login; LoginState? live=null;
        if(RawFiles(Live).Count>0) live=Capture(Live,keyring);
        bool isLive=live!=null && live.Subject==bundle.Account.Subject && live.Organization==bundle.Account.Organization;
        if(isLive) login=live!; var credentials=Credentials(login); var access=credentials["access_token"]!.GetValue<string>(); quotaClient ??=new QuotaClient();
        if(Expired(access,DateTimeOffset.UtcNow)) {
            // The live client owns its rotating refresh token, including profiles of the same user.
            if(live?.Subject==bundle.Account.Subject) throw new IOException("该登录正在作为本机当前账号使用，请通过 Factory 续期后再查询，避免同时刷新同一令牌。");
            if(!test) RequireQuotaRefreshSafe(); var tokens=await quotaClient.Refresh(credentials["refresh_token"]!.GetValue<string>(),cancel);
            login=ReplaceTokens(login,tokens.Access,tokens.Refresh);
            SaveEncrypted(AccountPath(id),new AccountBundle(bundle.Account with {SavedUtc=DateTime.UtcNow},login)); access=tokens.Access;
        }
        else if(isLive) SaveEncrypted(AccountPath(id),new AccountBundle(bundle.Account with {SavedUtc=DateTime.UtcNow},login));
        var report=await quotaClient.Fetch(access,login.Organization,cancel); SaveEncrypted(Path.Combine(Root,"quota",id+".dpapi"),report); return report;
    }
    static void RequireQuotaRefreshSafe() {
        var processes=System.Diagnostics.Process.GetProcessesByName("droid");
        try {foreach(var process in processes) {
            string path; try {path=process.MainModule?.FileName ?? "";} catch {throw new IOException("有无法识别的 Droid 进程，暂不续期，避免刷新令牌冲突。");}
            var desktopRoot=Path.GetDirectoryName(DesktopPath)!+Path.DirectorySeparatorChar;
            bool desktopDaemon=path.StartsWith(desktopRoot,StringComparison.OrdinalIgnoreCase) && path.Contains("\\resources\\bin\\",StringComparison.OrdinalIgnoreCase);
            if(!desktopDaemon) throw new IOException("检测到终端中的 Droid。查询需要续期，请先退出该终端再试；Factory 桌面版可以保持运行。");
        }} finally {foreach(var process in processes) process.Dispose();}
    }
}
