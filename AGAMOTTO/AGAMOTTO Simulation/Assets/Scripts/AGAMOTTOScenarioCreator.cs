using Google.Protobuf.WellKnownTypes;
using Photon.Pun.Demo.PunBasics;
using PimDeWitte.UnityMainThreadDispatcher;
using Ride;
using Ride.AI;
using Ride.Entities;
using Ride.Examples;
using Ride.IO;
using Ride.Movement;
using Ride.Terrain;
using Ride.WorldState;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Transactions;
using System.Xml;
using Unity.AI.Navigation;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Serialization;

namespace AGAMOTTO
{

    public enum SimStateType
    {
        Idle,
        Running,
        Stopped,
        GeneratingUnits,
        LoadingTerrain,
        TerrainLoaded
    }

    public class SimState
    {
        public SimStateType State { get; set; }
        public DateTime Timestamp { get; set; }
    }



    public class AGAMOTTOScenarioCreator : RideBaseMinimal
    {
        [Header("TAK / CoT")]
        [Tooltip("Listen for WinTAK/ATAK length-prefixed CoT on Play. No Python orchestrator required.")]
        public bool enableCotTcpListener = true;
        [Tooltip("TCP port the WinTAK plugin Send Map Data should target.")]
        public int cotListenPort = AGAMOTTO_CotTcpListener.DefaultPort;

        AGAMOTTO_CotTcpListener _cotTcpListener;

        static bool IsFriendlyCotType(string cotType)
        {
            if (string.IsNullOrEmpty(cotType))
                return false;
            string[] segments = cotType.Split('-');
            return segments.Length >= 2 && segments[0] == "a" && segments[1].StartsWith("f");
        }

        static bool IsHostileCotType(string cotType)
        {
            if (string.IsNullOrEmpty(cotType))
                return false;
            string[] segments = cotType.Split('-');
            return segments.Length >= 2 && segments[0] == "a" && segments[1].StartsWith("h");
        }

        public bool NavMeshBuilt { get; private set; } = false;
        protected TerrainLoader m_terrainLoader;
        protected string[] m_terrainPathsTileNames;
        protected string[] m_terrainPathsLmabNames;
        protected List<Ride.Terrain.ITerrain> m_terrains = new List<Ride.Terrain.ITerrain>();
        protected Dictionary<string, RideID> entityList;
        string _rlFriendlyEntityUid;
        string _lastDispatchMode = "Training";
        const string EvaluationFriendlyPrefab = "ChrUsaArmyInfantryAcu01Prefab";
        const string TrainingFriendlyPrefab = "ChrUsaArmyInfantryOcpMLPrefab";
        readonly HashSet<string> _spawnClaimedUids = new HashSet<string>();
        readonly HashSet<string> _spawnClaimedCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<(bool friendly, double lat, double lon)> _spawnedLatLon = new List<(bool, double, double)>();
        const double DuplicateSpawnRadiusM = 40.0;
        bool _episodeRespawnInProgress;

        public static AGAMOTTOScenarioCreator Active { get; private set; }

        string FriendlyPrefabForMode() =>
            string.Equals(_lastDispatchMode, "Training", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_lastDispatchMode, "Inference", StringComparison.OrdinalIgnoreCase)
                ? TrainingFriendlyPrefab
                : EvaluationFriendlyPrefab;

        GameObject GetAttackToDestroyMissionPrefab() =>
            m_settingsPrefabGameObjects?.FirstOrDefault(obj => obj.name == "AttackToDestroy");

        public bool EpisodeRespawnInProgress => _episodeRespawnInProgress;

        /// <summary>Mode of the most recent TAK send ("Evaluation", "Training", "Inference").</summary>
        public string LastDispatchMode => _lastDispatchMode;

        /// <summary>TAK uid to RIDE tactical group for the units currently spawned (read-only view).</summary>
        public IReadOnlyDictionary<string, RideID> SpawnedGroups => entityList;
        protected bool m_loadTrees { get => m_terrainLoader.m_loadTrees; set => m_terrainLoader.m_loadTrees = value; }
        protected bool m_cameraStartPosition { get => m_terrainLoader.m_cameraStartPosition; set => m_terrainLoader.m_cameraStartPosition = value; }
        protected int m_loadRadius { get => m_terrainLoader.m_renderRadius; set => m_terrainLoader.m_renderRadius = value; }
        protected float m_cpuPriority { get => m_terrainLoader.m_cpuPriority; set => m_terrainLoader.m_cpuPriority = value; }
        protected SimState simulationState;
        protected int m_tileEntrySelected;
        protected int m_tileLodSelected;
        protected int m_lmabEntrySelected;
        protected int m_lmabLodSelected;
        protected int m_colliderSelected;

        protected Dictionary<int, TerrainSO> tileMap = new Dictionary<int, TerrainSO>();
        protected Dictionary<int, TerrainSO> lmabMap = new Dictionary<int, TerrainSO>();

        float m_terrainLoadProgress = 0;
        float m_startTime = 0;

