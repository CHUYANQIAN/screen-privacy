using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Theme {
    internal static Color Background { get { return SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(245,247,250); } }
    internal static Color Surface { get { return SystemInformation.HighContrast ? SystemColors.Window : Color.White; } }
    internal static Color TextColor { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(24,34,48); } }
    internal static Color Secondary { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(92,103,119); } }
    internal static Color Border { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(224,230,237); } }
    internal static Color Accent { get { return SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(36,99,210); } }
    internal static Color Error { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(180,35,24); } }
    internal static Color Success { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(30,123,80); } }
    internal static Font BodyFont() { return new Font("Microsoft YaHei UI",9.5F,FontStyle.Regular,GraphicsUnit.Point); }
    internal static void Form(Form form,Size size) {
        form.SuspendLayout();
        form.Font=BodyFont(); form.BackColor=Background; form.ForeColor=TextColor;
        form.AutoScaleMode=AutoScaleMode.Dpi;
        SizeF dpi=form.CurrentAutoScaleDimensions; form.AutoScaleDimensions=dpi;
        form.ClientSize=new Size((int)Math.Round(size.Width*dpi.Width/96F),(int)Math.Round(size.Height*dpi.Height/96F));
        try { form.Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch {}
        form.DpiChanged+=delegate { if(form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(new Action(delegate { if(!form.IsDisposed) FitToWorkArea(form); })); };
    }
    internal static void Finish(Form form) { form.ResumeLayout(true); }
    internal static Label Label(string text,bool muted=false) {
        return new Label { Text=text,AutoSize=true,ForeColor=muted?Secondary:TextColor,Margin=new Padding(0,0,0,8),Dock=DockStyle.Top,UseMnemonic=false };
    }
    internal static Label Heading(string text,float points=14F) {
        Label label=Label(text); label.Font=new Font("Microsoft YaHei UI",points,FontStyle.Bold,GraphicsUnit.Point); return label;
    }
    internal static Button Button(string text,bool primary=false) {
        Button button=new Button { Text=text,AutoSize=true,MinimumSize=new Size(92,36),Padding=new Padding(12,3,12,3),Margin=new Padding(0,0,8,0),FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false };
        button.FlatAppearance.BorderColor=Border; button.BackColor=primary?Accent:Surface;
        button.ForeColor=primary?(SystemInformation.HighContrast?SystemColors.HighlightText:Color.White):TextColor;
        return button;
    }
    internal static TableLayoutPanel Vertical(int padding=0) {
        TableLayoutPanel table=new TableLayoutPanel { ColumnCount=1,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Top,Padding=new Padding(padding),Margin=Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); return table;
    }
    internal static void Row(TableLayoutPanel table,Control child) {
        int n=table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(child,0,n);
    }
    internal static void Wrap(Control parent,Label label) {
        parent.SizeChanged+=delegate { label.MaximumSize=new Size(Math.Max(100,parent.ClientSize.Width-parent.Padding.Horizontal-label.Margin.Horizontal-6),0); };
    }
    internal static void FitToWorkArea(Form form) {
        Rectangle work=Screen.FromControl(form).WorkingArea;
        form.Size=new Size(Math.Min(form.Width,Math.Max(300,work.Width-24)),Math.Min(form.Height,Math.Max(260,work.Height-24)));
        if(!work.IntersectsWith(form.Bounds) || form.Left<work.Left || form.Top<work.Top || form.Right>work.Right || form.Bottom>work.Bottom)
            form.Location=new Point(work.Left+Math.Max(0,(work.Width-form.Width)/2),work.Top+Math.Max(0,(work.Height-form.Height)/2));
    }
    internal static bool ProtectCapture(Form form) {
        uint affinity;
        return Native.SetWindowDisplayAffinity(form.Handle,0x11) && Native.GetWindowDisplayAffinity(form.Handle,out affinity) && affinity==0x11;
    }
    internal static string Help(Hotkey restore) {
        return "屏幕遮蔽 " + Program.ProductVersion + "\n\n"+
            "开启后控制面板会隐藏。按当前恢复快捷键 "+restore+" 恢复全部屏幕；开启恢复密码时，输入任一已保存密码后按 Enter。Esc 取消恢复，屏幕继续遮蔽。\n\n"+
            "遮罩保留活动桌面和点击穿透，便于兼容的 Codex 截图与操作。请勿在看不见底下内容时盲目点击。截图兼容性需在实际环境确认；本软件用于现场防旁观，不替代 Windows 锁屏。\n\n"+
            "模拟更新仅模拟画面，不执行 Windows 更新，不重启，也不会随进度自动恢复。副屏始终使用纯黑遮罩。\n\n"+
            "忘记密码后，可结束软件并重新启动，在首次开启遮蔽前重设。关闭窗口到托盘不算重新启动；软件结束时遮罩也会解除。\n\n"+
            "显示器布局改变时，软件会尝试补盖全部当前屏幕。无法承诺覆盖所有系统界面或消除布局切换瞬间的闪露。";
    }
}

internal sealed class SurfacePanel : Panel {
    internal SurfacePanel() { DoubleBuffered=true; BackColor=Theme.Surface; Padding=new Padding(16); Margin=new Padding(0,0,0,12); }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); using(Pen pen=new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
    }
}

