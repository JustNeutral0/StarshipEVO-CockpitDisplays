using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using Manager;
using VoxelEngine;

namespace CockpitDisplays
{
    // Display surfaces: a screen whose text holds [mode, COLOR] (e.g. [view, match] or [window, blue]) turns every
    // decal on its construct painted that colour into part of one display, so decals laid over a cockpit's walls,
    // wedges and corners show one seamless outside view. The screen itself is part of it too.
    // Decals, not whole blocks (tried first: a block's six faces, and the faces buried between neighbours, all drew
    // and looked broken). A decal is a box standing flush on a surface and reaching 1 unit into it along its -up; the
    // image lands where the box meets surfaces facing its way, so the display is those surfaces clipped to the box
    // (AddDecalFootprint), which also works on slopes, where the box sits square to the grid.
    // One feed renders the player's whole field of view (from the nearest Camera block for view/camera, or from the
    // player's eye with the construct left out for window/xray), and each surface triangle's texture coordinates are
    // where its corners land on the player's screen, recomputed every frame. Wherever a surface covers the player's
    // view it shows exactly what's behind it in that direction, so neighbouring faces join up at any angle.
    // Texture coordinates are interpolated across each triangle in 3D, not on screen, so faces are cut into cells of
    // CellSize metres to keep that error invisible.
    // The player's head pose is taken once per drawn frame (CapturePoses) and used for the sky, the ships and the
    // texture coordinates alike; reading the live camera at each step made the picture jump while moving.
    // A display covers the whole view, so its picture is DisplayScale of the game's own resolution.
    // Lua canvas (EvoCore's Monitors): link a Lua Computer to the controller screen and the script draws on the
    // surfaces, see-through over the outside view: a HUD on the cockpit glass. The canvas lies flat across the surfaces
    // as seen from inside (their decals' up), with the ship's up as its up, across the faces turned that way. [screen, COLOR] makes surfaces that show
    // only the canvas, on dark glass, with no outside view.
    public partial class Mod
    {
        public static float CellSize = 0.25f;
        public static int ColorTolerance = 3;
        static readonly Regex surfaceTag = new Regex(@"\[\s*(camera|window|xray|view|screen)\s*,\s*([^\]]+?)\s*\]", RegexOptions.IgnoreCase);

        class Display
        {
            // The block the display belongs to (its key): a controller screen, or a Display Decal (EvoCore), which is a
            // display on its own: just its own decal, no controller screen.
            public Command Owner;
            public CommandInputText Controller;   // null for a Display Decal
            public string Mode;
            public Color32 Color;
            public ParentEntity Construct;
            public Feed Feed;
            public GameObject Go;
            public Mesh Mesh;
            public long Signature = long.MinValue;
            public int Blocks;
            // Vertices by owner: a block's entity (entity-local positions) or, for null, the controller's text
            // (positions in its own space).
            public readonly List<(VoxelEntity Entity, int Start, int Count)> Groups = new List<(VoxelEntity, int, int)>();
            public Vector3[] Local = new Vector3[0], World = new Vector3[0];
            public Vector2[] Uv = new Vector2[0];
            // Each vertex's facing (the way its surface looks, in the same space as Local), and its unlifted world
            // position this frame.
            public Vector3[] Normals = new Vector3[0], Raw = new Vector3[0];
            // The Lua canvas layer: the same surfaces with the canvas's own texture coordinates, a little nearer.
            public GameObject Overlay;
            public Mesh OverlayMesh;
            public Material OverlayMaterial;
            public Vector3[] OverlayWorld = new Vector3[0];
            public float CanvasAspect;
            // The canvas lies on one plane: each vertex is moved Flat[k] along the canvas's facing (FacingLocal, in the
            // controller's entity space) to the front-most surface's depth, so text never splits between depths.
            public float[] Flat = new float[0];
            public Vector3 FacingLocal;
        }

        static readonly Dictionary<Command, Display> displays = new Dictionary<Command, Display>();

        // The mode and colour of a [mode, COLOR] tag. COLOR is "match" (the screen's own paint, so the screen and its
        // surfaces are painted with the same swatch), a swatch name or position (Swatch), #RRGGBB / RRGGBB, or an
        // HTML colour name.
        static bool ParseSurfaceTag(CommandInputText screen, string text, out string mode, out Color32 color)
        {
            mode = null;
            color = default;
            var m = surfaceTag.Match(text);
            if (!m.Success) return false;
            var name = m.Groups[2].Value.Trim();
            if (name.Equals("match", System.StringComparison.OrdinalIgnoreCase) || name.Equals("same", System.StringComparison.OrdinalIgnoreCase))
                color = screen.Color.a > 0f ? (Color32)screen.Color : screen.brickCoord.brickData.color;
            else if (!Swatch(name, out color))
            {
                if (Regex.IsMatch(name, "^[0-9a-fA-F]{6}$")) name = "#" + name;
                if (!ColorUtility.TryParseHtmlString(name, out var parsed)) return false;
                color = parsed;
            }
            mode = m.Groups[1].Value.ToLowerInvariant();
            return true;
        }

        // The game has no colour picker and doesn't name its swatches: the paint codex shows "Colors" as 3 rows of 10
        // hues (light, plain, dark), then "Grays" and "Tans" (StreamingAssets\PaintPalettes.xml, copied to the
        // player's own PaintPalettes.xml). These names are ours, listed with their swatches in the settings tab: a hue
        // with "light" for the top row and "dark" for the bottom ("blue", "light teal", "dark red"), a gray or tan
        // name, or palette and position ("colors 18", "tans 2", counted left to right, top to bottom).
        static readonly string[] hues = { "red", "orange", "yellow", "lime", "green", "teal", "cyan", "blue", "purple", "pink" };
        static readonly string[] grayNames = { "white", "light gray", "gray", "dark gray", "black" };
        static readonly string[] tanNames = { "brown", "rust", "dark brown", "cream", "sand", "khaki" };

