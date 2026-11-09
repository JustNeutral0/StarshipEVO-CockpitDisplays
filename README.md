# Cockpit Displays

A Starship EVO mod for **windowless cockpits**: screen blocks and painted surfaces that show live views of the
outside, plus surfaces a [Lua Computer](https://github.com/JustNeutral0/StarshipEVO-EvoCore) can draw a HUD on.

> Early test release. Built and tested against Starship EVO early-access **26w45a**, single-player.
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
**Camera screens** tab of the Mod Settings mod, which isn't published yet.

## Install

Needs [EvoCore](https://github.com/JustNeutral0/StarshipEVO-EvoCore) (install it first) and
[Starship Uplink](https://github.com/MadTheDog/Starship-Uplink).

1. In Uplink, choose **Add from GitHub** and enter `JustNeutral0/StarshipEVO-CockpitDisplays`.
2. Pick the `justneutral.cockpitdisplays-<version>.zip` asset, then install it into your profile.
3. Launch the game through Uplink.

## Licence

MIT, see [LICENSE](LICENSE). Starship EVO itself belongs to its developers.

Part of [Starship-EVO-Mods](https://github.com/JustNeutral0/Starship-EVO-Mods).