internal sealed class MonitorGlyph : Control {
    internal MonitorGlyph() { Size=new Size(52,40); Margin=new Padding(0,0,0,12); TabStop=false; AccessibleRole=AccessibleRole.Graphic; AccessibleName="抽象显示器图标"; }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        float s=Math.Min(Width/52F,Height/40F);
        using(Pen p=new Pen(Theme.Accent,2*s)) {
            e.Graphics.DrawRectangle(p,2*s,2*s,46*s,27*s); e.Graphics.DrawLine(p,25*s,29*s,25*s,36*s); e.Graphics.DrawLine(p,14*s,37*s,36*s,37*s);
        }
        using(Brush b=new SolidBrush(SystemInformation.HighContrast?SystemColors.Control:Color.FromArgb(232,239,252))) e.Graphics.FillRectangle(b,6*s,6*s,38*s,19*s);
    }
}

internal sealed class ControlForm : Form {
    internal bool Exiting;
    internal Button MainButton,SecondaryButton,SettingsButton;
    private Label mainState,secondaryState,secondaryDescription,mainKey,secondaryKey,restoreKey,registrationState,passwordState,problemText;
    private Button modifyKey;
    private AppConfig currentConfig;
    internal ControlForm(PrivacyContext context) {
        Text="屏幕遮蔽"; Theme.Form(this,new Size(660,570)); StartPosition=FormStartPosition.CenterScreen; MinimumSize=new Size(400,340);
        Panel scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(24) }; Controls.Add(scroll);
        TableLayoutPanel content=Theme.Vertical(); scroll.Controls.Add(content);
        Theme.Row(content,Theme.Heading("屏幕遮蔽，调试继续",18F));
        Label subtitle=Theme.Label("遮住实体屏幕，保留活动桌面，方便 Codex 继续截图和操作。",true); Theme.Wrap(content,subtitle); Theme.Row(content,subtitle);
        TableLayoutPanel cards=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Margin=new Padding(0,12,0,0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        SurfacePanel mainCard=MakeCard("主屏","系统主显示器",out mainState,out mainKey,out MainButton,null);
        mainCard.Margin=new Padding(0,0,8,12); MainButton.Text="遮蔽主屏"; MainButton.AccessibleName="遮蔽主屏"; MainButton.Click+=delegate { context.Toggle(true); };
        SurfacePanel secondaryCard=MakeCard("全部副屏","未检测到副屏",out secondaryState,out secondaryKey,out SecondaryButton,delegate(Label label) { secondaryDescription=label; });
        secondaryCard.Margin=new Padding(8,0,0,12); SecondaryButton.Text="遮蔽全部副屏"; SecondaryButton.AccessibleName="遮蔽全部副屏"; SecondaryButton.Click+=delegate { context.Toggle(false); };
        cards.Controls.Add(mainCard,0,0); cards.Controls.Add(secondaryCard,1,0); Theme.Row(content,cards);
        SurfacePanel restorePanel=new SurfacePanel { Dock=DockStyle.Top,AutoSize=true };
        TableLayoutPanel restoreContent=Theme.Vertical(); restorePanel.Controls.Add(restoreContent);
        Theme.Row(restoreContent,Theme.Label("恢复快捷键")); restoreKey=Theme.Heading("",15F); restoreKey.ForeColor=Theme.Accent; restoreKey.AccessibleName="当前生效恢复快捷键"; Theme.Row(restoreContent,restoreKey);
        Label restoreHint=Theme.Label("遮蔽后按此组合键恢复全部屏幕。开启密码时，输入后按 Enter。",true); Theme.Wrap(restoreContent,restoreHint); Theme.Row(restoreContent,restoreHint);
        FlowLayoutPanel keyLine=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Top,Margin=Padding.Empty };
        registrationState=Theme.Label(""); registrationState.Margin=new Padding(0,8,16,0); modifyKey=Theme.Button("修改恢复键"); modifyKey.AccessibleName="修改恢复快捷键"; modifyKey.Click+=delegate { context.EditSettings(1); };
        keyLine.Controls.Add(registrationState); keyLine.Controls.Add(modifyKey); Theme.Row(restoreContent,keyLine); Theme.Row(content,restorePanel);
        passwordState=Theme.Label(""); Theme.Row(content,passwordState);
        problemText=Theme.Label(""); problemText.ForeColor=Theme.Error; Theme.Wrap(content,problemText); Theme.Row(content,problemText);
        Label note=Theme.Label("启动默认不遮蔽。开启后面板隐藏；遮蔽期间请避免盲目点击底下应用。",true); Theme.Wrap(content,note); Theme.Row(content,note);
        FlowLayoutPanel footer=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,Margin=new Padding(0,8,0,0) };
        SettingsButton=Theme.Button("设置"); SettingsButton.AccessibleName="打开遮蔽设置"; SettingsButton.Click+=delegate { context.EditSettings(); }; footer.Controls.Add(SettingsButton);
        Button help=Theme.Button("使用说明 / 关于"); help.Click+=delegate { if(currentConfig!=null) MessageBox.Show(this,Theme.Help(currentConfig.RestoreHotkey),"使用说明与关于",MessageBoxButtons.OK,MessageBoxIcon.Information); }; footer.Controls.Add(help);
        Button exit=Theme.Button("退出软件"); exit.Click+=delegate { context.ExitRequested(); }; footer.Controls.Add(exit); Theme.Row(content,footer);
        FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(!Exiting && e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); } };
        Shown+=delegate { Theme.FitToWorkArea(this); };
        Theme.Finish(this);
    }
    private static SurfacePanel MakeCard(string title,string description,out Label state,out Label key,out Button button,Action<Label> descriptionReady) {
        SurfacePanel card=new SurfacePanel { Dock=DockStyle.Fill,AutoSize=true,MinimumSize=new Size(0,225) };
        TableLayoutPanel rows=Theme.Vertical(); card.Controls.Add(rows); Theme.Row(rows,new MonitorGlyph()); Theme.Row(rows,Theme.Heading(title,12F));
        Label detail=Theme.Label(description,true); Theme.Wrap(rows,detail); Theme.Row(rows,detail); if(descriptionReady!=null) descriptionReady(detail);
        state=Theme.Label("正常显示"); Theme.Row(rows,state); key=Theme.Label("",true); key.AccessibleName=title+"切换快捷键"; Theme.Row(rows,key);
        button=Theme.Button("",true); button.Dock=DockStyle.Top; button.Margin=Padding.Empty; Theme.Row(rows,button); return card;
    }
    internal void RefreshStatus(AppConfig config,bool mainReady,bool secondaryReady,bool restoreReady,bool main,bool secondary,int secondaryCount,string problem) {
        currentConfig=config; mainState.Text=main?"已遮蔽":"正常显示";
        secondaryState.Text=secondaryCount==0?"连接显示器后可使用":secondary?"已遮蔽":"正常显示";
        secondaryDescription.Text=secondaryCount==0?"未检测到副屏":"共 "+secondaryCount+" 台显示器";
        mainKey.Text="切换："+config.MainHotkey; secondaryKey.Text="切换："+config.SecondaryHotkey;
        MainButton.Enabled=restoreReady && mainReady && !main; SecondaryButton.Enabled=restoreReady && secondaryReady && secondaryCount>0 && !secondary;
        SettingsButton.Enabled=!main && !secondary; modifyKey.Enabled=SettingsButton.Enabled;
        restoreKey.Text=config.RestoreHotkey.ToString(); registrationState.Text=restoreReady?"恢复快捷键已就绪":"恢复快捷键不可用";
        registrationState.ForeColor=restoreReady?Theme.Success:Theme.Error;
        passwordState.Text="恢复密码："+(config.PasswordEnabled?"已开启（任一有效，共 "+config.PasswordEntries.Count+" 个）":"未开启")+(config.PasswordEnabled?"":" · 已保存 "+config.PasswordEntries.Count+" 个密码");
        problemText.Text=problem??(restoreReady?"":"当前不能开启遮蔽，请先修改快捷键或重试。"); problemText.Visible=problemText.Text.Length>0;
    }
}

