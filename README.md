# RZombies Reworked

DayZ-style infected for Rust servers running Oxide/uMod, tuned for small servers (1–5 players). Every player always has zombies nearby, and more come out at night. Monuments fill up when someone gets close, and the occasional night brings a horde on top. The zombies are Rust's scarecrow NPC.

**Install:** drop `RZombiesReworked.cs` into `oxide/plugins`. The config is created at `oxide/config/RZombiesReworked.json`.

**Optional plugins:** [Kits](https://umod.org/plugins/kits) for custom loadouts and [Vanish](https://umod.org/plugins/vanish), so vanished admins never get a horde spawned on them.

## Features
- **Roaming zombies per player (the DayZ part):** each online player gets their own group of zombies (10 by day, 20 at night by default). They spawn 40–100 m away, out of sight, and get recycled once the player moves on, so you keep running into them wherever you go. The total grows with how many people are online, and a killed zombie is replaced after 30 s.
- **Zombie profiles:** reusable zombie types (name, health, damage multiplier, grenades on or off, kits). Hordes and outbreaks just name the profile they use.
- **Scheduled hordes:** each horde has its own in-game rise and leave hours (windows can cross midnight), a chance to rise each night, a number of nights to skip between rises, a zombie count, and optional replacement of killed zombies after a delay.
- **Two placement modes:** around a random eligible player, or anywhere on the map. Spawns avoid water, rocks, objects and (by default) anything within 15 m of a base.
- **Monument outbreaks:** monuments fill up with zombies (12 each by default) only while a player is within 300 m, and empty out 90 s after everyone leaves. Nothing runs at monuments nobody is visiting. Killed zombies come back after a delay, and any that wander past the leash distance are replaced.
- **Horde nights:** a 35% chance each night of an extra horde rising around players, announced in chat.
- **Survives reloads:** if the plugin reloads mid-night, a horde that already rose that night comes straight back without re-rolling its chance.
- **Gentle on the server:** spawns and despawns go through a queue at a configurable rate, with a hard cap on total zombies. Zombies are never written to the save, so a crash never leaves orphaned NPCs.
- **Glowing eyes:** every zombie wears Rust's Glowing Eyes item by default, so at night you see pairs of eyes in the dark. Every zombie also wears a candle hat that lights up at dusk and goes out at dawn, so you can spot them in the dark. Both are removed when a zombie dies.
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
      "Health": 75.0,
      "Damage multiplier": 1.0,
      "Can throw grenades": true,
      "Kits (one picked at random, needs the Kits plugin)": [],
      "Kit replaces default gear": false,
      "Glowing eyes (wear the Glowing Eyes item)": true,
      "Candle hat (lit at night)": true
    },
    "brute": {
      "Name shown": "Brute",
      "Health": 150.0,
      "Damage multiplier": 1.25,
      "Can throw grenades": false,
      "Kits (one picked at random, needs the Kits plugin)": [],
      "Kit replaces default gear": false,
      "Glowing eyes (wear the Glowing Eyes item)": true,
      "Candle hat (lit at night)": true
    }
  },
  "Roaming zombies (per player)": {
    "Enabled": true,
    "Profile": "walker",
    "Zombies around each player (day)": 10,
    "Zombies around each player (night)": 20,
    "Night starts (hour 0-24)": 19.5,
    "Night ends (hour 0-24)": 6.5,
    "Min spawn distance from player": 40.0,
    "Max spawn distance from player": 100.0,
    "Recycle zombies further than this from their player": 160.0,
    "Respawn delay after kill (seconds)": 30.0
  },
  "Hordes": [
    {
      "Name": "Nightfall",
      "Enabled": true,
      "Profile": "walker",
      "Rises at (hour 0-24)": 20.0,
      "Leaves at (hour 0-24)": 6.5,
      "Chance to rise each night (%)": 35.0,
      "Nights to skip between rises": 0,
      "Zombie count": 40,
      "Replace killed zombies": true,
      "Replacement delay (seconds)": 10.0,
      "Placement (NearPlayers or Anywhere)": "NearPlayers",
      "Players online needed for NearPlayers": 1,
      "Min distance from player": 30.0,
      "Max distance from player": 70.0
    }
  ],
  "Monument outbreaks": {
    "Enabled": true,
    "Profile": "brute",
    "Zombies per monument": 12,
    "Spawn radius": 45.0,
    "Only active when a player is within (0 = always)": 300.0,
    "Empty out after nobody is near for (seconds)": 90.0,
    "Leash distance (0 = off)": 120.0,
    "Respawn delay after kill (seconds)": 90.0,
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
- **Eye glow:** the scarecrow's built-in eye glow is drawn on each player's computer, so no server plugin can make it brighter. This plugin adds the Glowing Eyes wearable on top. If a kit gives zombies a full-face mask that blocks the eyes slot, the console will say so once.
- **Candle hats:** if a zombie's outfit blocks the head slot (the default scarecrow mask usually does), the blocking item is swapped out for the hat. If no hat fits, the console says so once. Set "Candle hat (lit at night)" to false on a profile to turn it off.
- **Tuning density:** for more zombies around you, raise the two "Zombies around each player" values first. That's the main lever and it scales with player count. With 5 players at night the default is roughly 100 roaming zombies, plus any active monuments, plus a horde on horde nights. Keep "Max zombies on the server" above that total.
- **Nights to skip between rises:** `0` means every night, `1` every other night, and so on.
- **Placement fallback:** NearPlayers falls back to Anywhere when not enough players are online or no spot near a player is found.
- **Monument matching:** each monument's prefab file name is checked for any of the words in the list, e.g. `military_tunnel` matches `military_tunnel_1.prefab`. Safe zones (Outpost, Bandit Camp) and oil rigs are left out of the default list on purpose.
- **Adding more hordes:** add another object to `Hordes` with a unique `Name`.
- **Horde history** (last roll and last rise) is stored in `oxide/data/RZombiesReworked.json`.
