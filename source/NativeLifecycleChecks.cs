using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class LifecycleSnapshot {
    internal int MaskCount,ProcessId;
    internal bool MainCovered,SecondaryCovered,PanelVisible,PanelMinimized,PanelDoesNotIntersectMasks,Awake,Restoring,Exiting,HotkeysReady,PasswordResetAllowed;
    internal Rectangle PanelBounds;
}

// Explicit opt-in interactive verification. Unlike --check, this command shows
// actual masks for short intervals. It uses only a synthetic configuration and
// scopes native minimization to its own separate fixture process.
internal static class NativeLifecycleChecks {
    private static void Check(Dictionary<string,bool> checks,string name,bool result) { checks[name]=result; if(!result) throw new InvalidOperationException(name); }
    private sealed class FixtureReady {
        public int ProcessId { get; set; }
        public long[] Handles { get; set; }
        public string StopEvent { get; set; }
    }
    internal static int Run(string reportPath,bool slowObservation=false) {
        string report=Path.GetFullPath(reportPath),directory=Path.GetDirectoryName(report);
        Directory.CreateDirectory(directory);
        string scratch=Path.Combine(directory,"lifecycle-fixture-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var checks=new Dictionary<string,bool>(); var config=AppConfig.Default(); config.MainUpdateStyle=false;
        string configPath=Path.Combine(scratch,"settings.json"); config.Save(configPath);
        var screens=Target.Current(); int expected=screens.Count,secondary=screens.Count(s=>!s.Primary);
        Process fixture=null; FixtureReady ready=null; MinimizeResult minimized=null;
        int minimizerCalls=0,initialProcess=Process.GetCurrentProcess().Id,step=0;
        string errorType=null; bool allStagesCompleted=false,fixtureStopped=false;
        var age=Stopwatch.StartNew(); PrivacyContext controller=null;
        Func<Task<MinimizeResult>> minimizer=async delegate {
            minimizerCalls++; await Task.Delay(slowObservation?1500:200);
            if(ready==null) throw new InvalidOperationException("Fixture not ready.");
            minimized=await WindowMinimizer.MinimizeForTestingAsync(new[]{ready.ProcessId,initialProcess}); return minimized;
        };
        var timer=new System.Windows.Forms.Timer {Interval=slowObservation?2000:450};
        Rectangle initialPanel=Rectangle.Empty;
        try {
            controller=new PrivacyContext(config,configPath,minimizer);
            timer.Tick+=delegate {
                try {
                    var state=controller.InspectLifecycle();
                    if(state.ProcessId!=initialProcess) throw new InvalidOperationException("Unexpected controller process.");
                    if(age.Elapsed.TotalSeconds>(slowObservation?35:20)) throw new TimeoutException();
                    if(step<0) { if(!state.Restoring) { config.MinimizeOnRestore=false; controller.RestoreRequested(); controller.ExitRequested(); } return; }
                    switch(step) {
                    case 0:
                        Check(checks,"startup_zero_masks_and_panel_visible",state.MaskCount==0 && state.PanelVisible && state.HotkeysReady && !state.Awake);
                        if(!checks["startup_zero_masks_and_panel_visible"]) throw new InvalidOperationException("Startup not ready.");
                        initialPanel=state.PanelBounds; controller.Toggle(secondary>0?false:true); step++; break;
                    case 1:
                        Check(checks,"partial_cover_keeps_same_process_and_awake",state.MaskCount==(secondary>0?secondary:1) && state.Awake);
                        Check(checks,"panel_on_uncovered_display_or_hidden_when_all_covered",secondary>0?(state.PanelVisible && state.PanelDoesNotIntersectMasks):!state.PanelVisible);
                        controller.CoverAll(); step++; break;
                    case 2:
                        Check(checks,"one_click_all_covers_every_display",state.MaskCount==expected && !state.PanelVisible && state.Awake);
                        controller.CoverAll();
                        Check(checks,"cover_all_is_not_a_restore_toggle",controller.InspectLifecycle().MaskCount==expected);
                        controller.RestoreRequested(); step++; break;
                    case 3:
                        Check(checks,"restore_all_keeps_process_and_returns_panel",state.MaskCount==0 && state.PanelVisible && !state.PanelMinimized && !state.Awake && !state.Exiting);
                        Check(checks,"option_off_did_not_call_minimizer",minimizerCalls==0);
                        Check(checks,"panel_returns_to_original_display",screens.Any(s=>s.Bounds.IntersectsWith(initialPanel) && s.Bounds.Contains(state.PanelBounds)));
                        controller.Toggle(true); step++; break;
                    case 4:
                        Check(checks,"main_cover_target_present",state.MainCovered && state.MaskCount==1);
                        Check(checks,"main_cover_panel_fits_remaining_display",secondary>0?(state.PanelVisible && state.PanelDoesNotIntersectMasks):!state.PanelVisible);
                        controller.RestoreRequested(); step++; break;
                    case 5:
                        Check(checks,"second_restore_returns_same_live_panel",state.MaskCount==0 && state.PanelVisible && !state.Awake);
                        string readyPath=Path.Combine(scratch,"ready.json");
                        fixture=Process.Start(new ProcessStartInfo {FileName=Application.ExecutablePath,Arguments="--minimize-fixture --report \""+readyPath+"\"",UseShellExecute=false,CreateNoWindow=true});
                        step++; break;
                    case 6:
                        string path=Path.Combine(scratch,"ready.json");
                        if(!File.Exists(path)) break;
                        ready=new JavaScriptSerializer().Deserialize<FixtureReady>(File.ReadAllText(path));
                        if(fixture==null || ready==null || ready.ProcessId!=fixture.Id) throw new InvalidOperationException("Unexpected fixture.");
                        config.MinimizeOnRestore=true; controller.CoverAll(); step++; break;
                    case 7:
                        Check(checks,"all_masks_present_before_minimize_flow",state.MaskCount==expected);
                        if(secondary>0) {
                            controller.Toggle(true); var partial=controller.InspectLifecycle();
                            Check(checks,"partial_restore_does_not_minimize",partial.MaskCount==secondary && partial.PanelVisible && partial.PanelDoesNotIntersectMasks && minimizerCalls==0);
                            controller.CoverAll();
                        }
                        controller.RestoreRequested();
                        Check(checks,"masks_remain_during_minimization",controller.InspectLifecycle().MaskCount==expected && controller.InspectLifecycle().Restoring);
                        controller.RestoreRequested(); controller.CoverAll(); step++; break;
                    case 8:
                        if(state.Restoring) break;
                        Check(checks,"option_on_called_minimizer_once",minimizerCalls==1);
                        Check(checks,"two_test_windows_minimized",minimized!=null && minimized.Requested==2 && minimized.Minimized==2 && minimized.Failed==0 && !minimized.TimedOut);
                        Check(checks,"own_panel_minimized_and_process_alive_after_restore",state.MaskCount==0 && state.PanelVisible && state.PanelMinimized && !state.Awake && !state.Exiting);
                        Check(checks,"startup_password_reset_did_not_reopen",!state.PasswordResetAllowed);
                        controller.RestoreRequested(); Check(checks,"empty_restore_does_not_minimize_again",minimizerCalls==1);
                        allStagesCompleted=true; step++; controller.ExitRequested(); break;
                    }
                } catch(Exception ex) {
                    if(errorType==null) errorType=ex.GetType().Name;
                    checks["all_interactive_stages_completed"]=false; step=-1;
                    if(controller!=null && !controller.InspectLifecycle().Restoring) { config.MinimizeOnRestore=false; controller.RestoreRequested(); controller.ExitRequested(); }
                }
            };
            timer.Start(); Application.Run(controller);
            checks["all_interactive_stages_completed"]=allStagesCompleted;
            checks["normal_exit_after_restoration"]=controller.InspectLifecycle().Exiting;
        } catch(Exception ex) { errorType=ex.GetType().Name; checks["runner_completed"]=false; }
        finally {
            timer.Stop(); timer.Dispose();
            if(ready!=null) { try { using(var stop=EventWaitHandle.OpenExisting(ready.StopEvent)) stop.Set(); } catch {} }
            if(fixture!=null) { try { fixtureStopped=fixture.WaitForExit(16000); } catch {} fixture.Dispose(); }
            else fixtureStopped=true;
            if(controller!=null) controller.Dispose();
        }
        checks["fixture_stopped_normally"]=fixtureStopped;
        string executableHash;
        using(var h=SHA256.Create()) using(var f=File.OpenRead(Assembly.GetExecutingAssembly().Location)) executableHash=BitConverter.ToString(h.ComputeHash(f)).Replace("-","");
        bool pass=checks.Count>0 && checks.Values.All(x=>x);
        File.WriteAllText(report,new JavaScriptSerializer().Serialize(new {
            pass=pass,version=Program.ProductVersion,testedAt=DateTimeOffset.Now.ToString("o"),executableSha256=executableHash,
            scope="actual-controller-visible-mask-and-panel-lifecycle-with-synthetic-config",total=checks.Count,passed=checks.Values.Count(x=>x),checks=checks,errorType=errorType,
            actualDisplaysMasked=expected,maskOperationsExecuted=allStagesCompleted,physicalObservation="Requires separate human observation; not inferred from capture.",
            observationMode=slowObservation?"two-second-stage-holds":"fast-regression",
            minimizedOnlySyntheticFixtureWindows=true,userConfigurationReadOrWritten=false,realPasswordsUsed=false,
            limitations=new[]{"No real password input, WM_HOTKEY key delivery or new underlying-app screenshot/readback was executed by this runner.","Minimization is restricted to two fixture windows; arbitrary user windows are not targeted.","Display topology changes, timeout injection and another Windows device are not exercised."}
        }));
        return pass?0:1;
    }
}
