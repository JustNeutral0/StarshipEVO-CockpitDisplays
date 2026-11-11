using EvoCore;
using System.Collections.Generic;
using System.Linq;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace CockpitDisplays
{
    public partial class Mod
    {
        // Screens: each tagged screen block hides its text and shows its feed on a quad over the text area (the whole
        // panel for the resizable screen); removing the tag brings the text back.
        // Window screens have a feed of their own, shaped like the screen. Camera screens share their camera's 16:9
        // feed, each through a material of its own (Crop) that shows the middle of the picture in the screen's shape.
        class Screen { public GameObject Quad; public Feed Feed; public bool IsWindow; public Rect Area; public Material Crop; }
        static readonly Dictionary<CommandInputText, Screen> screens = new Dictionary<CommandInputText, Screen>();

        protected override void Tick()
        {
            if (!ready || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 1f;
            try { ScanScreens(); }
            catch (System.Exception e) { Log.Error("screens: " + e); }
        }

        static void ScanScreens()
        {
            var commands = CommandManager.commands.Datas;
            var cameras = commands.OfType<EnterCommandCamera>().Where(c => c.brickCoord.entity != null).ToList();
            TagRigScreens(commands);
            var seen = new HashSet<CommandInputText>();
            var surfaceScreens = new HashSet<Command>();
            foreach (var screen in commands.OfType<CommandInputText>())
            {
                if (screen.textMeshPro == null || screen.brickCoord.entity == null) continue;
                seen.Add(screen);
                var text = Show ? screen.data.text ?? "" : "";
                if (ParseSurfaceTag(screen, text, out var surfaceMode, out var surfaceColor))
                {
                    if (screens.TryGetValue(screen, out var plain)) Detach(screen, plain);
                    surfaceScreens.Add(screen);
                    KeepDisplay(screen, surfaceMode, surfaceColor, cameras);
                    continue;
                }
                bool xray = text.Contains(XRayTag), view = !xray && text.Contains(ViewTag), window = xray || view || text.Contains(WindowTag);
                bool camera = !window && text.Contains(CameraTag);
                screens.TryGetValue(screen, out var shown);
                if (shown != null && (!(window || camera) || shown.IsWindow != window || (window && (shown.Feed.XRay != xray || shown.Feed.View != view)))) { Detach(screen, shown); shown = null; }
                // A resized screen gets a new picture of its new shape.
                if (shown != null && !Same(shown.Area, Area(screen, out _))) { Detach(screen, shown); shown = null; }
                if (!(window || camera)) continue;
                if (shown == null) screens[screen] = shown = Attach(screen, window, xray, view);
                // [view] without a camera block on its construct is a plain [window].
                if (view) shown.Feed.Proxy = NearestCamera(screen, cameras);
                if (!window)
                {
                    var feed = CameraFeed(NearestCamera(screen, cameras));
                    if (shown.Feed != feed) { shown.Feed = feed; CropTo(shown); }
                }
            }
            foreach (var gone in screens.Keys.Where(s => !seen.Contains(s)).ToList()) Detach(gone, screens[gone]);
            // EvoCore's Display Decals: each a display of its own.
            foreach (var decal in commands.OfType<EvoCore.DisplayDecal>())
            {
                if (decal.brickCoord.entity == null) continue;
                surfaceScreens.Add(decal);
                KeepDecalDisplay(decal, cameras);
            }
            PruneDisplays(surfaceScreens);
            foreach (var feed in feeds)
                feed.Used = feed.Display != null || screens.Values.Any(s => s.Feed == feed);
            Status = $"{screens.Count(s => !s.Value.IsWindow)} camera screen(s), {screens.Count(s => s.Value.IsWindow)} window(s) ({screens.Count(s => s.Value.Feed?.Proxy != null)} seen from a camera), " +
                     $"{feeds.Count(f => f.Used && !f.Off)} of {feeds.Count(f => f.Used)} feed(s) on from {scenes.Count} scene copy(ies) ({cameras.Count} camera block(s) in the world)" + DisplayReport();
        }

        // On and off by logic: a screen with nothing linked into it is always on; once something is (a button, switch
        // or gate), it follows that signal like any other block, on above 0.5. Switched off it shows a dark panel and
        // its feed isn't drawn (a feed shared by several camera screens is drawn while any of them is on). Checked
        // every frame, so it reacts as fast as lights do. A Lua script's switching (Scripting.cs) comes first.
        // A Lua Computer linked in only draws on it (a Lua canvas) and doesn't count as a switch.
        static bool Powered(CommandInputText screen) =>
            scriptPower.TryGetValue(screen, out var on) ? on :
            OnlyComputers(screen) || screen.State > 0.5f;

        static bool OnlyComputers(CommandInputText screen)
        {
            foreach (var item in screen.predecessors.Items)
                if (item.Command != null && !(item.Command is LuaComputer)) return false;
            return true;
        }

        static Material glassMaterial;
        static Material GlassMaterial => glassMaterial != null ? glassMaterial :
            glassMaterial = new Material(Shader.Find("UI/Default")) { name = "Cockpit Displays Glass", color = new Color(0.006f, 0.01f, 0.008f, 1f) };

        static Material offMaterial;
        static Material OffMaterial => offMaterial != null ? offMaterial :
            offMaterial = new Material(Shader.Find("UI/Default")) { name = "Cockpit Displays Off", color = new Color(0.02f, 0.022f, 0.026f, 1f) };

        static void ApplyPower()
        {
            foreach (var feed in feeds) feed.Off = true;
            foreach (var pair in screens)
            {
                var shown = pair.Value;
                if (shown.Quad == null || shown.Feed == null) continue;
                var on = Powered(pair.Key);
                shown.Quad.GetComponent<MeshRenderer>().sharedMaterial = on ? (shown.Crop != null ? shown.Crop : shown.Feed.Material) : OffMaterial;
                if (on) shown.Feed.Off = false;
            }
            foreach (var pair in displays)
            {
                var d = pair.Value;
                if (d.Go == null) continue;
                var on = d.Controller == null || Powered(d.Controller);
                // [screen] surfaces show only the Lua canvas: dark glass, and their feed is never drawn.
                var glassOnly = d.Mode == "screen";
                d.Go.GetComponent<MeshRenderer>().sharedMaterial = !on ? OffMaterial : glassOnly ? GlassMaterial : d.Feed.Material;
                if (on && !glassOnly) d.Feed.Off = false;
            }
        }

        static EnterCommandCamera NearestCamera(CommandInputText screen, List<EnterCommandCamera> cameras)
        {
            var parent = screen.brickCoord.entity.Parent;
            var at = screen.brickCoord.WorldPosition;
            return cameras.Where(c => c.brickCoord.entity.Parent == parent)
                          .OrderBy(c => (c.brickCoord.WorldPosition - at).sqrMagnitude)
                          .FirstOrDefault();
        }

        static Screen Attach(CommandInputText screen, bool window, bool xray, bool view)
        {
            var text = screen.textMeshPro;
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "Cockpit Displays Screen";
            quad.layer = text.gameObject.layer;
            quad.transform.SetParent(text.transform, false);
            var r = Area(screen, out var z);
            quad.transform.localPosition = new Vector3(r.center.x, r.center.y, z);
            quad.transform.localRotation = Quaternion.identity;
            quad.transform.localScale = new Vector3(r.width, r.height, 1f);
            text.GetComponent<MeshRenderer>().enabled = false;
            var shown = new Screen { Quad = quad, IsWindow = window, Area = r };
            if (window)
            {
                shown.Feed = WindowFeed(quad.transform, screen.brickCoord.entity.Parent, xray, view);
                quad.GetComponent<MeshRenderer>().sharedMaterial = shown.Feed.Material;
            }
            return shown;
        }

        // Where the picture goes, in the text's own space (z: how far in front of it). The resizable screen (131)
        // keeps a 1x1 text area however big its panel is, so its picture covers the block's bounds instead.
        static Rect Area(CommandInputText screen, out float z)
        {
            var text = screen.textMeshPro;
            z = -0.05f;
            var bd = screen.brickCoord.brickData;
            if (bd.brickId != TabLogic.SCREEN_TRANS.ItemID) return text.rectTransform.rect;
            var b = bd.GetAabb(true).Bounds;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, front = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var p = text.transform.InverseTransformPoint(screen.brickCoord.TransformData.LocalToWorld(corner));
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                front = Mathf.Min(front, p.z);
            }
            // A centimetre in front of the panel's near face.
            z = front - 0.01f / Mathf.Max(1e-4f, Mathf.Abs(text.transform.lossyScale.z));
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        // A camera screen shows the middle of its camera's picture cut to the screen's own shape, not the whole
        // picture squeezed into it.
        static void CropTo(Screen shown)
        {
            var rt = shown.Feed.Rt;
            if (shown.Crop == null)
            {
                shown.Crop = new Material(shown.Feed.Material) { name = "Cockpit Displays Screen" };
                shown.Quad.GetComponent<MeshRenderer>().sharedMaterial = shown.Crop;
            }
            shown.Crop.mainTexture = rt;
            var size = shown.Quad.transform.lossyScale;
            var screenAspect = Mathf.Abs(size.x / Mathf.Max(1e-4f, Mathf.Abs(size.y)));
            var feedAspect = (float)rt.width / rt.height;
            var scale = screenAspect < feedAspect ? new Vector2(screenAspect / feedAspect, 1f) : new Vector2(1f, feedAspect / Mathf.Max(1e-4f, screenAspect));
            shown.Crop.mainTextureScale = scale;
            shown.Crop.mainTextureOffset = (Vector2.one - scale) * 0.5f;
        }

        static bool Same(Rect a, Rect b) =>
            Mathf.Abs(a.width - b.width) <= 0.01f * Mathf.Abs(a.width) && Mathf.Abs(a.height - b.height) <= 0.01f * Mathf.Abs(a.height) &&
            (a.center - b.center).sqrMagnitude <= 1e-4f * a.size.sqrMagnitude;

        static void Detach(CommandInputText screen, Screen shown)
        {
            if (shown.Quad != null) Object.Destroy(shown.Quad);
            if (shown.Crop != null) Object.Destroy(shown.Crop);
            if (shown.IsWindow && shown.Feed != null) DropFeed(shown.Feed);
            screens.Remove(screen);
            if (screen.textMeshPro != null) screen.textMeshPro.GetComponent<MeshRenderer>().enabled = true;
        }

    }
}