internal sealed class HotkeyBox : TextBox {
    internal Hotkey Value;
    internal string ValidationError;
    internal event EventHandler RecordingChanged;
    private readonly Action<bool> recording;
    private bool isRecording;
    private Hotkey beforeRecording;
    internal HotkeyBox(Hotkey key,Action<bool> recordingChanged) {
        Value=new Hotkey(key.Modifiers,key.Key); Text=Value.ToString(); recording=recordingChanged; ReadOnly=true; BackColor=Theme.Surface;
        Dock=DockStyle.Top; MinimumSize=new Size(120,28); Margin=new Padding(0,0,0,4); AccessibleDescription="按 Ctrl 或 Alt 与字母、数字或功能键组合；Tab 离开，Esc 保留当前值。";
    }
    internal void EndRecording() { if(!isRecording) return; isRecording=false; if(recording!=null) recording(false); }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); beforeRecording=new Hotkey(Value.Modifiers,Value.Key); isRecording=true; if(recording!=null) recording(true); SelectAll(); }
    protected override void OnLeave(EventArgs e) { EndRecording(); base.OnLeave(e); }
    protected override bool IsInputKey(Keys keyData) {
        Keys key=keyData & Keys.KeyCode;
        if(key==Keys.Escape) return true;
        return key==Keys.Tab?false:base.IsInputKey(keyData);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        if(e.KeyCode==Keys.Tab && !e.Control && !e.Alt) { base.OnKeyDown(e); return; }
        if(e.KeyCode==Keys.Escape) {
            if(beforeRecording!=null) { Value=new Hotkey(beforeRecording.Modifiers,beforeRecording.Key); Text=Value.ToString(); }
            ValidationError=null; EndRecording(); if(RecordingChanged!=null) RecordingChanged(this,EventArgs.Empty);
            e.Handled=true; e.SuppressKeyPress=true; return;
        }
        if(e.KeyCode==Keys.Enter && !e.Control && !e.Alt && !e.Shift) { EndRecording(); base.OnKeyDown(e); return; }
        e.SuppressKeyPress=true; e.Handled=true;
        if(!isRecording) return;
        if(e.KeyCode==Keys.ControlKey || e.KeyCode==Keys.Menu || e.KeyCode==Keys.ShiftKey) return;
        uint modifiers=(e.Control?2U:0U)|(e.Alt?1U:0U)|(e.Shift?4U:0U);
        Hotkey candidate=new Hotkey(modifiers,(uint)e.KeyCode);
        try { candidate.Validate(); Value=candidate; Text=Value.ToString(); ValidationError=null; SelectAll(); }
        catch(InvalidDataException ex) { ValidationError=ex.Message; }
        if(RecordingChanged!=null) RecordingChanged(this,EventArgs.Empty); base.OnKeyDown(e);
    }
}

