using EvoCore;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Common.Rendering;
using HarmonyLib;
using Kino;
using Manager;
using TMPro;
using UI;
using UnityEngine;
using VoxelEngine;

#if !BEPINEX
[assembly: MelonLoader.MelonInfo(typeof(CockpitDisplays.Mod), "Cockpit Displays", "0.14.13", "Claude")]
[assembly: MelonLoader.MelonGame("Moonfire Entertainment", "Starship EVO")]
[assembly: MelonLoader.MelonAdditionalDependencies("EvoCore")]
#endif

namespace CockpitDisplays
{
    // Windowless cockpits (grew out of the CamProbe probe). A screen block (bricks 131/137) whose text holds a tag
    // shows a live view instead of its text:
    //  - [camera]: the view of the nearest Camera block (brick 11) on the same construct, looking out of its lens
    //    like entering the camera does; with no camera block, a view from just past the player ship's nose.
    //  - [window]: what the player would see through the screen if it were a window cut through the hull: rendered
    //    from the player's eye through the screen's corners (an off-axis projection), starting at the outer face of
    //    the hull in front of the screen, so the ship's insides are cut away but its nose and wings still show.
    //  - [xray]: the same, but with the screen's whole construct left out: the view straight through the ship.
    //  - [view]: a window seen from the nearest Camera block on the same construct instead of the player's eye: each
    //    screen still shows the slice of the world in its direction from the player's eye, but taken from the camera,
    //    as if the whole cockpit sat where the camera is. Nothing is cut away; the ship's own outside shows.
    //  Windows at angles to each other join up into one wraparound view.
    //  - [mode, COLOR] (e.g. [view, match]): display surfaces. Every face under a decal painted COLOR on the
    //    construct, and the screen itself, shows one seamless view, any shape (Displays.cs).
    // The main renderer is tied to the player camera (its culling carries "previously visible" state between frames),
    // so feeds copy the game's target-zoom camera instead: an EvoRenderer holding copies of the player's ship and the
    // entities close to it, drawn forward-lit into the feed's texture with RenderTrackingCam. Stars and planets come
    // from a plain copy of the sky camera's settings, drawn first each frame, running the game's own sky command
    // buffers (stars; and with Far on, planet rings, glow sprites and far terrain and ships).
    // Files: Mod.cs (settings, feeds, drawing), Screens.cs (tagged screens), Displays.cs (display surfaces),
    // Scripting.cs (the `displays` global for EvoCore's Lua scripts), DevRigs.cs (test constructs built through DevBridge).
    // Settings: the "Camera screens" tab in Mod Settings (resolution, sharpness, refresh rate, far objects).
    // DevBridge: CockpitDisplays.Mod.Report(), BuildRig(tag), BuildArc(tag), BuildDisplayRig(), RemoveRig(),
    // RemoveDisplayRig(), Ahead, Fov, ViewNear, Every, Show, Sky, Far.
    // Matched by name by the ModSettings mod, so this mod needs no reference to it.
    [System.AttributeUsage(System.AttributeTargets.Method)]
    class ModSettingsTabAttribute : System.Attribute
    {
        public ModSettingsTabAttribute(string title) { }
    }

#if BEPINEX
    [BepInEx.BepInPlugin("evo.cockpitdisplays", "Cockpit Displays", "0.14.13")]
    [BepInEx.BepInDependency("evo.core")]
#endif
    public partial class Mod : EvoMod
    {
        public const string CameraTag = "[camera]", WindowTag = "[window]", XRayTag = "[xray]", ViewTag = "[view]";
        public static int Every = 1;
        public static float Ahead = 3f, Fov = 70f, ViewNear = 0.1f;
        public static bool Show = true, Sky = true, Far = true;
        // The longer side of each screen's picture, in pixels.
        public static int Resolution = 512;
        // Display surfaces' pictures, as a fraction of the game's own resolution (they cover the whole view).
        public static float DisplayScale = 0.75f;
        static readonly float[] displayScales = { 0.5f, 0.75f, 1f };
        static Setting<float> displayScalePref;
        static readonly int[] resolutions = { 256, 512, 768, 1024, 1536, 2048 };
        static Setting<int> resolutionPref, everyPref;
        static Setting<bool> farPref;
        public static new ModLog Log;

