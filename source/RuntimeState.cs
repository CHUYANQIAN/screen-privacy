using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// These state classes are used by the real UI and by non-disruptive logic tests.
internal sealed class AsyncAttempt {
    private int generation;
    internal bool Busy { get; private set; }
    internal bool Authorized { get; private set; }
    internal bool Cancelled { get; private set; }
    internal bool TryBegin(out int ticket) {
        ticket=generation;
        if(Busy || Cancelled || Authorized) return false;
        ticket=++generation; Busy=true; return true;
    }
    internal bool Accept(int ticket,bool success) {
        if(Cancelled || !Busy || ticket!=generation) return false;
        Busy=false; Authorized=success; return true;
    }
    internal void Cancel() { generation++; Cancelled=true; Busy=false; Authorized=false; }
}

internal sealed class SessionState {
    internal bool PasswordResetAllowed { get; private set; }
    internal SessionState() { PasswordResetAllowed=true; }
    internal void MaskCommitted() { PasswordResetAllowed=false; }
    internal bool CanShowPanel(int availableDisplayCount,bool exiting) { return availableDisplayCount>0 && !exiting; }
}

internal interface IHotkeyBackend {
    int Register(int id,Hotkey key);
    void Unregister(int id);
}

internal sealed class RegisteredKey {
    internal int Id;
    internal string Name;
    internal Hotkey Key;
    internal bool Required;
    internal bool Registered;
    internal int ErrorCode;
    internal string Problem {
        get {
            if(!Required || Registered) return null;
            string reason=ErrorCode==1409?"已被其他软件占用":ErrorCode==5?"当前运行环境不允许注册":"注册失败";
            return Name+"（"+Key+"）："+reason+"，错误 "+ErrorCode+"。";
        }
    }
}

internal sealed class RegistrationState {
    internal RegisteredKey Main,Secondary,Restore;
    internal bool RestoreReady { get { return Restore!=null && Restore.Registered; } }
    internal bool CanStart(bool main) { var key=main?Main:Secondary; return RestoreReady && key!=null && key.Required && key.Registered; }
    internal bool AllRequiredReady { get { return new[]{Main,Secondary,Restore}.All(k=>k!=null && (!k.Required || k.Registered)); } }
    internal string Problem { get { var errors=new[]{Restore,Main,Secondary}.Where(k=>k!=null).Select(k=>k.Problem).Where(s=>s!=null); string text=String.Join(" ",errors); return text.Length==0?null:text; } }
}

internal sealed class HotkeyRegistry {
    private readonly IHotkeyBackend backend;
    internal RegistrationState State { get; private set; }
    internal HotkeyRegistry(IHotkeyBackend value) { backend=value; }
    private RegisteredKey RegisterOne(int id,string name,Hotkey key,bool required) {
        var result=new RegisteredKey {Id=id,Name=name,Key=new Hotkey(key.Modifiers,key.Key),Required=required};
        if(required) { result.ErrorCode=backend.Register(id,key); result.Registered=result.ErrorCode==0; }
        return result;
    }
    internal RegistrationState Register(AppConfig config,bool hasSecondary) {
        config.Validate(); Unregister();
        var restore=RegisterOne(3,"恢复全部",config.RestoreHotkey,true);
        var main=RegisterOne(1,"主屏切换",config.MainHotkey,true);
        var secondary=RegisterOne(2,"副屏切换",config.SecondaryHotkey,hasSecondary);
        State=new RegistrationState {Main=main,Secondary=secondary,Restore=restore}; return State;
    }
    internal void UpdateSecondary(AppConfig config,bool required) {
        if(State==null) { Register(config,required); return; }
        if(State.Secondary.Required==required && (!required || State.Secondary.Registered)) return;
        backend.Unregister(2); State.Secondary=RegisterOne(2,"副屏切换",config.SecondaryHotkey,required);
    }
    internal void Unregister() {
        for(int id=1;id<=3;id++) backend.Unregister(id);
        if(State!=null) foreach(var item in new[]{State.Main,State.Secondary,State.Restore}) item.Registered=false;
    }
    internal string Apply(AppConfig previous,AppConfig candidate,bool hasSecondary,Action<AppConfig> save) {
        // Called only without masks. The caller commits its config only on null.
        candidate.Validate();
        string issue=null;
        try {
            var proposed=Register(candidate,hasSecondary);
            if(!proposed.AllRequiredReady) issue=proposed.Problem;
            else { save(candidate); return null; }
        } catch(Exception ex) { issue="设置未保存："+ex.Message; }
        var restored=Register(previous,hasSecondary);
        return issue+"\n原设置未更改。"+(restored.AllRequiredReady?"原快捷键已恢复。":"原快捷键未完全恢复："+restored.Problem)+"\n当前恢复组合键："+previous.RestoreHotkey+(restored.RestoreReady?"（已注册）":"（不可用，禁止开启遮蔽）");
    }
}

internal sealed class HotkeySink : NativeWindow,IDisposable,IHotkeyBackend {
    internal Action<int> Pressed;
    internal HotkeySink() { CreateHandle(new CreateParams {Caption="Screen Privacy Hotkeys",Parent=new IntPtr(-3)}); }
    public int Register(int id,Hotkey key) { return Native.RegisterHotKey(Handle,id,key.Modifiers|0x4000,key.Key)?0:Marshal.GetLastWin32Error(); }
    public void Unregister(int id) { Native.UnregisterHotKey(Handle,id); }
    protected override void WndProc(ref Message m) { if(m.Msg==0x312 && Pressed!=null) Pressed(m.WParam.ToInt32()); base.WndProc(ref m); }
    public void Dispose() { for(int id=1;id<=3;id++) Unregister(id); DestroyHandle(); }
}
