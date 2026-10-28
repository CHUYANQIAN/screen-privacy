using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

internal static class MaskUiPolicy {
    private static bool SameTarget(Target left,Target right) { return left.Device==right.Device && left.Primary==right.Primary && left.Bounds==right.Bounds; }
    internal static List<Target> Uncovered(IEnumerable<Target> screens,IEnumerable<Target> covered) {
        var rectangles=covered.Select(t=>t.Bounds).ToArray();
        return screens.Where(screen=>!rectangles.Any(rect=>rect.IntersectsWith(screen.Bounds))).ToList();
    }
    internal static List<Target> TargetsToCover(IEnumerable<Target> screens,IEnumerable<Target> covered,bool? primary) {
        var current=covered.ToArray();
        return screens.Where(screen=>(!primary.HasValue || screen.Primary==primary.Value) && !current.Any(mask=>SameTarget(screen,mask))).ToList();
    }
    internal static bool CanCoverAll(IEnumerable<Target> screens,IEnumerable<Target> covered,RegistrationState state) {
        var targets=TargetsToCover(screens,covered,null);
        return state!=null && state.RestoreReady && targets.Count>0 && targets.All(t=>state.CanStart(t.Primary));
    }
    internal static bool ShouldMinimizeOnRestore(bool enabled,int before,int remaining) { return enabled && before>0 && remaining==0; }
    internal static bool ShouldShowAfterRestore(int before,int remaining,bool exiting,bool minimizeEnabled=false) { return before>0 && remaining==0 && !exiting && !minimizeEnabled; }
    internal static bool TryFitPanel(Rectangle area,Rectangle current,Size minimum,out Rectangle result) {
        result=Rectangle.Empty;
        if(area.Width<minimum.Width || area.Height<minimum.Height || area.Width<=0 || area.Height<=0) return false;
        int marginX=area.Width>=minimum.Width+24?12:0, marginY=area.Height>=minimum.Height+24?12:0;
        int width=Math.Max(minimum.Width,Math.Min(current.Width,area.Width-2*marginX));
        int height=Math.Max(minimum.Height,Math.Min(current.Height,area.Height-2*marginY));
        int x=Math.Max(area.Left+marginX,Math.Min(current.Left,area.Right-marginX-width));
        int y=Math.Max(area.Top+marginY,Math.Min(current.Top,area.Bottom-marginY-height));
        result=new Rectangle(x,y,width,height); return area.Contains(result);
    }
}
