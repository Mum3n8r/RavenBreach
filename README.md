# Ravenbreach

**A milsim overhaul mod for Ravenfield.**  
BepInEx/HarmonyX — non-destructive Harmony patches on top of vanilla. No DLL replacement.

> Designed around Squad and Arma as the benchmark. More systems active than HavenM, better CPU performance.

---

## Features

### AI Behavior
- Staggered target acquisition and fire spreading across squads
- Engagement distance posture — bots crouch beyond 35m, go prone beyond 70m when engaging
- Suppression-driven prone and cover seek (4 tiers)
- Directional cover seeking away from fire origin
- Hold on arrival — squads seek nearest cover within 20m when ordered to hold
- Engagement memory — bots pursue last known position for 8 seconds after losing sight
- Grenade usage — bots throw grenades at clustered enemies within 8–35m (12–25s cooldown)
- Vehicle standoff — tanks hold at 55m from objectives instead of driving into them
- Sprint stagger variation per bot
- Faster target detection across all weapon classes
- Target spreading within squads to reduce focus-fire pile-on
- Idle look fix — bots no longer randomly sweep sky, ground, or look back at spawn

### Suppression System
- 0–100 suppression meter, 4 tiers (calm / stressed / suppressed / pinned)
- Tiered reload pools and return fire delays per tier
- Blast stun from nearby explosions
- Breathing + heartbeat audio scaling with suppression (tier 3+)
- Sprint stumble under suppression
- Directional cover seeking biased away from fire origin

### Combat Feel
- Headshot instant kill (player and AI)
- Hit location damage multipliers for AI actors
- Ragdoll velocity inheritance at death transition
- Gore system — wound decals, blood droplets, wall hit mist (YouTube safe, no dismemberment)
- Limb punishment system (arm / leg / abdomen / chest)
- Bullet crack (near-graze only, 11cm radius, enemy fire only) and whizz audio

### Ballistics
- Sniper class gravity tweak (flatter arc, visible drop at range)
- ~~Drag and velocity-based damage scaling~~ — removed, pending rework

### Tactical Map (M key)
- Full in-game tactical map with zoom, pan, double-click reset
- Friendly squad NATO symbols, member HP bars, body silhouette, KIA strikethrough
- Contact / Combat status per squad
- Enemy blips — spotted only, 30s fade from last seen
- Order system: Move, Attack, Defend, Suppress, Fallback, Hold, Flank, Regroup
- Hold on arrival toggle per squad
- Squad split button (2+ members required)
- Cancel order button
- Find vehicle / Exit vehicle buttons
- Pause menu suppressed while map is open
- Vanilla M key map suppressed

### HUD
- Custom compass
- Ammo warning
- Body part HUD (player only — head, neck, chest, abdomen, arms, legs)

---

## Installation

1. Install [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) for Ravenfield
2. Drop `RavenbreachMod.dll` into `BepInEx/plugins/`
3. Launch Ravenfield

---

## Compatibility

- Ravenfield EA37+ (Unity 2020.3.49f1)
- BepInEx 5.4.23.5
- Non-destructive — compatible with most other mods
- Known conflict: EHADS 3.0 (crash on death — EHADS issue, not Ravenbreach)

---

## Status

Early access. Active development. Not all systems are fully tuned yet.

See [CHANGELOG.md](CHANGELOG.md) for full version history.
