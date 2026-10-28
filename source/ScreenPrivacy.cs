using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("屏幕遮蔽")]
[assembly: AssemblyProduct("屏幕遮蔽")]
[assembly: AssemblyDescription("实体屏幕遮蔽与活动桌面辅助工具")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("0.3.0.0")]
[assembly: AssemblyFileVersion("0.3.0.0")]
[assembly: AssemblyInformationalVersion("0.3.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

internal static class Native {
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetWindowDisplayAffinity(IntPtr h, out uint a);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetLayeredWindowAttributes(IntPtr h,uint c,byte a,uint f);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h,int id);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmIsCompositionEnabled(out bool enabled);
}

internal sealed class Hotkey {
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
    public Hotkey() {}
    public Hotkey(uint modifiers,uint key) { Modifiers=modifiers; Key=key; }
    public bool Same(Hotkey other) { return other!=null && Modifiers==other.Modifiers && Key==other.Key; }
    public void Validate() {
        if((Modifiers & 3)==0 || (Modifiers & ~7U)!=0) throw new InvalidDataException("快捷键必须包含 Ctrl 或 Alt，可选 Shift。");
        bool valid=(Key>=0x30 && Key<=0x39)||(Key>=0x41 && Key<=0x5A)||(Key>=0x70 && Key<=0x87);
        if(!valid || Key==0x7B) throw new InvalidDataException("请使用字母、数字或功能键；F12 为系统保留键。");
    }
    public override string ToString() {
        return ((Modifiers&2)!=0?"Ctrl+":"")+((Modifiers&1)!=0?"Alt+":"")+((Modifiers&4)!=0?"Shift+":"")+((Keys)Key).ToString();
    }
}

internal sealed class PasswordEntry {
    public string Salt { get; set; }
    public string Hash { get; set; }
    public int Iterations { get; set; }
    public void Validate() {
        if(Iterations<200000 || Iterations>2000000 || Convert.FromBase64String(Salt??"").Length!=32 || Convert.FromBase64String(Hash??"").Length!=32) throw new InvalidDataException("已保存的密码配置损坏。");
    }
}

internal sealed class AppConfig {
    public int Version { get; set; }
    public Hotkey MainHotkey { get; set; }
    public Hotkey SecondaryHotkey { get; set; }
    public Hotkey RestoreHotkey { get; set; }
    public bool PasswordEnabled { get; set; }
    public bool MainUpdateStyle { get; set; }
    public string Salt { get; set; }
    public string PasswordHash { get; set; }
    public int Iterations { get; set; }
    public List<PasswordEntry> PasswordEntries { get; set; }
    public static AppConfig Default() { return new AppConfig { Version=2,MainHotkey=new Hotkey(3,0x77),SecondaryHotkey=new Hotkey(3,0x78),RestoreHotkey=new Hotkey(3,0x79),PasswordEnabled=false,MainUpdateStyle=true,Salt="",PasswordHash="",Iterations=600000,PasswordEntries=new List<PasswordEntry>() }; }
    public AppConfig Copy() { return new JavaScriptSerializer().Deserialize<AppConfig>(new JavaScriptSerializer().Serialize(this)); }
    public void Validate() {
        if(Version!=2 || MainHotkey==null || SecondaryHotkey==null || RestoreHotkey==null) throw new InvalidDataException("配置中的快捷键缺失或格式不正确，原配置未被重置。");
        MainHotkey.Validate(); SecondaryHotkey.Validate(); RestoreHotkey.Validate();
        if(MainHotkey.Same(SecondaryHotkey)) throw new InvalidDataException("主屏与副屏快捷键不能相同。");
        if(MainHotkey.Same(RestoreHotkey)) throw new InvalidDataException("主屏与恢复全部快捷键不能相同。");
        if(SecondaryHotkey.Same(RestoreHotkey)) throw new InvalidDataException("副屏与恢复全部快捷键不能相同。");
        if(PasswordEntries==null || PasswordEntries.Count>3 || PasswordEntries.Any(p=>p==null)) throw new InvalidDataException("最多可保存三个有效密码。");
        foreach(var entry in PasswordEntries) entry.Validate();
        if(PasswordEnabled && PasswordEntries.Count==0) throw new InvalidDataException("请至少设置一个恢复密码。");
    }
    public static AppConfig Load(string path) {
        if(!File.Exists(path)) return Default();
        var serializer=new JavaScriptSerializer(); string json=File.ReadAllText(path);
        AppConfig c=serializer.Deserialize<AppConfig>(json);
        if(c==null) throw new InvalidDataException("配置文件为空。");
        var fields=serializer.Deserialize<Dictionary<string,object>>(json);
        if(!fields.ContainsKey("RestoreHotkey")) c.RestoreHotkey=new Hotkey(3,0x79);
        if(!fields.ContainsKey("MainUpdateStyle")) c.MainUpdateStyle=true;
        if(c.PasswordEntries==null) c.PasswordEntries=new List<PasswordEntry>();
        if(c.PasswordEntries.Count==0 && !String.IsNullOrEmpty(c.PasswordHash)) c.PasswordEntries.Add(new PasswordEntry { Salt=c.Salt,Hash=c.PasswordHash,Iterations=c.Iterations });
        c.Salt=""; c.PasswordHash=""; c.Iterations=600000;
        c.Validate(); return c;
    }
    public void Save(string path) {
        Validate(); string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        var preferences=new { Version=Version,MainHotkey=MainHotkey,SecondaryHotkey=SecondaryHotkey,RestoreHotkey=RestoreHotkey,MainUpdateStyle=MainUpdateStyle,PasswordEnabled=PasswordEnabled,PasswordEntries=PasswordEntries,PasswordMode="remembered-with-startup-reset" };
        try {
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(preferences));
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class Passwords {
    internal static PasswordEntry Create(string password) {
        if(password.Length<8 || password.Length>128) throw new ArgumentException("密码长度须为 8 至 128 个字符。");
        byte[] salt=new byte[32]; using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(salt);
        byte[] hash=Derive(password,salt,600000);
        try { return new PasswordEntry { Salt=Convert.ToBase64String(salt),Hash=Convert.ToBase64String(hash),Iterations=600000 }; } finally { Array.Clear(hash,0,hash.Length); }
    }
    public static void Set(AppConfig c,string password) { SetAll(c,new[]{password}); }
    public static void SetAll(AppConfig c,IEnumerable<string> passwords) {
        var values=passwords.ToList(); if(values.Count<1 || values.Count>3) throw new ArgumentException("请设置一至三个有效密码。");
        var records=values.Select(Create).ToList(); c.PasswordEntries=records; c.PasswordEnabled=true; c.Salt=""; c.PasswordHash="";
    }
    private static byte[] Derive(string value,byte[] salt,int iterations) { using(var kdf=new Rfc2898DeriveBytes(value,salt,iterations,HashAlgorithmName.SHA256)) return kdf.GetBytes(32); }
    public static bool Verify(AppConfig c,string password) {
        if(!c.PasswordEnabled) return true;
        return VerifyAny(c,password);
    }
    public static bool CanChange(AppConfig c,bool startupResetAllowed,string password) { return startupResetAllowed || c.PasswordEntries.Count==0 || VerifyAny(c,password); }
    public static bool VerifyAny(AppConfig c,string password) {
        c.Validate(); if(password==null || password.Length>128) return false;
        foreach(var entry in c.PasswordEntries) {
            byte[] expected=Convert.FromBase64String(entry.Hash), actual=Derive(password,Convert.FromBase64String(entry.Salt),entry.Iterations);
            try { int difference=actual.Length^expected.Length; for(int i=0;i<expected.Length;i++) difference|=actual[i]^expected[i]; if(difference==0) return true; }
            finally { Array.Clear(actual,0,actual.Length); Array.Clear(expected,0,expected.Length); }
        }
        return false;
    }
}

internal sealed class Target {
    public string Device { get; set; }
    public bool Primary { get; set; }
    public Rectangle Bounds { get; set; }
    public static List<Target> Current() { return Screen.AllScreens.Select(s=>new Target { Device=s.DeviceName,Primary=s.Primary,Bounds=s.Bounds }).ToList(); }
    public static List<Target> Select(IEnumerable<Target> all,bool primary) { return all.Where(s=>s.Primary==primary).ToList(); }
    public static string Signature(IEnumerable<Target> all) { return String.Join("|",all.OrderBy(s=>s.Device).Select(s=>s.Device+":"+s.Primary+":"+s.Bounds)); }
}

internal sealed class MaskWindow : Form {
    internal Target Target { get; private set; }
    private readonly bool updateStyle;
    private readonly Stopwatch elapsed=new Stopwatch();
    private readonly System.Windows.Forms.Timer animation;
    internal MaskWindow(Target target,bool update=false) {
        Target=target; Text="Screen Privacy Mask"; FormBorderStyle=FormBorderStyle.None; BackColor=Color.Black;
        updateStyle=target.Primary && update;
        StartPosition=FormStartPosition.Manual; Bounds=target.Bounds; TopMost=true; ShowInTaskbar=false; AutoScaleMode=AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true);
        if(updateStyle) { animation=new System.Windows.Forms.Timer { Interval=50 }; animation.Tick+=delegate { Invalidate(); }; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x00080000|0x20|8|0x08000000|0x80; return p; } }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Color.Black);
        if(updateStyle) PaintUpdate(e.Graphics,ClientSize,Math.Min(99,1+(int)(elapsed.Elapsed.TotalSeconds/75)),elapsed.Elapsed.TotalSeconds*2.4);
    }
    internal static void PaintUpdate(Graphics g,Size size,int percent,double angle) {
        g.Clear(Color.FromArgb(8,8,8)); g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float scale=Math.Max(0.75f,Math.Min(1.6f,size.Width/1920f));
        float centerX=size.Width/2f,centerY=size.Height/2f-65*scale,radius=22*scale,dot=3.6f*scale;
        for(int i=0;i<8;i++) { double phase=angle+i*Math.PI/4; using(var b=new SolidBrush(Color.FromArgb(50+i*27,255,255,255))) g.FillEllipse(b,centerX+(float)Math.Cos(phase)*radius-dot,centerY+(float)Math.Sin(phase)*radius-dot,dot*2,dot*2); }
        using(var font=new Font("Microsoft YaHei UI",22*scale,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var format=new StringFormat { Alignment=StringAlignment.Center }) {
            string[] lines={"正在处理更新", "已完成 "+percent+"%", "请保持计算机开启。"};
            for(int i=0;i<lines.Length;i++) g.DrawString(lines[i],font,Brushes.White,new RectangleF(0,size.Height/2f+(i*36)*scale,size.Width,40*scale),format);
        }
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); if(animation!=null) { elapsed.Start(); animation.Start(); } }
    protected override void Dispose(bool disposing) { if(disposing && animation!=null) { animation.Stop(); animation.Dispose(); } base.Dispose(disposing); }
    internal void Prepare() {
        IntPtr h=Handle; bool dwm;
        if(Native.DwmIsCompositionEnabled(out dwm)!=0 || !dwm) throw new InvalidOperationException("Windows 桌面合成不可用，无法启动遮蔽。");
        if(!Native.SetLayeredWindowAttributes(h,0,255,2) || !Native.SetWindowDisplayAffinity(h,0x11)) throw new InvalidOperationException("无法创建截图可穿透的遮蔽窗口。错误："+Marshal.GetLastWin32Error());
        uint affinity; if(!Native.GetWindowDisplayAffinity(h,out affinity)||affinity!=0x11) throw new InvalidOperationException("遮蔽窗口的截图设置校验失败。");
    }
    internal void KeepAbove() { Native.SetWindowPos(Handle,new IntPtr(-1),Target.Bounds.X,Target.Bounds.Y,Target.Bounds.Width,Target.Bounds.Height,0x10); }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x10) { m.Result=IntPtr.Zero; return; }
        if(m.Msg==0x21) { m.Result=new IntPtr(3); return; }
        base.WndProc(ref m);
    }
}