        static bool Swatch(string name, out Color32 color)
        {
            color = default;
            var n = Regex.Replace(name.ToLowerInvariant().Replace("grey", "gray"), @"\s+", " ").Trim();
            var at = Regex.Match(n, @"^(colors?|grays?|tans?)\s*#?\s*(\d+)$");
            if (at.Success) return PaletteColor(at.Groups[1].Value, int.Parse(at.Groups[2].Value) - 1, out color);
            var gray = System.Array.IndexOf(grayNames, n);
            if (gray >= 0) return PaletteColor("grays", gray, out color);
            var tan = System.Array.IndexOf(tanNames, n);
            if (tan >= 0) return PaletteColor("tans", tan, out color);
            var row = 1;
            if (n.StartsWith("light ")) { row = 0; n = n.Substring(6); }
            else if (n.StartsWith("dark ")) { row = 2; n = n.Substring(5); }
            var hue = System.Array.IndexOf(hues, n);
            return hue >= 0 && PaletteColor("colors", row * hues.Length + hue, out color);
        }

        static bool PaletteColor(string palette, int index, out Color32 color)
        {
            color = default;
            var stem = palette.TrimEnd('s');
            foreach (var p in ItemPaint.PaintPalettes)
                if (p.name != null && p.name.ToLowerInvariant().TrimEnd('s') == stem && p.colors != null && index >= 0 && index < p.colors.Count)
                {
                    color = p.colors[index];
                    return true;
                }
            return false;
        }

        // The settings tab's swatch chart: each swatch drawn in its colour with the name to use.
        static Texture2D swatchTexture;

