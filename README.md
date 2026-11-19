# RZombies Reworked

Zombie hordes for Rust servers running Oxide/uMod. Hordes rise at night on a schedule you control, and outbreaks keep a few tougher zombies at monuments around the clock. The zombies are Rust's scarecrow NPC.

**Install:** drop `RZombiesReworked.cs` into `oxide/plugins`. The config is created at `oxide/config/RZombiesReworked.json`.

**Optional plugins:** [Kits](https://umod.org/plugins/kits) for custom loadouts and [Vanish](https://umod.org/plugins/vanish), so vanished admins never get a horde spawned on them.

## Features
- **Zombie profiles:** reusable zombie types (name, health, damage multiplier, grenades on or off, kits). Hordes and outbreaks just name the profile they use.
- **Scheduled hordes:** each horde has its own in-game rise and leave hours (windows can cross midnight), a chance to rise each night, a number of nights to skip between rises, a zombie count, and optional replacement of killed zombies after a delay.
- **Two placement modes:** around a random eligible player, or anywhere on the map. Spawns avoid water, rocks, objects and (by default) anything within 15 m of a base.
- **Monument outbreaks:** a few zombies at every matching monument. Killed ones come back after a delay, and any that wander past the leash distance are replaced.
- **Survives reloads:** if the plugin reloads mid-night, a horde that already rose that night comes straight back without re-rolling its chance.
- **Gentle on the server:** spawns and despawns go through a queue at a configurable rate, with a hard cap on total zombies. Zombies are never written to the save, so a crash never leaves orphaned NPCs.
- **Targeting rules:** leave sleepers alone, ignore HumanNPC-style bots, never target listed prefabs, and never target players with the immune permission.
- **Corpse control:** corpses carry the zombie's name and can be removed or have their loot turned off. Zombie backpack lifetime can be scaled.
- **Cleanup:** zombies stuck underwater are removed.
- **Chat announcements** when a horde rises or leaves (translatable through Oxide's lang files).

## Permissions
| Permission | Effect |
|---|---|
| `rzombiesreworked.admin` | Use the `rz` command. |
| `rzombiesreworked.immune` | Zombies won't target you, and hordes never spawn around you. |

## Command
Works in chat (`/rz ...`) and in the server or RCON console (`rz ...`).

| Command | What it does |
|---|---|
| `rz status` | Current in-game time, zombie count and queue, plus each horde's state and outbreak totals. |
| `rz start [horde\|all]` | Raise a horde now for the manual rise length (default 10 minutes). |
| `rz stop [horde\|all]` | Send a horde away. |
| `rz clear` | Remove every zombie. Outbreaks return after their respawn delay. |

## Configuration
```json
{
  "Zombie profiles": {
    "walker": {
      "Name shown": "Walker",
      "Health": 150.0,
      "Damage multiplier": 1.0,
      "Can throw grenades": true,
      "Kits (one picked at random, needs the Kits plugin)": [],
      "Kit replaces default gear": false
    },
    "brute": {
      "Name shown": "Brute",
      "Health": 400.0,
      "Damage multiplier": 1.5,
      "Can throw grenades": false,
      "Kits (one picked at random, needs the Kits plugin)": [],
      "Kit replaces default gear": false
    }
  },
  "Hordes": [
    {
      "Name": "Nightfall",
      "Enabled": true,
      "Profile": "walker",
      "Rises at (hour 0-24)": 20.0,
      "Leaves at (hour 0-24)": 6.5,
      "Chance to rise each night (%)": 100.0,
      "Nights to skip between rises": 0,
      "Zombie count": 40,
      "Replace killed zombies": true,
      "Replacement delay (seconds)": 20.0,
      "Placement (NearPlayers or Anywhere)": "NearPlayers",
      "Players online needed for NearPlayers": 5,
      "Min distance from player": 35.0,
      "Max distance from player": 80.0
    }
  ],
  "Monument outbreaks": {
    "Enabled": true,
    "Profile": "brute",
    "Zombies per monument": 2,
    "Spawn radius": 35.0,
    "Leash distance (0 = off)": 120.0,
    "Respawn delay after kill (seconds)": 180.0,
    "Monuments (prefab name contains)": [
      "airfield", "power_plant", "powerplant", "trainyard", "military_tunnel", "water_treatment",
      "launch_site", "junkyard", "satellite_dish", "sphere_tank", "harbor", "radtown", "excavator"
    ]
  },
  "Targeting": {
    "Attack sleeping players": false,
    "Attack non-Steam bot players": false,
    "Outpost sentries shoot zombies": true,
    "Never target (prefab short names)": [ "scarecrow", "scientistjunkpile" ]
  },
  "Corpses": {
    "Leave corpses": true,
    "Corpses drop loot": true,
    "Backpack lifetime multiplier": 0.5,
    "Death effect prefab (empty = none)": "assets/prefabs/npc/murderer/sound/death.prefab"
  },
  "Cleanup": {
    "Remove zombies underwater for (seconds, 0 = off)": 10.0,
    "Water depth that counts as underwater": 1.6
  },
  "Performance": {
    "Spawns per second": 10.0,
    "Max zombies on the server": 300,
    "Keep spawns this far from bases (0 = off)": 15.0
  },
  "Chat": {
    "Announce when a horde rises": true,
    "Announce when a horde leaves": false
  },
  "Manual rise length (minutes)": 10.0
}
```

### Notes
- **Nights to skip between rises:** `0` means every night, `1` every other night, and so on.
- **Placement fallback:** NearPlayers falls back to Anywhere when not enough players are online or no spot near a player is found.
- **Monument matching:** each monument's prefab file name is checked for any of the words in the list, e.g. `military_tunnel` matches `military_tunnel_1.prefab`. Safe zones (Outpost, Bandit Camp) and oil rigs are left out of the default list on purpose.
- **Adding more hordes:** add another object to `Hordes` with a unique `Name`.
- **Horde history** (last roll and last rise) is stored in `oxide/data/RZombiesReworked.json`.