internal sealed class PasswordSlot {
    internal readonly SurfacePanel Panel;
    internal readonly TextBox Value,Confirmation;
    internal readonly bool Exists;
    internal bool Deleted;
    private readonly Label state;
    private readonly TableLayoutPanel fields;
    private readonly Button edit,delete;
    internal PasswordSlot(int index,bool exists,Action<PasswordSlot> expand) {
        Exists=exists; Panel=new SurfacePanel { AutoSize=true,Dock=DockStyle.Top,Padding=new Padding(12),Margin=new Padding(0,0,0,10) };
        TableLayoutPanel content=Theme.Vertical(); Panel.Controls.Add(content);
        TableLayoutPanel top=new TableLayoutPanel { AutoSize=true,Dock=DockStyle.Top,ColumnCount=3,Margin=Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        state=Theme.Label("密码 "+(index+1)+(exists?" · 已设置":" · 未设置")); state.Margin=new Padding(0,8,8,0); top.Controls.Add(state,0,0);
        edit=Theme.Button(exists?"修改":"添加"); edit.MinimumSize=new Size(66,32); edit.AccessibleName="密码 "+(index+1)+(exists?" 修改":" 添加");
        edit.Click+=delegate { if(Deleted) return; expand(this); Value.Focus(); }; top.Controls.Add(edit,1,0);
        delete=Theme.Button("删除"); delete.MinimumSize=new Size(66,32); delete.Enabled=exists; delete.AccessibleName="密码 "+(index+1)+" 删除或撤销删除";
        delete.Click+=delegate { Deleted=!Deleted; Value.Clear(); Confirmation.Clear(); fields.Visible=false; edit.Enabled=!Deleted; delete.Text=Deleted?"撤销删除":"删除"; state.Text="密码 "+(index+1)+(Deleted?" · 保存后删除":exists?" · 已设置":" · 未设置"); }; top.Controls.Add(delete,2,0); Theme.Row(content,top);
        fields=Theme.Vertical(); fields.Padding=new Padding(0,10,0,0); fields.Visible=false;
        Theme.Row(fields,Theme.Label("新密码（8–128 字符；留空保留）"));
        Value=NewPasswordBox("密码 "+(index+1)+" 新密码"); Theme.Row(fields,Value);
        Theme.Row(fields,Theme.Label("再次输入")); Confirmation=NewPasswordBox("密码 "+(index+1)+" 再次输入"); Theme.Row(fields,Confirmation); Theme.Row(content,fields);
    }
    internal void SetExpanded(bool expanded) { fields.Visible=expanded && !Deleted; }
    internal void Clear() { Value.Clear(); Confirmation.Clear(); }
    internal static TextBox NewPasswordBox(string name) { return new TextBox { UseSystemPasswordChar=true,MaxLength=128,Dock=DockStyle.Top,Margin=new Padding(0,0,0,10),AccessibleName=name }; }
}

internal sealed class StylePreview : Control {
    internal bool UpdateStyle;
    internal StylePreview() { Height=140; Dock=DockStyle.Top; DoubleBuffered=true; Margin=new Padding(0,12,0,12); TabStop=false; AccessibleName="主屏遮蔽样式静态示意"; AccessibleRole=AccessibleRole.Graphic; }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); e.Graphics.Clear(UpdateStyle?Color.FromArgb(15,18,24):Color.Black);
        if(!UpdateStyle) return;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(Pen p=new Pen(Color.White,2.5F)) e.Graphics.DrawArc(p,Width/2F-12,20,24,24,-80,240);
        TextRenderer.DrawText(e.Graphics,"正在进行更新  3%",Font,new Rectangle(0,62,Width,24),Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics,"模拟画面 · 非真实系统更新",Font,new Rectangle(0,94,Width,24),Color.LightGray,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
    }
}

