# Blood System

Stupidly exaggerated blood visuals! When a bullet physically exits a sosig, it projects blood from the exit wound onto any surface behind it — walls, floors, other sosigs. Real penetration detection means blunt hits, armored stops, and ricochets produce nothing.

## Effects

- **Splash** — Raycast-based blood dots projected from the exit wound. Spread pattern sampled from included blood PNGs. Dots scale with distance and stretch along the bullet path.
- **Spray** — Quick particle burst at the exit wound. Gib explosions fire a 360° burst with lifetime and velocity scaled to bullet entry speed.
- **Drip stain** — Vanilla sosig blood drip particles are hooked at runtime. When a drip lands on a static surface (floor, wall) it spawns a cluster of 5–7 hard-edged blood drops that grow slightly on impact. Drips landing on dynamic objects (guns, other sosigs, moving RBs) are skipped.

## Blood color

Blood color is read from the sosig's actual body color, not hardcoded. Priority order:

1. **NGA SosigIntegrity config** — if the [NGA Sosig Integrity](https://h3vr.thunderstore.io/package/NGA/) mod is installed, the plugin reads that sosig's configured body color directly from its config values (`Mustard Colour` hex string, `Ketchup` bool).
2. **Sosig.Mustard field** — the vanilla per-instance color H3VR assigns to each sosig.
3. **Fallback** — default mustard yellow if neither is available.

This means alien-colored sosigs from custom scripts, and any sosig whose color NGA overrides, will bleed the right color automatically.

## How it works

- Blunt hit / ricochet / armor stop → no splatter
- Bullet punches through and continues → splatter on whatever is behind the exit wound
- Splatter direction and animation speed follow bullet exit speed and direction
- Segment explosions fire blood in all directions
- Splash stains on dynamic rigidbodies (dropped guns, ragdolled gibs) are parented to that object and move with it — stains won't float in the air after the body falls

## Config

All settings in F1 (ConfigurationManager) or the `.cfg` file in BepInEx/config.

| Setting | Default | Description |
|---|---|---|
| Enabled | true | Toggle all blood effects |
| Lifetime seconds | 30 | How long splash stains persist |
| Max rays per shot | 3000 | Raycasts per penetration event (capped to image pixel count) |
| Cone half-angle | 10 | Half-angle in degrees of the splash spread cone |
| Dot base radius | 0.008 | Base radius of each splash dot in metres |
| Range metres | 50 | Maximum splash cast distance |
| Projection Mode | Animated | How dots appear: Animated / Delayed / Immediate |
| Projection Speed Ratio | 2 | Bullet speed multiplier for dot travel speed in Animated mode |
| Projection Speed Bias | 10 | Flat m/s added to dot travel speed |
| Dot Max Scale | 5 | Maximum size multiplier for dots at full range |
| Dot Scale Range metres | 50 | Distance at which dots reach maximum size |
| Gib Ray Count | 200 | Rays fired on segment explosion |

## Performance Tips

Splash is the most CPU/GPU intensive effect. If you are dropping frames, apply these fixes in order of impact.

### Highest impact

**1. Switch Projection Mode to `Immediate`**

The default `Animated` mode keeps a live particle system running with thousands of in-flight dots. `Immediate` removes all flight animation — dots appear the instant the bullet exits. This is the single biggest FPS win. No visual difference in stain placement, only the flying animation is removed.

**2. Reduce Max rays per shot**

Default 3000 is high. Try 1000 or 500. Below ~200 the splatter starts to look sparse. This directly controls how many raycasts happen per shot and how many dot quads build up per second.

### Moderate impact

**3. Reduce Lifetime seconds**

Fewer accumulated stain meshes = fewer draw calls per frame across a long fight. 10–15 seconds keeps the scene looking fresh without piling up hundreds of meshes.

**4. Reduce Gib Ray Count**

Segment explosions fire rays in 360°. High gib fights (shotguns, explosives, multiple sosigs) multiply this cost fast. Cutting from 200 to 100 helps significantly in those scenarios.

### Lower impact

**5. Reduce Dot Max Scale**

Smaller maximum dot size = less GPU fragment overdraw from large-radius quads at range.

**6. Reduce Range metres**

Caps the maximum raycast distance. Combined with reduced Max rays per shot, limits the worst-case cost per shot.

### Low-end preset

`Projection Mode = Immediate`, `Max rays per shot = 500`, `Lifetime seconds = 10`, `Gib Ray Count = 100`