        static void DrawSwatches()
        {
            if (swatchTexture == null) { swatchTexture = new Texture2D(1, 1); swatchTexture.SetPixel(0, 0, Color.white); swatchTexture.Apply(); }
            void Row(string palette, IEnumerable<(int Index, string Name)> names)
            {
                GUILayout.BeginHorizontal();
                foreach (var (index, name) in names)
                {
                    if (!PaletteColor(palette, index, out var c)) continue;
                    GUILayout.BeginVertical(GUILayout.Width(78f));
                    var rect = GUILayoutUtility.GetRect(70f, 18f);
                    var old = GUI.color;
                    GUI.color = c;
                    GUI.DrawTexture(rect, swatchTexture);
                    GUI.color = old;
                    GUILayout.Label(name);
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
            for (int row = 0; row < 3; row++)
            {
                var prefix = row == 0 ? "light " : row == 2 ? "dark " : "";
                Row("colors", hues.Select((h, i) => (row * hues.Length + i, prefix + h)));
            }
            Row("grays", grayNames.Select((g, i) => (i, g)));
            Row("tans", tanNames.Select((t, i) => (i, t)));
        }

        // Called from the screen scan for each screen with a [mode, COLOR] tag.
        static void KeepDisplay(CommandInputText screen, string mode, Color32 color, List<EnterCommandCamera> cameras)
        {
            var d = Ensure(screen, screen, mode, color, screen.textMeshPro.gameObject.layer);
            // From the nearest Camera block if there is one ([view] and [camera]), else from the player's eye with the
            // construct left out.
            d.Feed.Proxy = mode == "view" || mode == "camera" ? NearestCamera(screen, cameras) : null;
            d.Feed.Scene = GetScene(d.Feed.Proxy != null ? null : d.Construct);
            BuildDisplay(d);
        }

        // A display for its owner (made, or remade when its mode or colour changed).
        static Display Ensure(Command owner, CommandInputText controller, string mode, Color32 color, int layer)
        {
            displays.TryGetValue(owner, out var d);
            if (d != null && (d.Mode != mode || !SameColor(d.Color, color))) { DropDisplay(owner, d); d = null; }
            if (d != null) return d;
            d = new Display { Owner = owner, Controller = controller, Mode = mode, Color = color, Construct = owner.brickCoord.entity.Parent };
            var w = Mathf.Max(64, Mathf.RoundToInt(UnityEngine.Screen.width * DisplayScale * Overscan));
            var h = Mathf.Max(64, Mathf.RoundToInt(UnityEngine.Screen.height * DisplayScale * Overscan));
            d.Feed = NewFeed(w, h);
            d.Feed.Display = d;
            d.Feed.Construct = d.Construct;
            d.Mesh = new Mesh { name = "Cockpit Displays Display", indexFormat = IndexFormat.UInt32 };
            d.Mesh.MarkDynamic();
            d.Go = new GameObject("Cockpit Displays Display");
            d.Go.layer = layer;
            d.Go.AddComponent<MeshFilter>().sharedMesh = d.Mesh;
            d.Go.AddComponent<MeshRenderer>().sharedMaterial = d.Feed.Material;
            if (controller != null) controller.textMeshPro.GetComponent<MeshRenderer>().enabled = false;
            displays[owner] = d;
            return d;
        }

        // An EvoCore Display Decal: a [screen] display of just itself, in its own paint. On a wall the picture stands
        // upright by the ship's up, however the decal was turned while placing (turned on its end it is a tall
        // screen, not a sideways one); on a floor or ceiling up is the decal's own. DecalTurn turns it in quarter turns.
        public static int DecalTurn;
        static int decalLayer = -1;

        static void KeepDecalDisplay(EvoCore.DisplayDecal decal, List<EnterCommandCamera> cameras)
        {
            if (decalLayer < 0)
            {
                var any = CommandManager.commands.Datas.OfType<CommandInputText>().FirstOrDefault(s => s.textMeshPro != null);
                decalLayer = any != null ? any.textMeshPro.gameObject.layer : 0;
            }
            var d = Ensure(decal, null, "screen", decal.brickCoord.brickData.color, decalLayer);
            d.Feed.Proxy = null;
            d.Feed.Scene = GetScene(d.Construct);
            BuildDisplay(d);
        }

        // Same paint: within ColorTolerance, or nearest to the same swatch. Blocks don't all keep a swatch exactly: a
        // screen painted the palette's green (69,177,42) holds (78,177,53), while decals keep the swatch as is.
        static bool SameColor(Color32 a, Color32 b) =>
            Mathf.Abs(a.r - b.r) <= ColorTolerance && Mathf.Abs(a.g - b.g) <= ColorTolerance && Mathf.Abs(a.b - b.b) <= ColorTolerance ||
            NearestSwatch(a, out var sa) && NearestSwatch(b, out var sb) && sa == sb;

        // Swatch distances past this are a colour of their own (a hex code), not a swatch.
        const int SwatchReach = 40;
        static readonly Dictionary<int, int> nearest = new Dictionary<int, int>();

        // The index (across all palettes) of the swatch nearest to c, if one is within SwatchReach on every channel.
        static bool NearestSwatch(Color32 c, out int swatch)
        {
            var key = c.r << 16 | c.g << 8 | c.b;
            if (!nearest.TryGetValue(key, out swatch))
            {
                swatch = -1;
                int best = int.MaxValue, index = 0;
                foreach (var p in ItemPaint.PaintPalettes)
                {
                    if (p.colors == null) continue;
                    foreach (Color32 s in p.colors)
                    {
                        int dr = s.r - c.r, dg = s.g - c.g, db = s.b - c.b;
                        var d = dr * dr + dg * dg + db * db;
                        if (d < best && Mathf.Abs(dr) <= SwatchReach && Mathf.Abs(dg) <= SwatchReach && Mathf.Abs(db) <= SwatchReach) { best = d; swatch = index; }
                        index++;
                    }
                }
                if (index > 0) nearest[key] = swatch;
            }
            return swatch >= 0;
        }

        static void PruneDisplays(HashSet<Command> keep)
        {
            foreach (var owner in displays.Keys.Where(s => !keep.Contains(s)).ToList()) DropDisplay(owner, displays[owner]);
        }

        static void DropDisplay(Command owner, Display d)
        {
            if (d.Go != null) Object.Destroy(d.Go);
            if (d.Mesh != null) Object.Destroy(d.Mesh);
            if (d.Overlay != null) Object.Destroy(d.Overlay);
            if (d.OverlayMesh != null) Object.Destroy(d.OverlayMesh);
            if (d.OverlayMaterial != null) Object.Destroy(d.OverlayMaterial);
            if (d.Feed != null) DropFeed(d.Feed);
            displays.Remove(owner);
            if (d.Controller != null && d.Controller.textMeshPro != null) d.Controller.textMeshPro.GetComponent<MeshRenderer>().enabled = true;
        }

        // Re-cut the surfaces when the painted blocks change (checked every scan: added, removed, moved, repainted).
        static void BuildDisplay(Display d)
        {
            var tree = d.Construct != null ? d.Construct.Tree : null;
            if (tree == null) return;
            long signature = tree.ChildrensAndSelf.Count;
            var areaZ = 0f;
            var area = d.Controller != null ? Area(d.Controller, out areaZ) : default;
            signature = signature * 31 + area.GetHashCode();
            // A Display Decal's display is just that decal.
            var own = d.Controller == null ? d.Owner.brickCoord.brickData.instanceId : -1;
            if (d.Controller == null) signature = signature * 31 + DecalTurn;
            var matches = new List<(VoxelEntity Entity, BrickInstanceData Brick)>();
            foreach (var e in tree.ChildrensAndSelf)
            {
                if (e == null || e.Bricks == null || !e.Bricks.brickDatas.IsCreated) continue;
                var bricks = e.Bricks.brickDatas;
                // Any block change on the entity, not just to the decals: the surfaces under them may have moved.
                signature = signature * 31 + bricks.LiveCount;
                for (int i = 0; i < bricks.Length; i++)
                {
                    var b = bricks[i];
                    if (!b.IsValid || b.Brick == null || !b.Brick.IsDecal) continue;
                    if (d.Controller == null ? b.instanceId != own : !SameColor(b.color, d.Color) || b.Brick.brickID == EvoCore.DisplayDecals.Id) continue;
                    matches.Add((e, b));
                    signature = signature * 31 + b.instanceId;
                    signature = signature * 31 + b.gridPosition.GetHashCode();
                    signature = signature * 31 + b.rotation * 4099 + b.scale * 7 + b.brickId;
                }
            }
            if (signature == d.Signature) return;
            d.Signature = signature;
            d.Blocks = matches.Count;
            var local = new List<Vector3>();
            var tris = new List<int>();
            var normals = new List<Vector3>();
            d.Groups.Clear();
            foreach (var group in matches.GroupBy(m => m.Entity))
            {
                var start = local.Count;
                // The entity's solid blocks, which the decals land on.
                var solids = new List<(BrickInstanceData Brick, Bounds Bounds)>();
                var all = group.Key.Bricks.brickDatas;
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    if (s.IsValid && s.Brick != null && !s.Brick.IsDecal && s.Brick.VoxelMesh?.meshBuffer != null) solids.Add((s, s.GetAabb(true).Bounds));
                }
                foreach (var (_, decal) in group)
                {
                    var before = local.Count;
                    AddDecalFootprint(decal, solids, local, tris);
                    var up = decal.Quaternion * Vector3.up;
                    while (normals.Count < local.Count) normals.Add(up);
                }
                d.Groups.Add((group.Key, start, local.Count - start));
            }
            // The controller screen's own picture area, in its text's space.
            if (d.Controller != null)
            {
                var controllerStart = local.Count;
                while (normals.Count < local.Count) normals.Add(Vector3.up);
                Vector3 C(float x, float y) => new Vector3(x, y, areaZ);
                Subdivide(C(area.xMin, area.yMin), C(area.xMax, area.yMin), C(area.xMax, area.yMax), local, tris, d.Controller.textMeshPro.transform);
                Subdivide(C(area.xMin, area.yMin), C(area.xMax, area.yMax), C(area.xMin, area.yMax), local, tris, d.Controller.textMeshPro.transform);
                d.Groups.Add((null, controllerStart, local.Count - controllerStart));
                while (normals.Count < local.Count) normals.Add(Vector3.back); // the text's front, in its own space
            }
            d.Local = local.ToArray();
            d.Normals = normals.ToArray();
            d.Raw = new Vector3[d.Local.Length];
            d.World = new Vector3[d.Local.Length];
            d.Uv = new Vector2[d.Local.Length];
            d.Mesh.Clear();
            d.Mesh.vertices = d.World;
            d.Mesh.uv = d.Uv;
            d.Mesh.SetTriangles(tris, 0);
            BuildCanvas(d, matches, tris);
        }

        // Flip the canvas top to bottom on surfaces (if it shows upside down).
        public static bool CanvasFlipV;

        // The Lua canvas's texture coordinates: every vertex projected onto one plane facing the viewer (the decals'
        // average up, and the controller's front), up along the ship's up, scaled to the bounding rectangle.
        static void BuildCanvas(Display d, List<(VoxelEntity Entity, BrickInstanceData Brick)> decals, List<int> tris)
        {
            var text = d.Controller != null && d.Controller.textMeshPro != null ? d.Controller.textMeshPro.transform : null;
            var world = new Vector3[d.Local.Length];
            foreach (var (entity, start, count) in d.Groups)
                for (int k = start; k < start + count; k++)
                    world[k] = entity == null ? (text != null ? text.TransformPoint(d.Local[k]) : Vector3.zero) : entity.TransformData.LocalToWorld(d.Local[k]);
            // The decals decide which way the display faces; the controller screen only when there are none, since it
            // can sit on a console turned another way (it was averaged in, and one turned 90 degrees tipped the whole
            // picture 45 degrees off the wall, seen from behind).
            var facing = Vector3.zero;
            foreach (var (entity, decal) in decals) facing += entity.TransformData.rotation * (decal.Quaternion * Vector3.up);
            if (facing.sqrMagnitude < 1e-6f && text != null) facing = -text.forward;
            if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
            facing.Normalize();
            // Up is the controller screen's up (it was placed to read upright); the ship's up only if that lies along
            // the facing (a ship whose up runs along the wall's face put the picture on its side).
            var shipRotation = d.Owner.brickCoord.TransformData.rotation;
            var up = text != null ? Vector3.ProjectOnPlane(text.up, facing) : Vector3.zero;
            // A Display Decal: the ship's up across its face (the decal's own on a floor or ceiling, where the ship's
            // has next to nothing across it), DecalTurn quarter turns round.
            if (d.Controller == null && decals.Count > 0)
            {
                var (de, db) = decals[0];
                var across = Vector3.ProjectOnPlane(de.TransformData.rotation * Vector3.up, facing);
                if (across.sqrMagnitude < 0.1f) across = Vector3.ProjectOnPlane(de.TransformData.rotation * (db.Quaternion * Vector3.forward), facing);
                up = Quaternion.AngleAxis(90f * DecalTurn, facing) * across;
            }
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(shipRotation * Vector3.up, facing);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(shipRotation * Vector3.forward, facing);
            up.Normalize();
            var right = Vector3.Cross(facing, up);
            // The controller screen is part of the display only where it touches the decals' surface (built into the
            // wall); one standing apart would stretch the picture across the gap between them, drawn in mid-air.
            int ownStart = 0, ownCount = 0;
            foreach (var (entity, start, count) in d.Groups) if (entity == null) { ownStart = start; ownCount = count; }
            if (ownCount > 0 && decals.Count > 0)
            {
                var surface = new Bounds();
                var any = false;
                for (int k = 0; k < world.Length; k++)
                {
                    if (k >= ownStart && k < ownStart + ownCount) continue;
                    if (!any) { surface = new Bounds(world[k], Vector3.zero); any = true; } else surface.Encapsulate(world[k]);
                }
                surface.Expand(0.1f);
                // Touching, and level with it: a screen against the wall but standing out from it isn't part of it (the
                // picture is laid flat at the surface's front, so its share would float in front of the wall).
                var wallFront = float.MinValue;
                for (int k = 0; k < world.Length; k++) if (k < ownStart || k >= ownStart + ownCount) wallFront = Mathf.Max(wallFront, Vector3.Dot(world[k], facing));
                var touching = false;
                for (int k = ownStart; k < ownStart + ownCount && !touching; k++) touching = surface.Contains(world[k]);
                for (int k = ownStart; k < ownStart + ownCount && touching; k++) touching = Mathf.Abs(Vector3.Dot(world[k], facing) - wallFront) < 0.05f;
                if (touching) ownStart = ownCount = 0;   // it counts like any other face
            }
            else ownStart = ownCount = 0;
            bool Apart(int k) => k >= ownStart && k < ownStart + ownCount;
            // The canvas spans the faces that look the viewer's way (within about 45 degrees); faces turned further
            // (a booth's side walls) would only get it squeezed edge-on, so they fall outside it and show nothing.
            var main = new HashSet<int>();
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                if (Apart(tris[t]) || Apart(tris[t + 1]) || Apart(tris[t + 2])) continue;
                var n = Vector3.Cross(world[tris[t + 1]] - world[tris[t]], world[tris[t + 2]] - world[tris[t]]);
                if (n.sqrMagnitude > 1e-12f && Mathf.Abs(Vector3.Dot(n.normalized, facing)) > 0.7f) { main.Add(tris[t]); main.Add(tris[t + 1]); main.Add(tris[t + 2]); }
            }
            if (main.Count == 0) for (int k = 0; k < world.Length; k++) main.Add(k);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            var uv = new Vector2[world.Length];
            for (int k = 0; k < world.Length; k++)
            {
                uv[k] = new Vector2(Vector3.Dot(world[k], right), Vector3.Dot(world[k], up));
                if (!main.Contains(k)) continue;
                u0 = Mathf.Min(u0, uv[k].x); u1 = Mathf.Max(u1, uv[k].x); v0 = Mathf.Min(v0, uv[k].y); v1 = Mathf.Max(v1, uv[k].y);
            }
            float w = Mathf.Max(1e-3f, u1 - u0), h = Mathf.Max(1e-3f, v1 - v0);
            for (int k = 0; k < uv.Length; k++)
            {
                var v = (uv[k].y - v0) / h;
                uv[k] = new Vector2((uv[k].x - u0) / w, CanvasFlipV ? 1f - v : v);
            }
            d.CanvasAspect = w / h;
            float front = float.MinValue;
            foreach (var k in main) front = Mathf.Max(front, Vector3.Dot(world[k], facing));
            d.Flat = new float[world.Length];
            for (int k = 0; k < world.Length; k++) d.Flat[k] = front - Vector3.Dot(world[k], facing);
            d.FacingLocal = Quaternion.Inverse(d.Owner.brickCoord.TransformData.rotation) * facing;
            // The controller screen stands proud of the wall; the canvas is see-through and doesn't hide what's behind,
            // so surface triangles behind the screen's picture would show it a second time, offset. Leave those out.
            var overlayTris = new List<int>(tris.Count);
            float cu0 = float.MaxValue, cu1 = float.MinValue, cv0 = float.MaxValue, cv1 = float.MinValue, cDepth = 0f;
            int cStart = 0, cCount = 0;
            foreach (var (entity, start, count) in d.Groups) if (entity == null) { cStart = start; cCount = count; }
            // Only a controller that's part of the surface (facing the same way) stands in front of it.
            if (cCount > 0 && !main.Contains(cStart)) cCount = 0;
            for (int k = cStart; k < cStart + cCount; k++)
            {
                cu0 = Mathf.Min(cu0, uv[k].x); cu1 = Mathf.Max(cu1, uv[k].x); cv0 = Mathf.Min(cv0, uv[k].y); cv1 = Mathf.Max(cv1, uv[k].y);
                cDepth += Vector3.Dot(world[k], facing) / cCount;
            }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                // Faces turned away (not in main) are left out: they'd only show the canvas's edge pixels smeared.
                if (!main.Contains(a) || !main.Contains(b) || !main.Contains(c)) continue;
                bool own = a >= cStart && a < cStart + cCount;
                if (!own && cCount > 0)
                {
                    var m = (uv[a] + uv[b] + uv[c]) / 3f;
                    var depth = (Vector3.Dot(world[a], facing) + Vector3.Dot(world[b], facing) + Vector3.Dot(world[c], facing)) / 3f;
                    if (m.x > cu0 && m.x < cu1 && m.y > cv0 && m.y < cv1 && depth < cDepth - 0.01f) continue;
                }
                overlayTris.Add(a); overlayTris.Add(b); overlayTris.Add(c);
            }
            if (d.OverlayMesh == null)
            {
                d.OverlayMesh = new Mesh { name = "Cockpit Displays Canvas", indexFormat = IndexFormat.UInt32 };
                d.OverlayMesh.MarkDynamic();
                d.OverlayMaterial = new Material(Shader.Find("UI/Default")) { name = "Cockpit Displays Canvas" };
                d.OverlayMaterial.renderQueue = d.Feed.Material.renderQueue + 1;
                d.Overlay = new GameObject("Cockpit Displays Canvas");
                d.Overlay.layer = d.Go.layer;
                d.Overlay.AddComponent<MeshFilter>().sharedMesh = d.OverlayMesh;
                d.Overlay.AddComponent<MeshRenderer>().sharedMaterial = d.OverlayMaterial;
                d.Overlay.SetActive(false);
            }
            d.OverlayWorld = new Vector3[world.Length];
            d.OverlayMesh.Clear();
            d.OverlayMesh.vertices = d.OverlayWorld;
            d.OverlayMesh.uv = uv;
            d.OverlayMesh.SetTriangles(overlayTris, 0);
        }