        IWorldStateSystem worldStateSystem;
        UnityGameObjectSystem unityGameObjectSystem;

        [FormerlySerializedAs("sceneGameObjects")]
        public GameObject[] m_settingsPrefabGameObjects;


        public static class ExportData
        {
            /// <summary>
            /// Collects runtime data and writes to a JSON file.
            /// </summary>

            public static void WriteStatus(string TerrainStatus = "Invalid")
            {
                // Example: Gather the values you care about
                var payload = new StatusPayload
                {
                    status = TerrainStatus
                };


                // Writes next to the project root (Editor) or next to the executable (Player)
                string outputPath = Path.Combine(Application.dataPath, "status.json");


                // Make sure directory exists
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Serialize using Unity's JsonUtility (requires a serializable class)
                string json = JsonUtility.ToJson(payload, prettyPrint: true);
                File.WriteAllText(outputPath, json);

                Debug.Log($"[ExportData] Wrote status to: {Path.GetFullPath(outputPath)}");
                //Debug.Log($"[ExportData] Payload:\n{json}");
            }

            [System.Serializable]
            private class StatusPayload
            {
                public string status;
            }

            public static void RemoveStatus()
            {
                string outputPath = Path.Combine(Application.dataPath, "status.json");
                File.Delete(outputPath);
            }
        }



        protected override void Start()
        {
            simulationState = new SimState();
            simulationState.State = SimStateType.LoadingTerrain;
            base.Start();
            ExportData.WriteStatus("Invalid");

            entityList = new Dictionary<string, RideID>();
            _spawnClaimedUids.Clear();
            _spawnClaimedCallsigns.Clear();
            _spawnedLatLon.Clear();
            _episodeRespawnInProgress = false;
            Active = this;
            worldStateSystem = Globals.api.worldStateSystem;
            worldStateSystem.AddListener<TerrainLoadedEvent>(WorldEvent.terrainLoaded, OnTerrainLoaded);
            LoadTerrain();

            Globals.api.attackSystem.flags =
                Ride.Combat.AttackSystemFlags.Auto_Attack | Ride.Combat.AttackSystemFlags.Acquire_Targets;

            //create a watcher to monitor input file directory for new or modified scenario files
            string filePath = Application.streamingAssetsPath;
            //Debug.Log(filePath);

            // TCP is the live TAK path. Watching StreamingAssets as well double-spawns
            // leftover entities/*.xml plus the WinTAK Send payload.
            if (!enableCotTcpListener)
                StartCoroutine(CreateWatcher(filePath));
            StartCoroutine(CreateNavMesh());
            StartCotTcpListener();

            // Causal experiments: idle until started from its on-screen panel (F8).
            if (GetComponent<AGAMOTTO_CausalRecorder>() == null)
                gameObject.AddComponent<AGAMOTTO_CausalRecorder>();
        }

        void StartCotTcpListener()
        {
            if (!enableCotTcpListener)
                return;
            StopCotTcpListener();
            _cotTcpListener = new AGAMOTTO_CotTcpListener(cotListenPort, IngestCotXml);
            _cotTcpListener.Start();
        }

        void StopCotTcpListener()
        {
            _cotTcpListener?.Stop();
            _cotTcpListener = null;
        }

