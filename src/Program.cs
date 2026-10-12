using System.Diagnostics;
using System.Text;

namespace DroidSwitcher;
internal static class Program
{
    [STAThread] static int Main(string[] args) {
        if(args.Length==2 && args[0]=="--quota-check") {
            var store=new Store(); var results=new List<object>();
            foreach(var account in store.Accounts()) {try {var report=store.FetchQuota(account.Id).GetAwaiter().GetResult(); results.Add(new {success=true,groups=report.Groups.Count,error=""});} catch(Exception ex) {results.Add(new {success=false,groups=0,error=ex is IOException?ex.Message:ex.GetType().Name});}}
            File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(results)); return 0;
        }
        if(args.Length==3 && args[0]=="--verify-login") {
            bool valid=false; string format="",error="";
            try {var login=Store.Capture(args[1]); valid=true; format=login.Files.ContainsKey("auth.v2.keyring")?"Windows keyring":"keyfile-v2";}
            catch(Exception ex) {error=ex.GetType().Name;}
            File.WriteAllText(args[2],System.Text.Json.JsonSerializer.Serialize(new {valid,format,error})); return valid?0:1;
        }
        if(args.Length>0 && args[0]=="--test-wait") {Thread.Sleep(20000); return 0;}
        if(args.Length>0 && args[0]=="--self-test") return Tests.Run(args.Length>1?args[1]:Path.Combine(AppContext.BaseDirectory,"test-results"));
        ApplicationConfiguration.Initialize();
        using var mutex=new Mutex(true,"Local\\DroidSwitcher.Windows.v1",out bool owner);
        if(!owner) { MessageBox.Show("切号工具已在运行，请打开现有窗口。"); return 0; }
        try { Application.Run(new MainForm(new Store())); return 0; }
        catch { MessageBox.Show("无法打开数据目录或启动工具。请确认 D 盘可写，并使用当前 Windows 用户运行。","Droid Switcher"); return 1; }
    }
}
internal sealed partial class MainForm : Form
{
    readonly Store store;
    readonly ListView list=new() {View=View.Details,FullRowSelect=true,MultiSelect=false,Dock=DockStyle.Fill,HideSelection=false};
    readonly Label status=new() {Dock=DockStyle.Fill,AutoSize=false,Padding=new Padding(16),ForeColor=Color.FromArgb(55,65,81)};
    readonly CheckBox reopen=new() {Text="切换成功后启动 Factory 桌面版",Checked=true,AutoSize=true,Margin=new Padding(16,8,8,8)};
    readonly FlowLayoutPanel actions=new() {Dock=DockStyle.Fill,Padding=new Padding(12,4,12,4),AutoSize=false,WrapContents=true};
    Process? loginProcess; string? loginHome, loginLabel;
    readonly System.Windows.Forms.Timer timer=new() {Interval=1500};
    bool busy;
    public MainForm(Store store) {
        this.store=store; Text="Factory Switcher for Windows 1.2 · 账号、额度与会话共享"; Width=1250; Height=800; MinimumSize=new Size(950,700);
        Font=new Font("Microsoft YaHei UI",10); BackColor=Color.White; StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=5,ColumnCount=1};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,85)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,146)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,90));
        var heading=new Label {Text="Droid 登录账号\n使用官方登录添加账号，保存后可在这里切换。",Font=new Font(Font.FontFamily,14),Dock=DockStyle.Fill,Padding=new Padding(16,14,8,8)};
        list.Columns.Add("账号名称",160); list.Columns.Add("邮箱 / 用户 ID",230); list.Columns.Add("状态",70); list.Columns.Add("保存时间",145);
        list.Columns.Add("5小时",130); list.Columns.Add("周",130); list.Columns.Add("月",130); list.Columns.Add("额度查询",200);
        var tabs=new TabControl {Dock=DockStyle.Fill}; var accountsPage=new TabPage("账号与额度"); var sessionsPage=new TabPage("会话共享");
        var accountPanel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2}; accountPanel.RowStyles.Add(new RowStyle(SizeType.Percent,65)); accountPanel.RowStyles.Add(new RowStyle(SizeType.Percent,35));
        quotaDetails=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text="选中账号查看额度详情。点击刷新额度查询官方接口。"};
        accountPanel.Controls.Add(list,0,0); accountPanel.Controls.Add(quotaDetails,0,1); accountsPage.Controls.Add(accountPanel); sessionsPage.Controls.Add(CreateSessionPanel()); tabs.TabPages.AddRange([accountsPage,sessionsPage]);
        list.SelectedIndexChanged+=(_,_)=>ShowQuotaDetails();
        layout.Controls.Add(heading,0,0); layout.Controls.Add(tabs,0,1); layout.Controls.Add(actions,0,2); layout.Controls.Add(reopen,0,3); layout.Controls.Add(status,0,4); Controls.Add(layout);
        Button("添加账号（官方登录）",()=>BeginLogin());
        Button("保存当前登录",()=> { var label=Prompt("给当前登录起个名称"); if(label!=null) { var a=store.SaveCurrent(label); RefreshList(); SetStatus("已加密保存："+a.Label); } });
        Button("切换所选账号",()=> { var a=Selected(); store.Switch(a.Id); RefreshList(); SetStatus("已切换到："+a.Label+"。启动 Droid 后确认邮箱和组织。"); if(reopen.Checked) LaunchDesktop(); });
        Button("启动桌面版",LaunchDesktop); Button("启动 Droid",()=>Process.Start(new ProcessStartInfo(Store.DroidPath) {UseShellExecute=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}));
        Button("刷新",RefreshList); Button("重试保存新登录",CompleteLogin);
        Button("放弃新登录",()=> { if(loginHome==null) throw new InvalidOperationException("没有待保存的新登录。"); if(MessageBox.Show("放弃尚未保存的新登录？已保存的账号不受影响。请先退出登录终端。","放弃新登录",MessageBoxButtons.OKCancel)==DialogResult.OK) {store.ClearPending(); loginHome=null; loginLabel=null; SetStatus("已清除待保存的新登录。");} });
        Button("恢复上次切换",()=> { store.Recover(); RefreshList(); SetStatus("恢复完成，原登录态已还原。"); });
        Button("移除所选快照",()=> { var a=Selected(); if(MessageBox.Show("移除“"+a.Label+"”的保存快照？当前 Droid 登录不受影响；历史备份仍保留。","移除账号",MessageBoxButtons.OKCancel)==DialogResult.OK) { store.Remove(a.Id); RefreshList(); } });
        AsyncButton("刷新所选额度",()=>RefreshQuotas(false)); AsyncButton("刷新全部额度",()=>RefreshQuotas(true));
        timer.Tick+=(_,_)=>CheckLogin(); timer.Start(); RefreshList();
        SetStatus(store.HasRecovery?"发现未完成的切换。请退出所有 Droid / Factory 后点击“恢复上次切换”。":"添加和保存账号无需退出其他客户端。切换账号时才需要退出当前 Droid / Factory（含后台）。数据在 "+store.Root+"。");
        var pending=store.Pending(); if(pending!=null) {loginHome=store.LoginHome(pending); loginLabel=pending.Label; SetStatus("有一个上次未保存的新登录。退出 Droid 后点击“重试保存新登录”，或点击“放弃新登录”。");}
        FormClosing+=(_,e)=> { if(loginProcess!=null && !loginProcess.HasExited) { MessageBox.Show("官方登录仍在进行。请先退出登录终端，再关闭工具。"); e.Cancel=true; } };
        FormClosed+=(_,_)=>timer.Dispose();
    }
    void SetStatus(string text)=>status.Text=text;
    void Button(string text,Action action) { var button=new Button {Text=text,AutoSize=true,Height=36,Padding=new Padding(8,3,8,3),Margin=new Padding(4)}; button.Click+=(_,_)=>Guard(action); actions.Controls.Add(button); }
    void Guard(Action action) {
        if(busy) return; busy=true; try { action(); }
        catch(Exception ex) { SetStatus(ex.Message); MessageBox.Show(ex.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        finally {busy=false;}
    }
    Account Selected()=>list.SelectedItems.Count==1 ? (Account)list.SelectedItems[0].Tag! : throw new InvalidOperationException("请先选择一个账号。");
    void RefreshList() {
        var selected=list.SelectedItems.Count==1 ? ((Account)list.SelectedItems[0].Tag!).Id : "";
        list.Items.Clear(); foreach(var a in store.Accounts()) {
            var quota=store.CachedQuota(a.Id);
            var item=new ListViewItem([a.Label,a.Email.Length>0?a.Email:a.Subject,a.Id==store.Active?"当前":"已保存",a.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),quota?.Column("5小时") ?? "未查询",quota?.Column("周") ?? "未查询",quota?.Column("月") ?? "未查询",quotaErrors.GetValueOrDefault(a.Id) ?? (quota==null?"未查询":quota.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"))]) {Tag=a};
            list.Items.Add(item); if(a.Id==selected) item.Selected=true;
        }
    }
    static string? Prompt(string title) {
        using var dialog=new Form {Text=title,Width=420,Height=170,FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MaximizeBox=false,MinimizeBox=false};
        var input=new TextBox {Left=20,Top=18,Width=360,MaxLength=80}; var ok=new Button {Text="确定",DialogResult=DialogResult.OK,Left=210,Top=64,Width=80}; var cancel=new Button {Text="取消",DialogResult=DialogResult.Cancel,Left=300,Top=64,Width=80};
        dialog.Controls.AddRange([input,ok,cancel]); dialog.AcceptButton=ok; dialog.CancelButton=cancel;
        return dialog.ShowDialog()==DialogResult.OK ? input.Text : null;
    }
    void BeginLogin() {
        if(loginHome!=null) throw new InvalidOperationException("已有一个登录待保存，请完成并退出它，然后点击“重试保存新登录”。");
        if(store.HasRecovery) throw new IOException("请先恢复未完成的切换。");
        var label=Prompt("新账号名称（例如 工作 / 个人）"); if(label==null) return; if(string.IsNullOrWhiteSpace(label)) throw new ArgumentException("请输入账号名称。");
        if(!File.Exists(Store.DroidPath)) throw new FileNotFoundException("找不到 D 盘的 Droid 客户端。");
        var pending=store.CreateLogin(label); loginHome=store.LoginHome(pending); var root=Path.GetDirectoryName(loginHome)!; loginLabel=label;
        var cmd=Path.Combine(root,"login.cmd");
        // Only generated paths are placed in the batch script; labels never enter shell code.
        File.WriteAllText(cmd,"@echo off\r\nset \"FACTORY_HOME_OVERRIDE="+root+"\"\r\nset FACTORY_API_KEY=\r\nset FACTORY_DISABLE_AUTO_UPDATE=1\r\ncd /d \""+root+"\"\r\n\""+Store.DroidPath+"\"\r\n",Encoding.ASCII);
        loginProcess=Process.Start(new ProcessStartInfo("cmd.exe") {Arguments="/c \""+cmd+"\"",UseShellExecute=true,WorkingDirectory=root});
        if(loginProcess!=null) store.TrackLogin(loginProcess);
        SetStatus("在新终端完成官方登录，然后 Ctrl+C 退出 Droid（不要 /logout）。浏览器如果自动选旧账号，请用无痕窗口打开登录链接。退出后自动保存。");
    }
    void CheckLogin() {
        if(busy || loginProcess==null || !loginProcess.HasExited) return;
        loginProcess.Dispose(); loginProcess=null; Guard(CompleteLogin);
    }
    void CompleteLogin() {
        if(loginHome==null) throw new InvalidOperationException("没有待保存的新登录。");
        if(loginProcess!=null && !loginProcess.HasExited) throw new InvalidOperationException("请先 Ctrl+C 退出登录终端。");
        var pending=store.Pending(); if(pending!=null) Store.EnsureLoginExited(pending);
        var a=store.SaveFrom(loginHome,loginLabel!); RefreshList();
        store.ClearPending();
        loginHome=null; loginLabel=null; SetStatus("已加密保存："+a.Label+"。选择它并点击“切换所选账号”。");
    }
    static void LaunchDesktop() { if(!File.Exists(Store.DesktopPath)) throw new FileNotFoundException("找不到 Factory 桌面版。"); Process.Start(new ProcessStartInfo(Store.DesktopPath) {UseShellExecute=true}); }
}
