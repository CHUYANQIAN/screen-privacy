using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Func<Task<MinimizeResult>> minimizeWindows;
    private Rectangle? panelBeforeMask;
    private bool editing,authenticating,exiting,awake,recording,restoring;
    private AsyncAttempt restoreAttempt;
    private PasswordForm passwordDialog;
    private DateTime ignoreHotkeysUntil,nextTopologyAttempt;
    private string signature,problem;
    private int pulse;
    internal PrivacyContext(AppConfig settings,string path,Func<Task<MinimizeResult>> minimizeOverride=null) {
        config=settings; configPath=path;
        minimizeWindows=minimizeOverride??new Func<Task<MinimizeResult>>(WindowMinimizer.MinimizeOtherApplicationsAsync);
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
        menu.Items.Add("遮蔽全部屏幕",null,delegate { CoverAll(); });
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
        bool available=!editing && !authenticating && !restoring && !exiting;
        panel.RefreshStatus(config,available && state.CanStart(true),available && state.CanStart(false),state.RestoreReady,Covered(true),Covered(false),screens.Count(s=>!s.Primary),issue,available && MaskUiPolicy.CanCoverAll(screens,masks.Select(m=>m.Target),state));
        tray.Text="屏幕遮蔽运行中："+config.RestoreHotkey+" 恢复";
        restoreMenu.Text="恢复全部（"+config.RestoreHotkey+"）";
        try {
            File.WriteAllText(statePath,new JavaScriptSerializer().Serialize(new {
                updatedAt=DateTimeOffset.Now.ToString("o"),version=Program.ProductVersion,
                hotkeysReady=state.AllRequiredReady,restoreReady=state.RestoreReady,
                mainHotkey=config.MainHotkey.ToString(),secondaryHotkey=config.SecondaryHotkey.ToString(),restoreHotkey=config.RestoreHotkey.ToString(),
                registrations=new {main=KeyStatus(state.Main),secondary=KeyStatus(state.Secondary),restore=KeyStatus(state.Restore)},
                passwordEnabled=config.PasswordEnabled,passwordCount=config.PasswordEntries.Count,passwordMode="remembered-with-startup-reset",passwordResetAllowed=session.PasswordResetAllowed,
                mainCovered=Covered(true),secondaryCovered=Covered(false),maskCount=masks.Count,awakeRequested=awake,
                editing=editing,authenticating=authenticating,restoring=restoring,panelVisible=panel.Visible,panelMinimized=panel.WindowState==FormWindowState.Minimized,
                minimizeOnRestore=config.MinimizeOnRestore,controllerRunning=!exiting,
                processId=Process.GetCurrentProcess().Id,
                screenCount=screens.Count,screens=screens.Select(s=>new {device=s.Device,primary=s.Primary,x=s.Bounds.X,y=s.Bounds.Y,width=s.Bounds.Width,height=s.Bounds.Height}).ToArray(),problem=issue
            }));
        } catch {}
    }
    private bool ShowPanel(bool activate=true,bool restoreSavedPosition=false) {
        if(exiting || restoring || panel.IsDisposed) return false;
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return false; }
        var available=MaskUiPolicy.Uncovered(Target.Current(),masks.Select(m=>m.Target));
        if(!session.CanShowPanel(available.Count,exiting)) { panel.Hide(); return false; }
        Rectangle requested=restoreSavedPosition && panelBeforeMask.HasValue?panelBeforeMask.Value:(panel.WindowState==FormWindowState.Normal?panel.Bounds:panel.RestoreBounds);
        if(panel.WindowState!=FormWindowState.Normal) panel.WindowState=FormWindowState.Normal;
        foreach(var target in available.OrderByDescending(s=>s.Bounds.IntersectsWith(requested)).ThenByDescending(s=>s.Primary)) {
            var actualScreen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==target.Device);
            Rectangle area=actualScreen==null?target.Bounds:actualScreen.WorkingArea,positioned;
            if(!MaskUiPolicy.TryFitPanel(area,requested,panel.MinimumSize,out positioned)) continue;
            panel.Bounds=positioned;
            if(masks.Any(m=>m.Target.Bounds.IntersectsWith(panel.Bounds))) continue;
            panel.Show(); if(activate) panel.Activate(); return true;
        }
        panel.Hide(); return false;
    }
    private void HandleHotkey(int id) {
        if(editing || restoring || exiting || DateTime.UtcNow<ignoreHotkeysUntil) return;
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(id==3) RestoreRequested(); else if(id==1 || id==2) Toggle(id==1);
    }
    internal void Toggle(bool main) {
        if(editing || authenticating || restoring || exiting) return;
        if(Covered(main)) { RestoreGroup(main,false); return; }
        Cover(main);
    }
    internal void CoverAll() { Cover(null); }
    private void Cover(bool? main) {
        if(editing || authenticating || restoring || exiting) return;
        var targets=MaskUiPolicy.TargetsToCover(Target.Current(),masks.Select(m=>m.Target),main);
        if(targets.Count==0) { Refresh(); return; }
        if(!registry.State.RestoreReady || targets.Any(t=>!registry.State.CanStart(t.Primary))) {
            problem=registry.State.Problem??"所需的遮蔽或恢复快捷键不可用。"; Refresh(); return;
        }
        var prepared=new List<MaskWindow>(); bool acquired=false,wasVisible=panel.Visible;
        if(masks.Count==0) panelBeforeMask=panel.WindowState==FormWindowState.Normal?panel.Bounds:panel.RestoreBounds;
        try {
            foreach(var target in targets) { var mask=new MaskWindow(target,config.MainUpdateStyle); prepared.Add(mask); mask.Prepare(); }
            if(!awake) { if(Native.SetThreadExecutionState(0x80000003)==0) throw new InvalidOperationException("无法请求临时保活，未开启遮蔽。"); awake=acquired=true; }
            panel.Hide();
            foreach(var mask in prepared) mask.Show();
            masks.AddRange(prepared); prepared.Clear(); session.MaskCommitted(); problem=null;
            if(wasVisible) ShowPanel(false);
            Log("MASK_ON group="+(main.HasValue?(main.Value?"main":"secondary"):"all")+" count="+masks.Count);
        } catch(Exception ex) {
            foreach(var mask in prepared) mask.Dispose();
            if(acquired && masks.Count==0) { Native.SetThreadExecutionState(0x80000000); awake=false; }
            problem=ex.Message; if(masks.Count==0) { ShowPanel(); MessageBox.Show(panel,ex.Message,"无法开启遮蔽"); }
            else tray.ShowBalloonTip(5000,"未能增加遮蔽",ex.Message,ToolTipIcon.Warning);
            if(wasVisible) ShowPanel(false);
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
        Log("RESTORE remaining="+masks.Count);
    }
    private async void RestoreGroup(bool? main,bool exitAfter) {
        if(editing || restoring || exiting) return;
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(masks.Count==0) { if(exitAfter) { exiting=true; ExitThread(); } return; }
        if(!Authorize()) return;
        int before=masks.Count,remaining=masks.Count(m=>main.HasValue && m.Target.Primary!=main.Value);
        // A request that releases the final group also releases any masks added
        // by a layout change while asynchronous minimization is in progress.
        bool? effectiveGroup=remaining==0?(bool?)null:main;
        var attempt=new AsyncAttempt(); int ticket; attempt.TryBegin(out ticket); restoreAttempt=attempt; restoring=true; problem=null; Refresh();
        try {
            if(MaskUiPolicy.ShouldMinimizeOnRestore(config.MinimizeOnRestore,before,remaining)) {
                Log("MINIMIZE_BEGIN");
                try { var result=await minimizeWindows(); if(result.Failed>0 || result.TimedOut || !String.IsNullOrEmpty(result.Error)) problem=result.Summary; Log("MINIMIZE_DONE requested="+result.Requested+" minimized="+result.Minimized+" failed="+result.Failed); }
                catch(Exception) { problem="部分应用窗口未能确认最小化，已继续恢复显示。"; Log("MINIMIZE_ERROR"); }
            }
            if(exiting || !attempt.Accept(ticket,true)) return;
            bool minimizePanel=config.MinimizeOnRestore && effectiveGroup==null && !exitAfter;
            if(minimizePanel) { panel.WindowState=FormWindowState.Minimized; panel.Show(); }
            RemoveMasks(effectiveGroup);
            restoring=false; restoreAttempt=null;
            if(exitAfter) { exiting=true; ExitThread(); return; }
            if(MaskUiPolicy.ShouldShowAfterRestore(before,masks.Count,false,config.MinimizeOnRestore)) ShowPanel(true,true);
            else if(masks.Count>0) ShowPanel();
            if(minimizePanel && problem!=null) tray.ShowBalloonTip(4000,"屏幕已恢复",problem,ToolTipIcon.Warning);
            Refresh();
        } finally {
            if(Object.ReferenceEquals(restoreAttempt,attempt)) { restoreAttempt=null; restoring=false; if(!exiting) Refresh(); }
        }
    }
    internal void RestoreRequested() {
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(editing || restoring || exiting || masks.Count==0) return; RestoreGroup(null,false);
    }
    internal void ExitRequested() {
        if(authenticating) { if(passwordDialog!=null) passwordDialog.FocusInput(); return; }
        if(editing || restoring || exiting) return;
        RestoreGroup(null,true);
    }
    internal LifecycleSnapshot InspectLifecycle() {
        return new LifecycleSnapshot {
            MaskCount=masks.Count,MainCovered=Covered(true),SecondaryCovered=Covered(false),PanelVisible=panel.Visible,PanelMinimized=panel.WindowState==FormWindowState.Minimized,
            PanelBounds=panel.Bounds,PanelDoesNotIntersectMasks=!masks.Any(m=>m.Target.Bounds.IntersectsWith(panel.Bounds)),
            Awake=awake,Restoring=restoring,Exiting=exiting,HotkeysReady=registry.State.AllRequiredReady,
            PasswordResetAllowed=session.PasswordResetAllowed,ProcessId=Process.GetCurrentProcess().Id
        };
    }
    private void RecordingChanged(bool value) {
        if(!editing || masks.Count>0 || exiting) return;
        recording=value;
        if(value) registry.Unregister(); else registry.Register(config,Target.Current().Any(s=>!s.Primary));
        Refresh();
    }
    internal void EditSettings(int initialTab=0) {
        if(editing || authenticating || restoring || exiting) return;
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
        var replacements=new List<MaskWindow>(); bool wasVisible=panel.Visible;
        try {
            foreach(var target in screens) { var mask=new MaskWindow(target,config.MainUpdateStyle); replacements.Add(mask); mask.Prepare(); }
            panel.Hide(); foreach(var mask in replacements) mask.Show();
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
            if(wasVisible && !authenticating && !restoring) ShowPanel(false);
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
        if(panel.Visible && masks.Any(m=>m.Target.Bounds.IntersectsWith(panel.Bounds))) ShowPanel(false);
        pulse++; if(pulse%20==0) Refresh();
    }
    protected override void ExitThreadCore() {
        exiting=true; if(restoreAttempt!=null) restoreAttempt.Cancel(); timer.Stop(); timer.Dispose();
        foreach(var mask in masks) mask.Dispose(); masks.Clear(); if(awake) Native.SetThreadExecutionState(0x80000000); awake=false;
        registry.Unregister(); sink.Dispose(); tray.Visible=false; tray.Dispose();
        showEvent.Dispose(); restoreEvent.Dispose(); exitEvent.Dispose(); panel.Exiting=true; panel.Dispose(); Log("STOPPED"); base.ExitThreadCore();
    }
}

internal static class Program {
    internal const string ProductVersion="0.3.1";
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
            if(args.Contains("--self-test")) { string report=Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.json")); int result=LogicTests.Run(report); return result==0?LifecycleLogicChecks.Append(report):result; }
            if(args.Contains("--preview-update")) { using(var image=new Bitmap(960,540)) { using(var graphics=Graphics.FromImage(image)) MaskWindow.PaintUpdate(graphics,image.Size,3,0.8); image.Save(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-preview.png")),System.Drawing.Imaging.ImageFormat.Png); } return 0; }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Contains("--render-ui")) return UiSnapshots.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-preview")));
            if(args.Contains("--check-minimization")) return NativeMinimizeChecks.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"minimization-check.json")));
            if(args.Contains("--minimize-fixture")) return NativeMinimizeChecks.RunFixture(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fixture-ready.json")));
            if(args.Contains("--check-mask-lifecycle")) return NativeLifecycleChecks.Run(Arg(args,"--report",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"lifecycle-check.json")),args.Contains("--slow-observation"));
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
