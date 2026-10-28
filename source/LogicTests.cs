using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Non-disruptive checks of the production models. No forms, masks, native
// registration calls, user settings or real passwords are created or touched.
internal static class LogicTests {
    private sealed class FakeBackend : IHotkeyBackend {
        internal readonly Dictionary<int,Hotkey> Active=new Dictionary<int,Hotkey>();
        internal readonly List<int> Registered=new List<int>();
        internal readonly List<int> Unregistered=new List<int>();
        internal Func<int,Hotkey,int> Failure;
        public int Register(int id,Hotkey key) {
            Registered.Add(id);
            int error=Failure==null?0:Failure(id,key);
            if(error==0 && Active.Any(p=>p.Key!=id && p.Value.Same(key))) error=1409;
            if(error==0) Active[id]=new Hotkey(key.Modifiers,key.Key);
            return error;
        }
        public void Unregister(int id) { Unregistered.Add(id); Active.Remove(id); }
        internal bool Holds(int id,Hotkey key) { return Active.ContainsKey(id) && Active[id].Same(key); }
    }

    private sealed class Results {
        internal readonly Dictionary<string,bool> Checks=new Dictionary<string,bool>();
        internal readonly Dictionary<string,string> Errors=new Dictionary<string,string>();
        internal void Check(string name,Func<bool> test) {
            try { Checks.Add(name,test()); }
            catch(Exception ex) { Checks[name]=false; Errors[name]=ex.GetType().Name+": "+ex.Message; }
        }
    }

    private static bool Throws(Action action) { try { action(); return false; } catch { return true; } }
    private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
    private static string Record(PasswordEntry value) { return value.Salt+"|"+value.Hash+"|"+value.Iterations; }

