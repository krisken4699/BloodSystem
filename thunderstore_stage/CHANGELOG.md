## 3.4.0
- Now works with thermals. Blood temperature starts at 36°C and slowly cools down to 20°C.

## 3.3.4
- Fixed splatter not appearing on penetrating hits, especially headshots — rays now pass through the sosig instead of stopping on it.

## 3.3.3
- Version bump only.

## 3.3.2
- Aiyke Compat Mode config removed — Override is now always used when the Aiyke code mod pack is detected. Aiyke's other features are untouched.
- New defaults from testing: Dot base radius 0.008, Range 40m, Dot Scale Range 50m, Spray Enabled on.

## 3.3.1
- Fixed Aiyke Compat "Override" silently doing nothing due to a startup crash.

## 3.3.0
- Fixed splatter being permanently invisible on some installs.
- New config: Splatter, Spray, Vanilla Particle Staining, and Blood Drip Stains can be toggled independently.
- New config: Aiyke Compat Mode.

## 3.2.7
- New config (default off): Splash On Human-mod Humans.
- Fixed drip stains floating in mid-air around Human-mod humans.

## 3.2.6
- Fixed spray particles staying red when blood color was set to mustard-yellow.

## 3.2.5
- Onslaught Mode interop (if installed): dead sosigs settle before disappearing, plus a fix for a crash when several died at once.

## 3.2.4
- Fixed blood defaulting to red instead of mustard-yellow, and Color Override doing nothing while Mode was Unset.

## 3.2.3
- Minor fix

## 3.2.2
- Minor fix

## 3.2.1
- Minor fix

## 3.2.0
- New config: Color Override / Color Override Mode — Soft keeps NGA-set colors (e.g. zombies), Hard overrides every sosig.
- Performance: blood only triggers for player-fired bullets, cutting frame spikes in large firefights.

## 3.1.0
- Fixed icon.png being used as a blood splatter shape.
- Penetration detection upgraded — armor and faceshields now correctly produce zero blood.
- Per-dot brightness variation so splatter looks dimensional instead of flat.
- Blood and noise textures normalized at load — drop in any custom image and it matches.
- Drip stains upgraded: soft edges, angle-based streaks, and wounds that keep dripping after penetration.
- Spray upgraded to three layers: outer mist, mid blobs, inner drops.
- Spray particles can leave stains on static surfaces.
- New config: Max shot groups, Max drip stains.

## 3.0.0
- Complete rewrite — no performance spikes, no audio issues
- Blood splatter projected from bullet exit wounds using penetration detection
- Animated mode: dots fly from wound to wall before settling
- Natural spread pattern sampled from included blood PNG images
- Dots scale with distance and stretch along the bullet path
- Blood spray particle burst from exit wound on penetration
- Segment explosion blood spray in all directions, scaled by bullet entry speed
- Blood drips from wounds
- Configurable projection mode (Animated / Delayed / Immediate), speed, dot scale, ray counts, and lifetime

## 1.0.0
- Initial release
- Wall splatter spawns when a bullet physically exits a sosig and hits a surface behind it
- Uses actual bullet penetration state — blunt damage and ricochets produce no splatter
- Splatter size and lifetime configurable via F1 or .cfg
