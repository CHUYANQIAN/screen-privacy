using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

// Appends non-disruptive checks of the actual 0.3.1 policies to the existing
// report. No real windows, masks, native APIs, user preferences or passwords
// are inspected. Controller wiring still requires separate integration tests.
internal static class LifecycleLogicChecks {
    private sealed class Results {
        internal readonly Dictionary<string,bool> Checks;
        internal readonly Dictionary<string,string> Errors;
        internal Results(Dictionary<string,bool> checks,Dictionary<string,string> errors) { Checks=checks; Errors=errors; }
        internal void Check(string name,Func<bool> action) {
            string id="lifecycle_"+name;
            try { Checks[id]=action(); }
            catch(Exception ex) { Checks[id]=false; Errors[id]=ex.GetType().Name; }
        }
    }
    private static string Record(PasswordEntry entry) { return entry.Salt+"|"+entry.Hash+"|"+entry.Iterations; }
    private static PasswordEntry SyntheticRecord(byte value) {
        return new PasswordEntry {
            Salt=Convert.ToBase64String(Enumerable.Repeat(value,32).ToArray()),
            Hash=Convert.ToBase64String(Enumerable.Repeat((byte)(value+20),32).ToArray()),
            Iterations=600000
        };
    }
    private static Target Display(string name,bool primary,int x,int y,int width,int height) {
        return new Target {Device=name,Primary=primary,Bounds=new Rectangle(x,y,width,height)};
    }
    private static RegistrationState Registered(bool main,bool secondary,bool restore,bool hasSecondary=true) {
        var config=AppConfig.Default();
        return new RegistrationState {
            Main=new RegisteredKey {Id=1,Name="main",Key=config.MainHotkey,Required=true,Registered=main},
            Secondary=new RegisteredKey {Id=2,Name="secondary",Key=config.SecondaryHotkey,Required=hasSecondary,Registered=secondary},
            Restore=new RegisteredKey {Id=3,Name="restore",Key=config.RestoreHotkey,Required=true,Registered=restore}
        };
    }
    internal static int Append(string reportPath) {
        var serializer=new JavaScriptSerializer();
        string fullReport=Path.GetFullPath(reportPath);
        var report=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(fullReport));
        var checks=serializer.Deserialize<Dictionary<string,bool>>(serializer.Serialize(report["checks"]));
        var errors=serializer.Deserialize<Dictionary<string,string>>(serializer.Serialize(report["errors"]));
        var results=new Results(checks,errors); int originalCount=checks.Count;
        string parent=Path.GetDirectoryName(fullReport);
        string scratch=Path.GetFullPath(Path.Combine(parent,"lifecycle-test-"+Guid.NewGuid().ToString("N")));
        string parentPrefix=parent.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!scratch.StartsWith(parentPrefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe lifecycle test directory.");
        Directory.CreateDirectory(scratch);
        try {
            Configuration(results,Path.Combine(scratch,"synthetic-preferences.json"),serializer);
            PanelAndCoverage(results);
            RestoreAndMinimize(results);
        } catch(Exception ex) {
            results.Check("runner_completed",delegate { return false; });
            errors["lifecycle_runner_completed"]=ex.GetType().Name;
        } finally {
            try {
                foreach(string file in Directory.GetFiles(scratch)) {
                    string resolved=Path.GetFullPath(file);
                    if(!resolved.StartsWith(scratch+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe lifecycle cleanup path.");
                    File.Delete(resolved);
                }
                Directory.Delete(scratch);
                results.Check("synthetic_files_cleaned",delegate { return true; });
            } catch(Exception ex) {
                results.Check("synthetic_files_cleaned",delegate { return false; });
                errors["lifecycle_synthetic_files_cleaned"]=ex.GetType().Name;
            }
        }
        report["checks"]=checks; report["errors"]=errors;
        report["total"]=checks.Count;
        report["passed"]=checks.Values.Count(value=>value);
        report["failed"]=checks.Values.Count(value=>!value);
        bool pass=checks.Count>0 && checks.Values.All(value=>value); report["pass"]=pass;
        report["lifecycleLogicChecks"]=new {
            added=checks.Count-originalCount,
            scope="real-config-and-pure-mask-ui-minimize-policies",
            visibleMasksCreated=0,nativeWindowActions=0,userConfigurationReadOrWritten=false,
            limits="Policy assertions do not prove controller lifetime, real panel placement, native minimization or keyboard recovery."
        };
        File.WriteAllText(fullReport,serializer.Serialize(report));
        return pass?0:1;
    }
    private static void Configuration(Results r,string path,JavaScriptSerializer serializer) {
        r.Check("minimize_default_is_opt_in",delegate { return !AppConfig.Default().MinimizeOnRestore; });
        var records=new List<PasswordEntry> {SyntheticRecord(1),SyntheticRecord(2),SyntheticRecord(3)};
        var old=new {
            Version=2,MainHotkey=new Hotkey(3,0x75),SecondaryHotkey=new Hotkey(3,0x76),RestoreHotkey=new Hotkey(3,0x79),
            PasswordEnabled=true,MainUpdateStyle=false,PasswordEntries=records
        };
        string oldJson=serializer.Serialize(old); File.WriteAllText(path,oldJson);
        var migrated=AppConfig.Load(path);
        r.Check("missing_minimize_field_defaults_false",delegate { return !migrated.MinimizeOnRestore; });
        r.Check("load_does_not_rewrite_old_preferences",delegate { return File.ReadAllText(path)==oldJson; });
        r.Check("old_configuration_preserves_derived_records",delegate { return migrated.PasswordEnabled && migrated.PasswordEntries.Select(Record).SequenceEqual(records.Select(Record)); });
        r.Check("old_configuration_preserves_keys_and_style",delegate { return migrated.MainHotkey.Same(old.MainHotkey) && migrated.SecondaryHotkey.Same(old.SecondaryHotkey) && migrated.RestoreHotkey.Same(old.RestoreHotkey) && !migrated.MainUpdateStyle; });
        migrated.MinimizeOnRestore=true; migrated.Save(path); var enabled=AppConfig.Load(path);
        r.Check("enabled_minimize_preference_roundtrips",delegate { return enabled.MinimizeOnRestore; });
        r.Check("enabled_preference_keeps_passwords_and_keys",delegate { return enabled.PasswordEnabled && enabled.PasswordEntries.Select(Record).SequenceEqual(records.Select(Record)) && enabled.MainHotkey.Same(old.MainHotkey) && enabled.SecondaryHotkey.Same(old.SecondaryHotkey) && enabled.RestoreHotkey.Same(old.RestoreHotkey) && !enabled.MainUpdateStyle; });
        r.Check("minimize_field_is_explicitly_serialized",delegate { var values=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(path)); return values.ContainsKey("MinimizeOnRestore") && Convert.ToBoolean(values["MinimizeOnRestore"]); });
        var copied=enabled.Copy(); copied.MinimizeOnRestore=false;
        r.Check("minimize_edit_copy_is_independent",delegate { return enabled.MinimizeOnRestore && !copied.MinimizeOnRestore; });
        enabled.MinimizeOnRestore=false; enabled.Save(path); var disabled=AppConfig.Load(path);
        r.Check("disabled_minimize_preference_roundtrips",delegate { return !disabled.MinimizeOnRestore; });
        r.Check("disabling_minimize_keeps_password_records",delegate { return disabled.PasswordEnabled && disabled.PasswordEntries.Select(Record).SequenceEqual(records.Select(Record)); });
    }
    private static void PanelAndCoverage(Results r) {
        var primary=Display("primary",true,0,0,1920,1080);
        var secondary=Display("secondary",false,0,1080,1920,600);
        var left=Display("left",false,-1280,0,1280,720);
        var pair=new[]{primary,secondary}; var all=new[]{primary,secondary,left};
        var none=new Target[0];
        r.Check("no_masks_leave_all_displays_available",delegate { return MaskUiPolicy.Uncovered(all,none).Select(t=>t.Device).SequenceEqual(all.Select(t=>t.Device)); });
        r.Check("secondary_mask_leaves_primary_available",delegate { var free=MaskUiPolicy.Uncovered(pair,new[]{secondary}); return free.Count==1 && free[0].Device==primary.Device; });
        r.Check("primary_mask_can_use_negative_secondary",delegate { var free=MaskUiPolicy.Uncovered(new[]{primary,left},new[]{primary}); return free.Count==1 && free[0].Device==left.Device; });
        r.Check("full_coverage_hides_panel",delegate { return MaskUiPolicy.Uncovered(all,all).Count==0; });
        r.Check("overlapping_screen_is_not_available",delegate { var overlap=Display("overlap",false,1800,100,640,480); return MaskUiPolicy.Uncovered(new[]{primary,overlap},new[]{primary}).Count==0; });
        r.Check("one_pixel_overlap_prevents_safe_display",delegate { var sliver=Display("sliver",false,1919,0,1,1); return MaskUiPolicy.Uncovered(new[]{primary},new[]{sliver}).Count==0; });
        r.Check("touching_edges_do_not_count_as_overlap",delegate { var right=Display("right",false,1920,0,1280,720); var free=MaskUiPolicy.Uncovered(new[]{primary,right},new[]{primary}); return free.Count==1 && free[0].Device==right.Device; });
        r.Check("cover_all_selects_only_missing_displays",delegate { var targets=MaskUiPolicy.TargetsToCover(all,new[]{primary},null); return targets.Select(t=>t.Device).SequenceEqual(new[]{"secondary","left"}); });
        r.Check("cover_all_already_covered_is_no_op",delegate { return MaskUiPolicy.TargetsToCover(all,all,null).Count==0; });
        r.Check("secondary_group_contains_all_non_primary",delegate { return MaskUiPolicy.TargetsToCover(all,none,false).Select(t=>t.Device).SequenceEqual(new[]{"secondary","left"}); });
        r.Check("cover_all_does_not_remove_existing_group",delegate { var covered=new[]{primary}; var targets=MaskUiPolicy.TargetsToCover(pair,covered,null); return covered.Length==1 && covered[0]==primary && targets.Count==1 && targets[0]==secondary; });
        r.Check("changed_geometry_requires_new_coverage",delegate { var moved=Display(primary.Device,true,100,100,1920,1080); return MaskUiPolicy.TargetsToCover(new[]{moved},new[]{primary},null).Count==1; });
        r.Check("single_monitor_cover_all_is_available",delegate { return MaskUiPolicy.CanCoverAll(new[]{primary},none,Registered(true,false,true,false)); });
        r.Check("single_monitor_cover_all_has_one_target",delegate { var targets=MaskUiPolicy.TargetsToCover(new[]{primary},none,null); return targets.Count==1 && targets[0]==primary; });
        r.Check("fully_covered_all_action_disabled",delegate { return !MaskUiPolicy.CanCoverAll(pair,pair,Registered(true,true,true)); });
        r.Check("empty_layout_all_action_disabled",delegate { return !MaskUiPolicy.CanCoverAll(none,none,Registered(true,true,true)); });
        r.Check("restore_key_failure_blocks_cover_all",delegate { return !MaskUiPolicy.CanCoverAll(pair,none,Registered(true,true,false)); });
        r.Check("missing_main_registration_blocks_required_group",delegate { return !MaskUiPolicy.CanCoverAll(pair,none,Registered(false,true,true)); });
        r.Check("missing_secondary_registration_blocks_required_group",delegate { return !MaskUiPolicy.CanCoverAll(pair,none,Registered(true,false,true)); });
        r.Check("covered_main_key_failure_does_not_block_secondary_add",delegate { return MaskUiPolicy.CanCoverAll(pair,new[]{primary},Registered(false,true,true)); });
        r.Check("covered_secondary_key_failure_does_not_block_main_add",delegate { return MaskUiPolicy.CanCoverAll(pair,new[]{secondary},Registered(true,false,true)); });
        r.Check("null_registration_state_blocks_cover_all",delegate { return !MaskUiPolicy.CanCoverAll(pair,none,null); });
        r.Check("unregistered_new_secondary_blocks_add_until_updated",delegate { return !MaskUiPolicy.CanCoverAll(pair,new[]{primary},Registered(true,false,true,false)); });

        Size minimum=new Size(400,340); Rectangle positioned;
        bool fit=MaskUiPolicy.TryFitPanel(new Rectangle(0,0,1920,1040),new Rectangle(1800,1000,990,855),minimum,out positioned);
        r.Check("panel_full_bounds_clamped_to_work_area",delegate { return fit && new Rectangle(0,0,1920,1040).Contains(positioned) && positioned.Width>=minimum.Width && positioned.Height>=minimum.Height; });
        Rectangle negative;
        bool negativeFit=MaskUiPolicy.TryFitPanel(new Rectangle(-1600,-900,1600,900),new Rectangle(100,100,990,855),minimum,out negative);
        r.Check("panel_fits_negative_coordinate_display",delegate { return negativeFit && new Rectangle(-1600,-900,1600,900).Contains(negative) && negative.Right<=0 && negative.Bottom<=0; });
        Rectangle shortened;
        bool shortFit=MaskUiPolicy.TryFitPanel(secondary.Bounds,new Rectangle(100,100,990,855),minimum,out shortened);
        r.Check("panel_can_shrink_to_short_unmasked_display",delegate { return shortFit && secondary.Bounds.Contains(shortened) && shortened.Height>=minimum.Height && !primary.Bounds.IntersectsWith(shortened); });
        Rectangle expanded;
        bool expandFit=MaskUiPolicy.TryFitPanel(primary.Bounds,new Rectangle(20,20,100,100),minimum,out expanded);
        r.Check("panel_respects_minimum_size",delegate { return expandFit && expanded.Width>=minimum.Width && expanded.Height>=minimum.Height && primary.Bounds.Contains(expanded); });
        Rectangle exact;
        Rectangle exactArea=new Rectangle(-400,-340,400,340);
        bool exactFit=MaskUiPolicy.TryFitPanel(exactArea,new Rectangle(0,0,990,855),minimum,out exact);
        r.Check("exact_minimum_area_is_usable",delegate { return exactFit && exact==exactArea; });
        Rectangle rejected;
        bool narrow=MaskUiPolicy.TryFitPanel(new Rectangle(0,0,399,340),new Rectangle(0,0,990,855),minimum,out rejected);
        r.Check("too_narrow_area_is_rejected",delegate { return !narrow && rejected==Rectangle.Empty; });
        Rectangle shortRejected;
        bool tooShort=MaskUiPolicy.TryFitPanel(new Rectangle(0,0,400,339),new Rectangle(0,0,990,855),minimum,out shortRejected);
        r.Check("too_short_area_is_rejected",delegate { return !tooShort && shortRejected==Rectangle.Empty; });
        Rectangle emptyResult;
        bool empty=MaskUiPolicy.TryFitPanel(Rectangle.Empty,new Rectangle(0,0,990,855),minimum,out emptyResult);
        r.Check("empty_work_area_is_rejected",delegate { return !empty && emptyResult==Rectangle.Empty; });
    }
    private static void RestoreAndMinimize(Results r) {
        r.Check("minimize_disabled_does_nothing_on_final_restore",delegate { return !MaskUiPolicy.ShouldMinimizeOnRestore(false,2,0); });
        r.Check("empty_restore_does_not_minimize",delegate { return !MaskUiPolicy.ShouldMinimizeOnRestore(true,0,0); });
        r.Check("partial_restore_does_not_minimize",delegate { return !MaskUiPolicy.ShouldMinimizeOnRestore(true,2,1); });
        r.Check("last_restore_minimizes_when_enabled",delegate { return MaskUiPolicy.ShouldMinimizeOnRestore(true,1,0) && MaskUiPolicy.ShouldMinimizeOnRestore(true,3,0); });
        r.Check("final_restore_shows_panel_when_running",delegate { return MaskUiPolicy.ShouldShowAfterRestore(1,0,false); });
        r.Check("minimize_option_does_not_reopen_normal_panel",delegate { return !MaskUiPolicy.ShouldShowAfterRestore(1,0,false,true); });
        r.Check("option_off_restores_normal_panel",delegate { return MaskUiPolicy.ShouldShowAfterRestore(1,0,false,false); });
        r.Check("empty_restore_does_not_request_panel_show",delegate { return !MaskUiPolicy.ShouldShowAfterRestore(0,0,false); });
        r.Check("partial_restore_does_not_request_final_panel_show",delegate { return !MaskUiPolicy.ShouldShowAfterRestore(2,1,false); });
        r.Check("application_exit_does_not_show_panel",delegate { return !MaskUiPolicy.ShouldShowAfterRestore(2,0,true); });
        r.Check("application_exit_can_minimize_without_showing_panel",delegate { return MaskUiPolicy.ShouldMinimizeOnRestore(true,2,0) && !MaskUiPolicy.ShouldShowAfterRestore(2,0,true); });

        r.Check("ordinary_foreign_window_is_minimize_target",delegate { return WindowMinimizer.ShouldMinimize(true,false,true,false,false,false,false); });
        r.Check("own_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,false,true,false,false,false,true); });
        r.Check("shell_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,false,true,false,false,true,false); });
        r.Check("tool_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,false,true,true,false,false,false); });
        r.Check("owned_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,false,true,false,true,false,false); });
        r.Check("invisible_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(false,false,true,false,false,false,false); });
        r.Check("already_iconic_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,true,true,false,false,false,false); });
        r.Check("non_minimizable_windows_excluded_from_minimization",delegate { return !WindowMinimizer.ShouldMinimize(true,false,false,false,false,false,false); });
    }
}
