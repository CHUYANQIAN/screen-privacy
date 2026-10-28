using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class MinimizeResult {
    internal int Requested,Minimized,Failed;
    internal bool TimedOut;
    internal string Error;
    internal string Summary {
        get {
            if(!String.IsNullOrEmpty(Error)) return Error;
            if(Requested==0) return "没有需要最小化的其他应用窗口。";
            if(Failed==0) return "已最小化 "+Minimized+" 个其他应用窗口。";
            return "已最小化 "+Minimized+" / "+Requested+" 个其他应用窗口；"+Failed+" 个未能确认最小化。";
        }
    }
}

// This service never closes windows, sends keyboard input, changes settings or
// minimizes a window owned by this process. ShowWindowAsync only starts a
// request; IsIconic is polled for a bounded time before reporting completion.
internal static class WindowMinimizer {
    private const long MinimizeBox=0x00020000L,ChildStyle=0x40000000L;
    private const long ToolWindow=0x00000080L,NoActivate=0x08000000L;
    private const int ShowMinNoActive=7,PollTimeoutMilliseconds=1500;
    private sealed class Candidate { internal IntPtr Handle; internal uint ProcessId; }
    private delegate bool EnumCallback(IntPtr window,IntPtr argument);

    [DllImport("user32.dll",SetLastError=true)] private static extern bool EnumWindows(EnumCallback callback,IntPtr argument);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window,int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);

    internal static bool ShouldMinimize(bool visible,bool iconic,bool hasMinimizeBox,bool tool,bool owned,bool shell,bool sameProcess) {
        return visible && !iconic && hasMinimizeBox && !tool && !owned && !shell && !sameProcess;
    }
    internal static Task<MinimizeResult> MinimizeOtherApplicationsAsync() {
        return Task.Run(delegate { return Minimize(null); });
    }
    internal static Task<MinimizeResult> MinimizeForTestingAsync(int[] allowedProcessIds) {
        if(allowedProcessIds==null) throw new ArgumentNullException("allowedProcessIds");
        var allowed=new HashSet<uint>(allowedProcessIds.Where(id=>id>0).Select(id=>(uint)id));
        // A supplied allowlist may narrow the production predicate but never
        // bypass its own-process, shell, tool-window or visibility exclusions.
        return Task.Run(delegate { return Minimize(allowed); });
    }
    private static long Style(IntPtr window,int index) {
        return IntPtr.Size==8?GetWindowLongPtr64(window,index).ToInt64():GetWindowLong32(window,index);
    }
    private static bool IsShell(IntPtr window) {
        if(window==GetShellWindow() || window==GetDesktopWindow()) return true;
        var name=new StringBuilder(128); GetClassName(window,name,name.Capacity);
        string value=name.ToString();
        return value=="Progman" || value=="WorkerW" || value=="Shell_TrayWnd" || value=="Shell_SecondaryTrayWnd";
    }
    private static bool Eligible(IntPtr window,uint ownProcess,HashSet<uint> allowed,out uint processId) {
        processId=0;
        if(!IsWindow(window) || GetWindowThreadProcessId(window,out processId)==0) return false;
        if(allowed!=null && !allowed.Contains(processId)) return false;
        long style=Style(window,-16),extended=Style(window,-20);
        bool visible=IsWindowVisible(window);
        int cloaked;
        if(visible && DwmGetWindowAttribute(window,14,out cloaked,sizeof(int))==0 && cloaked!=0) visible=false;
        return ShouldMinimize(visible,IsIconic(window),(style&MinimizeBox)!=0,
            (extended&(ToolWindow|NoActivate))!=0,GetWindow(window,4)!=IntPtr.Zero,
            (style&ChildStyle)!=0 || IsShell(window),processId==ownProcess);
    }
    private static bool SameLiveWindow(Candidate candidate) {
        uint processId;
        return IsWindow(candidate.Handle) && GetWindowThreadProcessId(candidate.Handle,out processId)!=0 && processId==candidate.ProcessId;
    }
    private static MinimizeResult Minimize(HashSet<uint> allowed) {
        var result=new MinimizeResult();
        try {
            uint ownProcess=(uint)Process.GetCurrentProcess().Id;
            var candidates=new List<Candidate>();
            bool enumerationFailed=false;
            EnumCallback callback=delegate(IntPtr window,IntPtr ignored) {
                try {
                    uint processId;
                    if(Eligible(window,ownProcess,allowed,out processId)) candidates.Add(new Candidate {Handle=window,ProcessId=processId});
                    return true;
                } catch { enumerationFailed=true; return false; }
            };
            if(!EnumWindows(callback,IntPtr.Zero) || enumerationFailed) {
                result.Error="未能完整读取其他应用窗口；已继续恢复屏幕。"; return result;
            }
            var pending=new List<Candidate>();
            foreach(var candidate in candidates) {
                uint currentProcess;
                if(!SameLiveWindow(candidate) || !Eligible(candidate.Handle,ownProcess,allowed,out currentProcess)) continue;
                result.Requested++;
                if(ShowWindowAsync(candidate.Handle,ShowMinNoActive)) pending.Add(candidate);
                else result.Failed++;
            }
            var elapsed=Stopwatch.StartNew();
            while(pending.Count>0 && elapsed.ElapsedMilliseconds<PollTimeoutMilliseconds) {
                foreach(var candidate in pending.ToArray()) {
                    if(!SameLiveWindow(candidate)) { pending.Remove(candidate); result.Failed++; }
                    else if(IsIconic(candidate.Handle)) { pending.Remove(candidate); result.Minimized++; }
                }
                if(pending.Count>0) Thread.Sleep(25);
            }
            // One final observation avoids misreporting completion exactly at
            // the deadline as a timeout. Never wait indefinitely for another UI.
            foreach(var candidate in pending.ToArray()) {
                if(SameLiveWindow(candidate) && IsIconic(candidate.Handle)) { pending.Remove(candidate); result.Minimized++; }
            }
            if(pending.Count>0) { result.TimedOut=true; result.Failed+=pending.Count; }
        } catch(Exception ex) {
            result.Failed=Math.Max(result.Failed,result.Requested-result.Minimized);
            result.Error="部分窗口未能最小化，已继续恢复屏幕（"+ex.GetType().Name+"）。";
        }
        return result;
    }
}
