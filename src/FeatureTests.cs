using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DroidSwitcher;
internal static partial class Tests
{
    static string Jwt(string sub,long expiry) {var payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {sub,exp=expiry})).TrimEnd('=').Replace('+','-').Replace('/','_'); return "e30."+payload+".fake";}
    sealed class FakeHTTP(Func<HttpRequestMessage,HttpResponseMessage> handler) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)=>Task.FromResult(handler(request));
    }
    static HttpResponseMessage Response(string json,HttpStatusCode status=HttpStatusCode.OK)=>new(status) {Content=new StringContent(json)};
    static void FeatureTests(Action<string,Action> test,string root) {
        test("Quota missing fields remain unknown and elapsed windows remain idle",()=> {
            var now=DateTimeOffset.UtcNow; var report=QuotaReport.Parse(Encoding.UTF8.GetBytes("{\"limits\":{\"standard\":{\"fiveHour\":{\"usedPercent\":35,\"secondsRemaining\":3600},\"weekly\":{},\"monthly\":{\"usedPercent\":40,\"secondsRemaining\":0}}},\"extraUsageBalanceCents\":1234}"),now);
            Assert(report.Column("5小时")=="剩余 65%" && report.Column("周")=="未知" && report.Column("月").StartsWith("待开启") && report.ExtraCents==1234);
        });
        test("Quota client sends credentials only to official endpoints with correct organization",()=> {
            int count=0; var client=new QuotaClient(new HttpClient(new FakeHTTP(request=> {count++; Assert(request.RequestUri!.AbsoluteUri=="https://api.factory.ai/api/billing/limits" && request.Headers.Authorization?.Parameter=="synthetic-token" && request.Headers.GetValues("X-Factory-Org-Id").Single()=="synthetic-org"); return Response("{\"limits\":{}}");}))); client.Fetch("synthetic-token","synthetic-org",default).GetAwaiter().GetResult(); Assert(count==1);
        });
        test("Expired inactive login rotates once and persists before quota fetch",()=> {
            var s=new Store(Path.Combine(root,"quota-store"),Path.Combine(root,"quota-live"),new MemoryKeyring()); var isolated=Path.Combine(root,"quota-isolated"); Write(isolated,Fixture("quota-account")); var a=s.SaveFrom(isolated,"quota",test:true); int calls=0;
            var client=new QuotaClient(new HttpClient(new FakeHTTP(request=> {
                calls++; if(request.Method==HttpMethod.Post) {Assert(request.RequestUri!.Host=="api.workos.com"); var form=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); Assert(form.Contains("grant_type=refresh_token") && !form.Contains("organization_id")); return Response(JsonSerializer.Serialize(new {access_token=Jwt("quota-account",DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()),refresh_token="rotated-synthetic"}));}
                Assert(calls==2); return Response("{\"limits\":{\"standard\":{\"fiveHour\":{\"usedPercent\":20,\"secondsRemaining\":3600}}}}");
            }))); s.FetchQuota(a.Id,client,test:true).GetAwaiter().GetResult(); Assert(calls==2 && s.CachedQuota(a.Id)!.Column("5小时")=="剩余 80%");
            var protectedFile=File.ReadAllBytes(Path.Combine(s.Root,"accounts",a.Id+".dpapi")); var plain=NativeCrypto.Protect(protectedFile,true); Assert(Encoding.UTF8.GetString(plain).Contains("quota-account"));
            // Decode through the saved bundle to inspect only synthetic rotated credentials.
            var bundle=JsonSerializer.Deserialize<Store.AccountBundle>(plain)!; Assert(Store.Credentials(bundle.Login)["refresh_token"]!.GetValue<string>()=="rotated-synthetic");
        });
        test("Failed OAuth refresh is not retried and cannot replace saved login",()=> {
            var s=new Store(Path.Combine(root,"quota-fail-store"),Path.Combine(root,"quota-fail-live"),new MemoryKeyring()); var isolated=Path.Combine(root,"quota-fail-login"); Write(isolated,Fixture("failed")); var a=s.SaveFrom(isolated,"failed",test:true); var path=Path.Combine(s.Root,"accounts",a.Id+".dpapi"); var before=File.ReadAllBytes(path); int count=0;
            var client=new QuotaClient(new HttpClient(new FakeHTTP(_=> {count++; return Response("{\"error\":\"invalid_grant\"}",HttpStatusCode.BadRequest);}))); MustFail(()=>s.FetchQuota(a.Id,client,test:true).GetAwaiter().GetResult()); Assert(count==1 && before.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        });
        test("Token renewal rejects mismatched identity and preserves credential fields",()=> {var login=Store.Validate(Fixture("identity")); var updated=Store.ReplaceTokens(login,Jwt("identity",DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()),"renewed"); Assert(updated.Subject=="identity" && updated.Organization==login.Organization); MustFail(()=>Store.ReplaceTokens(login,Jwt("other",DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()),"renewed"));});
        var live=Path.Combine(root,"sessions-live"); var sStore=new Store(Path.Combine(root,"sessions-store"),live,new MemoryKeyring()); var sessions=Path.Combine(live,"sessions"); Directory.CreateDirectory(sessions);
        var idA=Guid.NewGuid().ToString(); var idB=Guid.NewGuid().ToString(); var pathA=Path.Combine(sessions,idA+".jsonl"); var pathB=Path.Combine(sessions,idB+".jsonl");
        byte[] Make(string id,string org)=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {type="session_start",id,title="Synthetic session",organizationId=org,cwd="D:\\synthetic",other="keep"})+"\r\n"+"{\"type\":\"message\",\"content\":\"synthetic-body\"}\n");
        File.WriteAllBytes(pathA,Make(idA,"org-a")); File.WriteAllBytes(pathB,Make(idB,"org-b")); SessionBackup? shared=null;
        test("Session sharing affects selected header only and creates encrypted verified backup",()=> {var beforeA=File.ReadAllBytes(pathA); var beforeB=File.ReadAllBytes(pathB); shared=sStore.ShareSessions([Path.GetFileName(pathA)],true); var after=File.ReadAllBytes(pathA); var newline=Array.IndexOf(beforeA,(byte)10)+1; var afterNewline=Array.IndexOf(after,(byte)10)+1; Assert(beforeA.AsSpan(newline).SequenceEqual(after.AsSpan(afterNewline))); Assert(!Encoding.UTF8.GetString(after).Contains("organizationId") && beforeB.AsSpan().SequenceEqual(File.ReadAllBytes(pathB))); Assert(shared.Edits.Count==1 && shared.State=="shared");});
        test("Session restoration preserves newly appended messages and other header edits",()=> {File.AppendAllText(pathA,"{\"type\":\"message\",\"content\":\"new-synthetic-message\"}\n"); sStore.RestoreSessions(shared!.Id,true); Assert(Encoding.UTF8.GetString(File.ReadAllBytes(pathA)).Contains("new-synthetic-message") && sStore.Sessions().Single(x=>x.Id==idA).Organization=="org-a");});
        test("Session batch failure restores all changed metadata",()=> {var a=File.ReadAllBytes(pathA); var b=File.ReadAllBytes(pathB); MustFail(()=>sStore.ShareSessions([Path.GetFileName(pathA),Path.GetFileName(pathB)],true,()=>throw new IOException("Injected failure"))); Assert(a.AsSpan().SequenceEqual(File.ReadAllBytes(pathA)) && b.AsSpan().SequenceEqual(File.ReadAllBytes(pathB)));});
        test("Restoration refuses unrelated organization rebinding",()=> {var backup=sStore.ShareSessions([Path.GetFileName(pathB)],true); File.WriteAllBytes(pathB,Make(idB,"unrelated-org")); var before=File.ReadAllBytes(pathB); MustFail(()=>sStore.RestoreSessions(backup.Id,true)); Assert(before.AsSpan().SequenceEqual(File.ReadAllBytes(pathB))); File.WriteAllBytes(pathB,Make(idB,"org-b"));});
        test("Session scope rejects traversal and alternate data streams",()=> {MustFail(()=>Store.Scoped(sessions,"../escape.jsonl")); MustFail(()=>Store.Scoped(sessions,"file.jsonl:stream"));});
        test("Interrupted session operation can restore from persistent backup",()=> {var backup=sStore.ShareSessions([Path.GetFileName(pathB)],true); var manifest=Path.Combine(sStore.Root,"session-backups",backup.Id,"manifest.dpapi"); Store.Atomic(manifest,NativeCrypto.Protect(JsonSerializer.SerializeToUtf8Bytes(backup with {State="pending"}))); var reopened=new Store(sStore.Root,live,new MemoryKeyring()); Assert(reopened.SessionBackups().Any(x=>x.State=="pending")); reopened.RestoreSessions(backup.Id,true); Assert(reopened.Sessions().Single(x=>x.Id==idB).Organization=="org-b");});
    }
}
