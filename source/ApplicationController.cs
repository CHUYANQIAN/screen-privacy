using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class PrivacyContext : ApplicationContext {
    private AppConfig config;
    private readonly string configPath,logPath,statePath;
    private readonly HotkeySink sink;
    private readonly HotkeyRegistry registry;
    private readonly ControlForm panel;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem restoreMenu;
    private readonly System.Windows.Forms.Timer timer;
    private readonly EventWaitHandle showEvent,restoreEvent,exitEvent;
    private readonly List<MaskWindow> masks=new List<MaskWindow>();
    private readonly SessionState session=new SessionState();
    private bool editing,authenticating,exiting,awake,recording;
    private PasswordForm passwordDialog;
    private DateTime ignoreHotkeysUntil,nextTopologyAttempt;
    private string signature,problem;
    private int pulse;
    internal PrivacyContext(AppConfig settings,string path) {
        config=settings; configPath=path;
        logPath=Path.Combine(Path.GetDirectoryName(path),"screen-privacy.log"); statePath=Path.Combine(Path.GetDirectoryName(path),"status.json");
        sink=new HotkeySink(); registry=new HotkeyRegistry(sink); sink.Pressed=HandleHotkey;
        showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,Program.ShowEvent);
        restoreEvent=new EventWaitHandle(false,EventResetMode.ManualReset,Program.RestoreEvent); restoreEvent.Reset();
        exitEvent=new EventWaitHandle(false,EventResetMode.AutoReset,Program.ExitEvent);
        panel=new ControlForm(this);
        tray=new NotifyIcon {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath),Visible=true};
        var menu=new ContextMenuStrip();
        menu.Items.Add("打开控制面板",null,delegate { ShowPanel(); });
        menu.Items.Add("切换主屏遮蔽",null,delegate { Toggle(true); });
        menu.Items.Add("切换全部副屏遮蔽",null,delegate { Toggle(false); });
        restoreMenu=new ToolStripMenuItem("恢复全部",null,delegate { RestoreRequested(); }); menu.Items.Add(restoreMenu);
        menu.Items.Add("设置",null,delegate { EditSettings(); });
        menu.Items.Add("恢复全部并退出",null,delegate { ExitRequested(); });
        tray.ContextMenuStrip=menu; tray.DoubleClick+=delegate { ShowPanel(); };
        var screens=Target.Current(); signature=Target.Signature(screens); registry.Register(config,screens.Any(s=>!s.Primary));
        timer=new System.Windows.Forms.Timer {Interval=250}; timer.Tick+=Tick; timer.Start();
        Log("READY masks=0 hotkeys="+registry.State.AllRequiredReady); Refresh(); ShowPanel();
    }
    private bool Covered(bool main) { return masks.Any(m=>m.Target.Primary==main); }
    private void Log(string text) { try { File.AppendAllText(logPath,DateTimeOffset.Now.ToString("o")+" "+text+Environment.NewLine); } catch {} }
    private object KeyStatus(RegisteredKey key) { return new {key=key.Key.ToString(),required=key.Required,registered=key.Registered,errorCode=key.ErrorCode,problem=key.Problem}; }
    private void Refresh() {
        var screens=Target.Current(); var state=registry.State;
        string issue=problem??state.Problem;
        panel.RefreshStatus(config,!editing && state.CanStart(true),!editing && state.CanStart(false),state.RestoreReady,Covered(true),Covered(false),screens.Count(s=>!s.Primary),issue);
        tray.Text="屏幕遮蔽："+config.RestoreHotkey+" 恢复全部";
        restoreMenu.Text="恢复全部（"+config.RestoreHotkey+"）";
        try {
            File.WriteAllText(statePath,new JavaScriptSerializer().Serialize(new {
                updatedAt=DateTimeOffset.Now.ToString("o"),version=Program.ProductVersion,
                hotkeysReady=state.AllRequiredReady,restoreReady=state.RestoreReady,
                mainHotkey=config.MainHotkey.ToString(),secondaryHotkey=config.SecondaryHotkey.ToString(),restoreHotkey=config.RestoreHotkey.ToString(),
                registrations=new {main=KeyStatus(state.Main),secondary=KeyStatus(state.Secondary),restore=KeyStatus(state.Restore)},
                passwordEnabled=config.PasswordEnabled,passwordCount=config.PasswordEntries.Count,passwordMode="remembered-with-startup-reset",passwordResetAllowed=session.PasswordResetAllowed,
                mainCovered=Covered(true),secondaryCovered=Covered(false),maskCount=masks.Count,awakeRequested=awake,
                editing=editing,authenticating=authenticating,panelVisible=panel.Visible,
                screenCount=screens.Count,screens=screens.Select(s=>new {device=s.Device,primary=s.Primary,x=s.Bounds.X,y=s.Bounds.Y,width=s.Bounds.Width,height=s.Bounds.Height}).ToArray(),problem=issue
            }));
        } catch {}
    }
    private void ShowPanel() {
        if(!session.CanShowPanel(masks.Count,exiting)) return;
        panel.Show(); panel.WindowState=FormWindowState.Normal; panel.Activate();
    }
    private void HandleHotkey(int id) {
        if(editing || exiting || DateTime.UtcNow<ignoreHotkeysUntil) return;
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(id==3) RestoreRequested(); else if(id==1 || id==2) Toggle(id==1);
    }
    internal void Toggle(bool main) {
        if(editing || authenticating || exiting) return;
        if(Covered(main)) { if(Authorize()) RemoveMasks(main); return; }
        if(!registry.State.CanStart(main)) {
            problem=registry.State.Problem??"当前没有可遮蔽的这组屏幕。"; Refresh(); return;
        }
        var targets=Target.Select(Target.Current(),main); if(targets.Count==0) { Refresh(); return; }
        var prepared=new List<MaskWindow>(); bool acquired=false;
        try {
            foreach(var target in targets) { var mask=new MaskWindow(target,config.MainUpdateStyle); prepared.Add(mask); mask.Prepare(); }
            if(!awake) { if(Native.SetThreadExecutionState(0x80000003)==0) throw new InvalidOperationException("无法请求临时保活，未开启遮蔽。"); awake=acquired=true; }
            panel.Hide();
            foreach(var mask in prepared) mask.Show();
            masks.AddRange(prepared); prepared.Clear(); session.MaskCommitted(); problem=null;
            Log("MASK_ON group="+(main?"main":"secondary")+" count="+masks.Count);
        } catch(Exception ex) {
            foreach(var mask in prepared) mask.Dispose();
            if(acquired && masks.Count==0) { Native.SetThreadExecutionState(0x80000000); awake=false; }
            problem=ex.Message; if(masks.Count==0) { ShowPanel(); MessageBox.Show(panel,ex.Message,"无法开启遮蔽"); }
            else tray.ShowBalloonTip(5000,"未能增加遮蔽",ex.Message,ToolTipIcon.Warning);
        }
        Refresh();
    }
    private bool Authorize() {
        if(!config.PasswordEnabled || masks.Count==0) return true;
        if(authenticating) return false;
        authenticating=true; Refresh();
        try { using(passwordDialog=new PasswordForm(config.Copy())) return passwordDialog.ShowDialog()==DialogResult.OK; }
        finally { passwordDialog=null; authenticating=false; ignoreHotkeysUntil=DateTime.UtcNow.AddMilliseconds(400); Refresh(); }
    }
    private void RemoveMasks(bool? main) {
        foreach(var mask in masks.Where(m=>!main.HasValue || m.Target.Primary==main.Value).ToArray()) { masks.Remove(mask); mask.Dispose(); }
        if(masks.Count==0 && awake) { Native.SetThreadExecutionState(0x80000000); awake=false; }
        problem=null; Log("RESTORE remaining="+masks.Count); Refresh();
    }
    internal void RestoreRequested() {
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(editing || exiting || masks.Count==0) return; if(Authorize()) RemoveMasks(null);
    }
    internal void ExitRequested() {
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(editing || exiting) return;
        if(!Authorize()) return; exiting=true; RemoveMasks(null); ExitThread();
    }
    private void RecordingChanged(bool value) {
        if(!editing || masks.Count>0 || exiting) return;
        recording=value;
        if(value) registry.Unregister(); else registry.Register(config,Target.Current().Any(s=>!s.Primary));
        Refresh();
    }
    internal void EditSettings(int initialTab=0) {
        if(editing || authenticating || exiting) return;
        if(masks.Count>0) { tray.ShowBalloonTip(4000,"先恢复屏幕","请按 "+config.RestoreHotkey+" 恢复全部，再修改设置。",ToolTipIcon.Info); return; }
        editing=true; Refresh(); ShowPanel();
        try {
            using(var form=new SettingsForm(config.Copy(),session.PasswordResetAllowed,delegate(AppConfig candidate) {
                recording=false;
                string issue=registry.Apply(config,candidate,Target.Current().Any(s=>!s.Primary),delegate(AppConfig value) { value.Save(configPath); });
                if(issue==null) { config=candidate; problem=null; }
                else problem=issue;
                Refresh(); return issue;
            },initialTab,RecordingChanged)) form.ShowDialog(panel);
        } finally {
            if(recording) { recording=false; registry.Register(config,Target.Current().Any(s=>!s.Primary)); }
            else registry.UpdateSecondary(config,Target.Current().Any(s=>!s.Primary));
            editing=false; ignoreHotkeysUntil=DateTime.UtcNow.AddMilliseconds(800);
            problem=registry.State.Problem; Refresh(); Log("SETTINGS_CLOSED hotkeys="+registry.State.AllRequiredReady);
        }
    }
    private void TopologyChanged(List<Target> screens) {
        // Only id=2 may change here; the active restore registration is untouched.
        if(!editing) registry.UpdateSecondary(config,screens.Any(s=>!s.Primary));
        if(masks.Count==0) { signature=Target.Signature(screens); Refresh(); return; }
        if(screens.Count==0) return;
        var replacements=new List<MaskWindow>();
        try {
            foreach(var target in screens) { var mask=new MaskWindow(target,config.MainUpdateStyle); replacements.Add(mask); mask.Prepare(); }
            foreach(var mask in replacements) mask.Show();
            foreach(var old in masks) old.Dispose(); masks.Clear(); masks.AddRange(replacements); replacements.Clear(); panel.Hide();
            signature=Target.Signature(screens);
            if(passwordDialog!=null && !passwordDialog.IsDisposed) {
                var primary=screens.FirstOrDefault(s=>s.Primary)??screens[0]; var bounds=primary.Bounds;
                passwordDialog.Location=new Point(bounds.X+(bounds.Width-passwordDialog.Width)/2,bounds.Y+(bounds.Height-passwordDialog.Height)/2);
                passwordDialog.TopMost=true; passwordDialog.BringToFront(); passwordDialog.FocusInput();
            }
            problem="显示布局改变，已尝试补盖全部当前屏幕。恢复键："+config.RestoreHotkey+"。"; Log("TOPOLOGY_CHANGED coveredAll="+masks.Count);
        } catch(Exception) {
            foreach(var mask in replacements) mask.Dispose(); nextTopologyAttempt=DateTime.UtcNow.AddSeconds(2);
            problem="显示布局改变，遮蔽覆盖需检查，正在重试。恢复键："+config.RestoreHotkey+"。";
            Log("TOPOLOGY_ERROR"); tray.ShowBalloonTip(4000,"屏幕遮蔽","显示布局发生变化，遮蔽覆盖需检查。",ToolTipIcon.Warning);
        }
        Refresh();
    }
    private void Tick(object sender,EventArgs e) {
        if(exiting) return;
        if(showEvent.WaitOne(0) && !editing && !authenticating) ShowPanel();
        if(restoreEvent.WaitOne(0)) { restoreEvent.Reset(); RestoreRequested(); }
        if(exitEvent.WaitOne(0)) ExitRequested();
        if(exiting) return;
        var current=Target.Current();
        if(Target.Signature(current)!=signature && DateTime.UtcNow>=nextTopologyAttempt) TopologyChanged(current);
        if(!editing && !authenticating) foreach(var mask in masks) mask.KeepAbove();
        pulse++; if(pulse%20==0) Refresh();
    }
    protected override void ExitThreadCore() {
        exiting=true; timer.Stop(); timer.Dispose();
        foreach(var mask in masks) mask.Dispose(); masks.Clear(); if(awake) Native.SetThreadExecutionState(0x80000000); awake=false;
        registry.Unregister(); sink.Dispose(); tray.Visible=false; tray.Dispose();
        showEvent.Dispose(); restoreEvent.Dispose(); exitEvent.Dispose(); panel.Exiting=true; panel.Dispose(); Log("STOPPED"); base.ExitThreadCore();
    }
}

