using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// A short, explicit native registration probe. It creates only HotkeySink's
// message-only handles, never a form or MaskWindow, and never sends input.
internal static class NativeChecks {
    private sealed class BlockedCheck : Exception { internal BlockedCheck(string message):base(message) {} }

    private sealed class CaseResult {
        internal string Id,Status="NOT_RUN",Summary;
        internal int RegistrationAttempts,ProbeAttempts;
        internal readonly List<object> Observations=new List<object>();
        internal readonly List<string> CleanupProblems=new List<string>();
    }

    private sealed class RealBackend : IHotkeyBackend {
        private readonly HotkeySink sink;
        private readonly CaseContext context;
        private readonly string role;
        internal RealBackend(HotkeySink value,CaseContext owner,string name) { sink=value; context=owner; role=name; }
        public int Register(int id,Hotkey key) {
            int error=sink.Register(id,key); context.Result.RegistrationAttempts++;
            context.Result.Observations.Add(new {kind="native-register",owner=role,id=id,key=key.ToString(),errorCode=error});
            if(error==0) context.Remember(key);
            return error;
        }
        public void Unregister(int id) { sink.Unregister(id); }
    }

    private sealed class CaseContext : IDisposable {
        private readonly HotkeySink sink,holderSink;
        internal readonly CaseResult Result;
        internal readonly HotkeyRegistry Registry;
        internal readonly RealBackend Holder;
        internal readonly List<Hotkey> SuccessfullyHeld=new List<Hotkey>();
        internal CaseContext(CaseResult result) {
            Result=result; sink=new HotkeySink();
            try { holderSink=new HotkeySink(); Registry=new HotkeyRegistry(new RealBackend(sink,this,"application")); Holder=new RealBackend(holderSink,this,"controlled-holder"); }
            catch { sink.Dispose(); throw; }
        }
        internal void Remember(Hotkey key) {
            if(!SuccessfullyHeld.Any(item=>item.Same(key))) SuccessfullyHeld.Add(new Hotkey(key.Modifiers,key.Key));
        }
        internal void Defaults(AppConfig config,bool hasSecondary) {
            var state=Registry.Register(config,hasSecondary);
            Snapshot("baseline",state);
            if(!state.AllRequiredReady) throw new BlockedCheck("Baseline combinations are unavailable in this environment. "+state.Problem);
        }
        internal void Hold(Hotkey key) {
            int error=Holder.Register(9,key);
            if(error!=0) throw new BlockedCheck("Controlled holder could not reserve "+key+"; Win32 error "+error+".");
        }
        internal void Snapshot(string stage,RegistrationState state) {
            Result.Observations.Add(new {
                kind="registration-state",stage=stage,
                restoreReady=state.RestoreReady,canStartMain=state.CanStart(true),canStartSecondary=state.CanStart(false),allRequiredReady=state.AllRequiredReady,
                main=new {key=state.Main.Key.ToString(),required=state.Main.Required,registered=state.Main.Registered,errorCode=state.Main.ErrorCode},
                secondary=new {key=state.Secondary.Key.ToString(),required=state.Secondary.Required,registered=state.Secondary.Registered,errorCode=state.Secondary.ErrorCode},
                restore=new {key=state.Restore.Key.ToString(),required=state.Restore.Required,registered=state.Restore.Registered,errorCode=state.Restore.ErrorCode}
            });
        }
        internal void ExpectHeld(Hotkey key) { ExpectProbe(key,1409,"independent-probe-held"); }
        internal void ExpectAvailable(Hotkey key) { ExpectProbe(key,0,"independent-probe-available"); }
        private void ExpectProbe(Hotkey key,int expected,string kind) {
            int actual=Probe(key); Result.ProbeAttempts++;
            Result.Observations.Add(new {kind=kind,key=key.ToString(),expectedErrorCode=expected,errorCode=actual});
            if(actual==5) throw new BlockedCheck("Independent registration probe was denied by the environment.");
            Require(actual==expected,"Independent probe for "+key+" returned "+actual+"; expected "+expected+".");
        }
        internal void ExpectHeldDefaults(AppConfig config,bool hasSecondary) {
            ExpectHeld(config.RestoreHotkey); ExpectHeld(config.MainHotkey); if(hasSecondary) ExpectHeld(config.SecondaryHotkey);
        }
        public void Dispose() {
            // Explicitly release holder id=9 as HotkeySink.Dispose's own loop
            // addresses ids 1-3. Destroying both owned handles is the fallback.
            try { Registry.Unregister(); } catch(Exception ex) { Result.CleanupProblems.Add("Application unregister: "+ex.GetType().Name); }
            try { Holder.Unregister(9); } catch(Exception ex) { Result.CleanupProblems.Add("Holder unregister: "+ex.GetType().Name); }
            try { holderSink.Dispose(); } catch(Exception ex) { Result.CleanupProblems.Add("Holder handle disposal: "+ex.GetType().Name); }
            try { sink.Dispose(); } catch(Exception ex) { Result.CleanupProblems.Add("Application handle disposal: "+ex.GetType().Name); }
        }
    }

    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }

    private static int Probe(Hotkey key) {
        // Register on a distinct native handle: 1409 proves another handle
        // currently holds the same combination. A success is always released.
        using(var observer=new HotkeySink()) {
            try { return observer.Register(90,key); }
            finally { observer.Unregister(90); }
        }
    }

    private static CaseResult RunCase(string id,Action<CaseContext> operation) {
        var result=new CaseResult {Id=id}; CaseContext context=null;
        try {
            context=new CaseContext(result); operation(context);
            result.Status="PASS"; result.Summary="Native registrations and application state met the stated checks.";
        } catch(BlockedCheck ex) { result.Status="BLOCKED"; result.Summary=ex.Message; }
        catch(Win32Exception ex) { result.Status="BLOCKED"; result.Summary="Native handle/API access failed: "+ex.NativeErrorCode+". "+ex.Message; }
        catch(Exception ex) { result.Status="FAIL"; result.Summary=ex.GetType().Name+": "+ex.Message; }
        finally {
            if(context!=null) {
                context.Dispose();
                // Reacquire every combination this case successfully owned,
                // after both owned handles have been destroyed, then release it.
                foreach(var key in context.SuccessfullyHeld) {
                    try {
                        int error=Probe(key); result.ProbeAttempts++;
                        result.Observations.Add(new {kind="post-cleanup-reacquire",key=key.ToString(),errorCode=error});
                        if(error!=0) result.CleanupProblems.Add("Could not reacquire "+key+" after cleanup; error "+error+" (possible external owner or environment restriction).");
                    } catch(Exception ex) { result.CleanupProblems.Add("Post-cleanup probe: "+ex.GetType().Name+": "+ex.Message); }
                }
                if(result.CleanupProblems.Count>0 && result.Status=="PASS") {
                    result.Status="BLOCKED"; result.Summary="Owned handles were released, but clean post-test availability could not be confirmed.";
                }
            }
        }
        return result;
    }

    internal static int Run(string path) {
        var defaults=AppConfig.Default(); defaults.Validate();
        var candidate=defaults.Copy(); candidate.RestoreHotkey=new Hotkey(7,0x75); candidate.Validate();
        Require(!candidate.RestoreHotkey.Same(defaults.MainHotkey) && !candidate.RestoreHotkey.Same(defaults.SecondaryHotkey) && !candidate.RestoreHotkey.Same(defaults.RestoreHotkey),"Probe key must differ from every default combination.");
        var cases=new List<CaseResult>();

        cases.Add(RunCase("NATIVE-01-default-registration",delegate(CaseContext c) {
            c.Defaults(defaults,true);
            Require(c.Registry.State.RestoreReady && c.Registry.State.CanStart(true) && c.Registry.State.CanStart(false),"Default registration state is inconsistent.");
            c.ExpectHeldDefaults(defaults,true);
        }));

        cases.Add(RunCase("NATIVE-02-candidate-conflict-rollback",delegate(CaseContext c) {
            c.Defaults(defaults,true); c.Hold(candidate.RestoreHotkey);
            bool saved=false;
            string issue=c.Registry.Apply(defaults,candidate,true,delegate(AppConfig value) { saved=true; });
            c.Snapshot("candidate-conflict-rolled-back",c.Registry.State);
            Require(issue!=null && !saved,"A conflicting candidate must not invoke save or report success.");
            Require(c.Registry.State.AllRequiredReady && c.Registry.State.Restore.Key.Same(defaults.RestoreHotkey),"Previous key state was not restored after candidate conflict.");
            c.ExpectHeldDefaults(defaults,true); c.ExpectHeld(candidate.RestoreHotkey);
        }));

        cases.Add(RunCase("NATIVE-03-save-failure-rollback",delegate(CaseContext c) {
            c.Defaults(defaults,true); bool saveReached=false; Exception candidateProbeFailure=null;
            string issue=c.Registry.Apply(defaults,candidate,true,delegate(AppConfig value) {
                saveReached=true;
                try { c.ExpectHeld(candidate.RestoreHotkey); } catch(Exception ex) { candidateProbeFailure=ex; }
                throw new IOException("Synthetic save failure; no configuration file is written.");
            });
            c.Snapshot("save-failure-rolled-back",c.Registry.State);
            if(!saveReached) throw new BlockedCheck("Candidate registration did not reach the controlled save failure; candidate key may have an external owner.");
            if(candidateProbeFailure!=null) throw candidateProbeFailure;
            Require(issue!=null && c.Registry.State.AllRequiredReady && c.Registry.State.Restore.Key.Same(defaults.RestoreHotkey),"Save failure did not restore previous registrations.");
            c.ExpectHeldDefaults(defaults,true); c.ExpectAvailable(candidate.RestoreHotkey);
        }));

        cases.Add(RunCase("NATIVE-04-rollback-restore-conflict",delegate(CaseContext c) {
            c.Defaults(defaults,true); bool saveReached=false; int holderError=-1;
            string issue=c.Registry.Apply(defaults,candidate,true,delegate(AppConfig value) {
                saveReached=true; holderError=c.Holder.Register(9,defaults.RestoreHotkey);
                throw new IOException("Synthetic save failure after controlled acquisition of the old restore key.");
            });
            c.Snapshot("old-restore-unavailable-after-rollback",c.Registry.State);
            if(!saveReached) throw new BlockedCheck("Candidate registration could not reach the save stage; the candidate key may have an external owner.");
            if(holderError!=0) throw new BlockedCheck("Could not acquire released old restore key in the save callback; error "+holderError+".");
            Require(issue!=null && !c.Registry.State.RestoreReady && !c.Registry.State.AllRequiredReady,"A real failed restore-key rollback was falsely reported ready.");
            Require(!c.Registry.State.CanStart(true) && !c.Registry.State.CanStart(false),"Failed restore rollback must disable every start action.");
            Require(c.Registry.State.Restore.ErrorCode==1409,"Expected a real Win32 conflict for the old restore key.");
            c.ExpectHeld(defaults.RestoreHotkey); c.ExpectHeld(defaults.MainHotkey); c.ExpectHeld(defaults.SecondaryHotkey); c.ExpectAvailable(candidate.RestoreHotkey);
            c.Holder.Unregister(9); c.ExpectAvailable(defaults.RestoreHotkey);
            Require(!c.Registry.State.RestoreReady && !c.Registry.State.CanStart(true) && !c.Registry.State.CanStart(false),"Releasing the external holder must not fabricate a successful application registration.");
        }));

        cases.Add(RunCase("NATIVE-05-single-screen-idle-secondary-conflict",delegate(CaseContext c) {
            c.Hold(defaults.SecondaryHotkey); c.Defaults(defaults,false);
            var state=c.Registry.State;
            Require(!state.Secondary.Required && !state.Secondary.Registered && state.AllRequiredReady && state.CanStart(true) && !state.CanStart(false),"An occupied unused secondary key affected the single-screen registration model.");
            c.ExpectHeldDefaults(defaults,false); c.ExpectHeld(defaults.SecondaryHotkey);
            c.Holder.Unregister(9); c.ExpectAvailable(defaults.SecondaryHotkey);
        }));

        bool pass=cases.All(item=>item.Status=="PASS");
        string executableHash;
        using(var sha=SHA256.Create()) using(var stream=File.OpenRead(Assembly.GetExecutingAssembly().Location)) executableHash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
        string reportPath=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        File.WriteAllText(reportPath,new JavaScriptSerializer().Serialize(new {
            pass=pass,status=pass?"PASS":cases.Any(item=>item.Status=="FAIL")?"FAIL":"BLOCKED",
            scope="short-controlled-real-win32-hotkey-registration-and-rollback",
            testedAt=DateTimeOffset.Now.ToString("o"),executableSha256=executableHash,
            actualWin32Registrations=true,visibleMasksCreated=0,noKeyboardInput=true,
            visibleFormsCreated=0,userConfigurationReadOrWritten=false,
            registrationsAttempted=cases.Sum(item=>item.RegistrationAttempts),independentProbesAttempted=cases.Sum(item=>item.ProbeAttempts),
            total=cases.Count,passed=cases.Count(item=>item.Status=="PASS"),failed=cases.Count(item=>item.Status=="FAIL"),blocked=cases.Count(item=>item.Status=="BLOCKED"),
            defaultKeys=new {main=defaults.MainHotkey.ToString(),secondary=defaults.SecondaryHotkey.ToString(),restore=defaults.RestoreHotkey.ToString()},
            candidateRestoreKey=candidate.RestoreHotkey.ToString(),
            cases=cases.Select(item=>new {id=item.Id,status=item.Status,summary=item.Summary,registrationAttempts=item.RegistrationAttempts,independentProbeAttempts=item.ProbeAttempts,observations=item.Observations,cleanupProblems=item.CleanupProblems}).ToArray(),
            limitations=new[] {
                "No hotkey was pressed, no WM_HOTKEY delivery or keyboard route was exercised, and no mask or visible form was created.",
                "The single-screen case models required registrations; it does not test a physical single-monitor desktop.",
                "Save callbacks inject failures without writing configuration files; file-system rollback is covered separately.",
                "A baseline/candidate key held by unrelated software or denied API access is reported BLOCKED, never treated as product acceptance."
            }
        }));
        return pass?0:1;
    }
}
