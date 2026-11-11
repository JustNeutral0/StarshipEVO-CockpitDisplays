using System.Collections.Generic;
using System.Linq;
using EvoCore;
using VoxelEngine;

namespace CockpitDisplays
{
    // The `views` global in EvoCore's Lua scripts (it was `displays` until EvoCore's own Lua displays took that name):
    // the camera screens, windows and display surfaces on the script's ship, nearest the script's own screen first.
    //   views.count            how many
    //   views.get(i)           one of them (1 = nearest): .on (read or set), .kind ("screen" or "surface"), .mode,
    //                          .color (surfaces), .auto() (back to its logic link, or always on with none)
    //   views.on = false       switch them all; reading it says whether any is on
    //   views.auto()           all back to their logic links
    // Drawing on a display surface goes through EvoCore: link a Lua Computer to its controller screen, and its
    // `display` / `displays` draw on it (Displays.cs, BuildCanvas).
    // A script's switching wins over logic links while the script runs; when it stops, the links take over again.
    public partial class Mod
    {
        static readonly Dictionary<CommandInputText, bool> scriptPower = new Dictionary<CommandInputText, bool>();

        static void ExposeToLua()
        {
            Lua.Expose("views", c => new DisplaysApi(c));
            Help.Global("views", "The camera screens, windows and display surfaces on the script's ship, nearest the script's own screen first. " +
                                 "A script's switching wins over logic links while it runs. To draw on a display surface, link a Lua Computer " +
                                 "to its controller screen and use display.",
                "count", "how many",
                "get(i)", "one of them (1 = nearest): .on (read or set), .kind (screen or surface), .mode, .color (surfaces), .auto()",
                "on", "set false or true to switch them all; reading it says whether any is on",
                "auto()", "all back to their logic links");
            // EvoCore asks which blocks can carry a Lua canvas: a display surface's controller screen.
            Monitors.SurfaceAspect = c => c != null && displays.TryGetValue(c, out var d) && d.CanvasAspect > 0f ? d.CanvasAspect : 0f;
            // ...and where the player aims on one, for touch.
            Monitors.SurfaceHit = SurfaceHit;
        }

        public class DisplaysApi
        {
            readonly Lua.Context c;
            readonly HashSet<CommandInputText> touched = new HashSet<CommandInputText>();

            public DisplaysApi(Lua.Context context)
            {
                c = context;
                c.Stopped += auto;
            }

            List<CommandInputText> Mine()
            {
                var at = c.Host.brickCoord.entity != null ? c.Host.brickCoord.WorldPosition : default;
                return screens.Keys.Concat(displays.Values.Where(v => v.Controller != null).Select(v => v.Controller))
                    .Where(s => s.brickCoord.entity != null && s.brickCoord.entity.Parent == c.Ship)
                    .OrderBy(s => (s.brickCoord.WorldPosition - at).sqrMagnitude)
                    .ToList();
            }

            public int count => Mine().Count;

            public DisplayApi get(int i)
            {
                var all = Mine();
                return i >= 1 && i <= all.Count ? new DisplayApi(all[i - 1], touched) : null;
            }

            public bool on
            {
                get => Mine().Any(Powered);
                set { foreach (var s in Mine()) { scriptPower[s] = value; touched.Add(s); } }
            }

            public void auto()
            {
                foreach (var s in touched) scriptPower.Remove(s);
                touched.Clear();
            }
        }

        public class DisplayApi
        {
            readonly CommandInputText s;
            readonly HashSet<CommandInputText> touched;
            public DisplayApi(CommandInputText screen, HashSet<CommandInputText> owner) { s = screen; touched = owner; }

            public bool on
            {
                get => Powered(s);
                set { scriptPower[s] = value; touched.Add(s); }
            }

            public string kind => displays.ContainsKey(s) ? "surface" : "screen";

            public string mode => displays.TryGetValue(s, out var d) ? d.Mode :
                screens.TryGetValue(s, out var shown) ? (!shown.IsWindow ? "camera" : shown.Feed.XRay ? "xray" : shown.Feed.View ? "view" : "window") : "";

            public string color => displays.TryGetValue(s, out var d) ? "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(d.Color) : "";

            public void auto()
            {
                scriptPower.Remove(s);
                touched.Remove(s);
            }
        }
    }
}
