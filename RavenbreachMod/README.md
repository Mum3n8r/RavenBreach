# Ravenbreach

A milsim overhaul for Ravenfield built on BepInEx/HarmonyX — non-destructive patches, compatible with other mods.

## Installation

1. Install [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases) into your Ravenfield folder
2. Drop `RavenbreachMod.dll` into `BepInEx/plugins/`
3. Drop the `RavenbreachAssets/` folder into `BepInEx/plugins/`
4. Launch the game

---

## SUPPRESSION

- 4-tier suppression system driven by incoming fire volume and proximity
- Tier 1: accuracy degradation, slower acquisition. Tier 2: forced crouch, active cover-seeking. Tier 3: pinned, near-unable to engage. Tier 4: panic
- Bots go prone under sustained fire
- Explosions generate suppression spikes in radius
- Player suppression with heartbeat and breathing audio, vignette effects

## TACTICAL MAP *(M key)*

- Commander interface with full map overlay
- Orders: Move To, Fallback, Flank, Attack, Defend, Suppress, Hold Fire, Hold On Arrival, Cancel
- Squads automatically interrupt orders on heavy contact (leader death / 50%+ casualties) and show INTERRUPTED on map
- Vehicle squads supported — Exit Vehicle and Find Vehicle commands
- Flank executes as a real two-leg maneuver: lateral sweep then assault from the rear
- Spotted enemies appear on map with fade timer

## BOT AI

- Staggered target acquisition — bots don't all snap to you at once
- Target spreading across multiple enemies
- Engagement memory — bots pursue last known position for 8 seconds after losing sight
- Spawn mobilization — bots move toward the frontline on reinforcement instead of standing around
- Idle bots near abandoned vehicles will attempt to crew them
- Engagement range hard cap and weapon-class scaling
- Smarter suppressed behavior — directional cover-seeking, stumble under fire, return fire delays

## LETHALITY

- Headshots are always instant kills
- Limb-specific injury system with body part HUD
- Ballistics — drag, wind drift, gravity scaling by muzzle velocity
- Bullet crack and whizz with pitch variation

## MISC

- Enhanced blood and ragdoll velocity inheritance
- Bullet hole decals and wall hit mist
- Directional suppression audio, hearing stress, sound occlusion

---

**v0.1.0 — EA37**
Requires BepInEx 5.4.23.5
