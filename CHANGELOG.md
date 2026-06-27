## 3.0.0
- Complete rewrite — no performance spikes, no audio issues
- Blood splatter projected from bullet exit wounds using real penetration detection
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