        protected override void Init()
        {
            Log = base.Log;
            // Still the probe's category name, so settings carry over.
            var category = Settings.For("CamProbe", "Camera screens");
            resolutionPref = category.Add("Resolution", 512, "Pixels along the longer side of each screen's picture");
            everyPref = category.Add("RefreshEveryFrames", 1, "Redraw screens every this many frames (1 = every frame)");
            farPref = category.Add("FarObjects", true, "Draw planet rings, planet glow and far-away terrain and ships on screens");
            displayScalePref = category.Add("DisplayScale", 0.75f, "Display surfaces' picture size as a fraction of the game's resolution");
            DisplayScale = Mathf.Clamp(displayScalePref.Value, 0.25f, 1f);
            Resolution = Mathf.Clamp(resolutionPref.Value, 64, 4096);
            Every = Mathf.Clamp(everyPref.Value, 1, 10);
            Far = farPref.Value;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            ExposeToLua();
        }

        [ModSettingsTab("Camera screens")]
        static void DrawTab()
        {
            GUILayout.Label("Tag a screen's text with [camera], [window], [xray] or [view].");
            GUILayout.Label("[view, match]: put a decal painted the same swatch as the screen on each face that should be display; the whole face under the decal becomes one seamless display.");
            GUILayout.Label("Or name the swatch instead of match, e.g. [view, blue] (names below; or by position: colors 18, grays 4, tans 2).");
            DrawSwatches();
            GUILayout.Label($"Single tagged screens ([camera], [window], [xray], [view]): picture resolution, longer side {Resolution} px");
            GUILayout.BeginHorizontal();
            foreach (var r in resolutions)
                if (GUILayout.Toggle(Resolution == r, " " + r, GUILayout.Width(70f)) && Resolution != r) SetResolution(r);
            GUILayout.EndHorizontal();
            GUILayout.Label($"Display surfaces ([mode, COLOR] decals): sharpness {DisplayScale:P0} of the game's own resolution");
            GUILayout.BeginHorizontal();
            foreach (var r in displayScales)
                if (GUILayout.Toggle(Mathf.Approximately(DisplayScale, r), " " + r.ToString("P0"), GUILayout.Width(70f)) && !Mathf.Approximately(DisplayScale, r)) SetDisplayScale(r);
            GUILayout.EndHorizontal();
            GUILayout.Label(Every == 1 ? "Refresh: every frame" : $"Refresh: every {Every} frames");
            var every = Mathf.RoundToInt(GUILayout.HorizontalSlider(Every, 1f, 6f, GUILayout.Width(300f)));
            if (every != Every) { Every = everyPref.Value = every; Settings.SaveAll(); }
            var far = GUILayout.Toggle(Far, " Planet rings, planet glow and far-away objects");
            if (far != Far) { Far = farPref.Value = far; Settings.SaveAll(); }
            GUILayout.Label("Far-away objects only show where the game's own view also sees them.");
            GUILayout.Label(Report());
        }

        static void SetDisplayScale(float r)
        {
            DisplayScale = displayScalePref.Value = r;
            Settings.SaveAll();
            foreach (var screen in displays.Keys.ToList()) DropDisplay(screen, displays[screen]);
            nextScan = 0f;
        }

        static void SetResolution(int r)
        {
            Resolution = resolutionPref.Value = r;
            Settings.SaveAll();
            // Every picture is made again at the new size on the next screen scan.
            foreach (var screen in screens.Keys.ToList()) Detach(screen, screens[screen]);
            foreach (var screen in displays.Keys.ToList()) DropDisplay(screen, displays[screen]);
            foreach (var feed in feeds.ToList()) DropFeed(feed);
            nextScan = 0f;
        }
        public static string Status = "waiting for a world";
        public static double VoxelMs, SkyMs;

        // A copy of the nearby entities to draw feeds from, leaving one construct out (null: leave nothing out).
        class Scene
        {
            public ParentEntity Without;
            public EvoRenderer Renderer;
            public readonly HashSet<ParentEntity> Added = new HashSet<ParentEntity>();
            public CancellationTokenSource Cancel = new CancellationTokenSource();
        }

        // One per camera block in use (Source; null is the view past the ship's nose), or one per window screen.
        class Feed
        {
            public EnterCommandCamera Source;
            public Transform Window;
            public ParentEntity Construct;
            public bool XRay, View;
            public EnterCommandCamera Proxy; // [view]: the camera block the view is taken from
            public float HullAt, NextHullCheck; // [window]: how far past the screen the hull's outer face is
            public Scene Scene;
            public Camera Cam, SkyCam;
            public HiZBuffer HiZ;
            public RenderTexture Rt;
            public Material Material;
            public bool SkyDrawn, Used, Stars, FarAdded;
            public ForwardCommandBuffers Voxels; // the feed's voxel draws, recorded in the voxel hook, run after its sky
            public bool VoxelsReady;
            public UnityEngine.Rendering.CommandBuffer Rings, Depth; // this feed's planet rings (FillRings) and far terrain depth pre-pass (FillDepth)
            public Display Display; // a display surface set's feed: the player's whole view
            public Vector3 Eye; // displays: the head pose the picture is drawn for (CapturePoses)
            public Quaternion Rotation;
            public Matrix4x4 Projection, ViewProjection;
            public float FieldOfView, Aspect;
            public bool Posed;
            public bool Off; // every screen showing it is switched off (Screens.cs, ApplyPower): not drawn
        }

