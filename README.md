# Blood System

Wall blood splatter when bullets exit sosigs. Shoot a sosig and if the bullet punches through, it leaves a mark on whatever's behind them.

## How it works

Tracks the bullet as it passes through the sosig's body using the game's own penetration physics. When the bullet exits the other side and hits a wall or surface, splatter appears at the impact point. No guessing from damage values — if the bullet didn't actually exit, nothing happens.

- Blunt damage through armor: no splatter (bullet never exited)
- Bullet stopped inside sosig: no splatter
- Bullet ricocheted off helmet: no splatter
- Bullet punched through and hit the wall: splatter

## Config

All settings in F1 (ConfigurationManager) or the `.cfg` file.

| Setting | Default | Description |
|---|---|---|
| Enabled | true | Toggle all splatter |
| Size meters | 0.25 | Decal diameter |
| Lifetime seconds | 30 | How long decals stay |

