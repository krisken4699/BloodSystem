## 3.3.0
- Fixed: splatter (the wall/floor splash effect) could end up permanently invisible on any install — a startup step always built a technically-working-but-invisible material and, in doing so, blocked the one path that would have replaced it with a visible one. Splatter now renders correctly from the very first shot, no wall-shooting or workaround needed, and still upgrades itself automatically to the higher-quality material the first time it gets the chance.
- New config: Splatter Enabled, Spray Enabled (default OFF), Vanilla Particle Staining Enabled, Blood Drip Stains Enabled — the four blood effects can now be toggled independently. Spray (the wide-cone particle burst on wounds) defaults off since a lot of players found it looked unintentional.
- New config: Aiyke Compat Mode — only relevant if the "Aiyke code mod pack" is also installed. That mod rewrites bullet-penetration physics in a way that could make splatter almost never appear. Choose "Approximate" (default — splatter fires on any damaging hit, less precise placement, every Aiyke feature keeps working) or "Override" (this mod restores its own precise penetration detection; in exchange you lose Aiyke's own penetration-rework and output-damage-multiplier features specifically — its other features are unaffected).

## 3.2.7
- New config (default off): Splash On Human-mod Humans — the Human mod (invent60's AI
  companion/enemy replacement) was triggering the full splash/spray/gib burst on every ordinary
  kill, not just real grenade/gib explosions. Off by default now; turn back on if you want it.
- Fixed: drip stains could appear floating in mid-air around waist height on Human-mod humans —
  the floor-detection raycast was landing on one of that mod's own body hitbox colliders instead
  of the real floor.

## 3.2.6
- Fixed: spray particles (mist/blobs/drops on gunshots and gib explosions) still appeared red even with blood color set to mustard-yellow. The blood image's color was supposed to be stripped to plain white on load so it could be tinted any color, but the strip step silently skipped flat-colored source images (color-only-in-alpha-mask, which is how the shipped blood image is drawn) and left them in their original color. Spray now tints correctly to the configured blood color.

## 3.2.5
- Optional interop (only active if Onslaught Mode is installed): dead sosigs now fall and settle for a few seconds before disappearing instead of vanishing the instant they die, and fixed a crash in Onslaught's own cleanup logic that could occur when two or more sosigs died in the same moment.

## 3.2.4
- Fixed: with Color Override Mode left at its default (Unset), blood was appearing red instead of the correct mustard-yellow, and setting Color Override to a custom color had no effect while the mode was Unset. Blood now always defaults to mustard-yellow as intended; Color Override only needs Mode set to Soft or Hard to apply.

## 3.2.3
- Minor fix

## 3.2.2
- Minor fix

## 3.2.1
- Minor fix

## 3.2.0
- New config: Color Override / Color Override Mode — set a custom blood color instead of each sosig's natural color. Mode 1 (Soft) replaces default blood color but leaves sosigs with a color specifically set via NGA SosigIntegrityConfigs (e.g. zombies) untouched. Mode 2 (Hard) replaces blood color for every sosig, no exceptions. Any other value in Mode = Unset, vanilla per-sosig color behavior.
- Performance: blood effects (splash, spray, drip) now only trigger for bullets fired by the player — sosig-vs-sosig gunfire no longer spawns blood, cutting frame spikes during large firefights

## 3.1.0
- Fixed icon.png being picked up and used as a blood splatter shape texture (it sits in the same folder as the DLL after install)
- Penetration detection upgraded to geometric dot-product check — bullet position vs surface normal determines real penetration vs deflection; armor and faceshields now correctly produce zero blood
- Per-dot brightness variation from noise.png: each splash dot gets an individual brightness level (10 buckets, 0.3–1.0 range) so splatter looks dimensional instead of flat
- Blood PNG and noise texture luminance normalized at load — drop in any custom image and it matches
- Drip stains upgraded: smoothstep edge (opaque center, gradual falloff); elongated streaks based on impact angle and velocity; separate growing stains from wounds that drip for several seconds after penetration
- Spray upgraded to three-layer system: outer mist ring, mid-range blobs, inner fast drops — each with independent physics and gravity (still barely noticable)
- Spray particles have a chance to leave stains on static surfaces
- Stains fade out smoothly over their lifetime instead of popping out
- New config: Max shot groups — caps how many shots worth of splash decals stay in scene; oldest shot removed as a group when exceeded
- New config: Max drip stains — caps drip stains from particle detection separately from shot groups

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