        static readonly List<Feed> feeds = new List<Feed>();
        static readonly List<Scene> scenes = new List<Scene>();
        // The game draws the stars with a command buffer on its sky camera (refilled every frame); feeds' sky cameras
        // run the same buffer. Its other buffers draw far-away ships for that camera's own view and are left out.
        static readonly AccessTools.FieldRef<World.Starfield.StarfieldParticle, UnityEngine.Rendering.CommandBuffer> starBuffer =
            AccessTools.FieldRefAccess<World.Starfield.StarfieldParticle, UnityEngine.Rendering.CommandBuffer>("commandBuffer");
        static DecalsTiledRenderingBuffers decals;
        static MaterialPropertyBlock props;
        static bool ready;
        static float nextSync, nextScan;
        static int frame;

        static ParentEntity Ship => Dependency.PlayerManager != null ? Dependency.PlayerManager.LastSpaceship : null;

        static bool Setup()
        {
            if (ready) return true;
            if (Dependency.RenderPipeline == null || Dependency.RenderPipeline.targetZoomCamera == null || UI_TrackingCamera.instance == null) return false;
            decals = new DecalsTiledRenderingBuffers();
            props = new MaterialPropertyBlock();
            ready = true;
            return true;
        }

        static Scene GetScene(ParentEntity without)
        {
            var scene = scenes.FirstOrDefault(s => s.Without == without);
            if (scene != null) return scene;
            scene = new Scene { Without = without, Renderer = new EvoRenderer("CockpitDisplays", 1f, instantBufferUpdate: false, 512) { UseNewInstanceId = false } };
            scenes.Add(scene);
            nextSync = 0f;
            return scene;
        }

        static Feed NewFeed(int width, int height)
        {
            var feed = new Feed();
            var go = Object.Instantiate(Dependency.RenderPipeline.targetZoomCamera.gameObject);
            go.name = "Cockpit Displays Camera";
            Object.DontDestroyOnLoad(go);
            feed.Cam = go.GetComponent<Camera>();
            feed.Cam.enabled = false;
            var glitch = go.GetComponent<DigitalGlitch>();
            if (glitch != null) glitch.enabled = false;
            feed.HiZ = go.GetComponent<HiZBuffer>();
            // No alpha channel: the skybox leaves alpha at 0, which would make the screen picture see-through.
            feed.Rt = new RenderTexture(width, height, 24, RenderTextureFormat.RGB111110Float) { name = "Cockpit Displays Feed", filterMode = FilterMode.Bilinear, antiAliasing = 2, wrapMode = TextureWrapMode.Clamp };
            feed.Rt.Create();
            feed.Cam.targetTexture = feed.Rt;
            feed.Cam.depthTextureMode = DepthTextureMode.None;
            var sky = Dependency.RenderPipeline.skyCamera;
            if (sky != null)
            {
                var skyGo = new GameObject("Cockpit Displays Sky Camera");
                Object.DontDestroyOnLoad(skyGo);
                feed.SkyCam = skyGo.AddComponent<Camera>();
                feed.SkyCam.CopyFrom(sky);
                feed.SkyCam.enabled = false;
                feed.SkyCam.targetTexture = feed.Rt;
            }
            feed.Material = new Material(Shader.Find("UI/Default")) { name = "Cockpit Displays Feed", mainTexture = feed.Rt };
            feeds.Add(feed);
            return feed;
        }

        static Feed CameraFeed(EnterCommandCamera source)
        {
            var feed = feeds.FirstOrDefault(f => f.Window == null && f.Display == null && f.Source == source);
            if (feed != null) return feed;
            feed = NewFeed(Resolution, Mathf.Max(32, Resolution * 9 / 16));
            feed.Source = source;
            feed.Scene = GetScene(null);
            return feed;
        }