internal sealed class SettingsForm : Form {
    private readonly AppConfig original;
    private readonly Func<AppConfig,string> apply;
    private readonly bool startupResetAllowed;
    private readonly Action<bool> recording;
    private readonly HotkeyBox mainKey,secondaryKey,restoreKey;
    private readonly Label[] keyErrors=new Label[3];
    private readonly PasswordSlot[] slots=new PasswordSlot[3];
    private readonly CheckBox passwordEnabled;
    private readonly RadioButton updateStyle;
    private readonly TextBox currentPassword;
    private readonly Label error;
    private readonly Button save;
    private readonly TabControl tabs;
    private bool busy,closed;
    private int generation;
    internal SettingsForm(AppConfig config,bool canResetWithoutOldPassword,Func<AppConfig,string> applyConfig,int initialTab=0,Action<bool> hotkeyRecording=null) {
        original=config.Copy(); apply=applyConfig; startupResetAllowed=canResetWithoutOldPassword; recording=hotkeyRecording;
        Text="设置 - 屏幕遮蔽"; Theme.Form(this,new Size(660,570)); StartPosition=FormStartPosition.CenterParent; MinimumSize=new Size(420,340); MaximizeBox=false; MinimizeBox=false;
        TableLayoutPanel outer=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(16) };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent,100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(outer);
        tabs=new TabControl { Dock=DockStyle.Fill }; outer.Controls.Add(tabs,0,0);
        TableLayoutPanel general=TabContent("常规"); Theme.Row(general,Theme.Heading("主屏遮蔽样式"));
        RadioButton black=new RadioButton { Text="纯黑遮罩",AutoSize=true,Margin=new Padding(0,8,0,10),Checked=!config.MainUpdateStyle };
        updateStyle=new RadioButton { Text="模拟更新",AutoSize=true,Margin=new Padding(0,0,0,10),Checked=config.MainUpdateStyle }; Theme.Row(general,black); Theme.Row(general,updateStyle);
        StylePreview preview=new StylePreview { UpdateStyle=config.MainUpdateStyle }; Theme.Row(general,preview); updateStyle.CheckedChanged+=delegate { preview.UpdateStyle=updateStyle.Checked; preview.Invalidate(); };
        Label simulation=Theme.Label("仅模拟画面，不执行 Windows 更新，不重启，也不会随进度自动解除遮蔽。副屏始终保持纯黑。",true); Theme.Wrap(general,simulation); Theme.Row(general,simulation);
        Label power=Theme.Label("启动默认不遮蔽。遮蔽期间临时请求保持显示与系统活动，恢复全部后释放；不修改电源计划。",true); Theme.Wrap(general,power); Theme.Row(general,power);
        TableLayoutPanel keys=TabContent("快捷键"); Theme.Row(keys,Theme.Heading("全局快捷键"));
        Label keyHelp=Theme.Label("选择录入框，再按新的组合键。录入时暂时停用全局键；Tab 离开，Esc 取消本次录入。三个动作不能重复。",true); Theme.Wrap(keys,keyHelp); Theme.Row(keys,keyHelp);
        mainKey=KeyField(keys,"主屏遮蔽切换",config.MainHotkey,0); secondaryKey=KeyField(keys,"全部副屏遮蔽切换",config.SecondaryHotkey,1); restoreKey=KeyField(keys,"恢复全部屏幕",config.RestoreHotkey,2);
        Label active=Theme.Label("待保存；当前生效恢复键："+config.RestoreHotkey,true); Theme.Wrap(keys,active); Theme.Row(keys,active);
        Label keyboardRules=Theme.Label("组合键须含 Ctrl 或 Alt，可加 Shift；支持字母、数字、功能键，F12 不可用。默认恢复：Ctrl+Alt+F10。",true); Theme.Wrap(keys,keyboardRules); Theme.Row(keys,keyboardRules);
        Button defaults=Theme.Button("恢复默认快捷键"); defaults.Click+=delegate { SetKey(mainKey,new Hotkey(3,0x77)); SetKey(secondaryKey,new Hotkey(3,0x78)); SetKey(restoreKey,new Hotkey(3,0x79)); }; Theme.Row(keys,defaults);
        TableLayoutPanel passwords=TabContent("恢复密码");
        passwordEnabled=new CheckBox { Text="恢复屏幕时验证密码",AutoSize=true,Checked=config.PasswordEnabled,Margin=new Padding(0,4,0,12) }; Theme.Row(passwords,passwordEnabled);
        Label passwordHelp=Theme.Label("最多保存 3 个密码，任意一个正确即可恢复。关闭验证不删除已保存密码；不修改时自动沿用。",true); Theme.Wrap(passwords,passwordHelp); Theme.Row(passwords,passwordHelp);
        string rule=startupResetAllowed?"本次启动尚未开启遮蔽，可直接管理已保存密码。":config.PasswordEntries.Count==0?"本次尚无已保存密码，可直接添加新密码。":"本次已使用过遮蔽，修改密码设置前需要验证任一当前密码。";
        Label resetRule=Theme.Label(rule); Theme.Wrap(passwords,resetRule); Theme.Row(passwords,resetRule);
        Label oldLabel=Theme.Label("当前任一密码（仅修改密码设置时填写）"); bool requiresOld=config.PasswordEntries.Count>0 && !startupResetAllowed;
        oldLabel.Visible=requiresOld; Theme.Row(passwords,oldLabel); currentPassword=PasswordSlot.NewPasswordBox("当前任一已保存密码"); currentPassword.Visible=requiresOld; Theme.Row(passwords,currentPassword);
        for(int i=0;i<slots.Length;i++) { slots[i]=new PasswordSlot(i,i<config.PasswordEntries.Count,ExpandSlot); Theme.Row(passwords,slots[i].Panel); }
        Label forgotten=Theme.Label("忘记密码：结束软件并重新启动，在首次开启遮蔽前重设。关闭窗口到托盘不算重新启动；软件结束时遮罩也会解除。",true); Theme.Wrap(passwords,forgotten); Theme.Row(passwords,forgotten);
        TableLayoutPanel bottom=Theme.Vertical(); bottom.Padding=new Padding(0,12,0,0); outer.Controls.Add(bottom,0,1);
        error=Theme.Label(""); error.ForeColor=Theme.Error; error.AccessibleName="设置保存结果"; Theme.Wrap(bottom,error); Theme.Row(bottom,error);
        FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty };
        Button cancel=Theme.Button("取消"); cancel.Click+=delegate { CancelAndClose(); }; actions.Controls.Add(cancel);
        save=Theme.Button("保存设置",true); save.Click+=SaveSettings; actions.Controls.Add(save); Theme.Row(bottom,actions); AcceptButton=save; CancelButton=cancel;
        tabs.SelectedIndex=Math.Max(0,Math.Min(2,initialTab));
        FormClosing+=delegate { closed=true; generation++; EndRecording(); };
        Shown+=delegate { Theme.FitToWorkArea(this); if(!Theme.ProtectCapture(this)) { MessageBox.Show(this,"当前环境无法排除设置窗口的截图捕获，已取消设置。","屏幕遮蔽",MessageBoxButtons.OK,MessageBoxIcon.Warning); CancelAndClose(); } };
        Theme.Finish(this);
    }
    private TableLayoutPanel TabContent(string title) {
        TabPage page=new TabPage(title) { BackColor=Theme.Background,Padding=new Padding(16),AutoScroll=true }; tabs.TabPages.Add(page);
        TableLayoutPanel table=Theme.Vertical(); page.Controls.Add(table); return table;
    }
    private HotkeyBox KeyField(TableLayoutPanel table,string name,Hotkey value,int index) {
        Label label=Theme.Label(name); label.Margin=new Padding(0,10,0,6); Theme.Row(table,label);
        HotkeyBox box=new HotkeyBox(value,recording) { AccessibleName=name+"快捷键录入" }; Theme.Row(table,box);
        Label issue=Theme.Label(""); issue.ForeColor=Theme.Error; issue.Visible=false; issue.Margin=new Padding(0,0,0,8); Theme.Wrap(table,issue); keyErrors[index]=issue;
        box.RecordingChanged+=delegate { issue.Text=box.ValidationError??""; issue.Visible=issue.Text.Length>0; }; Theme.Row(table,issue); return box;
    }
    private void SetKey(HotkeyBox box,Hotkey value) { box.Value=value; box.Text=value.ToString(); box.ValidationError=null; foreach(Label label in keyErrors) { if(label!=null) label.Visible=false; } }
    private void ExpandSlot(PasswordSlot slot) { foreach(PasswordSlot other in slots) if(other!=null) other.SetExpanded(other==slot); }
    private void EndRecording() { mainKey.EndRecording(); secondaryKey.EndRecording(); restoreKey.EndRecording(); }
    private void CancelAndClose() { generation++; closed=true; DialogResult=DialogResult.Cancel; Close(); }
    private async void SaveSettings(object sender,EventArgs e) {
        if(busy || closed) return; save.Focus(); EndRecording();
        AppConfig candidate; string oldValue; string[] values,confirmations; bool[] deleted; bool changingPassword;
        try {
            HotkeyBox[] allKeys=new[] { mainKey,secondaryKey,restoreKey };
            if(allKeys.Any(k=>!String.IsNullOrEmpty(k.ValidationError))) { tabs.SelectedIndex=1; throw new InvalidDataException("请修正快捷键输入；无效组合未替换当前值。"); }
            candidate=original.Copy(); candidate.MainHotkey=new Hotkey(mainKey.Value.Modifiers,mainKey.Value.Key); candidate.SecondaryHotkey=new Hotkey(secondaryKey.Value.Modifiers,secondaryKey.Value.Key); candidate.RestoreHotkey=new Hotkey(restoreKey.Value.Modifiers,restoreKey.Value.Key); candidate.MainUpdateStyle=updateStyle.Checked;
            candidate.MainHotkey.Validate(); candidate.SecondaryHotkey.Validate(); candidate.RestoreHotkey.Validate();
            if(candidate.MainHotkey.Same(candidate.SecondaryHotkey)) { tabs.SelectedIndex=1; throw new InvalidDataException("主屏和全部副屏使用了相同组合，请选择不同的快捷键。"); }
            if(candidate.RestoreHotkey.Same(candidate.MainHotkey) || candidate.RestoreHotkey.Same(candidate.SecondaryHotkey)) { tabs.SelectedIndex=1; throw new InvalidDataException("恢复全部快捷键不能与主屏或全部副屏的切换键重复。"); }
            values=slots.Select(s=>s.Value.Text).ToArray(); confirmations=slots.Select(s=>s.Confirmation.Text).ToArray(); deleted=slots.Select(s=>s.Deleted).ToArray(); oldValue=currentPassword.Text;
            changingPassword=original.PasswordEnabled!=passwordEnabled.Checked || values.Any(v=>v.Length>0) || confirmations.Any(v=>v.Length>0) || deleted.Any(d=>d);
            for(int i=0;i<3;i++) if(!deleted[i] && (values[i].Length>0 || confirmations[i].Length>0)) {
                if(values[i]!=confirmations[i]) { tabs.SelectedIndex=2; ExpandSlot(slots[i]); slots[i].Confirmation.Focus(); throw new InvalidDataException("密码 "+(i+1)+" 的两次输入不一致，请重新确认。"); }
                if(values[i].Length<8 || values[i].Length>128) { tabs.SelectedIndex=2; ExpandSlot(slots[i]); throw new InvalidDataException("密码长度须为 8 至 128 个字符。"); }
            }
            candidate.PasswordEnabled=passwordEnabled.Checked;
        } catch(Exception ex) { error.Text=ex.Message; return; }
        busy=true; tabs.Enabled=false; save.Enabled=false; error.ForeColor=Theme.Secondary; error.Text="正在验证并保存… 可按 Esc 取消。"; int ticket=++generation;
        try {
            AppConfig prepared=await Task.Run(delegate {
                if(changingPassword && !Passwords.CanChange(original,startupResetAllowed,oldValue)) throw new InvalidDataException("当前密码不正确，原有设置未更改。");
                List<PasswordEntry> entries=new List<PasswordEntry>();
                for(int i=0;i<3;i++) {
                    if(deleted[i]) continue;
                    if(values[i].Length==0 && confirmations[i].Length==0) { if(i<candidate.PasswordEntries.Count) entries.Add(candidate.PasswordEntries[i]); }
                    else entries.Add(Passwords.Create(values[i]));
                }
                candidate.PasswordEntries=entries; candidate.Validate(); return candidate;
            });
            if(closed || IsDisposed || ticket!=generation) return;
            string problem=apply(prepared); if(problem!=null) throw new InvalidDataException(problem);
            DialogResult=DialogResult.OK; Close();
        } catch(Exception ex) { if(!closed && !IsDisposed && ticket==generation) { error.ForeColor=Theme.Error; error.Text=ex.Message; } }
        finally {
            oldValue=null; Array.Clear(values,0,values.Length); Array.Clear(confirmations,0,confirmations.Length);
            if(!closed && !IsDisposed && ticket==generation) { busy=false; tabs.Enabled=true; save.Enabled=true; }
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { currentPassword.Clear(); foreach(PasswordSlot slot in slots) slot.Clear(); base.OnFormClosed(e); }
}

internal sealed class PasswordForm : Form {
    private readonly AppConfig config;
    private readonly AsyncAttempt attempt=new AsyncAttempt();
    private readonly TextBox password;
    private readonly Label feedback;
    private readonly Button verify;
    private readonly CheckBox showPassword;
    private bool closed;
    internal PasswordForm(AppConfig c) {
        config=c.Copy(); Text="恢复全部屏幕"; Theme.Form(this,new Size(460,320)); StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false; ShowInTaskbar=false; TopMost=true;
        Panel scroll=new Panel { Dock=DockStyle.Fill,Padding=new Padding(22),AutoScroll=true }; Controls.Add(scroll);
        TableLayoutPanel rows=Theme.Vertical(); scroll.Controls.Add(rows);
        Theme.Row(rows,Theme.Heading("恢复屏幕显示",14F)); Label hint=Theme.Label("输入任一已保存密码，按 Enter 恢复显示。",true); Theme.Wrap(rows,hint); Theme.Row(rows,hint);
        Theme.Row(rows,Theme.Label("恢复密码")); password=PasswordSlot.NewPasswordBox("恢复密码输入框"); password.TabIndex=0; Theme.Row(rows,password);
        showPassword=new CheckBox { Text="显示密码",AutoSize=true,Margin=new Padding(0,0,0,10),TabIndex=1 }; showPassword.CheckedChanged+=delegate { password.UseSystemPasswordChar=!showPassword.Checked; }; Theme.Row(rows,showPassword);
        feedback=Theme.Label("Esc 取消恢复，屏幕继续保持遮蔽。",true); feedback.MinimumSize=new Size(0,38); feedback.AccessibleName="恢复验证结果"; Theme.Wrap(rows,feedback); Theme.Row(rows,feedback);
        FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty };
        verify=Theme.Button("验证并恢复",true); verify.TabIndex=2; verify.Click+=VerifyPassword; actions.Controls.Add(verify);
        Button cancel=Theme.Button("取消"); cancel.TabIndex=3; cancel.Click+=delegate { CancelAndClose(); }; actions.Controls.Add(cancel); Theme.Row(rows,actions);
        AcceptButton=verify; CancelButton=cancel;
        FormClosing+=delegate { closed=true; attempt.Cancel(); };
        Shown+=delegate { Theme.FitToWorkArea(this); if(!Theme.ProtectCapture(this)) { MessageBox.Show(this,"当前环境无法排除密码窗口的截图捕获。已取消恢复，屏幕继续保持遮蔽。","屏幕遮蔽",MessageBoxButtons.OK,MessageBoxIcon.Warning); CancelAndClose(); return; } FocusInput(); };
        Theme.Finish(this);
    }
    internal void FocusInput() { if(closed || IsDisposed) return; TopMost=true; BringToFront(); Activate(); if(!attempt.Busy) password.Focus(); }
    private void CancelAndClose() { attempt.Cancel(); closed=true; DialogResult=DialogResult.Cancel; Close(); }
    private async void VerifyPassword(object sender,EventArgs e) {
        if(closed || attempt.Busy) return;
        string input=password.Text;
        if(input.Length==0) { feedback.Text="请输入任一已保存密码。"; feedback.ForeColor=Theme.Error; password.Focus(); return; }
        int ticket; if(!attempt.TryBegin(out ticket)) return;
        password.Enabled=false; showPassword.Enabled=false; verify.Enabled=false; feedback.Text="正在验证… 可按 Esc 取消。"; feedback.ForeColor=Theme.Secondary;
        try {
            bool valid=await Task.Run(delegate { return Passwords.VerifyAny(config,input); });
            if(closed || IsDisposed || !attempt.Accept(ticket,valid)) return;
            if(valid) { DialogResult=DialogResult.OK; Close(); return; }
            feedback.Text="密码不正确，屏幕继续保持遮蔽。"; feedback.ForeColor=Theme.Error; password.Clear();
        } catch(Exception) {
            if(closed || IsDisposed || !attempt.Accept(ticket,false)) return;
            feedback.Text="验证未完成，屏幕继续保持遮蔽。请重试。"; feedback.ForeColor=Theme.Error;
        } finally {
            input=null;
            if(!closed && !IsDisposed && !attempt.Busy) { password.Enabled=true; showPassword.Enabled=true; verify.Enabled=true; password.Focus(); }
        }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { password.Clear(); base.OnFormClosed(e); }
}

internal static class UiSnapshots {
    internal static int Run(string directory) {
        Directory.CreateDirectory(directory); var files=new List<string>(); AppConfig config=AppConfig.Default();
        using(ControlForm form=new ControlForm(null)) {
            form.RefreshStatus(config,true,true,true,false,false,1,null); Save(form,directory,"main-panel.png"); files.Add("main-panel.png");
        }
        string[] names={"settings-general.png","settings-hotkeys.png","settings-passwords.png"};
        for(int i=0;i<names.Length;i++) using(SettingsForm form=new SettingsForm(config,true,delegate { return null; },i)) { Save(form,directory,names[i]); files.Add(names[i]); }
        using(PasswordForm form=new PasswordForm(config)) { Save(form,directory,"restore-password.png"); files.Add("restore-password.png"); }
        File.WriteAllText(Path.Combine(directory,"snapshot-validation.json"),new JavaScriptSerializer().Serialize(new {
            version=Program.ProductVersion,scope="hidden-controls-DrawToBitmap-static-layout-only",windowsShown=false,visibleMasksCreated=0,passwordInputsPopulated=false,configurationSaved=false,files=files
        })); return 0;
    }
    private static void LayoutTree(Control control) {
        IntPtr handle=control.Handle; foreach(Control child in control.Controls) LayoutTree(child); control.PerformLayout();
    }
    private static void Save(Form form,string directory,string name) {
        IntPtr handle=form.Handle; LayoutTree(form); LayoutTree(form);
        using(Bitmap bitmap=new Bitmap(form.Width,form.Height)) {
            form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
            using(Graphics graphics=Graphics.FromImage(bitmap)) {
                Point offset=new Point((form.Width-form.ClientSize.Width)/2,form.Height-form.ClientSize.Height-(form.Width-form.ClientSize.Width)/2);
                foreach(Control child in form.Controls.Cast<Control>().Reverse()) DrawTree(child,graphics,new Point(offset.X+child.Left,offset.Y+child.Top));
            }
            bitmap.Save(Path.Combine(directory,name),System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    private static bool LocallyVisible(Control control) {
        // Rendering a never-shown form requires the local visibility flag: Visible
        // includes its hidden ancestors. This reads only our own WinForms tree.
        MethodInfo state=typeof(Control).GetMethod("GetState",BindingFlags.Instance|BindingFlags.NonPublic);
        return state==null || (bool)state.Invoke(control,new object[]{2});
    }
    private static void DrawTree(Control control,Graphics graphics,Point origin) {
        if(!LocallyVisible(control) || control.Width<=0 || control.Height<=0) return;
        using(Bitmap surface=new Bitmap(control.Width,control.Height)) {
            control.DrawToBitmap(surface,new Rectangle(Point.Empty,surface.Size)); graphics.DrawImageUnscaled(surface,origin);
        }
        GraphicsState saved=graphics.Save(); graphics.SetClip(new Rectangle(origin,control.ClientSize),CombineMode.Intersect);
        foreach(Control child in control.Controls.Cast<Control>().Reverse()) {
            TabControl tabs=control as TabControl; if(tabs!=null && child!=tabs.SelectedTab) continue;
            DrawTree(child,graphics,new Point(origin.X+child.Left,origin.Y+child.Top));
        }
        graphics.Restore(saved);
    }
}
