using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Explicit native verification, restricted to windows created for this test.
// This does not call MinimizeOtherApplicationsAsync or read user configuration.
internal static class NativeMinimizeChecks {
    private sealed class Ready {
        public Ready() {}
        public int ProcessId { get; set; }
        public long[] Handles { get; set; }
        public string StopEvent { get; set; }
    }
    private sealed class ProbeForm : Form {
        internal ProbeForm(string suffix,Point location) {
            Text="屏幕遮蔽最小化验证 "+suffix; StartPosition=FormStartPosition.Manual;
            Location=location; ClientSize=new Size(340,130); MinimizeBox=true; MaximizeBox=false;
            ShowInTaskbar=true; FormBorderStyle=FormBorderStyle.FixedSingle;
            Controls.Add(new Label { Dock=DockStyle.Fill,Text="仅用于本次最小化验证。\n此窗口会自动关闭，不包含用户项目或密码。",TextAlign=ContentAlignment.MiddleCenter });
        }
        protected override bool ShowWithoutActivation { get { return true; } }
    }
    private sealed class FixtureContext : ApplicationContext {
        private readonly ProbeForm first,second;
        private readonly EventWaitHandle stop;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Stopwatch age=Stopwatch.StartNew();
        private bool finished;
        internal FixtureContext(string readyPath) {
            var bounds=Screen.PrimaryScreen.WorkingArea;
            first=new ProbeForm("A",new Point(bounds.Left+30,bounds.Top+30));
            second=new ProbeForm("B",new Point(bounds.Left+80,bounds.Top+190));
            string eventName="Local\\ScreenPrivacyMinimizeFixture-"+Guid.NewGuid().ToString("N");
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,eventName);
            timer=new System.Windows.Forms.Timer {Interval=100}; timer.Tick+=delegate { if(stop.WaitOne(0) || age.Elapsed.TotalSeconds>=15) Finish(); };
            try {
                first.Show(); second.Show();
                var ready=new Ready {ProcessId=Process.GetCurrentProcess().Id,Handles=new[]{first.Handle.ToInt64(),second.Handle.ToInt64()},StopEvent=eventName};
                string temp=readyPath+".tmp";
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(ready)); File.Move(temp,readyPath);
                timer.Start();
            } catch { timer.Dispose(); stop.Dispose(); first.Dispose(); second.Dispose(); throw; }
        }
        private void Finish() { if(finished) return; finished=true; ExitThread(); }
        protected override void ExitThreadCore() {
            timer.Stop(); timer.Dispose(); first.Dispose(); second.Dispose(); stop.Dispose(); base.ExitThreadCore();
        }
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);

    internal static int RunFixture(string readyPath) {
        try { using(var fixture=new FixtureContext(Path.GetFullPath(readyPath))) Application.Run(fixture); return 0; }
        catch { return 1; }
    }
    private static bool IsFixtureWindow(long handle,int expectedProcess,bool minimized) {
        IntPtr window=new IntPtr(handle); uint processId;
        return IsWindow(window) && IsWindowVisible(window) && GetWindowThreadProcessId(window,out processId)!=0 && processId==(uint)expectedProcess && IsIconic(window)==minimized;
    }
    private static string QuotedArgument(string value) {
        // A ready-file name always ends in .json, so its closing quote cannot
        // follow a trailing backslash. Reject quotes rather than shell-escape.
        if(value.IndexOf('"')>=0 || value.EndsWith("\\")) throw new ArgumentException("Invalid fixture path.");
        return "\""+value+"\"";
    }
    internal static int Run(string reportPath) {
        string report=Path.GetFullPath(reportPath),parent=Path.GetDirectoryName(report);
        Directory.CreateDirectory(parent);
        string scratch=Path.GetFullPath(Path.Combine(parent,"minimize-fixture-"+Guid.NewGuid().ToString("N")));
        if(!scratch.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture directory.");
        Directory.CreateDirectory(scratch);
        string readyPath=Path.Combine(scratch,"ready.json");
        Process child=null; Ready ready=null; ProbeForm sentinel=null;
        MinimizeResult result=null; string stage="starting",errorType=null;
        bool pass=false,ownWindowRetained=false,cleanupComplete=false,fixtureExited=false;
        try {
            var bounds=Screen.PrimaryScreen.WorkingArea;
            sentinel=new ProbeForm("本程序保留窗口",new Point(bounds.Left+460,bounds.Top+30)); sentinel.Show();
            stage="launching-fixture";
            child=Process.Start(new ProcessStartInfo {
                FileName=Application.ExecutablePath,
                Arguments="--minimize-fixture --report "+QuotedArgument(readyPath),
                UseShellExecute=false,CreateNoWindow=true
            });
            if(child==null) throw new InvalidOperationException("Fixture did not start.");
            stage="waiting-ready";
            var wait=Stopwatch.StartNew();
            while(wait.ElapsedMilliseconds<6000 && !child.HasExited) {
                if(File.Exists(readyPath)) { ready=new JavaScriptSerializer().Deserialize<Ready>(File.ReadAllText(readyPath)); break; }
                Thread.Sleep(25);
            }
            if(ready==null || ready.ProcessId!=child.Id || ready.Handles==null || ready.Handles.Length!=2 || String.IsNullOrEmpty(ready.StopEvent)) throw new InvalidOperationException("Fixture readiness was not confirmed.");
            if(!ready.Handles.All(handle=>IsFixtureWindow(handle,child.Id,false))) throw new InvalidOperationException("Fixture windows were not visible and normal.");
            stage="minimizing-allowlisted-fixture";
            // Including this process proves the production own-process filter
            // still wins over the test allowlist. Only the child may be targeted.
            result=WindowMinimizer.MinimizeForTestingAsync(new[]{child.Id,Process.GetCurrentProcess().Id}).GetAwaiter().GetResult();
            ownWindowRetained=sentinel.Visible && sentinel.WindowState!=FormWindowState.Minimized && !IsIconic(sentinel.Handle);
            pass=result.Requested==2 && result.Minimized==2 && result.Failed==0 && !result.TimedOut && String.IsNullOrEmpty(result.Error) && ownWindowRetained && ready.Handles.All(handle=>IsFixtureWindow(handle,child.Id,true));
            stage=pass?"verified":"native-result-mismatch";
        } catch(Exception ex) { errorType=ex.GetType().Name; pass=false; }
        finally {
            if(ready!=null) {
                try { using(var stop=EventWaitHandle.OpenExisting(ready.StopEvent)) stop.Set(); } catch {}
            }
            if(child!=null) {
                try {
                    // The fixture has a 15-second self-expiry even if this parent
                    // cannot open its stop event. No process is killed.
                    fixtureExited=child.WaitForExit(18000);
                    if(!fixtureExited || child.ExitCode!=0) pass=false;
                } catch { pass=false; }
                child.Dispose();
            }
            if(sentinel!=null) sentinel.Dispose();
            try {
                if(child==null || fixtureExited) {
                    foreach(string path in new[]{readyPath,readyPath+".tmp"}) if(File.Exists(path)) File.Delete(path);
                    Directory.Delete(scratch); cleanupComplete=true;
                }
            } catch { pass=false; }
        }
        string executableHash;
        using(var hash=SHA256.Create()) using(var file=File.OpenRead(Assembly.GetExecutingAssembly().Location)) executableHash=BitConverter.ToString(hash.ComputeHash(file)).Replace("-","");
        File.WriteAllText(report,new JavaScriptSerializer().Serialize(new {
            pass=pass,version=Program.ProductVersion,testedAt=DateTimeOffset.Now.ToString("o"),executableSha256=executableHash,
            scope="native-minimization-of-test-created-fixture-windows-only",stage=stage,errorType=errorType,
            fixtureApplicationWindows=2,ownProcessSentinelRetained=ownWindowRetained,
            requested=result==null?0:result.Requested,minimized=result==null?0:result.Minimized,failed=result==null?0:result.Failed,timedOut=result!=null && result.TimedOut,
            visibleMasksCreated=0,noKeyboardInput=true,userConfigurationReadOrWritten=false,
            unrelatedApplicationsTargeted=false,fixtureExitedNormally=fixtureExited,temporaryInstrumentationCleaned=cleanupComplete,
            limitations=new[]{"This test uses two synthetic WinForms windows and an own-process sentinel, not arbitrary third-party applications.","It does not execute the complete authorization/mask-removal lifecycle or prove that every application accepts minimization.","No user desktop screenshots, window titles, process ids, handles or private paths are included in this report."}
        }));
        return pass?0:1;
    }
}
