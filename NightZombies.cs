using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.AI;
using Pool = Facepunch.Pool;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Night Zombies", "0x89A", "6.0.0")]
    [Description("Spawns scarecrow zombies at configurable in-game times and keeps persistent zombies at monuments")]
    public class NightZombies : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "nightzombies.admin";
        private const string PermIgnore = "nightzombies.ignore";

        private const string ScarecrowPrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const string DeathSound = "assets/prefabs/npc/murderer/sound/death.prefab";
        private const string DataFileName = "NightZombies";
        private const string CurrentConfigVersion = "6.0.0";

        private const int GrenadeItemId = 1840822026; // grenade.beancan
        private const float ForceSpawnDuration = 600f;
        private const float WaterCheckInterval = 2f;
        private const int MaxRememberedCorpses = 10000;

        private Configuration _config;

        [PluginReference("Kits")] private Plugin _kits;
        [PluginReference("Vanish")] private Plugin _vanish;

        private readonly List<SpawnWave> _waves = new List<SpawnWave>();
        private MonumentSpawner _monuments;

        // Every zombie this plugin owns, mapped to whoever spawned it.
        private readonly Dictionary<ScarecrowNPC, IZombieOwner> _zombies = new Dictionary<ScarecrowNPC, IZombieOwner>();

        // userID -> display name, so corpses and backpacks of our zombies can be recognised after death.
        private readonly Dictionary<ulong, string> _corpseNames = new Dictionary<ulong, string>();

        // Seconds each zombie has been fully submerged.
        private readonly Dictionary<ScarecrowNPC, float> _submerged = new Dictionary<ScarecrowNPC, float>();

        private readonly HashSet<string> _ignoredPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warnedKits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _spawnBlockMask;
        private Timer _waterTimer;
        private bool _timeHooked;

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermIgnore, this);

            AddCovalenceCommand("forcespawn", nameof(CmdForceSpawn));
            AddCovalenceCommand("despawnall", nameof(CmdDespawnAll));
            AddCovalenceCommand("nzstatus", nameof(CmdStatus));

            foreach (string entry in _config.Behaviour.Ignored)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                    _ignoredPrefabs.Add(StripPrefabExtension(entry.Trim()));
            }

            if (_config.Behaviour.SentriesAttackZombies)
                Unsubscribe(nameof(OnTurretTarget));
            if (_config.Destroy.SpawnLoot)
                Unsubscribe(nameof(OnCorpsePopulate));
        }

        private void OnServerInitialized()
        {
            _spawnBlockMask = LayerMask.GetMask("Default", "Tree", "Construction", "World", "Vehicle_Detailed", "Deployed");

            if (_kits == null && HasAnyKits())
                PrintWarning("Kits is not loaded - zombies will use their default scarecrow loadout");

            Dictionary<string, int> savedDays = LoadData();
            foreach (Configuration.SpawnWave waveConfig in _config.SpawnWaves)
            {
                var wave = new SpawnWave(this, waveConfig);
                int days;
                wave.DaysSinceLastSpawn = savedDays.TryGetValue(waveConfig.WaveName, out days)
                    ? days
                    : waveConfig.Chance.Days + 1; // let the first cycle spawn
                _waves.Add(wave);
            }

            if (_waves.Count > 0 && TOD_Sky.Instance != null)
            {
                TOD_Sky.Instance.Components.Time.OnMinute += OnMinute;
                TOD_Sky.Instance.Components.Time.OnDay += OnDay;
                _timeHooked = true;
            }

            _monuments = new MonumentSpawner(this);
            _monuments.Initialize();

            if (_config.Behaviour.WaterDespawnSeconds > 0f)
                _waterTimer = timer.Every(WaterCheckInterval, CheckSubmerged);
        }

        private void Unload()
        {
            if (_timeHooked && TOD_Sky.Instance != null)
            {
                TOD_Sky.Instance.Components.Time.OnMinute -= OnMinute;
                TOD_Sky.Instance.Components.Time.OnDay -= OnDay;
            }

            SaveData();

            _waterTimer?.Destroy();
            foreach (SpawnWave wave in _waves)
                wave.Shutdown();
            _monuments?.Shutdown();

            // Anything left over (shouldn't be, but never leave orphans behind).
            var leftovers = Pool.Get<List<ScarecrowNPC>>();
            leftovers.AddRange(_zombies.Keys);
            _zombies.Clear();
            foreach (ScarecrowNPC zombie in leftovers)
            {
                if (IsAlive(zombie))
                    zombie.AdminKill();
            }
            Pool.FreeUnmanaged(ref leftovers);
        }

        private void OnServerSave() => SaveData();

        private object OnNpcTarget(ScarecrowNPC npc, BaseEntity target)
        {
            if (npc == null || target == null || !_zombies.ContainsKey(npc))
                return null;
            return ShouldIgnoreTarget(target) ? (object)true : null;
        }

        private object OnTurretTarget(NPCAutoTurret turret, ScarecrowNPC target)
        {
            // Only subscribed when sentries should NOT shoot zombies.
            if (target == null || !_zombies.ContainsKey(target))
                return null;
            return true;
        }

        private object OnPlayerDeath(ScarecrowNPC zombie, HitInfo info)
        {
            IZombieOwner owner;
            if (zombie == null || !_zombies.TryGetValue(zombie, out owner))
                return null;

            _zombies.Remove(zombie);
            _submerged.Remove(zombie);

            Effect.server.Run(DeathSound, zombie.transform.position);
            owner.OnZombieDied(zombie);

            if (_config.Destroy.LeaveCorpseKilled)
                return null;

            NextTick(() =>
            {
                if (IsAlive(zombie))
                    zombie.AdminKill();
            });
            return true;
        }

        private void OnEntityKill(ScarecrowNPC zombie)
        {
            // Zombie removed by something other than this plugin (admin entity kill, cleanup plugins, etc).
            IZombieOwner owner;
            if (zombie == null || !_zombies.TryGetValue(zombie, out owner))
                return;

            _zombies.Remove(zombie);
            _submerged.Remove(zombie);
            owner.OnZombieRemoved(zombie);
        }

        private object OnCorpsePopulate(ScarecrowNPC npc, NPCPlayerCorpse corpse)
        {
            // Only subscribed when loot should NOT spawn. Returning the corpse skips loot generation.
            if (npc == null || corpse == null || !_corpseNames.ContainsKey(npc.userID))
                return null;
            return corpse;
        }

        private void OnEntitySpawned(NPCPlayerCorpse corpse)
        {
            if (corpse == null || _corpseNames.Count == 0)
                return;

            NextTick(() =>
            {
                string name;
                if (corpse == null || corpse.IsDestroyed || !_corpseNames.TryGetValue(corpse.playerSteamID, out name))
                    return;

                corpse.playerName = name;
                corpse.SendNetworkUpdate();
            });
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (!_config.Destroy.HalfBodybagDespawn || container == null || _corpseNames.Count == 0)
                return;

            NextTick(() =>
            {
                if (container == null || container.IsDestroyed || !_corpseNames.Remove(container.playerSteamID))
                    return;

                container.CancelInvoke(container.RemoveMe);
                container.Invoke(container.RemoveMe, container.CalculateRemovalTime() / 2f);
            });
        }

        #endregion

        #region Time

        private void OnMinute()
        {
            foreach (SpawnWave wave in _waves)
                wave.Tick();
        }

        private void OnDay()
        {
            foreach (SpawnWave wave in _waves)
                wave.DaysSinceLastSpawn = Mathf.Min(wave.DaysSinceLastSpawn + 1, 10000);
        }

        private static bool IsWithinWindow(float start, float end)
        {
            float now = ConVar.Env.time;
            if (Mathf.Approximately(start, end))
                return false;
            return start > end
                ? now >= start || now < end   // window crosses midnight, e.g. 19.8 -> 7.3
                : now >= start && now < end;  // same-day window, e.g. 6.0 -> 8.0
        }

        #endregion

        #region Commands

        private void CmdForceSpawn(IPlayer player, string command, string[] args)
        {
            if (!HasAdmin(player))
                return;

            if (args.Length > 0)
            {
                string waveName = string.Join(" ", args);
                SpawnWave wave = _waves.Find(w => w.Config.WaveName.Equals(waveName, StringComparison.OrdinalIgnoreCase));
                if (wave == null)
                {
                    player.Reply(Lang("UnknownWave", player, waveName, string.Join(", ", _waves.ConvertAll(w => w.Config.WaveName))));
                    return;
                }
                wave.ForceSpawn();
                player.Reply(Lang("ForceSpawned", player, wave.Config.WaveName, ForceSpawnDuration / 60f));
                return;
            }

            foreach (SpawnWave wave in _waves)
                wave.ForceSpawn();
            player.Reply(Lang("ForceSpawned", player, "all waves", ForceSpawnDuration / 60f));
        }

        private void CmdDespawnAll(IPlayer player, string command, string[] args)
        {
            if (!HasAdmin(player))
                return;

            foreach (SpawnWave wave in _waves)
                wave.DespawnNow();
            _monuments.DespawnAll();
            player.Reply(Lang("DespawnedAll", player, _config.Monument.CheckInterval));
        }

        private void CmdStatus(IPlayer player, string command, string[] args)
        {
            if (!HasAdmin(player))
                return;

            var sb = new StringBuilder();
            sb.AppendLine(Lang("StatusHeader", player, ConVar.Env.time.ToString("0.00"), _zombies.Count));
            foreach (SpawnWave wave in _waves)
            {
                Configuration.SpawnWave c = wave.Config;
                sb.AppendLine(Lang("StatusWave", player, c.WaveName, wave.StateName, wave.Count, c.Zombies.Population,
                    c.SpawnTime.ToString("0.0"), c.DestroyTime.ToString("0.0"), wave.DaysSinceLastSpawn));
            }
            sb.Append(Lang("StatusMonuments", player, _monuments.SiteCount, _monuments.Count));
            player.Reply(sb.ToString());
        }

        private bool HasAdmin(IPlayer player)
        {
            if (player.IsServer || player.HasPermission(PermAdmin))
                return true;
            player.Reply(Lang("NoPermission", player));
            return false;
        }

        #endregion

        #region Zombie Management

        private interface IZombieOwner
        {
            // Killed by damage. The owner may choose to replace it.
            void OnZombieDied(ScarecrowNPC zombie);

            // Removed for any other reason (water, external kill). Just forget it.
            void OnZombieRemoved(ScarecrowNPC zombie);
        }

        private ScarecrowNPC CreateZombie(Vector3 position, Configuration.ZombieSettings settings, IZombieOwner owner)
        {
            var zombie = GameManager.server.CreateEntity(ScarecrowPrefab, position) as ScarecrowNPC;
            if (zombie == null)
                return null;

            zombie.EnableSaving(false); // never persist into the save; we respawn them ourselves
            zombie.Spawn();
            zombie.displayName = settings.DisplayName;

            BaseNavigator navigator;
            if (zombie.TryGetComponent(out navigator))
            {
                navigator.ForceToGround();
                navigator.PlaceOnNavMesh(0f);
            }

            zombie.SetMaxHealth(settings.Health);
            zombie.SetHealth(settings.Health);

            GiveKit(zombie, settings.Kits);

            if (!_config.Behaviour.ThrowGrenades)
            {
                RemoveItems(zombie.inventory.containerBelt, GrenadeItemId);
                RemoveItems(zombie.inventory.containerMain, GrenadeItemId);
                ItemManager.DoRemoves();
            }

            _zombies[zombie] = owner;

            if (_corpseNames.Count >= MaxRememberedCorpses)
                _corpseNames.Clear();
            _corpseNames[zombie.userID] = settings.DisplayName;

            return zombie;
        }

        // Called by owners for their own scheduled despawns (end of wave, leash, despawnall).
        private void Despawn(ScarecrowNPC zombie, bool allowCorpse)
        {
            if (zombie == null)
                return;

            _zombies.Remove(zombie);
            _submerged.Remove(zombie);

            if (!IsAlive(zombie))
                return;

            if (allowCorpse && _config.Destroy.LeaveCorpse)
                zombie.Die();
            else
                zombie.AdminKill();
        }

        private void GiveKit(ScarecrowNPC zombie, List<string> kits)
        {
            if (kits == null || kits.Count == 0 || _kits == null || !_kits.IsLoaded)
                return;

            string kit = kits.GetRandom();
            zombie.inventory.containerWear.Clear();
            ItemManager.DoRemoves();

            object result = _kits.Call("GiveKit", zombie, kit);
            if (result is string && _warnedKits.Add(kit))
                PrintWarning($"Kits refused kit '{kit}': {result}");
        }

        private static void RemoveItems(ItemContainer container, int itemId)
        {
            if (container == null)
                return;

            for (int i = container.itemList.Count - 1; i >= 0; i--)
            {
                Item item = container.itemList[i];
                if (item.info.itemid == itemId)
                    item.Remove();
            }
        }

        private void CheckSubmerged()
        {
            if (_zombies.Count == 0)
                return;

            var drowned = Pool.Get<List<ScarecrowNPC>>();
            foreach (ScarecrowNPC zombie in _zombies.Keys)
            {
                if (!IsAlive(zombie))
                    continue;

                if (WaterLevel.GetWaterDepth(zombie.transform.position, true, true) > _config.Behaviour.SubmergedDepth)
                {
                    float seconds;
                    _submerged.TryGetValue(zombie, out seconds);
                    seconds += WaterCheckInterval;
                    _submerged[zombie] = seconds;

                    if (seconds >= _config.Behaviour.WaterDespawnSeconds)
                        drowned.Add(zombie);
                }
                else
                {
                    _submerged.Remove(zombie);
                }
            }

            foreach (ScarecrowNPC zombie in drowned)
            {
                IZombieOwner owner;
                if (_zombies.TryGetValue(zombie, out owner))
                    owner.OnZombieRemoved(zombie);
                Despawn(zombie, false);
            }
            Pool.FreeUnmanaged(ref drowned);
        }

        private bool ShouldIgnoreTarget(BaseEntity target)
        {
            if (_ignoredPrefabs.Count > 0 && _ignoredPrefabs.Contains(target.ShortPrefabName))
                return true;

            var player = target as BasePlayer;
            if (player == null)
                return false;

            if (IsRealPlayer(player))
            {
                if (permission.UserHasPermission(player.UserIDString, PermIgnore))
                    return true;
                return !_config.Behaviour.AttackSleepers && player.IsSleeping();
            }

            return _config.Behaviour.IgnoreHumanNpc && !(player is ScientistNPC) && !(player is ScarecrowNPC);
        }

        #endregion

        #region Spawn Positions

        private bool TryGetRandomPlayer(out BasePlayer player)
        {
            var candidates = Pool.Get<List<BasePlayer>>();
            foreach (BasePlayer p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsDead() || p.IsSleeping() || p.IsFlying || p.InSafeZone())
                    continue;
                if (permission.UserHasPermission(p.UserIDString, PermIgnore))
                    continue;
                if (_vanish != null && _vanish.Call("IsInvisible", p) is true)
                    continue;
                candidates.Add(p);
            }

            player = candidates.Count > 0 ? candidates.GetRandom() : null;
            Pool.FreeUnmanaged(ref candidates);
            return player != null;
        }

        private bool TryGetPositionNear(Vector3 center, float minDistance, float maxDistance, out Vector3 position)
        {
            if (maxDistance < minDistance)
                maxDistance = minDistance;

            for (int i = 0; i < 10; i++)
            {
                Vector2 dir = Random.insideUnitCircle.normalized;
                float dist = Random.Range(minDistance, maxDistance);
                position = new Vector3(center.x + dir.x * dist, 0f, center.z + dir.y * dist);
                position.y = TerrainMeta.HeightMap.GetHeight(position) + 0.5f;

                if (IsValidSpawnPosition(position))
                    return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool TryGetRandomMapPosition(out Vector3 position)
        {
            Vector3 origin = TerrainMeta.Position;
            Vector3 size = TerrainMeta.Size;

            for (int i = 0; i < 15; i++)
            {
                position = new Vector3(origin.x + Random.Range(0f, size.x), 0f, origin.z + Random.Range(0f, size.z));
                position.y = TerrainMeta.HeightMap.GetHeight(position) + 0.5f;

                if (IsValidSpawnPosition(position))
                    return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool TryGetMonumentPosition(Vector3 center, float radius, out Vector3 position)
        {
            for (int i = 0; i < 10; i++)
            {
                Vector2 offset = Random.insideUnitCircle * radius;
                Vector3 sample = new Vector3(center.x + offset.x, 0f, center.z + offset.y);
                sample.y = TerrainMeta.HeightMap.GetHeight(sample);

                // Monuments are full of concrete and buildings, so snap to the walkable navmesh instead of raw terrain.
                NavMeshHit hit;
                if (NavMesh.SamplePosition(sample, out hit, 8f, NavMesh.AllAreas))
                    sample = hit.position;

                position = sample + Vector3.up * 0.5f;
                if (IsValidSpawnPosition(position))
                    return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool IsValidSpawnPosition(Vector3 position)
        {
            return !AntiHack.TestInsideTerrain(position)
                   && !Physics.CheckSphere(position + Vector3.up, 0.5f, _spawnBlockMask, QueryTriggerInteraction.Ignore)
                   && WaterLevel.GetWaterDepth(position, true, true) <= 0.25f;
        }

        #endregion

        #region Spawn Wave

        private class SpawnWave : IZombieOwner
        {
            private enum State { Idle, Spawning, Active, Despawning }

            public readonly Configuration.SpawnWave Config;
            public int DaysSinceLastSpawn;

            private readonly NightZombies _plugin;
            private readonly HashSet<ScarecrowNPC> _zombies = new HashSet<ScarecrowNPC>();
            private readonly WaitForSeconds _spawnDelay = new WaitForSeconds(0.1f);

            private State _state = State.Idle;
            private bool _cycleRolled;
            private bool _forced;
            private Coroutine _routine;
            private Timer _forceTimer;

            public SpawnWave(NightZombies plugin, Configuration.SpawnWave config)
            {
                _plugin = plugin;
                Config = config;
            }

            public int Count => _zombies.Count;
            public string StateName => _forced && _state != State.Idle ? "Forced" : _state.ToString();
            private bool IsOut => _state == State.Spawning || _state == State.Active;
            private bool InWindow => IsWithinWindow(Config.SpawnTime, Config.DestroyTime);

            public void Tick()
            {
                if (InWindow)
                {
                    // Roll the chance once per window, not once per in-game minute.
                    if (_cycleRolled)
                        return;
                    _cycleRolled = true;

                    if ((!IsOut || _forced)
                        && DaysSinceLastSpawn > Config.Chance.Days
                        && Random.Range(0f, 100f) < Config.Chance.Chance)
                    {
                        DaysSinceLastSpawn = 0;
                        Begin(false);
                    }
                }
                else
                {
                    _cycleRolled = false;
                    if (IsOut && !_forced)
                        End();
                }
            }

            public void ForceSpawn()
            {
                if (IsOut)
                {
                    // Already out - just top the population back up.
                    StartRoutine(SpawnRoutine());
                    return;
                }

                Begin(true);
                _forceTimer = _plugin.timer.Once(ForceSpawnDuration, () =>
                {
                    _forceTimer = null;
                    if (_forced)
                        End();
                });
            }

            public void DespawnNow()
            {
                StopRoutine();
                CancelForce();
                DespawnSnapshot(false);
                _state = State.Idle;
            }

            public void Shutdown() => DespawnNow();

            public void OnZombieDied(ScarecrowNPC zombie)
            {
                if (!_zombies.Remove(zombie))
                    return;

                if (_state == State.Active && Config.RespawnKilled)
                    SpawnOne();
            }

            public void OnZombieRemoved(ScarecrowNPC zombie) => _zombies.Remove(zombie);

            private void Begin(bool forced)
            {
                CancelForce();
                _forced = forced;
                _state = State.Spawning;
                StartRoutine(SpawnRoutine());
            }

            private void End()
            {
                CancelForce();
                _state = State.Despawning;
                StartRoutine(DespawnRoutine());
            }

            private IEnumerator SpawnRoutine()
            {
                int before = _zombies.Count;
                int attempts = Config.Zombies.Population * 2;
                while (_zombies.Count < Config.Zombies.Population && attempts-- > 0)
                {
                    SpawnOne();
                    yield return _spawnDelay;
                }

                _state = State.Active;
                _routine = null;

                int spawned = _zombies.Count - before;
                if (spawned > 0 && _plugin._config.Broadcast.DoBroadcast)
                    _plugin.BroadcastLang("ChatBroadcast", spawned, Config.WaveName);
            }

            private IEnumerator DespawnRoutine()
            {
                var snapshot = new List<ScarecrowNPC>(_zombies);
                foreach (ScarecrowNPC zombie in snapshot)
                {
                    // Remove one at a time so an interrupted despawn leaves the rest tracked.
                    _zombies.Remove(zombie);
                    _plugin.Despawn(zombie, true);
                    yield return _spawnDelay;
                }

                _state = State.Idle;
                _forced = false;
                _routine = null;
            }

            private void DespawnSnapshot(bool allowCorpse)
            {
                var snapshot = Pool.Get<List<ScarecrowNPC>>();
                snapshot.AddRange(_zombies);
                _zombies.Clear();
                foreach (ScarecrowNPC zombie in snapshot)
                    _plugin.Despawn(zombie, allowCorpse);
                Pool.FreeUnmanaged(ref snapshot);
                _forced = false;
            }

            private void SpawnOne()
            {
                if (_zombies.Count >= Config.Zombies.Population)
                    return;

                Vector3 position;
                BasePlayer target;
                bool found = Config.SpawnNearPlayers
                             && BasePlayer.activePlayerList.Count >= Config.MinNearPlayers
                             && _plugin.TryGetRandomPlayer(out target)
                             && _plugin.TryGetPositionNear(target.transform.position, Config.MinDistance, Config.MaxDistance, out position);

                if (!found && !_plugin.TryGetRandomMapPosition(out position))
                    return;

                ScarecrowNPC zombie = _plugin.CreateZombie(position, Config.Zombies, this);
                if (zombie != null)
                    _zombies.Add(zombie);
            }

            private void StartRoutine(IEnumerator routine)
            {
                StopRoutine();
                _routine = ServerMgr.Instance.StartCoroutine(routine);
            }

            private void StopRoutine()
            {
                if (_routine != null && ServerMgr.Instance != null)
                    ServerMgr.Instance.StopCoroutine(_routine);
                _routine = null;
            }

            private void CancelForce()
            {
                _forceTimer?.Destroy();
                _forceTimer = null;
            }
        }

        #endregion

        #region Monument Spawner

        private class MonumentSpawner : IZombieOwner
        {
            private class Site
            {
                public string Name;
                public Vector3 Center;
                public readonly HashSet<ScarecrowNPC> Zombies = new HashSet<ScarecrowNPC>();
            }

            private readonly NightZombies _plugin;
            private readonly Configuration.MonumentSettings _config;
            private readonly List<Site> _sites = new List<Site>();
            private readonly Dictionary<ScarecrowNPC, Site> _owners = new Dictionary<ScarecrowNPC, Site>();
            private readonly WaitForSeconds _spawnDelay = new WaitForSeconds(0.1f);

            private Timer _checkTimer;
            private Coroutine _routine;

            public MonumentSpawner(NightZombies plugin)
            {
                _plugin = plugin;
                _config = plugin._config.Monument;
            }

            public int SiteCount => _sites.Count;
            public int Count => _owners.Count;

            public void Initialize()
            {
                if (_config.Population <= 0 || _config.Monuments.Count == 0 || TerrainMeta.Path == null)
                    return;

                var keywords = _config.Monuments.ConvertAll(Normalize);
                foreach (MonumentInfo monument in TerrainMeta.Path.Monuments)
                {
                    if (monument == null)
                        continue;

                    string name = Normalize(monument.name);
                    if (!keywords.Exists(k => k.Length > 0 && name.Contains(k)))
                        continue;

                    _sites.Add(new Site { Name = monument.name, Center = monument.transform.position });
                }

                if (_sites.Count == 0)
                {
                    _plugin.PrintWarning("No monuments on this map matched the configured Monument List - monument zombies are disabled");
                    return;
                }

                _plugin.Puts($"Monument zombies active at {_sites.Count} monument(s)");
                Refill();
                _checkTimer = _plugin.timer.Every(Mathf.Max(5f, _config.CheckInterval), Check);
            }

            public void OnZombieDied(ScarecrowNPC zombie) => Forget(zombie);
            public void OnZombieRemoved(ScarecrowNPC zombie) => Forget(zombie);

            public void DespawnAll()
            {
                StopRoutine();
                var all = Pool.Get<List<ScarecrowNPC>>();
                all.AddRange(_owners.Keys);
                foreach (ScarecrowNPC zombie in all)
                {
                    Forget(zombie);
                    _plugin.Despawn(zombie, false);
                }
                Pool.FreeUnmanaged(ref all);
            }

            public void Shutdown()
            {
                _checkTimer?.Destroy();
                DespawnAll();
            }

            private void Check()
            {
                // Pull back zombies that wandered too far from their monument.
                if (_config.MaxRoamDistance > 0f)
                {
                    float maxSqr = _config.MaxRoamDistance * _config.MaxRoamDistance;
                    var strays = Pool.Get<List<ScarecrowNPC>>();
                    foreach (KeyValuePair<ScarecrowNPC, Site> pair in _owners)
                    {
                        if (IsAlive(pair.Key) && (pair.Key.transform.position - pair.Value.Center).sqrMagnitude > maxSqr)
                            strays.Add(pair.Key);
                    }
                    foreach (ScarecrowNPC zombie in strays)
                    {
                        Forget(zombie);
                        _plugin.Despawn(zombie, false);
                    }
                    Pool.FreeUnmanaged(ref strays);
                }

                Refill();
            }

            private void Refill()
            {
                if (_routine != null)
                    return;
                _routine = ServerMgr.Instance.StartCoroutine(RefillRoutine());
            }

            private IEnumerator RefillRoutine()
            {
                foreach (Site site in _sites)
                {
                    site.Zombies.RemoveWhere(z => !IsAlive(z));
                    int missing = _config.Population - site.Zombies.Count;
                    for (int i = 0; i < missing; i++)
                    {
                        Vector3 position;
                        if (!_plugin.TryGetMonumentPosition(site.Center, _config.SpawnRadius, out position))
                            break;

                        ScarecrowNPC zombie = _plugin.CreateZombie(position, _config.Zombies, this);
                        if (zombie != null)
                        {
                            site.Zombies.Add(zombie);
                            _owners[zombie] = site;
                        }
                        yield return _spawnDelay;
                    }
                }
                _routine = null;
            }

            private void Forget(ScarecrowNPC zombie)
            {
                Site site;
                if (zombie != null && _owners.TryGetValue(zombie, out site))
                {
                    site.Zombies.Remove(zombie);
                    _owners.Remove(zombie);
                }
            }

            private void StopRoutine()
            {
                if (_routine != null && ServerMgr.Instance != null)
                    ServerMgr.Instance.StopCoroutine(_routine);
                _routine = null;
            }

            // "assets/.../military_tunnel_1.prefab" -> "militarytunnel"; "Water Treatment" -> "watertreatment"
            private static string Normalize(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return string.Empty;

                int slash = value.LastIndexOf('/');
                if (slash >= 0)
                    value = value.Substring(slash + 1);
                value = StripPrefabExtension(value);

                var sb = new StringBuilder(value.Length);
                foreach (char c in value)
                {
                    if (char.IsLetter(c))
                        sb.Append(char.ToLowerInvariant(c));
                }
                return sb.ToString();
            }
        }

        #endregion

        #region Helpers

        private static bool IsAlive(BaseEntity entity) => entity != null && !entity.IsDestroyed;

        // userID is an EncryptedValue<ulong> on current Rust builds, so cast before using ulong extensions.
        private static bool IsRealPlayer(BasePlayer player) => ((ulong)player.userID).IsSteamId();

        private static string StripPrefabExtension(string value) =>
            value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 7) : value;

        private bool HasAnyKits()
        {
            if (_config.Monument.Zombies.Kits.Count > 0)
                return true;
            return _config.SpawnWaves.Exists(w => w.Zombies.Kits.Count > 0);
        }

        #endregion

        #region Data

        private Dictionary<string, int> LoadData()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, int>>(DataFileName)
                       ?? new Dictionary<string, int>();
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not read data file, spawn day counters reset: {ex.Message}");
                return new Dictionary<string, int>();
            }
        }

        private void SaveData()
        {
            if (_waves.Count == 0)
                return;

            var data = new Dictionary<string, int>();
            foreach (SpawnWave wave in _waves)
                data[wave.Config.WaveName] = wave.DaysSinceLastSpawn;
            Interface.Oxide.DataFileSystem.WriteObject(DataFileName, data);
        }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Config Version")]
            public string Version;

            [JsonProperty("Spawn Waves")]
            public List<SpawnWave> SpawnWaves = new List<SpawnWave>();

            [JsonProperty("Monument Settings")]
            public MonumentSettings Monument = new MonumentSettings();

            [JsonProperty("Destroy Settings")]
            public DestroySettings Destroy = new DestroySettings();

            [JsonProperty("Behaviour Settings")]
            public BehaviourSettings Behaviour = new BehaviourSettings();

            [JsonProperty("Broadcast Settings")]
            public ChatSettings Broadcast = new ChatSettings();

            public class ZombieSettings
            {
                [JsonProperty("Display Name")]
                public string DisplayName = "Scarecrow";

                [JsonProperty("Scarecrow Health")]
                public float Health = 200f;

                [JsonProperty("Scarecrow Kits")]
                public List<string> Kits = new List<string>();
            }

            public class WaveZombieSettings : ZombieSettings
            {
                [JsonProperty("Scarecrow Population (total amount)")]
                public int Population = 50;
            }

            public class ChanceSettings
            {
                [JsonProperty("Chance per cycle")]
                public float Chance = 100f;

                [JsonProperty("Days between spawns")]
                public int Days = 0;

                // Pre-6.0 configs spelt this key wrong; read it once, then drop it.
                [JsonProperty("Days betewen spawn")]
                public int? LegacyDays;

                public bool ShouldSerializeLegacyDays() => false;
            }

            public class SpawnWave
            {
                [JsonProperty("Wave Name")]
                public string WaveName = "Night Wave";

                [JsonProperty("Spawn Time")]
                public float SpawnTime = 19.8f;

                [JsonProperty("Destroy Time")]
                public float DestroyTime = 7.3f;

                [JsonProperty("Spawn near players")]
                public bool SpawnNearPlayers = false;

                [JsonProperty("Min pop for near player spawn")]
                public int MinNearPlayers = 10;

                [JsonProperty("Min distance from player")]
                public float MinDistance = 30f;

                [JsonProperty("Max distance from player")]
                public float MaxDistance = 60f;

                [JsonProperty("Respawn killed zombies while wave is active")]
                public bool RespawnKilled = true;

                [JsonProperty("Zombie Settings")]
                public WaveZombieSettings Zombies = new WaveZombieSettings();

                [JsonProperty("Chance Settings")]
                public ChanceSettings Chance = new ChanceSettings();
            }

            public class MonumentSettings
            {
                [JsonProperty("Monument Zombie Population")]
                public int Population = 3;

                [JsonProperty("Spawn radius around monument")]
                public float SpawnRadius = 40f;

                [JsonProperty("Max roam distance before respawning at monument (0 = off)")]
                public float MaxRoamDistance = 150f;

                [JsonProperty("Respawn check interval (seconds)")]
                public float CheckInterval = 60f;

                [JsonProperty("Monument List")]
                public List<string> Monuments = DefaultMonuments();

                [JsonProperty("Zombie Settings")]
                public ZombieSettings Zombies = new ZombieSettings { DisplayName = "Monument Zombie", Health = 300f };
            }

            public class DestroySettings
            {
                [JsonProperty("Leave Corpse, when destroyed")]
                public bool LeaveCorpse = false;

                [JsonProperty("Leave Corpse, when killed by player")]
                public bool LeaveCorpseKilled = true;

                [JsonProperty("Spawn Loot")]
                public bool SpawnLoot = true;

                [JsonProperty("Half bodybag despawn time")]
                public bool HalfBodybagDespawn = true;
            }

            public class BehaviourSettings
            {
                [JsonProperty("Attack sleeping players")]
                public bool AttackSleepers = false;

                [JsonProperty("Zombies attacked by outpost sentries")]
                public bool SentriesAttackZombies = true;

                [JsonProperty("Throw Grenades")]
                public bool ThrowGrenades = true;

                [JsonProperty("Ignore Human NPCs")]
                public bool IgnoreHumanNpc = true;

                [JsonProperty("Despawn after fully submerged for (seconds, 0 = off)")]
                public float WaterDespawnSeconds = 10f;

                [JsonProperty("Water depth counted as fully submerged")]
                public float SubmergedDepth = 1.8f;

                [JsonProperty("Ignored entities (full entity shortname)")]
                public List<string> Ignored = new List<string> { "scientistjunkpile", "scarecrow" };
            }

            public class ChatSettings
            {
                [JsonProperty("Broadcast spawn amount")]
                public bool DoBroadcast = false;
            }

            // Matched against monument prefab file names, ignoring case, spaces, underscores and numbers.
            public static List<string> DefaultMonuments() => new List<string>
            {
                "airfield", "powerplant", "trainyard", "military_tunnel", "water_treatment",
                "launch_site", "junkyard", "satellite_dish", "sphere_tank", "harbor",
                "radtown", "excavator", "gas_station", "supermarket", "warehouse"
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    throw new JsonException("Config file is empty");

                MigrateConfig();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Config file is invalid, using default values (your file was NOT overwritten): {ex.Message}");
                LoadDefaultConfig();
            }
        }

        private void MigrateConfig()
        {
            if (_config.SpawnWaves == null)
                _config.SpawnWaves = new List<Configuration.SpawnWave>();

            foreach (Configuration.SpawnWave wave in _config.SpawnWaves)
            {
                if (wave.Chance.LegacyDays.HasValue)
                {
                    wave.Chance.Days = wave.Chance.LegacyDays.Value;
                    wave.Chance.LegacyDays = null;
                }
            }

            if (_config.Version == null)
            {
                // Pre-6.0 monument names never matched real monuments (and included safe zones / oil rigs).
                _config.Monument.Monuments = Configuration.DefaultMonuments();
                for (int i = 0; i < _config.Behaviour.Ignored.Count; i++)
                    _config.Behaviour.Ignored[i] = StripPrefabExtension(_config.Behaviour.Ignored[i]);

                PrintWarning("Updated config from a pre-6.0 version: monument list was reset to working monument names");
            }

            _config.Version = CurrentConfigVersion;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration
        {
            Version = CurrentConfigVersion,
            SpawnWaves = new List<Configuration.SpawnWave>
            {
                new Configuration.SpawnWave
                {
                    WaveName = "Night Wave",
                    SpawnTime = 19.8f,
                    DestroyTime = 7.3f,
                    SpawnNearPlayers = true,
                    MinNearPlayers = 10,
                    Zombies = new Configuration.WaveZombieSettings { DisplayName = "Scarecrow", Population = 50, Health = 200f }
                }
            }
        };

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Localisation

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command.",
                ["ForceSpawned"] = "Forced spawn started for {0}. Zombies will despawn in {1} minutes.",
                ["UnknownWave"] = "No wave named '{0}'. Waves: {1}",
                ["DespawnedAll"] = "All zombies have been despawned. Monument zombies will return within {0} seconds.",
                ["ChatBroadcast"] = "<color=#c0392b>[Night Zombies]</color> {0} zombies have risen ({1})",
                ["StatusHeader"] = "Night Zombies - time {0}, {1} zombies alive",
                ["StatusWave"] = "  {0}: {1}, {2}/{3} zombies, window {4}-{5}, days since spawn {6}",
                ["StatusMonuments"] = "  Monuments: {0} sites, {1} zombies"
            }, this);
        }

        private string Lang(string key, IPlayer player, params object[] args) =>
            string.Format(lang.GetMessage(key, this, player.IsServer ? null : player.Id), args);

        private void BroadcastLang(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
                player.ChatMessage(string.Format(lang.GetMessage(key, this, player.UserIDString), args));
        }

        #endregion
    }
}