    internal static int Run(string reportPath) {
        var results=new Results();
        string fullReport=Path.GetFullPath(reportPath);
        string reportDirectory=Path.GetDirectoryName(fullReport);
        Directory.CreateDirectory(reportDirectory);
        string scratch=Path.GetFullPath(Path.Combine(reportDirectory,"logic-test-"+Guid.NewGuid().ToString("N")));
        string reportPrefix=reportDirectory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!scratch.StartsWith(reportPrefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test directory.");
        Directory.CreateDirectory(scratch);
        string preferences=Path.Combine(scratch,"synthetic-settings.json");
        string blockedDestination=Path.Combine(scratch,"directory-not-config");
        try {
            DefaultsAndConfig(results,preferences,blockedDestination);
            RegistrationAndTransactions(results,preferences);
            AuthenticationAndSession(results);
        } catch(Exception ex) {
            results.Check("test_runner_completed",delegate { return false; });
            results.Errors["test_runner_completed"]=ex.GetType().Name+": "+ex.Message;
        } finally {
            // Only known files immediately beneath this newly created directory
            // are removed; no recursive deletion or user configuration cleanup.
            try {
                foreach(string file in Directory.GetFiles(scratch)) {
                    string resolved=Path.GetFullPath(file);
                    if(!resolved.StartsWith(scratch+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
                    File.Delete(resolved);
                }
                if(Directory.Exists(blockedDestination)) Directory.Delete(blockedDestination);
                Directory.Delete(scratch);
                results.Check("synthetic_test_files_cleaned",delegate { return true; });
            } catch(Exception ex) {
                results.Check("synthetic_test_files_cleaned",delegate { return false; });
                results.Errors["synthetic_test_files_cleaned"]=ex.GetType().Name+": "+ex.Message;
            }
        }
        bool pass=results.Checks.Count>0 && results.Checks.Values.All(value=>value);
        string executable=Assembly.GetExecutingAssembly().Location, executableHash;
        using(var sha=SHA256.Create()) using(var stream=File.OpenRead(executable)) executableHash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
        File.WriteAllText(fullReport,Json(new {
            pass=pass,
            scope="production-model-logic-with-fake-hotkey-backend",
            testedAt=DateTimeOffset.Now.ToString("o"),
            executableSha256=executableHash,
            visibleMasksCreated=0,
            nativeHotkeyRegistrationsAttempted=0,
            guiOrPhysicalAcceptancePerformed=false,
            userSettingsReadOrWritten=false,
            total=results.Checks.Count,
            passed=results.Checks.Values.Count(value=>value),
            failed=results.Checks.Values.Count(value=>!value),
            checks=results.Checks,
            errors=results.Errors,
            limitations=new[] {
                "Fake registration/save-failure injection verifies application state logic, not Win32 behavior.",
                "No real display masks, keyboard input, capture compatibility, GUI lifecycle or physical observation were tested.",
                "Session and cancellation state checks do not replace tests of every actual UI caller."
            }
        }));
        return pass?0:1;
    }

    private static void DefaultsAndConfig(Results r,string path,string blockedDestination) {
        AppConfig config=AppConfig.Default(); config.Validate();
        r.Check("default_restore_is_ctrl_alt_f10",delegate { return config.RestoreHotkey.ToString()=="Ctrl+Alt+F10"; });
        r.Check("default_main_and_secondary_keys",delegate { return config.MainHotkey.ToString()=="Ctrl+Alt+F8" && config.SecondaryHotkey.ToString()=="Ctrl+Alt+F9"; });
        r.Check("password_optional_default_off",delegate { return !config.PasswordEnabled && config.PasswordEntries.Count==0; });
        r.Check("main_update_style_available_by_default",delegate { return config.MainUpdateStyle; });
        r.Check("default_hotkey_objects_not_shared",delegate { var other=AppConfig.Default(); other.RestoreHotkey.Key=0x76; return config.RestoreHotkey.Key==0x79; });

        var one=new[]{new Target {Device="synthetic-primary",Primary=true,Bounds=new Rectangle(0,0,1920,1080)}};
        var two=one.Concat(new[]{new Target {Device="synthetic-secondary",Primary=false,Bounds=new Rectangle(0,1080,1920,600)}}).ToArray();
        var multiple=two.Concat(new[]{new Target {Device="synthetic-left",Primary=false,Bounds=new Rectangle(-1280,0,1280,720)}}).ToArray();
        r.Check("single_monitor_main_supported",delegate { return Target.Select(one,true).Count==1; });
        r.Check("single_monitor_secondary_empty",delegate { return Target.Select(one,false).Count==0; });
        r.Check("dual_monitor_separate_groups",delegate { return Target.Select(two,true).Count==1 && Target.Select(two,false).Count==1; });
        r.Check("all_non_primary_displays_form_secondary_group",delegate { return Target.Select(multiple,false).Count==2; });
        r.Check("topology_signature_independent_of_enumeration_order",delegate { return Target.Signature(multiple)==Target.Signature(multiple.Reverse()); });
        r.Check("topology_signature_detects_bounds_change",delegate { var changed=two.Select(t=>new Target {Device=t.Device,Primary=t.Primary,Bounds=t.Bounds}).ToArray(); changed[1].Bounds=new Rectangle(-1920,0,1920,600); return Target.Signature(changed)!=Target.Signature(two); });

        r.Check("main_secondary_collision_rejected",delegate { var c=config.Copy(); c.SecondaryHotkey=new Hotkey(c.MainHotkey.Modifiers,c.MainHotkey.Key); return Throws(c.Validate); });
        r.Check("main_restore_collision_rejected",delegate { var c=config.Copy(); c.RestoreHotkey=new Hotkey(c.MainHotkey.Modifiers,c.MainHotkey.Key); return Throws(c.Validate); });
        r.Check("secondary_restore_collision_rejected",delegate { var c=config.Copy(); c.RestoreHotkey=new Hotkey(c.SecondaryHotkey.Modifiers,c.SecondaryHotkey.Key); return Throws(c.Validate); });
        r.Check("f12_rejected_for_all_actions",delegate { return new[]{1,2,3}.All(id=>{ var c=config.Copy(); if(id==1) c.MainHotkey.Key=0x7B; else if(id==2) c.SecondaryHotkey.Key=0x7B; else c.RestoreHotkey.Key=0x7B; return Throws(c.Validate); }); });
        r.Check("missing_ctrl_or_alt_rejected",delegate { var c=config.Copy(); c.RestoreHotkey.Modifiers=4; return Throws(c.Validate); });
        r.Check("unsupported_modifier_rejected",delegate { var c=config.Copy(); c.RestoreHotkey.Modifiers=10; return Throws(c.Validate); });
        r.Check("f10_released_when_restore_changes",delegate { var c=config.Copy(); c.RestoreHotkey=new Hotkey(3,0x76); c.MainHotkey=new Hotkey(3,0x79); c.Validate(); return true; });
        r.Check("hotkey_copy_does_not_mutate_original",delegate { var c=config.Copy(); c.RestoreHotkey.Key=0x76; return config.RestoreHotkey.Key==0x79; });

        const string first="Synthetic-first-123!",second="Synthetic-second-456!",third="Synthetic-third-789!";
        var protectedConfig=config.Copy(); Passwords.Set(protectedConfig,first);
        r.Check("password_correct_accepted",delegate { return Passwords.Verify(protectedConfig,first); });
        r.Check("password_wrong_rejected",delegate { return !Passwords.Verify(protectedConfig,"Wrong-synthetic-value"); });
        r.Check("empty_password_rejected",delegate { return !Passwords.Verify(protectedConfig,""); });
        r.Check("password_not_trimmed",delegate { return !Passwords.Verify(protectedConfig,first+" "); });
        r.Check("null_and_overlong_passwords_rejected",delegate { return !Passwords.Verify(protectedConfig,null) && !Passwords.Verify(protectedConfig,new string('x',129)); });
        r.Check("password_creation_length_bounds",delegate { return Throws(delegate { Passwords.Create("short"); }) && Throws(delegate { Passwords.Create(new string('x',129)); }); });
        r.Check("random_salt",delegate { var another=Passwords.Create(first); return another.Salt!=protectedConfig.PasswordEntries[0].Salt; });
        r.Check("password_work_factor_retained",delegate { return protectedConfig.PasswordEntries[0].Iterations==600000; });
        r.Check("plaintext_not_serialized",delegate { return !Json(protectedConfig).Contains(first); });
        r.Check("corrupt_password_config_rejected",delegate { var c=protectedConfig.Copy(); c.PasswordEntries[0].Hash="corrupt"; return Throws(c.Validate); });
        r.Check("unsafe_password_work_factor_rejected",delegate { var c=protectedConfig.Copy(); c.PasswordEntries[0].Iterations=1; return Throws(c.Validate); });
        r.Check("enabled_with_zero_passwords_rejected",delegate { var c=config.Copy(); c.PasswordEnabled=true; return Throws(c.Validate); });
        r.Check("four_passwords_rejected_before_creation",delegate { var c=config.Copy(); return Throws(delegate { Passwords.SetAll(c,new[]{first,second,third,"Synthetic-fourth-123!"}); }) && c.PasswordEntries.Count==0; });

        var three=config.Copy(); Passwords.SetAll(three,new[]{first,second,third});
        r.Check("three_passwords_each_accepted",delegate { return Passwords.Verify(three,first) && Passwords.Verify(three,second) && Passwords.Verify(three,third); });
        r.Check("three_passwords_reject_unknown",delegate { return !Passwords.Verify(three,"Unknown-synthetic-value"); });
        r.Check("password_list_copy_is_independent",delegate { var copy=three.Copy(); copy.PasswordEntries[0].Hash=Convert.ToBase64String(new byte[32]); copy.PasswordEntries.RemoveAt(1); return three.PasswordEntries.Count==3 && copy.PasswordEntries[0].Hash!=three.PasswordEntries[0].Hash; });
        r.Check("delete_preserves_remaining_password_records",delegate { var copy=three.Copy(); string expected=Record(copy.PasswordEntries[1])+Record(copy.PasswordEntries[2]); copy.PasswordEntries.RemoveAt(0); return copy.PasswordEntries.Count==2 && expected==Record(copy.PasswordEntries[0])+Record(copy.PasswordEntries[1]); });
        r.Check("fresh_launch_can_reset_without_old_password",delegate { return Passwords.CanChange(three,true,""); });
        r.Check("used_session_requires_existing_password_to_change",delegate { return !Passwords.CanChange(three,false,"") && Passwords.CanChange(three,false,first); });
        r.Check("no_saved_password_can_be_added_after_session_use",delegate { return Passwords.CanChange(config,false,""); });
        r.Check("disabled_password_records_still_require_authorization_to_change",delegate { var c=three.Copy(); c.PasswordEnabled=false; return !Passwords.CanChange(c,false,"") && Passwords.CanChange(c,false,first); });

        three.RestoreHotkey=new Hotkey(3,0x76); three.MainUpdateStyle=false; three.Save(path);
        string saved=File.ReadAllText(path);
        var loaded=AppConfig.Load(path);
        r.Check("custom_restore_key_persists",delegate { return loaded.RestoreHotkey.Same(three.RestoreHotkey); });
        r.Check("saved_passwords_remembered_after_restart",delegate { return loaded.PasswordEnabled && loaded.PasswordEntries.Count==3 && Passwords.Verify(loaded,third); });
        r.Check("saved_records_unchanged_after_roundtrip",delegate { return loaded.PasswordEntries.Select(Record).SequenceEqual(three.PasswordEntries.Select(Record)); });
        r.Check("saved_preferences_preserved",delegate { return !loaded.MainUpdateStyle && loaded.MainHotkey.Same(three.MainHotkey) && loaded.SecondaryHotkey.Same(three.SecondaryHotkey); });
        r.Check("save_writes_only_derived_password_fields",delegate { var fields=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(saved); return fields.ContainsKey("PasswordEntries") && fields.ContainsKey("RestoreHotkey") && !fields.ContainsKey("PasswordHash") && !saved.Contains(first) && !saved.Contains(second) && !saved.Contains(third); });

        var oldThree=new {Version=2,MainHotkey=new Hotkey(3,0x75),SecondaryHotkey=new Hotkey(3,0x76),PasswordEnabled=true,MainUpdateStyle=false,PasswordEntries=three.PasswordEntries};
        File.WriteAllText(path,Json(oldThree));
        var migrated=AppConfig.Load(path); migrated.Save(path); var remigrated=AppConfig.Load(path);
        r.Check("missing_restore_key_migrates_to_default",delegate { return migrated.RestoreHotkey.ToString()=="Ctrl+Alt+F10" && remigrated.RestoreHotkey.Same(migrated.RestoreHotkey); });
        r.Check("migration_preserves_other_keys_and_style",delegate { return migrated.MainHotkey.Same(oldThree.MainHotkey) && migrated.SecondaryHotkey.Same(oldThree.SecondaryHotkey) && !migrated.MainUpdateStyle; });
        r.Check("migration_preserves_three_derived_records_exactly",delegate { return migrated.PasswordEnabled && remigrated.PasswordEntries.Select(Record).SequenceEqual(three.PasswordEntries.Select(Record)); });

        File.WriteAllText(path,Json(new {Version=2,MainHotkey=config.MainHotkey,SecondaryHotkey=config.SecondaryHotkey,PasswordEnabled=true,Salt=protectedConfig.PasswordEntries[0].Salt,PasswordHash=protectedConfig.PasswordEntries[0].Hash,Iterations=protectedConfig.PasswordEntries[0].Iterations}));
        var single=AppConfig.Load(path); single.Save(path);
        r.Check("legacy_single_password_preserved",delegate { return single.PasswordEntries.Count==1 && Record(single.PasswordEntries[0])==Record(protectedConfig.PasswordEntries[0]) && Passwords.Verify(single,first); });
        r.Check("legacy_single_hash_field_removed_only_after_migration",delegate { return !File.ReadAllText(path).Contains("PasswordHash"); });
        r.Check("legacy_missing_animation_uses_default",delegate { return single.MainUpdateStyle; });

        foreach(string kind in new[]{"null","illegal","duplicate"}) {
            var invalid=config.Copy();
            invalid.RestoreHotkey=kind=="null"?null:kind=="illegal"?new Hotkey(0,0x76):new Hotkey(config.MainHotkey.Modifiers,config.MainHotkey.Key);
            string invalidJson=Json(invalid); File.WriteAllText(path,invalidJson);
            string testKind=kind;
            r.Check("explicit_"+testKind+"_restore_field_rejected",delegate { return Throws(delegate { AppConfig.Load(path); }) && File.ReadAllText(path)==invalidJson; });
        }
        config.Save(path); var replacementConfig=config.Copy(); replacementConfig.RestoreHotkey=new Hotkey(3,0x76); replacementConfig.Save(path);
        r.Check("atomic_replace_saves_new_config",delegate { return AppConfig.Load(path).RestoreHotkey.Same(replacementConfig.RestoreHotkey); });
        r.Check("successful_save_leaves_no_temporary_files",delegate { return Directory.GetFiles(Path.GetDirectoryName(path),"*.tmp").Length==0; });
        Directory.CreateDirectory(blockedDestination);
        r.Check("failed_save_cleans_temporary_file",delegate { return Throws(delegate { replacementConfig.Save(blockedDestination); }) && Directory.Exists(blockedDestination) && Directory.GetFiles(Path.GetDirectoryName(path),"*.tmp").Length==0; });
    }

    private static void RegistrationAndTransactions(Results r,string path) {
        var config=AppConfig.Default();
        var fake=new FakeBackend(); var registry=new HotkeyRegistry(fake); var state=registry.Register(config,true);
        r.Check("registration_attempts_restore_first",delegate { return fake.Registered.SequenceEqual(new[]{3,1,2}); });
        r.Check("all_required_keys_ready_on_success",delegate { return state.AllRequiredReady && state.RestoreReady && state.CanStart(true) && state.CanStart(false) && state.Problem==null; });

        fake=new FakeBackend {Failure=delegate(int id,Hotkey key) { return id==2?1409:0; }}; registry=new HotkeyRegistry(fake); state=registry.Register(config,false);
        r.Check("single_screen_does_not_register_secondary_key",delegate { return !fake.Registered.Contains(2) && !state.Secondary.Required && !state.Secondary.Registered; });
        r.Check("single_screen_idle_key_conflict_does_not_block_main",delegate { return state.AllRequiredReady && state.RestoreReady && state.CanStart(true) && !state.CanStart(false); });

        fake=new FakeBackend {Failure=delegate(int id,Hotkey key) { return id==1?1409:0; }}; registry=new HotkeyRegistry(fake); state=registry.Register(config,true);
        r.Check("main_key_conflict_preserves_other_keys",delegate { return !state.AllRequiredReady && !state.CanStart(true) && state.CanStart(false) && state.RestoreReady && fake.Holds(3,config.RestoreHotkey); });
        r.Check("conflict_error_names_actual_key",delegate { return state.Problem.Contains(config.MainHotkey.ToString()) && state.Problem.Contains("1409"); });
        fake=new FakeBackend {Failure=delegate(int id,Hotkey key) { return id==3?5:0; }}; registry=new HotkeyRegistry(fake); state=registry.Register(config,true);
        r.Check("unavailable_restore_blocks_every_start_action",delegate { return !state.RestoreReady && !state.CanStart(true) && !state.CanStart(false); });
        r.Check("restore_access_denied_recorded",delegate { return state.Restore.ErrorCode==5 && state.Problem.Contains(config.RestoreHotkey.ToString()); });

        fake=new FakeBackend(); registry=new HotkeyRegistry(fake); registry.Register(config,false); fake.Registered.Clear(); fake.Unregistered.Clear();
        registry.UpdateSecondary(config,true);
        r.Check("adding_secondary_does_not_unregister_restore",delegate { return registry.State.CanStart(false) && !fake.Unregistered.Contains(3) && !fake.Registered.Contains(3) && fake.Holds(3,config.RestoreHotkey); });
        fake.Registered.Clear(); fake.Unregistered.Clear(); registry.UpdateSecondary(config,false);
        r.Check("removing_secondary_preserves_main_and_restore",delegate { return registry.State.CanStart(true) && registry.State.RestoreReady && !registry.State.Secondary.Required && !fake.Unregistered.Contains(3) && !fake.Unregistered.Contains(1); });

        var candidate=config.Copy(); candidate.RestoreHotkey=new Hotkey(3,0x76);
        fake=new FakeBackend(); registry=new HotkeyRegistry(fake); registry.Register(config,true);
        config.Save(path); string oldFile=File.ReadAllText(path),oldMemory=Json(config); bool saved=false;
        string issue=registry.Apply(config,candidate,true,delegate(AppConfig value) { saved=true; value.Save(path); });
        r.Check("successful_transaction_registers_then_saves",delegate { return issue==null && saved && registry.State.RestoreReady && fake.Holds(3,candidate.RestoreHotkey) && AppConfig.Load(path).RestoreHotkey.Same(candidate.RestoreHotkey); });
        r.Check("transaction_does_not_mutate_callers_previous_config",delegate { return Json(config)==oldMemory; });

        fake=new FakeBackend(); registry=new HotkeyRegistry(fake); registry.Register(config,true); config.Save(path); oldFile=File.ReadAllText(path); saved=false;
        fake.Failure=delegate(int id,Hotkey key) { return key.Same(candidate.RestoreHotkey)?1409:0; };
        issue=registry.Apply(config,candidate,true,delegate(AppConfig value) { saved=true; value.Save(path); });
        r.Check("candidate_registration_failure_does_not_save",delegate { return issue!=null && !saved && File.ReadAllText(path)==oldFile; });
        r.Check("candidate_registration_failure_restores_previous_keys",delegate { return registry.State.AllRequiredReady && fake.Holds(1,config.MainHotkey) && fake.Holds(2,config.SecondaryHotkey) && fake.Holds(3,config.RestoreHotkey); });
        r.Check("candidate_registration_error_reports_actual_previous_restore",delegate { return issue.Contains(candidate.RestoreHotkey.ToString()) && issue.Contains(config.RestoreHotkey.ToString()); });

        fake=new FakeBackend(); registry=new HotkeyRegistry(fake); registry.Register(config,true); config.Save(path); oldFile=File.ReadAllText(path);
        issue=registry.Apply(config,candidate,true,delegate(AppConfig value) { throw new IOException("Synthetic configuration write failure"); });
        r.Check("save_failure_preserves_original_file",delegate { return issue!=null && File.ReadAllText(path)==oldFile; });
        r.Check("save_failure_revokes_candidate_and_restores_previous_keys",delegate { return registry.State.AllRequiredReady && fake.Holds(3,config.RestoreHotkey) && !fake.Active.Values.Any(key=>key.Same(candidate.RestoreHotkey)); });

        bool rollingBack=false; fake=new FakeBackend {Failure=delegate(int id,Hotkey key) { return rollingBack && id==3 && key.Same(config.RestoreHotkey)?1409:0; }};
        registry=new HotkeyRegistry(fake); registry.Register(config,true);
        issue=registry.Apply(config,candidate,true,delegate(AppConfig value) { rollingBack=true; throw new IOException("Synthetic configuration write failure"); });
        r.Check("rollback_restore_failure_not_reported_ready",delegate { return issue!=null && !registry.State.RestoreReady && !registry.State.AllRequiredReady && !registry.State.CanStart(true) && !registry.State.CanStart(false); });
        r.Check("rollback_failure_identifies_unavailable_restore",delegate { return issue.Contains(config.RestoreHotkey.ToString()) && issue.Contains("不可用"); });

        fake=new FakeBackend(); registry=new HotkeyRegistry(fake); registry.Register(config,true); int before=fake.Registered.Count; var invalid=config.Copy(); invalid.RestoreHotkey=null;
        r.Check("invalid_candidate_does_not_modify_registration",delegate { return Throws(delegate { registry.Apply(config,invalid,true,delegate(AppConfig value) {}); }) && fake.Registered.Count==before && registry.State.AllRequiredReady; });

        fake=new FakeBackend {Failure=delegate(int id,Hotkey key) { return id==2?1409:0; }}; registry=new HotkeyRegistry(fake); registry.Register(config,false); saved=false;
        issue=registry.Apply(config,candidate,false,delegate(AppConfig value) { saved=true; });
        r.Check("single_screen_transaction_ignores_unused_secondary_conflict",delegate { return issue==null && saved && registry.State.AllRequiredReady && !fake.Registered.Contains(2); });
    }

    private static void AuthenticationAndSession(Results r) {
        var attempt=new AsyncAttempt(); int ticket,duplicate;
        bool began=attempt.TryBegin(out ticket),again=attempt.TryBegin(out duplicate);
        r.Check("authentication_starts_one_attempt",delegate { return began && attempt.Busy && !again; });
        r.Check("stale_authentication_result_is_ignored",delegate { return !attempt.Accept(ticket-1,true) && attempt.Busy && !attempt.Authorized; });
        r.Check("wrong_password_completes_without_authorizing",delegate { return attempt.Accept(ticket,false) && !attempt.Busy && !attempt.Authorized && !attempt.Cancelled; });
        int second; bool retried=attempt.TryBegin(out second);
        r.Check("failed_authentication_allows_new_submit",delegate { return retried && second>ticket && attempt.Busy; });
        r.Check("previous_submission_cannot_authorize_new_attempt",delegate { return !attempt.Accept(ticket,true) && attempt.Busy && !attempt.Authorized; });
        attempt.Cancel();
        r.Check("cancel_clears_busy_and_authorized_state",delegate { return attempt.Cancelled && !attempt.Busy && !attempt.Authorized; });
        r.Check("correct_result_arriving_after_cancel_is_ignored",delegate { return !attempt.Accept(second,true) && !attempt.Authorized; });
        r.Check("cancelled_dialog_cannot_restart_its_attempt",delegate { int next; return !attempt.TryBegin(out next); });

        var successful=new AsyncAttempt(); int successTicket; successful.TryBegin(out successTicket);
        r.Check("correct_current_result_authorizes_once",delegate { return successful.Accept(successTicket,true) && successful.Authorized && !successful.Busy; });
        r.Check("duplicate_correct_result_does_not_complete_again",delegate { return !successful.Accept(successTicket,true) && successful.Authorized; });
        r.Check("authorized_attempt_rejects_repeat_submission",delegate { int next; return !successful.TryBegin(out next); });

        var session=new SessionState();
        r.Check("new_process_opens_password_reset_window",delegate { return session.PasswordResetAllowed; });
        r.Check("panel_allowed_with_available_display_and_not_exiting",delegate { return session.CanShowPanel(1,false) && session.CanShowPanel(3,false) && !session.CanShowPanel(0,false) && !session.CanShowPanel(1,true); });
        r.Check("panel_queries_do_not_close_reset_window",delegate { session.CanShowPanel(1,false); session.CanShowPanel(0,false); return session.PasswordResetAllowed; });
        session.MaskCommitted();
        r.Check("successful_mask_commit_closes_reset_window",delegate { return !session.PasswordResetAllowed; });
        r.Check("restored_desktop_does_not_reopen_reset_window",delegate { return session.CanShowPanel(1,false) && !session.PasswordResetAllowed; });
        session.MaskCommitted();
        r.Check("later_mask_commits_keep_reset_window_closed",delegate { return !session.PasswordResetAllowed; });
        r.Check("new_session_gets_independent_reset_window",delegate { return new SessionState().PasswordResetAllowed && !session.PasswordResetAllowed; });
    }
}