        // A window's texture has the screen's shape, so nothing is stretched.
        static Feed WindowFeed(Transform window, ParentEntity construct, bool xray, bool view)
        {
            var size = window.lossyScale;
            var aspect = Mathf.Abs(size.x / size.y);
            var width = aspect >= 1f ? Resolution : Mathf.RoundToInt(Resolution * aspect);
            var height = aspect >= 1f ? Mathf.RoundToInt(Resolution / aspect) : Resolution;
            var feed = NewFeed(Mathf.Clamp(width, 32, 4096), Mathf.Clamp(height, 32, 4096));
            feed.Window = window;
            feed.Construct = construct;
            feed.XRay = xray;
            feed.View = view;
            feed.Scene = GetScene(xray ? construct : null);
            return feed;
        }

        // How far past the screen, along the way it looks, the outer face of the hull in front of it is: cast out to
        // the wall's inner face, then back from further out to its outer face (walls up to 8 m thick). Only the
        // screen's own construct counts. 0 when there's no hull in that direction.
        static float HullAhead(Vector3 from, Vector3 into, ParentEntity construct)
        {
            var ray = new VoxelRaycast();
            if (!FirstHitOn(ray, from, into, 200f, construct, out var inner)) return 0f;
            foreach (var thickness in new[] { 2f, 4f, 8f })
            {
                var back = from + into * (inner + thickness);
                if (FirstHitOn(ray, back, -into, thickness, construct, out var outer) && outer > 0.05f)
                    return inner + thickness - outer;
            }
            return inner;
        }

        static bool FirstHitOn(VoxelRaycast ray, Vector3 from, Vector3 dir, float distance, ParentEntity construct, out float at)
        {
            at = 0f;
            for (int i = 0; i < 8 && distance > 0f; i++)
            {
                if (!ray.Raycast(from, dir, out var hit, distance)) return false;
                if (hit.HasEntityReference && hit.Entity != null && hit.Entity.Parent == construct) { at += hit.distance; return true; }
                var step = hit.distance + 0.02f;
                at += step; from += dir * step; distance -= step;
            }
            return false;
        }

        // Each scene holds the ship and every entity the game counts as close, minus its left-out construct; it's
        // re-copied when that set changes.
        static void Sync()
        {
            var close = new HashSet<ParentEntity>(VoxelRenderingManager.EntitiesClose.Where(e => e != null));
            if (Ship != null) close.Add(Ship);
            foreach (var scene in scenes)
            {
                var want = new HashSet<ParentEntity>(close.Where(e => e != scene.Without));
                if (want.SetEquals(scene.Added)) continue;
                scene.Cancel.Cancel();
                scene.Cancel.Dispose();
                scene.Cancel = new CancellationTokenSource();
                scene.Renderer.Reset();
                scene.Added.Clear();
                foreach (var e in want)
                {
                    scene.Added.Add(e);
                    _ = e.AddToBrickRendererAsync(scene.Renderer, scene.Cancel.Token);
                }
            }
        }

        static bool Place(Feed feed) => feed.Display != null ? PlaceDisplay(feed) : feed.Window != null ? PlaceWindow(feed) : PlaceCamera(feed);

        // A camera block's feed looks out of its lens, the way entering the camera does: along the block's right axis
        // (its "Forward" in build mode), with the block's up as the picture's up.
        static bool PlaceCamera(Feed feed)
        {
            if (feed.Source != null)
            {
                if (!CameraEye(feed.Source, out var eye)) return false;
                var bc = feed.Source.brickCoord;
                feed.Cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(bc.WorldRight, bc.WorldUp));
            }
            else
            {
                var ship = Ship;
                if (ship == null) return false;
                var bounds = ship.ParentBounds;
                var nose = bounds.center + Vector3.forward * (bounds.extents.z + Ahead);
                feed.Cam.transform.SetPositionAndRotation(ship.transform.TransformPoint(nose), ship.transform.rotation);
            }
            feed.Cam.ResetProjectionMatrix();
            feed.Cam.fieldOfView = Fov;
            if (feed.SkyCam != null) { feed.SkyCam.ResetProjectionMatrix(); feed.SkyCam.fieldOfView = Fov; feed.SkyCam.aspect = (float)feed.Rt.width / feed.Rt.height; }
            return true;
        }

        // Where a camera block's view is taken from: its lens, the point entering the camera puts the player's view.
        static bool CameraEye(EnterCommandCamera camera, out Vector3 eye)
        {
            eye = default;
            var bc = camera.brickCoord;
            if (bc.entity == null) return false;
            eye = bc.TransformData.LocalToWorld(bc.Position + bc.quaternion * new Vector3(7f / 32f, 0.125f, 0f));
            return true;
        }

