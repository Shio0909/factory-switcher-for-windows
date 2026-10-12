namespace DroidSwitcher;
internal sealed partial class MainForm
{
    TextBox quotaDetails=null!;
    readonly Dictionary<string,string> quotaErrors=new();
    readonly ListView sessionsList=new() {View=View.Details,CheckBoxes=true,FullRowSelect=true,Dock=DockStyle.Fill};
    readonly ComboBox backups=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=450};
    readonly CancellationTokenSource requests=new();
    void AsyncButton(string text,Func<Task> action) {
        var button=new Button {Text=text,AutoSize=true,Padding=new Padding(8,3,8,3),Margin=new Padding(4)};
        button.Click+=async (_,_)=> {if(busy) return; busy=true; actions.Enabled=false; try {await action();} catch(Exception ex) {SetStatus("查询未完成："+ex.Message);} finally {busy=false; if(!IsDisposed) actions.Enabled=true;}};
        actions.Controls.Add(button);
    }
    async Task RefreshQuotas(bool all) {
        var targets=all?store.Accounts():new List<Account>{Selected()}; int success=0;
        foreach(var account in targets) {SetStatus("正在查询："+account.Label); try {await store.FetchQuota(account.Id,cancel:requests.Token); quotaErrors.Remove(account.Id); success++;}
            catch(Exception ex) {quotaErrors[account.Id]=ex is OperationCanceledException?"连接超时或查询取消，保留缓存":ex is HttpRequestException?"网络连接失败，保留缓存":ex.Message;}
            if(IsDisposed) return; RefreshList();
        }
        ShowQuotaDetails(); SetStatus("额度查询完成："+success+" / "+targets.Count+" 个账号成功。失败账号保留上次结果。");
    }
    void ShowQuotaDetails() {
        if(list.SelectedItems.Count!=1) return; var account=(Account)list.SelectedItems[0].Tag!;
        try {quotaDetails.Text=account.Label+Environment.NewLine+(store.CachedQuota(account.Id)?.Details() ?? "尚未查询额度。")+(quotaErrors.TryGetValue(account.Id,out var error)?Environment.NewLine+"本次查询："+error:"");}
        catch {quotaDetails.Text="额度缓存无法读取，请重新查询。";}
    }
    Control CreateSessionPanel() {
        var panel=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=4,ColumnCount=1}; panel.RowStyles.Add(new RowStyle(SizeType.Absolute,70)); panel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute,55)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute,70));
        panel.Controls.Add(new Label {Text="勾选本地会话后可供其他账号继续。共享只移除组织归属，不上传记录。\n在另一个账号继续对话时，内容可能发送给该账号所属组织。请先预览；应用前需退出 Droid / Factory。",Dock=DockStyle.Fill,Padding=new Padding(10)},0,0);
        sessionsList.Columns.Add("会话标题",300); sessionsList.Columns.Add("组织 / 共享状态",220); sessionsList.Columns.Add("项目目录",300); sessionsList.Columns.Add("更新时间",150); panel.Controls.Add(sessionsList,0,1);
        var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill};
        void Add(string label,Action action) {var b=new Button {Text=label,AutoSize=true,Padding=new Padding(5),Margin=new Padding(4)}; b.Click+=(_,_)=>Guard(action); buttons.Controls.Add(b);}
        Add("刷新会话",RefreshSessions); Add("全选",()=> {foreach(ListViewItem item in sessionsList.Items) item.Checked=true;}); Add("取消全选",()=> {foreach(ListViewItem item in sessionsList.Items) item.Checked=false;});
        Add("预览共享",()=> {var selected=SessionSelection(); MessageBox.Show(SessionPreview(selected),"共享预览");});
        Add("共享勾选会话",()=> {var selected=SessionSelection(); if(MessageBox.Show(SessionPreview(selected)+"\n\n这些记录将可供其他本地账号继续使用。继续时可能发送到目标账号的组织。确认共享？","共享所选会话",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK) return;
            var backup=store.ShareSessions(selected.Select(s=>s.Relative)); RefreshSessions(); SetStatus("已共享 "+backup.Edits.Count+" 个会话，并保存加密备份。");});
        panel.Controls.Add(buttons,0,2); var backupPanel=new FlowLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(4)}; backupPanel.Controls.Add(new Label {Text="恢复组织归属：",AutoSize=true,Margin=new Padding(4,10,4,4)}); backupPanel.Controls.Add(backups);
        var restore=new Button {Text="恢复所选备份",AutoSize=true}; restore.Click+=(_,_)=>Guard(()=> {if(backups.SelectedItem is not BackupOption selected) throw new IOException("请选择备份。"); if(MessageBox.Show("恢复该批会话原来的组织归属？共享后追加的消息会保留。","恢复会话",MessageBoxButtons.OKCancel)!=DialogResult.OK) return; store.RestoreSessions(selected.Backup.Id); RefreshSessions(); SetStatus("原组织归属已恢复，新消息保留。");}); backupPanel.Controls.Add(restore); panel.Controls.Add(backupPanel,0,3);
        Shown+=(_,_)=>Guard(RefreshSessions); FormClosed+=(_,_)=> {requests.Cancel();requests.Dispose();}; return panel;
    }
    List<LocalSession> SessionSelection() {var selected=sessionsList.CheckedItems.Cast<ListViewItem>().Select(x=>(LocalSession)x.Tag!).ToList(); if(selected.Count==0) throw new IOException("请勾选要共享的会话。"); return selected;}
    static string SessionPreview(List<LocalSession> selected)=>"选中 "+selected.Count+" 个会话；需要移除组织归属 "+selected.Count(s=>s.Organization.Length>0)+" 个。\n"+string.Join("\n",selected.Take(15).Select(s=>"• "+s.Title+"："+(s.Organization.Length==0?"已共享":s.Organization+" → 共享")))+(selected.Count>15?"\n……更多记录见列表":"");
    record BackupOption(SessionBackup Backup) {public override string ToString()=>Backup.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")+" · "+Backup.Edits.Count+" 个会话 · "+(Backup.State=="pending"?"未完成（请恢复）":Backup.State=="restored"?"已恢复":"已共享");}
    void RefreshSessions() {
        var selected=sessionsList.CheckedItems.Cast<ListViewItem>().Select(x=>((LocalSession)x.Tag!).Relative).ToHashSet(); sessionsList.Items.Clear();
        foreach(var s in store.Sessions()) sessionsList.Items.Add(new ListViewItem([s.Title,s.Organization.Length==0?"共享":s.Organization,s.Cwd,s.Updated.ToString("MM-dd HH:mm")]) {Tag=s,Checked=selected.Contains(s.Relative)});
        backups.Items.Clear(); foreach(var backup in store.SessionBackups()) backups.Items.Add(new BackupOption(backup)); if(backups.Items.Count>0) backups.SelectedIndex=0;
    }
}