        // Shows the controller's Lua canvas over its surfaces, while a script draws on it and the display is on.
        static void UpdateCanvas(Display d, Vector3 eye)
        {
            if (d.Overlay == null) return;
            var tex = Show && d.Feed.Posed && (d.Controller == null || Powered(d.Controller)) ? EvoCore.Monitors.CanvasTexture(d.Owner) : null;
            d.Overlay.SetActive(tex != null);
            if (tex == null) return;
            d.OverlayMaterial.mainTexture = tex;
            if (d.OverlayWorld.Length != d.World.Length || d.Flat.Length != d.World.Length) return;
            var facing = d.Owner.brickCoord.TransformData.rotation * d.FacingLocal;
            // Only from the side the glass faces.
            if (d.Raw.Length > 0 && Vector3.Dot(eye - d.Raw[0], facing) <= 0f) { d.Overlay.SetActive(false); return; }
            for (int k = 0; k < d.World.Length; k++) d.OverlayWorld[k] = d.Raw[k] + facing * (d.Flat[k] + Lift + 0.005f);
            d.OverlayMesh.vertices = d.OverlayWorld;
            d.OverlayMesh.RecalculateBounds();
        }

        // For EvoCore's touch: where a ray first meets a shown Lua canvas within reach (its controller screen, the point
        // as the canvas's texture coordinates, how far), using the canvas layer as drawn this frame.
        static (Command Block, Vector2 Uv, float Distance)? SurfaceHit(Ray ray, float reach)
        {
            (Command, Vector2, float)? best = null;
            var nearest = reach;
            foreach (var pair in displays)
            {
                var d = pair.Value;
                if (d.Overlay == null || !d.Overlay.activeSelf || d.OverlayMesh == null) continue;
                var p = d.OverlayWorld;
                var uv = d.OverlayMesh.uv;
                var tris = d.OverlayMesh.triangles;
                if (uv.Length != p.Length) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 a = p[tris[t]], b = p[tris[t + 1]], c = p[tris[t + 2]];
                    // Moller-Trumbore, from both sides.
                    Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(ray.direction, e2);
                    var det = Vector3.Dot(e1, h);
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    var s = ray.origin - a;
                    var u = Vector3.Dot(s, h) / det;
                    if (u < 0f || u > 1f) continue;
                    var q = Vector3.Cross(s, e1);
                    var v = Vector3.Dot(ray.direction, q) / det;
                    if (v < 0f || u + v > 1f) continue;
                    var dist = Vector3.Dot(e2, q) / det;
                    if (dist <= 0f || dist >= nearest) continue;
                    nearest = dist;
                    best = (pair.Key, uv[tris[t]] * (1f - u - v) + uv[tris[t + 1]] * u + uv[tris[t + 2]] * v, dist);
                }
            }
            return best;
        }

        // A decal is a box (its mesh, resized) that paints wherever it cuts into surfaces facing along its up; blocks
        // only turn in 90 degree steps, so on a slope the box sits square to the grid and the image is the slope's
        // patch inside it. Same here: every surface triangle of the blocks it overlaps that faces its way is clipped
        // to the box (all in entity space), and what's left becomes display.
        static void AddDecalFootprint(BrickInstanceData decal, List<(BrickInstanceData Brick, Bounds Bounds)> solids, List<Vector3> local, List<int> tris)
        {
            var mesh = decal.Brick.VoxelMesh;
            if (mesh == null) return;
            var q = decal.Quaternion;
            var g = decal.GridSize;
            var lo = mesh.ResizePoint(decal.Scale, mesh.bounds.min);
            var hi = mesh.ResizePoint(decal.Scale, mesh.bounds.max);
            var center = decal.Position + q * (g * (lo + hi) * 0.5f);
            var half = g * (hi - lo) * 0.5f + Vector3.one * 0.001f;
            var axes = new[] { q * Vector3.right, q * Vector3.up, q * Vector3.forward };
            var halves = new[] { Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z) };
            var up = axes[1];
            var reach = new Bounds(center, Vector3.zero);
            foreach (var sx in new[] { -1f, 1f }) foreach (var sy in new[] { -1f, 1f }) foreach (var sz in new[] { -1f, 1f })
                reach.Encapsulate(center + axes[0] * (sx * halves[0]) + axes[1] * (sy * halves[1]) + axes[2] * (sz * halves[2]));
            // A decal marks the whole face it sits on: the face of the block straight under its centre, found by
            // casting from the top of the box down along -up. The game can't paint single faces and decals spill onto
            // neighbouring surfaces, so this lets one small decal per face do it, triangles and slopes included.
            // A decal centred on the seam between two blocks casts along their shared edge and can slip through, so a
            // miss is retried a few millimetres off centre.
            foreach (var shift in new[] { Vector3.zero, axes[0] * 0.01f, axes[2] * 0.01f, -axes[0] * 0.01f, -axes[2] * 0.01f, (axes[0] + axes[2]) * 0.01f })
                if (WholeFace(center + shift + up * halves[1], -up, 2f * halves[1], up, reach, solids, local, tris, center, axes, halves)) return;
            var polygon = new List<Vector3>(8);
            foreach (var (b, bounds) in solids)
            {
                if (!bounds.Intersects(reach)) continue;
                var bm = b.Brick.VoxelMesh;
                var buffer = bm.meshBuffer;
                var bq = b.Quaternion;
                var bg = b.GridSize;
                var bp = b.Position;
                for (int t = 0; t + 2 < buffer.triangles.Count; t += 3)
                {
                    Vector3 V(int k) => bp + bq * (bg * bm.ResizePoint(b.Scale, buffer.vertices[buffer.triangles[t + k]]));
                    Vector3 a = V(0), c1 = V(1), c2 = V(2);
                    var normal = Vector3.Cross(c1 - a, c2 - a);
                    if (normal.sqrMagnitude < 1e-10f || Vector3.Dot(normal.normalized, up) < 0.2f) continue;
                    polygon.Clear();
                    polygon.Add(a); polygon.Add(c1); polygon.Add(c2);
                    for (int axis = 0; axis < 3 && polygon.Count >= 3; axis++)
                    {
                        Clip(polygon, center, axes[axis], halves[axis]);
                        if (polygon.Count >= 3) Clip(polygon, center, -axes[axis], halves[axis]);
                    }
                    for (int k = 1; k + 1 < polygon.Count; k++) Subdivide(polygon[0], polygon[k], polygon[k + 1], local, tris);
                }
            }
        }

        // The nearest surface triangle facing "facing" hit by the ray, then every triangle of that block in the same
        // plane (a cube side or wedge slope is two triangles), and the faces of other blocks in that plane whose
        // middle (of the whole face) lies inside the decal's footprint: a wall built of many small blocks (the generated fighter's
        // cabin) needs one stretched decal, not one per block. False when the ray hits nothing.
        static bool WholeFace(Vector3 origin, Vector3 dir, float length, Vector3 facing, Bounds reach,
                              List<(BrickInstanceData Brick, Bounds Bounds)> solids, List<Vector3> local, List<int> tris,
                              Vector3 center, Vector3[] axes, float[] halves)
        {
            var best = float.MaxValue;
            BrickInstanceData hitBrick = default;
            var hitNormal = Vector3.zero;
            var hitPoint = Vector3.zero;
            foreach (var (b, bounds) in solids)
            {
                if (!bounds.Intersects(reach)) continue;
                foreach (var (a, c1, c2) in Triangles(b))
                {
                    var normal = Vector3.Cross(c1 - a, c2 - a);
                    if (normal.sqrMagnitude < 1e-10f || Vector3.Dot(normal.normalized, facing) < 0.2f) continue;
                    if (RayTriangle(origin, dir, a, c1, c2, out var distance) && distance <= length && distance < best)
                    {
                        best = distance;
                        hitBrick = b;
                        hitNormal = normal.normalized;
                        hitPoint = origin + dir * distance;
                    }
                }
            }
            if (best == float.MaxValue) return false;
            // Inside the footprint: within the decal box along its two flat axes (its up axis is the depth).
            bool InFootprint(Vector3 p) =>
                Mathf.Abs(Vector3.Dot(p - center, axes[0])) <= halves[0] + 0.01f && Mathf.Abs(Vector3.Dot(p - center, axes[2])) <= halves[2] + 0.01f;
            // Another block's face counts whole or not at all, by the middle of the face (all its triangles in that
            // plane): judged triangle by triangle, a decal straddling two faces took one triangle of the other face
            // and left a sawtooth.
            var face = new List<(Vector3, Vector3, Vector3)>();
            foreach (var (b, bounds) in solids)
            {
                bool hit = b.Equals(hitBrick) && b.gridPosition == hitBrick.gridPosition;
                if (!hit && !bounds.Intersects(reach)) continue;
                face.Clear();
                var middle = Vector3.zero;
                var area = 0f;
                foreach (var (a, c1, c2) in Triangles(b))
                {
                    var normal = Vector3.Cross(c1 - a, c2 - a);
                    if (normal.sqrMagnitude < 1e-10f || Vector3.Dot(normal.normalized, hitNormal) < 0.999f) continue;
                    if (Mathf.Abs(Vector3.Dot(a - hitPoint, hitNormal)) > 0.002f) continue;
                    face.Add((a, c1, c2));
                    var size = normal.magnitude;
                    middle += (a + c1 + c2) / 3f * size;
                    area += size;
                }
                if (face.Count == 0 || !hit && !InFootprint(middle / area)) continue;
                foreach (var (a, c1, c2) in face) Subdivide(a, c1, c2, local, tris);
            }
            return true;
        }

        // A block's triangles in entity space, its mesh resized, turned and placed.
        static IEnumerable<(Vector3, Vector3, Vector3)> Triangles(BrickInstanceData b)
        {
            var mesh = b.Brick.VoxelMesh;
            var buffer = mesh.meshBuffer;
            var q = b.Quaternion;
            var g = b.GridSize;
            var p = b.Position;
            for (int t = 0; t + 2 < buffer.triangles.Count; t += 3)
            {
                Vector3 V(int k) => p + q * (g * mesh.ResizePoint(b.Scale, buffer.vertices[buffer.triangles[t + k]]));
                yield return (V(0), V(1), V(2));
            }
        }

        // Moller-Trumbore, both sides.
        static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(dir, e2);
            var det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-8f) return false;
            var inv = 1f / det;
            var s = origin - a;
            var u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(dir, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(e2, q) * inv;
            return distance >= 0f;
        }

        // Sutherland-Hodgman: keep the part of the polygon where dot(p - center, axis) <= half.
        static void Clip(List<Vector3> polygon, Vector3 center, Vector3 axis, float half)
        {
            var input = polygon.ToArray();
            polygon.Clear();
            for (int i = 0; i < input.Length; i++)
            {
                var p = input[i];
                var n = input[(i + 1) % input.Length];
                var dp = Vector3.Dot(p - center, axis) - half;
                var dn = Vector3.Dot(n - center, axis) - half;
                if (dp <= 0f) polygon.Add(p);
                if ((dp < 0f && dn > 0f) || (dp > 0f && dn < 0f)) polygon.Add(p + (n - p) * (dp / (dp - dn)));
            }
        }

        // Cut a triangle into n x n smaller ones, n from its longest edge (measured in world units via space, if given).
        static void Subdivide(Vector3 a, Vector3 b, Vector3 c, List<Vector3> verts, List<int> tris, Transform space = null)
        {
            float Len(Vector3 u, Vector3 v) => space != null ? (space.TransformPoint(u) - space.TransformPoint(v)).magnitude : (u - v).magnitude;
            var longest = Mathf.Max(Len(a, b), Mathf.Max(Len(b, c), Len(c, a)));
            var n = Mathf.Clamp(Mathf.CeilToInt(longest / Mathf.Max(0.02f, CellSize)), 1, 16);
            var start = verts.Count;
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n - i; j++)
                    verts.Add(a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n));
            int Index(int i, int j) => start + i * (n + 1) - i * (i - 1) / 2 + j;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n - i; j++)
                {
                    tris.Add(Index(i, j)); tris.Add(Index(i + 1, j)); tris.Add(Index(i, j + 1));
                    if (i + j < n - 1) { tris.Add(Index(i + 1, j)); tris.Add(Index(i + 1, j + 1)); tris.Add(Index(i, j + 1)); }
                }
        }

        // The picture is drawn Overscan times wider than the view, with a margin past each edge: when the picture is
        // a frame behind the head (refresh every few frames), the faces at the edge being moved toward still land
        // inside it instead of off its edge (that flickered).
        public static float Overscan = 1.2f;

        // The head pose a display's next picture is drawn for, taken on frames that draw.
        static void CapturePoses()
        {
            var player = Dependency.RenderPipeline.playerCamera;
            if (player == null) return;
            foreach (var d in displays.Values)
            {
                var f = d.Feed;
                f.Eye = player.transform.position;
                f.Rotation = player.transform.rotation;
                // Overscan: the same view, Overscan times wider on each side.
                var widen = Matrix4x4.Scale(new Vector3(1f / Overscan, 1f / Overscan, 1f));
                f.Projection = widen * player.projectionMatrix;
                f.ViewProjection = f.Projection * player.worldToCameraMatrix;
                f.FieldOfView = 2f * Mathf.Atan(Mathf.Tan(0.5f * player.fieldOfView * Mathf.Deg2Rad) * Overscan) * Mathf.Rad2Deg;
                f.Aspect = player.aspect;
                f.Posed = true;
            }
        }

        // Every frame: move the surfaces with their blocks and point each vertex at where it lands in the picture,
        // seen from the pose the picture was drawn for. Vertices are pulled Lift toward that eye along the line of
        // sight (which doesn't change where they land) so they draw in front of the decals they cover.
        // How far surfaces stand off their faces, past the decals (about 1 cm proud).
        public static float Lift = 0.015f;

        static void UpdateDisplays()
        {
            if (displays.Count == 0) return;
            foreach (var d in displays.Values)
            {
                d.Go.SetActive(Show && d.Feed.Posed);
                if (!Show || !d.Feed.Posed || d.Local.Length == 0) { if (d.Overlay != null) d.Overlay.SetActive(false); continue; }
                var eye = d.Feed.Eye;
                var vp = d.Feed.ViewProjection;
                // Lifted straight off its face toward the side it faces (clear of decals, which stand about 1 cm proud),
                // or pushed into the wall when seen from behind, so it never shows through seams from outside.
                void Place(int k, Vector3 at, Vector3 normal)
                {
                    d.Raw[k] = at;
                    d.World[k] = at + normal * (Vector3.Dot(eye - at, normal) > 0f ? Lift : -0.03f);
                }
                foreach (var (entity, start, count) in d.Groups)
                {
                    if (entity == null)
                    {
                        var space = d.Controller != null && d.Controller.textMeshPro != null ? d.Controller.textMeshPro.transform : null;
                        if (space == null) continue;
                        for (int k = start; k < start + count; k++) Place(k, space.TransformPoint(d.Local[k]), space.TransformDirection(d.Normals[k]).normalized);
                    }
                    else
                    {
                        var td = entity.TransformData;
                        for (int k = start; k < start + count; k++) Place(k, td.LocalToWorld(d.Local[k]), td.rotation * d.Normals[k]);
                    }
                }
                for (int k = 0; k < d.World.Length; k++)
                {
                    var c = vp * new Vector4(d.World[k].x, d.World[k].y, d.World[k].z, 1f);
                    var w = Mathf.Max(1e-4f, c.w);
                    d.Uv[k] = new Vector2(0.5f + 0.5f * c.x / w, 0.5f + 0.5f * c.y / w);
                }
                d.Mesh.vertices = d.World;
                d.Mesh.uv = d.Uv;
                d.Mesh.RecalculateBounds();
                UpdateCanvas(d, eye);
            }
        }

        // A display's feed sees the player's whole view: same rotation and projection as the player camera, from the
        // eye or from its camera block.
        static bool PlaceDisplay(Feed feed)
        {
            if (!feed.Posed) return false;
            var from = feed.Eye;
            if (feed.Proxy != null && !CameraEye(feed.Proxy, out from)) return false;
            feed.Cam.transform.SetPositionAndRotation(from, feed.Rotation);
            feed.Cam.projectionMatrix = feed.Projection;
            if (feed.SkyCam != null)
                feed.SkyCam.projectionMatrix = Matrix4x4.Perspective(feed.FieldOfView, feed.Aspect, feed.SkyCam.nearClipPlane, feed.SkyCam.farClipPlane);
            return true;
        }

        // Testing: every decal on each display's construct, whether its paint matches, and how many surface triangles
        // it makes (WholeFace hit or clipped footprint).
        public static string DecalReport()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in displays.Values)
            {
                sb.AppendLine($"[{d.Mode}] colour {d.Color}, construct {d.Construct?.EntityName}");
                foreach (var e in d.Construct.Tree.ChildrensAndSelf)
                {
                    if (e == null || e.Bricks == null || !e.Bricks.brickDatas.IsCreated) continue;
                    var all = e.Bricks.brickDatas;
                    var solids = new List<(BrickInstanceData Brick, Bounds Bounds)>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var s = all[i];
                        if (s.IsValid && s.Brick != null && !s.Brick.IsDecal && s.Brick.VoxelMesh?.meshBuffer != null) solids.Add((s, s.GetAabb(true).Bounds));
                    }
                    for (int i = 0; i < all.Length; i++)
                    {
                        var b = all[i];
                        if (!b.IsValid || b.Brick == null || !b.Brick.IsDecal) continue;
                        var local = new List<Vector3>(); var tris = new List<int>();
                        AddDecalFootprint(b, solids, local, tris);
                        sb.AppendLine($"  decal {b.gridPosition} rot {b.rotation} scale {b.Scale} colour {b.color} match {SameColor(b.color, d.Color)} -> {tris.Count / 3} tris");
                    }
                    foreach (var (s, bounds) in solids) sb.AppendLine($"  solid {s.Brick.name} {s.gridPosition} rot {s.rotation} scale {s.Scale} lvl {s.gridLevel}");
                }
            }
            return sb.ToString();
        }

        // Testing: the unsubdivided surface triangles one decal makes (by its grid position), in its entity's space.
        public static string DecalTriangles(int x, int y, int z)
        {
            var sb = new System.Text.StringBuilder();
            var cell = CellSize;
            CellSize = 1000f;
            try
            {
                foreach (var d in displays.Values)
                    foreach (var e in d.Construct.Tree.ChildrensAndSelf)
                    {
                        if (e == null || e.Bricks == null || !e.Bricks.brickDatas.IsCreated) continue;
                        var all = e.Bricks.brickDatas;
                        var solids = new List<(BrickInstanceData Brick, Bounds Bounds)>();
                        for (int i = 0; i < all.Length; i++)
                        {
                            var s = all[i];
                            if (s.IsValid && s.Brick != null && !s.Brick.IsDecal && s.Brick.VoxelMesh?.meshBuffer != null) solids.Add((s, s.GetAabb(true).Bounds));
                        }
                        for (int i = 0; i < all.Length; i++)
                        {
                            var b = all[i];
                            if (!b.IsValid || b.Brick == null || !b.Brick.IsDecal || b.gridPosition != new Vector3i(x, y, z)) continue;
                            var local = new List<Vector3>(); var tris = new List<int>();
                            AddDecalFootprint(b, solids, local, tris);
                            sb.AppendLine($"decal {b.gridPosition} at {b.Position} up {b.Quaternion * Vector3.up}");
                            for (int t = 0; t + 2 < tris.Count; t += 3)
                                sb.AppendLine($"  {local[tris[t]]} {local[tris[t + 1]]} {local[tris[t + 2]]}");
                        }
                    }
            }
            finally { CellSize = cell; }
            return sb.ToString();
        }

        static string DisplayReport() => displays.Count == 0 ? "" :
            "; displays: " + string.Join(", ", displays.Values.Select(d => $"[{d.Mode}] {d.Blocks} block(s){(d.Feed.Proxy != null ? " from a camera" : "")}"));
    }
}