internal static class Program {
    internal const string ProductVersion="0.3.0";
    internal const string RestoreEvent="Local\\CodexSecondaryShadeStop-20261027";
    internal const string ShowEvent="Local\\CodexScreenPrivacyShow-v2";
    internal const string ExitEvent="Local\\CodexScreenPrivacyExit-v2";
    private const string MutexName="Local\\CodexScreenPrivacyInstance-v2";
    private static string Arg(string[] args,string key,string fallback) { int n=Array.IndexOf(args,key); return n>=0&&n+1<args.Length?args[n+1]:fallback; }
    private static void Signal(string name) { try { using(var signal=EventWaitHandle.OpenExisting(name)) signal.Set(); } catch(WaitHandleCannotBeOpenedException) {} }
    [STAThread] private static int Main(string[] args) {
        if(args.Contains("--restore") || args.Contains("--stop") || Path.GetFileNameWithoutExtension(Application.ExecutablePath)=="RestoreSecondary") { Signal(RestoreEvent); return 0; }
        if(args.Contains("--exit")) { Signal(ExitEvent); return 0; }
        try {
            if(args.Contains("--self-test")) return LogicTests.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.json")));
            if(args.Contains("--preview-update")) { using(var image=new Bitmap(960,540)) { using(var graphics=Graphics.FromImage(image)) MaskWindow.PaintUpdate(graphics,image.Size,3,0.8); image.Save(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-preview.png")),System.Drawing.Imaging.ImageFormat.Png); } return 0; }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--render-ui")) return UiSnapshots.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-preview")));
            if(args.Contains("--check-hotkey-transactions")) return NativeChecks.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-hotkey-checks.json")));
            if(args.Contains("--check")) return Check(args);
            bool created;
            using(var mutex=new Mutex(true,MutexName,out created)) {
                if(!created) { Signal(ShowEvent); return 0; }
                string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.json"); var config=AppConfig.Load(path); config.Save(path);
                Application.Run(new PrivacyContext(config,path));
            }
            return 0;
        } catch(Exception ex) { MessageBox.Show("软件未能启动，未自动开启遮蔽。原配置未重置。\n"+ex.Message,"屏幕遮蔽"); return 1; }
    }
    private static int Check(string[] args) {
        string path=Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capabilities.json"));
        string settings=Arg(args,"--config",null); var config=settings==null?AppConfig.Default():AppConfig.Load(settings);
        var results=new List<object>(); bool masksReady=true; RegistrationState state;
        var targets=Target.Current();
        using(var sink=new HotkeySink()) { var registry=new HotkeyRegistry(sink); state=registry.Register(config,targets.Any(t=>!t.Primary)); }
        foreach(var target in targets) {
            try { using(var mask=new MaskWindow(target)) { mask.Prepare(); results.Add(new {device=target.Device,primary=target.Primary,width=target.Bounds.Width,height=target.Bounds.Height,hiddenMaskPrepared=true,visible=false,error=(string)null}); } }
            catch(Exception ex) { masksReady=false; results.Add(new {device=target.Device,primary=target.Primary,width=target.Bounds.Width,height=target.Bounds.Height,hiddenMaskPrepared=false,visible=false,error=ex.Message}); }
        }
        bool pass=state.AllRequiredReady && masksReady && results.Count>0;
        File.WriteAllText(path,new JavaScriptSerializer().Serialize(new {pass=pass,version=ProductVersion,configurationScope=settings==null?"defaults-only":"explicit-config",hotkeysReady=state.AllRequiredReady,restoreReady=state.RestoreReady,mainHotkey=config.MainHotkey.ToString(),secondaryHotkey=config.SecondaryHotkey.ToString(),restoreHotkey=config.RestoreHotkey.ToString(),hotkeyError=state.Problem,visibleMasksCreated=0,screens=results})); return pass?0:1;
    }
}