        /// <summary>Parse a CoT XML payload received over TCP (WinTAK Send Map Data).</summary>
        public void IngestCotXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                ParseCotAndCreateEntity(doc);
            }
            catch (Exception ex)
            {
                Debug.LogError($"AGAMOTTO: failed to parse inbound CoT XML: {ex.Message}");
            }
        }

        protected override void Update()
        {
            base.Update();
        }

        protected void LoadTerrain()
        {
            m_terrainLoader = FindObjectOfType<TerrainLoader>();
            if (m_terrainLoader == null)
                RideLogSystem.LogError("There is no TerrainLoader in this scene. The scene will not work properly without one.");

            var namesTile = new List<string>();
            var namesLmab = new List<string>();
            foreach (var terrain in m_terrainLoader.TerrainDatasets.terrains)
            {
                if (!string.IsNullOrEmpty(terrain.tilePath))
                {
                    namesTile.Add(terrain.terrainName);
                    tileMap.Add(namesTile.Count - 1, terrain);
                }

                if (!string.IsNullOrEmpty(terrain.lmabPath))
                {
                    namesLmab.Add(terrain.terrainName);
                    lmabMap.Add(namesLmab.Count - 1, terrain);
                }
            }
            m_terrainPathsTileNames = namesTile.ToArray();
            m_terrainPathsLmabNames = namesLmab.ToArray();

            OnGUILoadTerrainSplitLmab();
        }

        /// <summary>
        /// The heart of the scenario generator. Received a parsed Cot Message, creates units as shown in the CoT (if CoT is for a unit), and begins applicable scenario run based on AGAMOTTO dispatch mode
        /// </summary>
        private void ParseCotAndCreateEntity(XmlDocument doc)
        {
            try
            {
                XmlNode root = doc.SelectSingleNode("/event");

                if (root != null)
                {


                    string uid = root.Attributes["uid"]?.Value;
                    string type = root.Attributes["type"]?.Value;
                    string time = root.Attributes["time"]?.Value;

                    XmlNode pointNode = root.SelectSingleNode("point");
                    string lat = pointNode?.Attributes["lat"]?.Value;
                    string lon = pointNode?.Attributes["lon"]?.Value;

                    XmlNode contactNode = root.SelectSingleNode("detail/contact");
                    string callsign = contactNode?.Attributes["callsign"]?.Value;
                    string mode = root.Attributes["mode"]?.Value;
                    // Hybrid/AWAC dispatch is retired: treat as Evaluation (spawn only).
                    // Raw ATAK CoT has no mode attribute — default to Evaluation so units still spawn.
                    if (string.IsNullOrWhiteSpace(mode)
                        || string.Equals(mode, "Hybrid", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(mode, "Hybrid", StringComparison.OrdinalIgnoreCase))
                            Debug.LogWarning("AGAMOTTO: Hybrid/AWAC CoT ignored — spawning as Evaluation.");
                        mode = "Evaluation";
                    }
                    _lastDispatchMode = mode;

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        if (type != null && type.StartsWith("a")
                            && !TryBeginEntitySpawnFromCot(uid, type, callsign, lat, lon))
                            return;

                        if (string.Equals(mode, "Training", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"Received a CoT Message in RL Training Mode");
                            if (type != null && type.StartsWith("a"))
                            {
                                Debug.Log($"Creating {type} Unit at Lat:{lat}, Lon:{lon}");
                                RideID unitCreated = CreateUnit(type, uid, double.Parse(lat), double.Parse(lon), false);
                                if (IsHostileCotType(type))
                                {
                                    Debug.Log(
                                        "AGAMOTTO: Hostile unit spawned (Red); Training mode ignored for a-h-* (no ML-Agents).");
                                }
                                else if (IsFriendlyCotType(type))
                                {
                                    Debug.Log("AGAMOTTO: Beginning RL training for friendly squad (Blue).");
                                    _rlFriendlyEntityUid = uid;
                                    StartRLTraining(unitCreated);
                                }
                                else
                                {
                                    Debug.LogWarning(
                                        $"AGAMOTTO: Unknown atomic CoT type '{type}' in Training mode; unit spawned without RL.");
                                }
                            }
                        }
                        else if (string.Equals(mode, "Inference", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"Received a CoT Message in Inference Mode");
                            if (type != null && type.StartsWith("a"))
                            {
                                RideID unitCreated = CreateUnit(type, uid, double.Parse(lat), double.Parse(lon), false);
                                if (IsFriendlyCotType(type))
                                {
                                    Debug.Log("AGAMOTTO: Beginning post-training inference for friendly squad (Blue).");
                                    StartRLTraining(unitCreated);
                                }
                            }
                        }
                        else if (string.Equals(mode, "Evaluation", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"Received a CoT Message in Evaluation Mode - Creating entity {type}");
                            if (type != null && type.StartsWith("a")
                                && !string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon))
                            {
                                RideID unitCreated = CreateUnit(type, uid, double.Parse(lat), double.Parse(lon), false);
                            }

                            if (type != null && type.EndsWith("r"))
                            {
                                RideVector3[] path = ParseRouteWorldPath(root);
                                if (path != null && path.Length >= 2)
                                {
                                    Debug.Log($"Attempting to create path for {callsign}");
                                    CreatePath(callsign, path);
                                }
                            }
                        }


                    });
                }




                else
                {
                    Debug.Log("No <root> node found.");
                }


            }
            catch (Exception ex)
            {
                Debug.Log(doc + "\r\n\r\n" + ex.Message);
                throw ex;
            }
        }
        protected virtual void OnTerrainLoaded(WorldEventMarker marker, TerrainLoadedEvent e)
        {
            Debug.Log("Terrain Finished Loading");
            // status.json "Valid" is polled by the eval suite; units must be allowed to spawn now,
            // not only after CreateNavMesh finishes (that was racing ahead of CoT delivery).
            simulationState.State = SimStateType.TerrainLoaded;
            ExportData.WriteStatus("Valid");
        }

        bool IsTerrainReadyForUnitSpawn() =>
            simulationState.State == SimStateType.TerrainLoaded
            || simulationState.State == SimStateType.Idle;

        static bool IsEntityOrRouteCotPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return false;
            string p = fullPath.Replace('\\', '/').ToLowerInvariant();
            return p.Contains("/entities/") || p.Contains("/routes/");
        }

        IEnumerator SpawnCotAfterTerrainReady(string cotFilePath)
        {
            const int maxFrames = 36000;
            for (int i = 0; i < maxFrames && !IsTerrainReadyForUnitSpawn(); i++)
                yield return null;

            if (!IsTerrainReadyForUnitSpawn())
            {
                Debug.LogError($"AGAMOTTO: Timed out waiting for terrain before spawning from {cotFilePath}");
                yield break;
            }

            yield return null;
            ProcessCotFileFromPath(cotFilePath, deferIfNotReady: false);
        }

        void ProcessCotFileFromPath(string fullPath, bool deferIfNotReady = true)
        {
            if (!IsEntityOrRouteCotPath(fullPath))
                return;

            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                if (deferIfNotReady && !IsTerrainReadyForUnitSpawn())
                {
                    Debug.Log($"AGAMOTTO: Deferring CoT spawn until terrain is ready: {fullPath}");
                    StartCoroutine(SpawnCotAfterTerrainReady(fullPath));
                    return;
                }

                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(fullPath);
                    ParseCotAndCreateEntity(doc);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"AGAMOTTO: Failed to load CoT from {fullPath}: {ex.Message}");
                }
            });
        }

        RideVector3[] ParseRouteWorldPath(XmlNode root)
        {
            XmlNode routeNode = root?.SelectSingleNode("detail/route");
            if (routeNode == null)
                return null;

            var points = new List<RideVector3>();
            foreach (XmlNode point in routeNode)
            {
                string plat = point.Attributes["lat"]?.Value;
                string plon = point.Attributes["lon"]?.Value;
                if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(plon))
                    continue;
                RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(
                    Convert.ToDouble(plat), Convert.ToDouble(plon));
                points.Add(scenPos);
            }
            return points.Count > 0 ? points.ToArray() : null;
        }

        bool CreatePath(string callsignOrUid, RideVector3[] path)
        {
            string entityKey = callsignOrUid;
            if (string.IsNullOrEmpty(entityKey) || !entityList.ContainsKey(entityKey))
            {
                Debug.Log(
                    "Entity associated with route does not exist in list; entity either does not exist "
                    + "or has not yet been created; please try again");
                return false;
            }

            if (simulationState.State != SimStateType.Idle && simulationState.State != SimStateType.TerrainLoaded)
            {
                Debug.Log("CreatePath refused — simulation not idle.");
                return false;
            }

            Globals.api.movementSystem.MoveGroupPath(entityList[entityKey], path, FormationProcedureType.Wedge);
            return true;
        }

        static bool IsFriendlyCotTypeFromUid(string uid) => !string.IsNullOrEmpty(uid);

        public bool IsATDMissionComplete(Ride.Team team)
        {
            
            IEnumerable<RideID> groupAgents = Globals.api.scenarioSystem.GetAgents(team);
            if (groupAgents == null) return false;

            IEnumerable<RideID> enemyAgents = Globals.api.scenarioSystem.GetAgents(Ride.Team.Red);
            if (enemyAgents == null) return false;

            bool groupDead = groupAgents != null && groupAgents.All(m => Globals.api.agentSystem.IsAgentDead(m));

            bool enemyDead = enemyAgents != null && enemyAgents.All(m => Globals.api.agentSystem.IsAgentDead(m));

            return (groupDead || enemyDead);
        }


        public async Task WaitUntilATDComplete(string tacticalGroupleader)
        {


            RideID tacticalGroup = entityList[tacticalGroupleader];

            bool actionComplete = IsATDMissionComplete(Ride.Team.Blue);

            Debug.Log("Waiting for ATD complete...");


            while (actionComplete) 
            {
                //need a backstop timeout for a stalemate
                await Task.Delay(1000); // non-blocking wait
                
            }

            Debug.Log("ATD mission complete!");
        }

        bool TryBeginEntitySpawnFromCot(string uid, string cotType = null, string callsign = null, string latStr = null, string lonStr = null)
        {
            if (string.IsNullOrEmpty(uid))
                return false;
            if (_episodeRespawnInProgress)
            {
                Debug.Log($"AGAMOTTO: Ignoring CoT spawn uid={uid} (episode respawn in progress).");
                return false;
            }

            if (entityList != null && entityList.ContainsKey(uid))
            {
                Debug.Log($"AGAMOTTO: Ignoring duplicate CoT for uid={uid} (unit already spawned).");
                return false;
            }

            if (!_spawnClaimedUids.Add(uid))
            {
                Debug.Log($"AGAMOTTO: Ignoring duplicate CoT for uid={uid} (spawn already claimed).");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(callsign) && !_spawnClaimedCallsigns.Add(callsign.Trim()))
            {
                Debug.Log($"AGAMOTTO: Ignoring duplicate CoT callsign={callsign} uid={uid}.");
                _spawnClaimedUids.Remove(uid);
                return false;
            }

            if (double.TryParse(latStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(lonStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon))
            {
                bool friendly = IsFriendlyCotType(cotType);
                foreach (var prev in _spawnedLatLon)
                {
                    if (prev.friendly != friendly)
                        continue;
                    if (HorizontalMeters(prev.lat, prev.lon, lat, lon) > DuplicateSpawnRadiusM)
                        continue;
                    Debug.Log(
                        $"AGAMOTTO: Ignoring near-duplicate {(friendly ? "friendly" : "hostile")} spawn "
                        + $"uid={uid} at {lat:F5},{lon:F5} (already spawned within {DuplicateSpawnRadiusM:0} m).");
                    _spawnClaimedUids.Remove(uid);
                    if (!string.IsNullOrWhiteSpace(callsign))
                        _spawnClaimedCallsigns.Remove(callsign.Trim());
                    return false;
                }
                _spawnedLatLon.Add((friendly, lat, lon));
            }

            return true;
        }

        static double HorizontalMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double metersPerDegLat = 111320.0;
            double dLat = (lat2 - lat1) * metersPerDegLat;
            double dLon = (lon2 - lon1) * metersPerDegLat * Math.Cos(lat1 * Math.PI / 180.0);
            return Math.Sqrt(dLat * dLat + dLon * dLon);
        }

        RideID CreateUnit(string cotType, string uid, double lat, double lng, bool imitationLearning)
        {
            var spec = new AGAMOTTO_EpisodeRespawn.UnitSpawnSpec
            {
                Uid = uid,
                CotType = cotType,
                Lat = lat,
                Lon = lng,
                ImitationLearning = imitationLearning,
            };
            return SpawnUnitFromSpec(spec, registerOnlyIfNew: true);
        }

        RideID SpawnUnitFromSpec(AGAMOTTO_EpisodeRespawn.UnitSpawnSpec spec, bool registerOnlyIfNew)
        {
            if (registerOnlyIfNew && entityList != null && entityList.ContainsKey(spec.Uid))
                return entityList[spec.Uid];

            if (simulationState.State != SimStateType.TerrainLoaded && simulationState.State != SimStateType.Idle)
            {
                Debug.Log("Terrain Not Loaded - Cannot Create Units");
                return new RideID(0, 0);
            }

            string cotType = spec.CotType;
            string[] segments = cotType.Split('-');
            RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(spec.Lat, spec.Lon);
            if (IsFriendlyCotType(cotType))
                AGAMOTTO_GeoAnchor.SetAnchor(spec.Lat, spec.Lon, scenPos.x, scenPos.z);

            // Causal trials move the unit by a logged random offset from its TAK point.
            if (AGAMOTTO_EpisodeRespawn.TryGetSceneOffset(spec.Uid, out float trialDx, out float trialDz))
            {
                scenPos = new RideVector3(scenPos.x + trialDx, scenPos.y, scenPos.z + trialDz);
                scenPos.y = Globals.api.terrainSystem.GetTerrainHeight(scenPos + new RideVector3(0, 1000, 0));
            }

            RideID created = new RideID(0, 0);
            switch (segments.Length >= 2 ? segments[1] : "")
            {
                case "f":
                    created = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(spec.Uid)
                    {
                        prefab = FriendlyPrefabForMode(),
                        rows = 2,
                        cols = 2,
                        position = scenPos,
                        rotation = RideQuaternion.Euler(0, -90, 0),
                        team = Ride.Team.Blue
                    });
                    break;

                case "h":
                {
                    int hostileCount;
                    int rows;
                    int cols;
                    if (spec.IsHostileGrid)
                    {
                        rows = spec.HostileRows;
                        cols = spec.HostileCols;
                        hostileCount = rows * cols;
                    }
                    else
                    {
                        hostileCount = UnityEngine.Random.Range(5, 7);
                        rows = hostileCount == 6 ? 2 : 1;
                        cols = hostileCount == 6 ? 3 : 5;
                        spec.HostileRows = rows;
                        spec.HostileCols = cols;
                        spec.IsHostileGrid = true;
                    }

                    created = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams(spec.Uid)
                    {
                        rows = rows,
                        cols = cols,
                        position = scenPos,
                        rotation = RideQuaternion.Euler(0, 0, 0),
                        team = Ride.Team.Red
                    });
                    Debug.Log(
                        $"AGAMOTTO: Red opposing team spawned count={hostileCount} grid={rows}×{cols} uid={spec.Uid}.");
                    break;
                }

                default:
                    Debug.Log($"CoT type invalid - cannot create unit! type={cotType}");
                    return new RideID(0, 0);
            }

            if (!AGAMOTTO_ATD_Agent.IsValidRideId(created))
            {
                Debug.LogError($"AGAMOTTO: Spawn failed for uid={spec.Uid} type={cotType}");
                _spawnClaimedUids.Remove(spec.Uid);
                return created;
            }

            if (IsFriendlyCotType(cotType))
            {
                Debug.Log(
                    $"AGAMOTTO: friendly squad spawned prefab="
                    + $"{FriendlyPrefabForMode()} uid={spec.Uid}.");
            }

            entityList[spec.Uid] = created;
            _spawnClaimedUids.Add(spec.Uid);
            simulationState.State = SimStateType.Idle;
            AGAMOTTO_EpisodeRespawn.RegisterSpawn(spec, IsFriendlyCotType(cotType));
            if (string.Equals(_lastDispatchMode, "Evaluation", StringComparison.OrdinalIgnoreCase))
            {
                AGAMOTTO_RideDestroy.StripMlComponentsOnTacticalGroup(created);
                AGAMOTTO_RideDestroy.StripEmbeddedMlAgentsInScene();
            }

            return created;
        }

        public bool TryRespawnRegisteredUnits(
            string rlFriendlyUid,
            out AGAMOTTO_EpisodeRespawn.RespawnResult result,
            bool attachRlPolicy = true)
        {
            result = default;
            if (entityList == null)
                return false;

            var specs = new List<AGAMOTTO_EpisodeRespawn.UnitSpawnSpec>();
            foreach (string uid in AGAMOTTO_EpisodeRespawn.GetRegisteredUids())
            {
                if (AGAMOTTO_EpisodeRespawn.TryGetSpec(uid, out var spec))
                    specs.Add(spec);
            }

            if (specs.Count == 0)
            {
                Debug.LogWarning("AGAMOTTO: Episode respawn — no registered spawn specs.");
                return false;
            }

            _episodeRespawnInProgress = true;
            try
            {
                var registeredGroups = new List<RideID>();
                foreach (var kv in entityList)
                    registeredGroups.Add(kv.Value);
                foreach (string uid in AGAMOTTO_EpisodeRespawn.GetRegisteredUids())
                {
                    if (entityList.TryGetValue(uid, out RideID groupId))
                        registeredGroups.Add(groupId);
                }

                AGAMOTTO_RideDestroy.DestroyAllCombatants(registeredGroups);
                AGAMOTTO_RideDestroy.SweepAllCombatTeams();
                entityList.Clear();
                _spawnClaimedUids.Clear();
                _spawnClaimedCallsigns.Clear();
                _spawnedLatLon.Clear();
                SuppressCombatDuringReset();
                AGAMOTTO_RideDestroy.StripEmbeddedMlAgentsInScene();

                specs.Sort((a, b) =>
                {
                    bool aHostile = IsHostileCotType(a.CotType);
                    bool bHostile = IsHostileCotType(b.CotType);
                    if (aHostile == bHostile)
                        return 0;
                    return aHostile ? -1 : 1;
                });

                RideID friendlyGroup = default;
                RideID hostileGroup = default;
                RideVector3 friendlySpawn = default;
                RideVector3 hostileSpawn = default;
                string friendlyUid = rlFriendlyUid;

                foreach (var spec in specs)
                {
                    RideID tacticalGroup = SpawnUnitFromSpec(spec, registerOnlyIfNew: false);
                    if (!AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup))
                        continue;

                    RideVector3 pos = Globals.api.terrainSystem.ConvertToScenePosition(spec.Lat, spec.Lon);
                    if (IsFriendlyCotType(spec.CotType))
                    {
                        friendlyGroup = tacticalGroup;
                        friendlySpawn = pos;
                        if (string.IsNullOrEmpty(friendlyUid))
                            friendlyUid = spec.Uid;
                    }
                    else if (IsHostileCotType(spec.CotType))
                    {
                        hostileGroup = tacticalGroup;
                        hostileSpawn = pos;
                    }
                }

                if (!AGAMOTTO_ATD_Agent.IsValidRideId(friendlyGroup))
                {
                    Debug.LogError("AGAMOTTO: Episode respawn failed — friendly squad missing.");
                    return false;
                }

                RideID friendlyLeader = Globals.api.groupSystem.GetGroupLeader(friendlyGroup);
                RideID primaryEnemy = default;
                if (AGAMOTTO_ATD_Agent.IsValidRideId(hostileGroup))
                {
                    primaryEnemy = Globals.api.groupSystem.GetGroupLeader(hostileGroup);
                    if (!AGAMOTTO_ATD_Agent.IsValidRideId(primaryEnemy))
                        primaryEnemy = hostileGroup;
                }

                if (attachRlPolicy)
                {
                    if (!ReattachRlTraining(friendlyGroup))
                    {
                        Debug.LogError("AGAMOTTO: Episode respawn failed — RL reattach failed.");
                        return false;
                    }
                }
                else
                {
                    AGAMOTTO_RideDestroy.SuppressEmbeddedMlAgentsInScene();
                }

                result = new AGAMOTTO_EpisodeRespawn.RespawnResult
                {
                    Success = true,
                    FriendlyTacticalGroup = friendlyGroup,
                    FriendlyLeader = friendlyLeader,
                    PrimaryEnemy = primaryEnemy,
                    FriendlyLeaderSpawn = friendlySpawn,
                    EnemySpawn = hostileSpawn,
                    FriendlyUid = friendlyUid,
                };

                Debug.Log(
                    $"AGAMOTTO: Episode respawn complete — friendlyLeader={friendlyLeader.id}, "
                    + $"enemy={primaryEnemy.id}, units={specs.Count}.");
                return true;
            }
            finally
            {
                _episodeRespawnInProgress = false;
            }
        }

        static void SuppressCombatDuringReset()
        {
            Globals.api.attackSystem.flags = Ride.Combat.AttackSystemFlags.Acquire_Targets;
        }

        bool ReattachRlTraining(RideID tacticalGroup)
        {
            RideID squadLeaderId = Globals.api.groupSystem.GetGroupLeader(tacticalGroup);
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(squadLeaderId))
            {
                Debug.LogError("AGAMOTTO: ReattachRlTraining failed — no squad leader.");
                return false;
            }

            GameObject missionSettings = m_settingsPrefabGameObjects?.FirstOrDefault(obj => obj.name == "AttackToDestroy");
            if (missionSettings == null)
            {
                Debug.LogError(
                    "AGAMOTTO: ReattachRlTraining failed — AttackToDestroy mission prefab missing "
                    + "(must include AGAMOTTO_ATD_Agent + BehaviorParameters).");
                return false;
            }

            AGAMOTTO_RideDestroy.SuppressEmbeddedMlAgentsInScene();
            AGAMOTTO_EvaluationRunConfig.BeginRespawnReattach();
            try
            {
                var rlBehavior = new AGAMOTTO_RL_Behavior(missionSettings, 1000f, tacticalGroup);
                Globals.api.agentSystem.SetAgentBehaviour(squadLeaderId, rlBehavior);
                _rlFriendlyEntityUid = FindUidForTacticalGroup(tacticalGroup);
                Debug.Log(
                    $"AGAMOTTO: Reattached RL policy to leader id={squadLeaderId.id} "
                    + $"(tacticalGroup={tacticalGroup.id}).");
                return true;
            }
            finally
            {
                AGAMOTTO_EvaluationRunConfig.EndRespawnReattach();
            }
        }

        string FindUidForTacticalGroup(RideID tacticalGroup)
        {
            foreach (var kv in entityList)
            {
                if (kv.Value.id == tacticalGroup.id)
                    return kv.Key;
            }

            return _rlFriendlyEntityUid;
        }

        bool StartRLTraining(RideID tacticalGroup, string mission = "AttackToDestroy")
        {
            if (!AGAMOTTO_EvaluationRunConfig.TryClaimRlTrainingSlot())
            {
                Debug.LogWarning("AGAMOTTO: RL training slot limit reached; skipping extra friendly policy.");
                return false;
            }

            RideID squadLeaderId = Globals.api.groupSystem.GetGroupLeader(tacticalGroup);
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(squadLeaderId))
            {
                Debug.LogError("AGAMOTTO: StartRLTraining failed — no squad leader.");
                return false;
            }

            Team leaderTeam = Globals.api.agentSystem.GetAgentTeam(squadLeaderId);
            if (leaderTeam != Team.Blue)
            {
                Debug.LogError(
                    $"AGAMOTTO: StartRLTraining refused — leader team is {leaderTeam}, expected Blue (friendly).");
                return false;
            }

            GameObject missionSettings = m_settingsPrefabGameObjects?.FirstOrDefault(obj => obj.name == "AttackToDestroy");
            if (missionSettings == null)
            {
                Debug.LogError(
                    "AGAMOTTO: StartRLTraining failed — AttackToDestroy mission prefab missing "
                    + "(needs AGAMOTTO_ATD_Agent + BehaviorParameters).");
                return false;
            }

            AGAMOTTO_EvaluationRunConfig.WriteSacTrainingRun();

            //collect settings specific to ATD, which includes custom ATD agent
            Debug.Log(
                $"AGAMOTTO: Attaching RL policy to friendly leader id={squadLeaderId.id} "
                + $"(tacticalGroup={tacticalGroup.id}) mission={mission}");
            AGAMOTTO_RL_Behavior RLBehavior = new AGAMOTTO_RL_Behavior(missionSettings, 1000f, tacticalGroup);

            // One policy on the squad leader; movement uses squad teleport (see AGAMOTTO_ATD_Agent).
            Globals.api.agentSystem.SetAgentBehaviour(squadLeaderId, RLBehavior);

            return true;
        }

        protected virtual void OnGUILoadTerrainSplitLmab()
        {
            float lastProgress = 0;
            var loadTerrainProgress = new Progress<LoadTerrainProgress>((progress) =>
            {
                if (progress.overallProgress - lastProgress >= 0.1 || progress.overallProgress >= 1)
                {
                    lastProgress = progress.overallProgress;
                    RideLogSystem.LogFormat("Loading... overall: {0}% - renderLoadFinished: {1} - physicsLoadFinished: {2}", (progress.overallProgress * 100).ToString("f2"), progress.renderLoadFinished, progress.physicsLoadFinished);
                }

                m_terrainLoadProgress = progress.overallProgress;

                if (progress.physicsLoadFinished)
                {
                    m_startTime = Time.time - m_startTime;
                    RideLogSystem.LogFormat("Finished: Time: {0}", m_startTime);

#if false
                    MeshFilter [] filters = GameObject.FindObjectsOfType<MeshFilter>();
                    foreach (var filter in filters)
                    {
                        if (filter.mesh.isReadable)
                        {
                            Debug.LogFormat("{0} - {1} - normals: {2} - tangents: {3}",
                                filter.transform.parent.gameObject.terrainName,
                                filter.gameObject.terrainName,
                                filter.mesh.normals != null ? filter.mesh.normals.Length : 0,
                                filter.mesh.tangents != null ? filter.mesh.tangents.Length : 0);
                        }
                    }
#endif
                }
            });


            /*if (m_debugMenu.DrawGUIButton("Clear Terrain"))
            {
                foreach (var terrain in m_terrains)
                    Globals.api.terrainSystem.DestroyTerrain(terrain);

                m_terrains.Clear();
                m_terrainLoadProgress = 0;
                m_planeLoaded = false;
            }

            if (m_debugMenu.DrawGUIButton("Clear Data Cache"))
            {
                var dataCache = Ride.Globals.api.systemAccessSystem.GetSystem<Ride.DataCache>();
                dataCache.ClearCache();

                RideUtils.ClearAssetBundleCache();
            }*/



            m_loadTrees = true;


            int tileEntryselected = 25;
            if (tileEntryselected != m_tileEntrySelected)
            {
                m_tileEntrySelected = tileEntryselected;
                m_tileLodSelected = 0;
                m_colliderSelected = 0;

                m_terrainLoader.m_terrainSO = tileMap[m_tileEntrySelected];
                m_terrainLoader.m_terrain = tileMap[m_tileEntrySelected].tilePath;
                m_terrainLoader.m_collision = "";
                m_terrainLoader.m_collisionLod = "";
            }

            if (tileMap[m_tileEntrySelected].lods.Count > 0)
            {

                var lods = tileMap[m_tileEntrySelected].lods.ToArray();
                m_terrainLoader.m_lod = tileMap[m_tileEntrySelected].lods[0];
            }

            m_startTime = Time.time;

            var terrainSO = tileMap[m_tileEntrySelected];
            string path = terrainSO.tilePath;
            string pathcrc = terrainSO.tilePath + ".crc";
            string lod = null;
            if (terrainSO.lods.Count > 0)
                lod = terrainSO.lods[m_tileLodSelected];
            string colliderPath = "";
            string colliderLod = "";
            if (m_colliderSelected != 0)
            {
                colliderPath = path;
                if (terrainSO.lods.Count > 0)
                    colliderLod = terrainSO.lods[m_colliderSelected - 1];
            }

            Vector3 startLoadingPriorityPosition = Vector3.zero;
            if (m_cameraStartPosition)
            {
                var camera = GameObject.Find("Main Camera");
                if (camera == null)
                    camera = GameObject.Find("TerrainCamera");

                startLoadingPriorityPosition = camera.transform.position;
            }

            int renderRadius = m_loadRadius == 0 ? -1 : (int)m_loadRadius;

            var terrain = Globals.api.terrainSystem.LoadTerrain(new Ride.Terrain.LoadTerrainParams()
            {
                path = path,
                pathcrc = pathcrc,
                lod = lod,
                assetBundleColliderPath = colliderPath,
                assetBundleColliderLod = colliderLod,
                loadTrees = m_loadTrees,
                startLoadingPriorityPosition = startLoadingPriorityPosition,
                renderRadius = renderRadius,
                useAssetBundles = false,
                cpuPriority = m_cpuPriority,
                loadTerrainProgress = loadTerrainProgress
            });
            m_terrains.Add(terrain);
        }

        IEnumerator CreateNavMesh()
        {
            while (m_terrainLoadProgress != 1)
            {

                yield return null;
            }

            //build nav mesh
            NavMeshSurface surface = GameObject.Find("TerrainRoot").AddComponent<NavMeshSurface>();
            surface.BuildNavMesh();
            simulationState.State = SimStateType.TerrainLoaded;
        }

        IEnumerator CreateWatcher(string folder)
        {
            using var watcher = new FileSystemWatcher(folder);

            watcher.NotifyFilter = NotifyFilters.Attributes
                                 | NotifyFilters.CreationTime
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.FileName
                                 | NotifyFilters.LastAccess
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Security
                                 | NotifyFilters.Size;

            watcher.Changed += OnChanged;
            watcher.Created += OnCreated;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            watcher.Filter = "*.xml";
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;

            while (true)
            {

                yield return null;
            }

        }

        private void OnDestroy()
        {
            if (Active == this)
                Active = null;
            StopCotTcpListener();
            StopAllCoroutines();
            ExportData.RemoveStatus();
        }



        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
                return;

            Debug.Log($"Changed: {e.FullPath}");
            ProcessCotFileFromPath(e.FullPath);
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            Debug.Log($"Created: {e.FullPath}");
            ProcessCotFileFromPath(e.FullPath);
        }

        private static void OnDeleted(object sender, FileSystemEventArgs e) =>
            Debug.Log($"Deleted: {e.FullPath}");

        private static void OnRenamed(object sender, RenamedEventArgs e)
        {
            Debug.Log($"Renamed:");
            Debug.Log($"    Old: {e.OldFullPath}");
            Debug.Log($"    New: {e.FullPath}");
        }

        private static void OnError(object sender, ErrorEventArgs e) =>
            Debug.Log(e.GetException());
    }
}