        // A window: the camera sits at the player's eye, faces the screen square on, and its frustum is cut to pass
        // exactly through the screen's corners, with the near plane on the screen (nothing between the eye and the
        // screen is drawn on it). The sky camera gets the same frustum scaled out to its own near plane.
        // [view] keeps that frustum (its directions come from the player's eye) but moves it to the camera block.
        static bool PlaceWindow(Feed feed)
        {
            var window = feed.Window;
            if (window == null) return false;
            var eye = Dependency.RenderPipeline.playerCamera.transform.position;
            var into = window.forward;
            if (Vector3.Dot(into, window.position - eye) < 0f) into = -into;
            var proxied = feed.View && feed.Proxy != null;
            var from = eye;
            if (proxied && !CameraEye(feed.Proxy, out from)) return false;
            var cam = feed.Cam.transform;
            cam.SetPositionAndRotation(from, Quaternion.LookRotation(into, window.up));
            var corners = new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(-0.5f, 0.5f), new Vector3(0.5f, 0.5f) }
                .Select(c => window.TransformPoint(c) - eye).ToArray();
            var screenAt = Vector3.Dot(into, corners[0]);
            if (screenAt < 0.05f) return false; // the eye is level with or behind the screen
            // [window] starts the view at the hull's outer face, so the frustum is the screen's, scaled out to it.
            if (!feed.XRay && !proxied && Time.unscaledTime >= feed.NextHullCheck)
            {
                feed.NextHullCheck = Time.unscaledTime + 1f;
                feed.HullAt = feed.Construct != null ? HullAhead(window.position, into, feed.Construct) : 0f;
            }
            // [view] starts just past the camera, so nearby parts of the ship show.
            var near = proxied ? ViewNear : screenAt + (feed.XRay ? 0f : feed.HullAt);
            var scale = near / screenAt;
            float left = scale * corners.Min(c => Vector3.Dot(cam.right, c)), right = scale * corners.Max(c => Vector3.Dot(cam.right, c));
            float bottom = scale * corners.Min(c => Vector3.Dot(cam.up, c)), top = scale * corners.Max(c => Vector3.Dot(cam.up, c));
            feed.Cam.projectionMatrix = Matrix4x4.Frustum(left, right, bottom, top, near, Mathf.Max(near + 1f, feed.Cam.farClipPlane));
            // The quad's own left and right may run opposite to the camera's; mirror the picture to match.
            feed.Material.mainTextureScale = new Vector2(Vector3.Dot(window.right, cam.right) < 0f ? -1f : 1f, 1f);
            feed.Material.mainTextureOffset = new Vector2(feed.Material.mainTextureScale.x < 0f ? 1f : 0f, 0f);
            if (feed.SkyCam != null)
            {
                var skyNear = feed.SkyCam.nearClipPlane;
                var k = skyNear / near;
                feed.SkyCam.projectionMatrix = Matrix4x4.Frustum(left * k, right * k, bottom * k, top * k, skyNear, feed.SkyCam.farClipPlane);
            }
            return true;
        }

        static bool Due => Show && ready && frame % Mathf.Max(1, Every) == 0;

        // Same moment the game draws its target-zoom view: after the frame's voxel buffers are recorded.
        [HarmonyPatch(typeof(UI_TrackingCamera), nameof(UI_TrackingCamera.DoOnRender))]
        static class Hook
        {
            static void Postfix()
            {
                if (!Show) return;
                try
                {
                    if (!Setup()) return;
                    if (Time.unscaledTime >= nextSync) { nextSync = Time.unscaledTime + 2f; Sync(); }
                    if (!Due) return;
                    var watch = Stopwatch.StartNew();
                    foreach (var feed in feeds.Where(f => f.Used && !f.Off))
                    {
                        if (!Place(feed)) continue;
                        if (feed.Voxels == null) feed.Voxels = new ForwardCommandBuffers(null);
                        feed.Voxels.Clear();
                        if (feed.HiZ != null) feed.HiZ.AddToCommandBuffer(feed.Voxels.cbDepth, feed.Rt);
                        feed.Scene.Renderer.RenderTrackingCam(feed.Voxels, props, feed.Cam.transform.position, decals, feed.Cam);
                        if (AfterSky) feed.VoxelsReady = true;
                        else DrawVoxels(feed);
                    }
                    VoxelMs = VoxelMs * 0.95 + watch.Elapsed.TotalMilliseconds * 0.05;
                }
                catch (System.Exception e)
                {
                    Show = false;
                    Status = "failed: " + e.Message;
                    Log.Error(e.ToString());
                }
            }
        }

        // Timing, all around the game's sky camera, which renders first each frame (depth -2), sits under the player
        // camera and carries the OnRenderCaller that runs the voxel hook:
        //  - onPreCull: the head pose is taken (the frame's final one; taken in OnLateUpdate, before the game moved its
        //    camera, displays lagged a frame and slid while moving) and display surfaces are placed.
        //  - OnPreRender (the voxel hook): the game fills its command buffers for the frame, and each feed records
        //    its voxel draws.
        //  - onPostRender (AfterSky): each feed's sky is rendered, then its voxels drawn over it. The game empties its
        //    command buffers in Update and fills them in that OnPreRender, so a sky rendered any earlier ran empty
        //    far buffers: no planet bodies, glow sprites or far terrain. Unity won't start a render from inside
        //    OnPreRender, so this is the first moment the buffers are full. The player camera renders after, so the
        //    displays still show this frame.
        // With AfterSky off the sky is rendered at onPreCull (without far objects) and voxels drawn in the hook.
        public static bool PreCull = true, AfterSky = true;
        static int workedFrame = -1, postFrame = -1;

        protected override void LateTick()
        {
            frame++;
            if (!PreCull) FrameWork();
        }

        static void OnPreCull(Camera camera)
        {
            if (!PreCull || workedFrame == Time.frameCount) return;
            var pipeline = Dependency.RenderPipeline;
            if (pipeline == null || camera != pipeline.skyCamera) return;
            workedFrame = Time.frameCount;
            FrameWork();
        }

        static void FrameWork()
        {
            try
            {
                ApplyPower();
                if (Due) CapturePoses();
                UpdateDisplays();
            }
            catch (System.Exception e) { Status = "displays failed: " + e.Message; Log.Error("displays: " + e); }
            if (!AfterSky) DrawSkies();
        }

        static void OnPostRender(Camera camera)
        {
            if (!AfterSky || postFrame == Time.frameCount) return;
            var pipeline = Dependency.RenderPipeline;
            if (pipeline == null || camera != pipeline.skyCamera) return;
            postFrame = Time.frameCount;
            DrawSkies();
            foreach (var feed in feeds.Where(f => f.VoxelsReady))
            {
                feed.VoxelsReady = false;
                try { DrawVoxels(feed); }
                catch (System.Exception e) { Status = "voxels failed: " + e.Message; Log.Error("voxels: " + e); }
            }
        }

        static void DrawSkies()
        {
            if (!Due || !Sky) return;
            try
            {
                var watch = Stopwatch.StartNew();
                var stars = World.Starfield.StarfieldParticle.instance != null ? starBuffer(World.Starfield.StarfieldParticle.instance) : null;
                // The star shader grows each star by a fixed amount on the picture, whatever the zoom, so a window
                // showing a narrow slice of sky drew them too big (and bigger the closer the player stood). Scale
                // _Size by the feed's zoom against the game's sky camera, just for the feed, so stars keep the size
                // they have in the player's own view.
                var starMaterial = Manager.AssetRepoGalaxy.Material_Starfield;
                var baseSize = starMaterial != null ? starMaterial.GetFloat("_Size") : 0f;
                var mainZoom = Dependency.RenderPipeline.skyCamera.projectionMatrix.m00;
                try
                {
                    foreach (var feed in feeds.Where(f => f.Used && !f.Off && f.SkyCam != null))
                    {
                        if (!feed.Stars && stars != null) { feed.SkyCam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterSkybox, stars); feed.Stars = true; }
                        if (feed.FarAdded != Far) SetFar(feed, Far);
                        FillRings(feed);
                        if (!Place(feed)) continue;
                        feed.SkyCam.transform.SetPositionAndRotation(feed.Cam.transform.position, feed.Cam.transform.rotation);
                        if (starMaterial != null && mainZoom > 0f) starMaterial.SetFloat("_Size", baseSize * feed.SkyCam.projectionMatrix.m00 / mainZoom);
                        CullFar(feed);
                        FillDepth(feed);
                        var saved = HideNearDepth();
                        try { feed.SkyCam.Render(); }
                        finally { RestoreNearDepth(saved); }
                        feed.SkyDrawn = true;
                    }
                }
                finally
                {
                    if (starMaterial != null) starMaterial.SetFloat("_Size", baseSize);
                }
                SkyMs = SkyMs * 0.95 + watch.Elapsed.TotalMilliseconds * 0.05;
            }
            catch (System.Exception e)
            {
                Sky = false;
                Status = "sky failed: " + e.Message;
                Log.Error("sky: " + e);
            }
        }

        // The game's sky camera also draws the far half of the world: "Opaque Far Camera" before opaques and
        // "Transparent Far Camera" before transparents (planet rings, planet glow sprites, far terrain, oceans and
        // ships). They're filled each frame with world-space draws, so a feed's sky camera can run them too; only
        // what the game's own view culled away is missing.
        static readonly UnityEngine.Rendering.CameraEvent[] farEvents = { UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, UnityEngine.Rendering.CameraEvent.BeforeForwardAlpha };

        static void SetFar(Feed feed, bool on)
        {
            var sky = Dependency.RenderPipeline.skyCamera;
            foreach (var e in farEvents)
                foreach (var cb in sky.GetCommandBuffers(e).Where(b => b.name.EndsWith("Far Camera")))
                {
                    feed.SkyCam.RemoveCommandBuffer(e, cb);
                    if (on) feed.SkyCam.AddCommandBuffer(e, cb);
                }
            feed.FarAdded = on;
        }

        // What a feed's sky sees is decided for the player's own view: far terrain and ships are GPU-culled each frame
        // (EvoRenderer.DoCullingFarCamera, in the sky camera's "Depth Texture" buffer) against the player's frustum and
        // depth, so things behind a cockpit's walls or off the player's screen were missing on displays. Before each
        // feed's sky renders, run that culling again for the feed's camera, with an empty Hi-Z map so nothing near
        // the player hides anything. The game's own sky camera renders after the feeds and culls for the player again
        // (its pre-pass sets the matrices back), so the player's view is untouched.
        public static bool FeedCulling = true, ClearNearDepth = true;
        static readonly AccessTools.FieldRef<ComputeShader> cullingCS = AccessTools.StaticFieldRefAccess<ComputeShader>(AccessTools.Field(typeof(EvoRenderer), "cullingCS"));
        static UnityEngine.Rendering.CommandBuffer cullBuffer;
        static Texture2D farDepth;

        static void CullFar(Feed feed)
        {
            var renderer = GlobalRenderer.renderer;
            var cs = cullingCS();
            if (!FeedCulling || !Far || renderer == null || cs == null) return;
            if (cullBuffer == null) cullBuffer = new UnityEngine.Rendering.CommandBuffer { name = "Cockpit Displays Far Culling" };
            cullBuffer.Clear();
            var cam = feed.SkyCam;
            var view = cam.worldToCameraMatrix;
            var viewProjection = cam.projectionMatrix * view;
            cullBuffer.SetComputeMatrixParam(cs, "_UNITY_MATRIX_VP", viewProjection);
            cullBuffer.SetComputeMatrixParam(cs, "_UNITY_MATRIX_VP_FAR", viewProjection);
            cullBuffer.SetComputeMatrixParam(cs, "_UNITY_MATRIX_V", view);
            cullBuffer.SetComputeVectorParam(cs, "_CamPosition", cam.transform.position);
            var hiZ = Shader.GetGlobalTexture("_HiZMap");
            cullBuffer.SetGlobalTexture("_HiZMap", FarDepth);
            renderer.DoCullingFarCamera(cullBuffer, new DrawCallParams { trs = Matrix4x4.identity, camPosObjectSpace = cam.transform.position, isFarCam = true }, cam);
            Graphics.ExecuteCommandBuffer(cullBuffer);
            if (hiZ != null) Shader.SetGlobalTexture("_HiZMap", hiZ);
        }

        // Far terrain (planet bodies seen from a distance) is drawn in two steps on the game's sky camera: a depth
        // pre-pass in its "Depth Texture" buffer (before the depth texture), then colour in "Opaque Far Camera". A feed's
        // sky camera borrows the colour buffer but can't borrow the pre-pass buffer, which also re-culls for the
        // player, so planets were missing. Each feed records its own pre-pass: the terrain depth draw, over the
        // feed's own culling (CullFar).
        static MaterialPropertyBlock depthProps;

        static void FillDepth(Feed feed)
        {
            if (feed.Depth == null)
            {
                feed.Depth = new UnityEngine.Rendering.CommandBuffer { name = "Cockpit Displays Far Depth" };
                feed.SkyCam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeDepthTexture, feed.Depth);
            }
            feed.Depth.Clear();
            var renderer = GlobalRenderer.renderer;
            if (!FeedCulling || !Far || renderer == null || DebugToggleRepo.DeactivateTerrainRendering.isActive) return;
            if (depthProps == null) depthProps = new MaterialPropertyBlock();
            renderer.pass_terrain.DrawTerrain(feed.Depth, new DrawCallParams { trs = Matrix4x4.identity, camPosObjectSpace = Vector3.zero, props = depthProps }, TerrainShaderPass.depth, isFar: true);
        }

        // Depth textures of the player's own view that far shaders may test against: while a feed's sky renders they
        // read as empty (0 is the far plane with reversed depth), so walls in front of the player hide nothing.
        static readonly string[] nearDepths = { "_CameraDepthTexture", "_VoxelCameraGBufferDepth", "_HiZMap" };

        static Texture2D FarDepth
        {
            get
            {
                if (farDepth != null) return farDepth;
                farDepth = new Texture2D(1, 1, TextureFormat.RFloat, false) { name = "Cockpit Displays Far Depth" };
                farDepth.SetPixel(0, 0, Color.clear);
                farDepth.Apply();
                return farDepth;
            }
        }

        static Texture[] HideNearDepth()
        {
            if (!ClearNearDepth) return null;
            var saved = new Texture[nearDepths.Length];
            for (int i = 0; i < nearDepths.Length; i++)
            {
                saved[i] = Shader.GetGlobalTexture(nearDepths[i]);
                Shader.SetGlobalTexture(nearDepths[i], FarDepth);
            }
            return saved;
        }

        static void RestoreNearDepth(Texture[] saved)
        {
            if (saved == null) return;
            for (int i = 0; i < nearDepths.Length; i++)
                if (saved[i] != null) Shader.SetGlobalTexture(nearDepths[i], saved[i]);
        }

        // Planet rings: the far buffers only hold a ring's depth and backdrop passes (SolarBodyRenderer passes 7 and
        // 3/4); the ring itself (pass 6) goes into the player camera's "Voxel Entity Transparent Base", which also
        // holds glass and render target switches made for that camera and can't be borrowed. So each feed's sky
        // camera draws the rings itself before its transparents, the way SolarBodyRenderer does from outside a ring.
        static readonly AccessTools.FieldRef<World.SolarBodyRenderer, bool> insideRing = AccessTools.FieldRefAccess<World.SolarBodyRenderer, bool>("isInsideRing");
        static MaterialPropertyBlock ringProps;

        static void FillRings(Feed feed)
        {
            if (feed.Rings == null)
            {
                feed.Rings = new UnityEngine.Rendering.CommandBuffer { name = "Cockpit Displays Rings" };
                feed.SkyCam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardAlpha, feed.Rings);
            }
            feed.Rings.Clear();
            if (!Far || DebugToggleRepo.DisableRingRendering.isActive || AssetRepoPlanets.RingMesh == null) return;
            if (ringProps == null) ringProps = new MaterialPropertyBlock();
            foreach (var body in World.PlanetarySystem.SolarBodies)
            {
                if (body == null || !body.hasPlanetaryRing || body.instanceGo == null) continue;
                if (body.Renderer != null && insideRing(body.Renderer)) continue;
                ringProps.SetInt("_PlanetaryRingId", body.planetIdForShader);
                var trs = Matrix4x4.TRS(body.instanceGo.transform.position, body.ringData.rotation, Vector3.one);
                // DrawMesh copies the property block, so one block serves every planet.
                feed.Rings.DrawMesh(AssetRepoPlanets.RingMesh, trs, AssetRepoPlanets.RingMaterial, 0, 6, ringProps);
            }
        }

        // RenderingUtils.RenderVirtualCamera, but keeping the sky's colour when it was drawn this frame. Depth is
        // always cleared: the sky camera's near and far planes (200 m to 1e13 m) aren't the voxel camera's.
        static void DrawVoxels(Feed feed)
        {
            var current = Camera.current;
            Camera.SetupCurrent(feed.Cam);
            Graphics.SetRenderTarget(feed.Rt);
            GL.Clear(clearDepth: true, clearColor: !feed.SkyDrawn, Color.black, 1f);
            feed.SkyDrawn = false;
            GL.PushMatrix();
            GL.LoadProjectionMatrix(feed.Cam.projectionMatrix);
            GL.modelview = feed.Cam.worldToCameraMatrix;
            foreach (var cb in feed.Voxels.Buffers) Graphics.ExecuteCommandBuffer(cb);
            Graphics.SetRenderTarget(null);
            Camera.SetupCurrent(current);
            GL.PopMatrix();
        }

        static void DropFeed(Feed feed)
        {
            feeds.Remove(feed);
            if (feed.Cam != null) Object.Destroy(feed.Cam.gameObject);
            if (feed.SkyCam != null) Object.Destroy(feed.SkyCam.gameObject);
            if (feed.Rt != null) feed.Rt.Release();
            feed.Rings?.Release();
            feed.Depth?.Release();
        }

        public static string Report() => $"{Status}; voxels {VoxelMs:0.00} ms, sky {SkyMs:0.00} ms per frame for all feeds";
    }
}
