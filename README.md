# RZombies Reworked

An Oxide/uMod plugin for Rust that spawns scarecrow "zombies" in waves at in-game times you choose, and keeps a small population of persistent zombies at monuments.

By Swishhyy. Drop `RZombiesReworked.cs` into `oxide/plugins`. The [Kits](https://umod.org/plugins/kits) plugin is **optional**. It's only needed if you want custom zombie loadouts.

## Features
* **Multiple spawn waves.** Each wave has its own in-game spawn and despawn time, population, health, name, kits, spawn chance and "days between spawns". Windows can cross midnight (`19.8 -> 7.3`) or fall within one day (`6.0 -> 8.0`).
* **Spawn near players or across the map.** Near-player spawns pick a random eligible player (not dead, sleeping, flying, in a safe zone, vanished or ignored) and place zombies in a ring between the min and max distance.
* **Monument zombies.** A persistent group at each matching monument. They respawn if killed and are pulled back if they roam too far.
* **Gradual spawn and despawn.** Zombies spawn and despawn a few at a time, so the server doesn't hitch.
* **Water cleanup.** Zombies that stay fully underwater for too long are removed.
* **Targeting rules.** Zombies can ignore sleepers, human NPCs (e.g. HumanNPC bots), specific prefabs and players with `rzombiesreworked.ignore`.
* **Corpse and loot control.** Corpses show the zombie's name, and you can choose whether corpses and loot are left behind. Zombie backpacks can despawn twice as fast.
* **Sentry and grenade toggles.** Choose whether outpost sentries shoot zombies and whether zombies throw grenades.
* **Spawn broadcast.** Optionally post a chat message when a wave spawns.
* **Never saved.** Zombies aren't written to the save, so a crash or restart never leaves orphaned NPCs.

## Permissions
* `rzombiesreworked.admin`: use `forcespawn`, `despawnall` and `nzstatus`.
* `rzombiesreworked.ignore`: zombies won't target this player, and waves won't spawn around them.

## Commands
All commands work in chat (`/forcespawn`) and in the server/RCON console (`forcespawn`).

* `forcespawn [wave name]`: spawns every wave, or just the named one, right now. Forced zombies despawn after 10 minutes unless that wave's normal window starts in the meantime.
* `despawnall`: removes every zombie right away. Monument zombies come back on the next respawn check.
* `nzstatus`: shows the in-game time, each wave's state and zombie count, days since it last spawned, and monument totals.

## Configuration

### Spawn Waves (one entry per wave)
| Key | Description |
|---|---|
| Wave Name | Unique name. Used by `forcespawn <name>` and for saving the day counter. |
| Spawn Time / Destroy Time | In-game hours (0–24) when the wave appears and disappears. |
| Spawn near players | `true`: spawn around a random player. `false`: random map positions. |
| Min pop for near player spawn | Online players needed before near-player spawning is used. |
| Min / Max distance from player | Ring around the player that zombies spawn in. |
| Respawn killed zombies while wave is active | Replace zombies that are killed so the population stays full. |
| Zombie Settings | `Display Name`, `Scarecrow Health`, `Scarecrow Kits` (a random one is picked), `Scarecrow Population (total amount)`. |
| Chance Settings | `Chance per cycle`: rolled **once** each time the window opens. `Days between spawns`: `0` = every night, `1` = every other night, and so on. |

### Monument Settings
| Key | Description |
|---|---|
| Monument Zombie Population | Zombies kept at **each** matching monument. `0` turns this off. |
| Spawn radius around monument | How far from the monument centre zombies can spawn. |
| Max roam distance before respawning at monument | Zombies further away than this get respawned at their monument. `0` = off. |
| Respawn check interval (seconds) | How often dead or stray monument zombies are replaced. |
| Monument List | Words matched against monument prefab names. Case, spaces, underscores and numbers are ignored, so `military_tunnel` matches `military_tunnel_1.prefab`. |
| Zombie Settings | `Display Name`, `Scarecrow Health`, `Scarecrow Kits`. |

The default monument list leaves out safe zones (Outpost, Bandit Camp) and offshore monuments (oil rigs).

### Destroy Settings
* **Leave Corpse, when destroyed**: leave a corpse when a wave ends and its zombies are despawned.
* **Leave Corpse, when killed by player**: leave a corpse when a zombie is killed.
* **Spawn Loot**: whether zombie corpses get loot.
* **Half bodybag despawn time**: zombie backpacks despawn twice as fast.

### Behaviour Settings
* **Attack sleeping players**
* **Zombies attacked by outpost sentries**
* **Throw Grenades**
* **Ignore Human NPCs**: ignore non-Steam BasePlayers such as HumanNPC bots (scientists are still attacked).
* **Despawn after fully submerged for (seconds, 0 = off)**, and **Water depth counted as fully submerged**
* **Ignored entities (full entity shortname)**: e.g. `scarecrow`, `scientistjunkpile`. A `.prefab` suffix is optional.

### Broadcast Settings
* **Broadcast spawn amount**: post a chat message when a wave spawns.

### Example
```json
{
  "Config Version": "6.0.0",
  "Spawn Waves": [
    {
      "Wave Name": "Night Wave",
      "Spawn Time": 19.8,
      "Destroy Time": 7.3,
      "Spawn near players": true,
      "Min pop for near player spawn": 10,
      "Min distance from player": 30.0,
      "Max distance from player": 60.0,
      "Respawn killed zombies while wave is active": true,
      "Zombie Settings": {
        "Display Name": "Scarecrow",
        "Scarecrow Health": 200.0,
        "Scarecrow Kits": [],
        "Scarecrow Population (total amount)": 50
      },
      "Chance Settings": {
        "Chance per cycle": 100.0,
        "Days between spawns": 0
      }
    },
    {
      "Wave Name": "Dawn Wave",
      "Spawn Time": 6.0,
      "Destroy Time": 8.0,
      "Spawn near players": false,
      "Min pop for near player spawn": 0,
      "Min distance from player": 40.0,
      "Max distance from player": 80.0,
      "Respawn killed zombies while wave is active": false,
      "Zombie Settings": {
        "Display Name": "Dawn Scarecrow",
        "Scarecrow Health": 150.0,
        "Scarecrow Kits": [ "dawnkit" ],
        "Scarecrow Population (total amount)": 30
      },
      "Chance Settings": {
        "Chance per cycle": 50.0,
        "Days between spawns": 1
      }
    }
  ],
  "Monument Settings": {
    "Monument Zombie Population": 3,
    "Spawn radius around monument": 40.0,
    "Max roam distance before respawning at monument (0 = off)": 150.0,
    "Respawn check interval (seconds)": 60.0,
    "Monument List": [
      "airfield", "powerplant", "trainyard", "military_tunnel", "water_treatment",
      "launch_site", "junkyard", "satellite_dish", "sphere_tank", "harbor",
      "radtown", "excavator", "gas_station", "supermarket", "warehouse"
    ],
    "Zombie Settings": {
      "Display Name": "Monument Zombie",
      "Scarecrow Health": 300.0,
      "Scarecrow Kits": []
    }
  },
  "Destroy Settings": {
    "Leave Corpse, when destroyed": false,
    "Leave Corpse, when killed by player": true,
    "Spawn Loot": true,
    "Half bodybag despawn time": true
  },
  "Behaviour Settings": {
    "Attack sleeping players": false,
    "Zombies attacked by outpost sentries": true,
    "Throw Grenades": true,
    "Ignore Human NPCs": true,
    "Despawn after fully submerged for (seconds, 0 = off)": 10.0,
    "Water depth counted as fully submerged": 1.8,
    "Ignored entities (full entity shortname)": [ "scientistjunkpile", "scarecrow" ]
  },
  "Broadcast Settings": {
    "Broadcast spawn amount": false
  }
}
```

## Upgrading from Night Zombies (5.x)
This plugin used to be called **Night Zombies**. To switch over:
1. Remove `NightZombies.cs` from `oxide/plugins` and add `RZombiesReworked.cs`.
2. Your existing `oxide/config/NightZombies.json` is copied to `RZombiesReworked.json` the first time the plugin loads.
3. **Permissions have new names.** Grant them again:
   * `nightzombies.admin` -> `rzombiesreworked.admin`
   * `nightzombies.ignore` -> `rzombiesreworked.ignore`

On that first load the plugin also:
* renames the misspelt `Days betewen spawn` key to `Days between spawns` and keeps your value,
* resets **Monument List** to names that actually match Rust monuments (the old names never matched, so monument zombies were spawning near the map centre),
* adds the new keys with their defaults.

Day counters are now saved in `oxide/data/RZombiesReworked.json`. The old `NightZombies-daysSinceSpawn.json` and `NightZombies.json` files can be deleted.
