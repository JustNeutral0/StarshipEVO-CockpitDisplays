# Cockpit Displays

A Starship EVO mod for **windowless cockpits**: screen blocks and painted surfaces that show live views of the
outside, plus surfaces a [Lua Computer](https://github.com/JustNeutral0/StarshipEVO-EvoCore) can draw a HUD on.

> Early test release. Built and tested against Starship EVO early-access **26w45a** (Unity 6 beta), single-player.
> Game updates may break it until it's rebuilt. Please report problems in Issues.

## What it adds

Put a tag in a screen block's text:

- `[camera]`: the view from the nearest Camera block on the same ship.
- `[window]`: what you'd see if the screen were a window cut through the hull.
- `[xray]`: the same, straight through your own ship.
- `[view]`: a window view taken from the ship's Camera block instead of your eye.
- `[view, COLOR]` (any mode, any paint colour): every face under a decal of that colour shows one seamless view,
  any shape, so a whole cockpit wall can be a display.
- `[screen, COLOR]`: the painted surface becomes a canvas. Link a Lua Computer to the controller screen and draw a
  HUD on it from Lua.

It runs on sensible defaults. Resolution, sharpness, refresh rate and far objects can be tuned in the
**Camera screens** tab of [Mod Settings](https://github.com/JustNeutral0/StarshipEVO-ModSettings) (optional).

## Install

Needs [EvoCore](https://github.com/JustNeutral0/StarshipEVO-EvoCore) (install it first) and
[Starship Uplink](https://github.com/MadTheDog/Starship-Uplink).

**First, the game build:** these mods are built for the **Unity 6 beta** of Starship EVO, not the default build. In Steam, right-click Starship EVO, open **Properties > Betas**, pick **unity6**, and let it update. The mods don't work on the default build (character creation breaks, for one).

1. In Uplink, choose **Add from GitHub** and enter `JustNeutral0/StarshipEVO-CockpitDisplays`.
2. Pick the `justneutral.cockpitdisplays-<version>.zip` asset, then install it into your profile.
3. Launch the game through Uplink.

## Building from source

You need the .NET SDK and Starship EVO with BepInEx (Starship Uplink sets it up). This mod builds against EvoCore's source: clone [StarshipEVO-EvoCore](https://github.com/JustNeutral0/StarshipEVO-EvoCore) next to this repo (both folders side by side).

```
dotnet build CockpitDisplays.csproj -c Release -p:Loader=BepInEx -p:Game="C:\path\to\Starship EVO"
```

`-p:Game` is your game folder (it defaults to the usual Steam location). The DLL lands in `bin\BepInEx\Release\`.

## Licence

MIT, see [LICENSE](LICENSE). Starship EVO itself belongs to its developers.

Part of [Starship-EVO-Mods](https://github.com/JustNeutral0/Starship-EVO-Mods).
