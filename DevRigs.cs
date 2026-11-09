using System.Collections.Generic;
using System.Linq;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace CockpitDisplays
{
    // Test constructs for checking the views through DevBridge; nothing in play uses them. Each is built as constructs
    // of its own in front of the player, and its screens are tagged once the game has made them.
    public partial class Mod
    {
        // Test rig, a construct of its own in front of the player (nothing on their ships changes), laid out as
        // <-camera [wall] screen->: a 3x3 wall of 1 m hull, a screen in the middle of the player's side, and on top
        // a Camera block looking away, at a plus sign of hull 6 m beyond the wall. The player
        // can't see the plus sign through the wall; if the screen shows it, the screen is seeing through the wall.
        // The plus sign is a construct of its own: a window leaves its screen's construct out of the view.
        // tag: what the screen gets ([camera], [window] or [xray]). A block of the rig itself sticks out 3 m behind the
        // wall, off to the right: [window] shows it, [xray] doesn't.
        const string RigName = "CockpitDisplays Rig", MarkerName = "CockpitDisplays Marker";
        static ParentEntity rig;
        static string rigTag;
        static int rigScreenCount;

        // Called from each screen scan: a new rig's screens get its tag. Built bricks get their commands when the game
        // processes the build, not straight away.
        static void TagRigScreens(IEnumerable<Command> commands)
        {
            if (rigTag == null || rig == null) return;
            var rigScreens = commands.OfType<CommandInputText>().Where(c => c.brickCoord.entity != null && c.brickCoord.entity.Parent == rig).ToList();
            foreach (var rigScreen in rigScreens.Where(c => c.data.text != rigTag))
                rigScreen.SetData(new CommandInputText.InputTextData { text = rigTag, fontSize = 15f });
            if (rigScreens.Count >= rigScreenCount) rigTag = null;
        }

        public static string BuildRig(string tag)
        {
            if (rig != null) return "a rig already exists; RemoveRig() first";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var origin = view.position + view.forward * 6f - view.up * 1.5f;
            // The rig's +z points away from the player, so its -z face is the one they see.
            var rotation = Quaternion.LookRotation(view.forward, view.up);
            rig = player.NetworkSetup.ServerCreateEntity(Hull(0, 0, 0), new WorldTransform(origin, rotation), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity (not the server?)";
            rig.EntityName = RigName;
            rig.name = RigName;
            for (int x = -1; x <= 1; x++)
                for (int y = 0; y <= 2; y++)
                    if (x != 0 || y != 0) Build(Hull(x, y, 0));
            // A 1 m cube's grid position is its centre and parts stand on theirs along their own up (16 = half a
            // metre in 1/32 m units). The screen's panel stands along its up and its text reads along its right
            // axis, so turned -90 degrees about up it faces the player, standing just in front of the wall.
            Build(Part(TabLogic.SCREEN_TEXT_INPUT, new Vector3i(0, 32, -20), Quaternion.Euler(0f, -90f, 0f)));
            Build(Lens());
            Build(Hull(1, 1, 3));
            var marker = player.NetworkSetup.ServerCreateEntity(Hull(0, 1, 0), new WorldTransform(origin + rotation * new Vector3(0f, 0f, 7f), rotation), EntityType.spaceship);
            if (marker != null)
            {
                marker.EntityName = marker.name = MarkerName;
                foreach (var (x, y) in new[] { (-1, 1), (1, 1), (0, 0), (0, 2) })
                    player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = Hull(x, y, 0), entityID = marker.ID });
            }
            rigTag = tag;
            rigScreenCount = 1;
            nextScan = 0f;
            return $"rig built 6 m ahead as {RigName}; its screen gets {rigTag} once the game has made it";

            void Build(BrickInstanceData brick) => player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = rig.ID });
        }

        // Angled test: a U-shaped booth 5 m ahead, open toward the player. Three [window] screens across its front
        // wall and two on the inner face of each side wall, at right angles to the front (blocks only turn in 90
        // degree steps). Behind it, a construct of its own: a 17 m bar of hull above eye level on a centre post. If
        // the windows join up, the bar runs on as one straight line from panel to panel, round the corners too.
        public static string BuildArc() => BuildArc(WindowTag);

        // tag [view] also puts a Camera block on top of the front wall, about 5 m ahead of the player's eye and 1.5 m
        // up (the view is taken from there), and the bar goes at eye level 12 m out.
        public static string BuildArc(string tag)
        {
            if (rig != null) return "a rig already exists; RemoveRig() first";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var origin = view.position + view.forward * 5f - view.up * 1f; // the eye is at (0, 1, -5) in rig space
            var rotation = Quaternion.LookRotation(view.forward, view.up);
            rig = player.NetworkSetup.ServerCreateEntity(Hull(0, 0, 0), new WorldTransform(origin, rotation), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity (not the server?)";
            rig.EntityName = rig.name = RigName;
            var built = rig;
            for (int y = 0; y <= 2; y++)
            {
                for (int x = -1; x <= 1; x++) if (x != 0 || y != 0) Build(built, Hull(x, y, 0));
                foreach (var side in new[] { -2, 2 }) for (int z = -2; z <= 0; z++) Build(built, Hull(side, y, z));
            }
            // Front screens face the player turned -90 degrees; the left wall's face +x (turned 180), the right's -x.
            for (int x = -1; x <= 1; x++) Build(built, Part(TabLogic.SCREEN_TEXT_INPUT, new Vector3i(32 * x, 32, -20), Quaternion.Euler(0f, -90f, 0f)));
            foreach (var z in new[] { -1, -2 })
            {
                Build(built, Part(TabLogic.SCREEN_TEXT_INPUT, new Vector3i(-44, 32, 32 * z), Quaternion.Euler(0f, 180f, 0f)));
                Build(built, Part(TabLogic.SCREEN_TEXT_INPUT, new Vector3i(44, 32, 32 * z), Quaternion.identity));
            }
            var proxied = tag == ViewTag;
            if (proxied) Build(built, Lens());
            var marker = player.NetworkSetup.ServerCreateEntity(Hull(0, 0, 0), new WorldTransform(origin + rotation * new Vector3(0f, 0f, proxied ? 12f : 4f), rotation), EntityType.spaceship);
            if (marker != null)
            {
                marker.EntityName = marker.name = MarkerName;
                if (!proxied) Build(marker, Hull(0, 1, 0));
                for (int x = -8; x <= 8; x++) Build(marker, Hull(x, proxied ? 1 : 2, 0));
            }
            rigTag = tag;
            rigScreenCount = 7;
            nextScan = 0f;
            return $"booth built 5 m ahead as {RigName}, bar 4 m behind it; its 7 screens get {tag} once the game has made them";

            void Build(ParentEntity on, BrickInstanceData brick) => player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = on.ID });
        }

        static BrickInstanceData Hull(int x, int y, int z) => new BrickInstanceData
        {
            brickId = (ushort)TabHull.HULL_A.ItemID,
            gridPosition = new Vector3i(32 * x, 32 * y, 32 * z),
            color = TabHull.HULL_A.defaultPaint,
            rotation = Quaternion.identity.ToByte(),
            healthScore = byte.MaxValue,
            gridLevel = 3,
        };

        // A Camera block standing on top of the middle of a rig's front wall, its lens (its right axis) turned -90
        // degrees about up to look away from the player.
        static BrickInstanceData Lens() => Part(TabSystem.CAMERA, new Vector3i(0, 80, 0), Quaternion.Euler(0f, -90f, 0f));

        static BrickInstanceData Part(Brick brick, Vector3i at, Quaternion q) => new BrickInstanceData
        {
            brickId = (ushort)brick.ItemID,
            gridPosition = at,
            color = brick.defaultPaint,
            rotation = q.ToByte(),
            healthScore = byte.MaxValue,
            gridLevel = 3,
        };

        // Every construct with this name (an earlier rig's default name was "New Entity"). Despawned the way the
        // game removes NPC ships; ServerDestroyEntity would only wreck it and drop upgrade chips.
        public static string RemoveEntity(string name)
        {
            var found = Object.FindObjectsByType<ParentEntity>().Where(e => (e.EntityName == name || e.name == name) && e != Ship).ToList();
            foreach (var entity in found)
            {
                BlockEntityManager.instance.RemoveAndDestroy(entity, removeFromLocationMapping: true);
                if (entity == rig) rig = null;
            }
            return $"removed {found.Count} named {name}";
        }

        public static string RemoveRig() => RemoveEntity(RigName) + ", " + RemoveEntity(MarkerName);

        // Display-surface test: a U-shaped booth 5 m ahead like BuildArc's, its inner faces covered with 1 m Quad
        // decals painted DisplayColor, one screen in the middle of the front wall (painted the same) tagged
        // [view, match], a Camera block on top, and a bar 12 m out at eye level. The decals should show one seamless
        // view, corners and all.
        // Named apart from BuildArc's booth and marker so removing one leaves the other.
        const string DisplayRigName = "CockpitDisplays Display Rig", DisplayMarkerName = "CockpitDisplays Display Marker";
        // The middle-row blue swatch (#0A54C4).
        public static readonly Color32 DisplayColor = new Color32(0x0A, 0x54, 0xC4, 255);

        public static string BuildDisplayRig()
        {
            if (rig != null) return "a rig already exists; remove it first";
            var player = Dependency.PlayerManager;
            var view = Dependency.RenderPipeline.playerCamera.transform;
            var origin = view.position + view.forward * 5f - view.up * 1f; // the eye is at (0, 1, -5) in rig space
            var rotation = Quaternion.LookRotation(view.forward, view.up);
            rig = player.NetworkSetup.ServerCreateEntity(Hull(0, 0, 0), new WorldTransform(origin, rotation), EntityType.spaceship);
            if (rig == null) return "the game didn't create an entity (not the server?)";
            rig.EntityName = rig.name = DisplayRigName;
            var built = rig;
            for (int y = 0; y <= 2; y++)
            {
                for (int x = -1; x <= 1; x++) if (x != 0 || y != 0) Build(built, Hull(x, y, 0));
                foreach (var side in new[] { -2, 2 }) for (int z = -2; z <= 0; z++) Build(built, Hull(side, y, z));
            }
            // Hulls stand on their position (a 1 m hull at y spans y to y + 1 m), so decals sit 16 units up, mid-face.
            // Decals stand on the surface along their up: the front wall's inner face (z -0.5) faces -z, the side
            // walls' (x -1.5 and 1.5) face inward. The screen takes the front's middle cell.
            for (int y = 0; y <= 2; y++)
            {
                for (int x = -1; x <= 1; x++) if (x != 0 || y != 1) Build(built, Decal(new Vector3i(32 * x, 32 * y + 16, -16), Quaternion.Euler(-90f, 0f, 0f)));
                for (int z = -2; z <= 0; z++)
                {
                    Build(built, Decal(new Vector3i(-48, 32 * y + 16, 32 * z), Quaternion.Euler(0f, 0f, -90f)));
                    Build(built, Decal(new Vector3i(48, 32 * y + 16, 32 * z), Quaternion.Euler(0f, 0f, 90f)));
                }
            }
            // Painted like the walls: its [view, match] tag takes the screen's own colour.
            var screen = Part(TabLogic.SCREEN_TEXT_INPUT, new Vector3i(0, 32, -20), Quaternion.Euler(0f, -90f, 0f));
            screen.color = DisplayColor;
            Build(built, screen);
            Build(built, Lens());
            var marker = player.NetworkSetup.ServerCreateEntity(Hull(0, 0, 0), new WorldTransform(origin + rotation * new Vector3(0f, 0f, 12f), rotation), EntityType.spaceship);
            if (marker != null)
            {
                marker.EntityName = marker.name = DisplayMarkerName;
                for (int x = -8; x <= 8; x++) Build(marker, Hull(x, 1, 0));
            }
            rigTag = "[view, match]";
            rigScreenCount = 1;
            nextScan = 0f;
            return $"display rig built 5 m ahead as {DisplayRigName}; its screen gets {rigTag} once the game has made it";

            void Build(ParentEntity on, BrickInstanceData brick) => player.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = brick, entityID = on.ID });
        }

        // A 1 m Quad decal painted DisplayColor, standing on the surface at "at" along its up.
        static BrickInstanceData Decal(Vector3i at, Quaternion q)
        {
            var b = Part(TabDecals.DECAL_QUAD, at, q);
            b.color = DisplayColor;
            return b;
        }

        // Puts back the display rig's left-wall decals on the faces that have none (for when some get deleted by accident).
        public static string RestoreDisplayRigLeftWall()
        {
            var built = Object.FindObjectsByType<ParentEntity>().FirstOrDefault(e => e.EntityName == DisplayRigName || e.name == DisplayRigName);
            if (built == null) return "no display rig";
            var bricks = built.Bricks.brickDatas;
            var placed = 0;
            for (int y = 0; y <= 2; y++)
                for (int z = -2; z <= 0; z++)
                {
                    bool has = false;
                    for (int i = 0; i < bricks.Length && !has; i++)
                    {
                        var b = bricks[i];
                        has = b.IsValid && b.Brick != null && b.Brick.IsDecal && b.gridPosition.x == -48 && b.gridPosition.z == 32 * z &&
                              b.gridPosition.y >= 32 * y && b.gridPosition.y <= 32 * y + 16;
                    }
                    if (has) continue;
                    Dependency.PlayerManager.NetworkedBuilder.LocalVoxelAction(new VoxelActionData { type = VoxelActionType.build, brickData = Decal(new Vector3i(-48, 32 * y + 16, 32 * z), Quaternion.Euler(0f, 0f, -90f)), entityID = built.ID });
                    placed++;
                }
            nextScan = 0f;
            return $"placed {placed} decal(s) on the left wall";
        }

        public static string RemoveDisplayRig() => RemoveEntity(DisplayRigName) + ", " + RemoveEntity(DisplayMarkerName);
    }
}
