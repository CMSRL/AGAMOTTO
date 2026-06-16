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
        string _pendingHybridLeaderEntityUid;
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
            AGAMOTTO_HybridAwacHub.ResetForNewScenario();
            _pendingHybridLeaderEntityUid = null;
            worldStateSystem = Globals.api.worldStateSystem;
            worldStateSystem.AddListener<TerrainLoadedEvent>(WorldEvent.terrainLoaded, OnTerrainLoaded);
            LoadTerrain();

            Globals.api.attackSystem.flags =
                Ride.Combat.AttackSystemFlags.Auto_Attack | Ride.Combat.AttackSystemFlags.Acquire_Targets;

            //create a watcher to monitor input file directory for new or modified scenario files
            string filePath = Application.streamingAssetsPath;
            //Debug.Log(filePath);

            StartCoroutine(CreateWatcher(filePath));
            StartCoroutine(CreateNavMesh());

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
                    string lat = pointNode.Attributes["lat"]?.Value;
                    string lon = pointNode.Attributes["lon"]?.Value;

                    XmlNode contactNode = root.SelectSingleNode("detail/contact");
                    string callsign = contactNode?.Attributes["callsign"]?.Value;
                    string mode = root.Attributes["mode"]?.Value;

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        if (type != null && type.StartsWith("a") && entityList != null && entityList.ContainsKey(uid))
                        {
                            Debug.Log($"AGAMOTTO: Ignoring duplicate CoT for uid={uid} (unit already spawned).");
                            return;
                        }

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
                                    StartRLTraining(unitCreated);
                                }
                                else
                                {
                                    Debug.LogWarning(
                                        $"AGAMOTTO: Unknown atomic CoT type '{type}' in Training mode; unit spawned without RL.");
                                }
                            }
                        }
                        else if (string.Equals(mode, "Evaluation", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"Received a CoT Message in Evaluation Mode - Creating entity {type}");
                            if (type != null && type.StartsWith("a"))
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
                        else if (string.Equals(mode, "Hybrid", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"Received a CoT Message in Hybrid Mode - Creating entity {type}");
                            AGAMOTTO_HybridAwacHub.EnableHybridDispatch();
                            if (type != null && type.StartsWith("a"))
                            {
                                RideID unitCreated = CreateUnit(type, uid, double.Parse(lat), double.Parse(lon), true);
                                if (IsFriendlyCotType(type) && AGAMOTTO_ATD_Agent.IsValidRideId(unitCreated))
                                {
                                    AGAMOTTO_HybridAwacHub.RegisterEntity(uid, callsign, unitCreated);
                                    _pendingHybridLeaderEntityUid = uid;
                                }
                            }

                            if (type != null && type.EndsWith("r"))
                            {
                                RideVector3[] path = ParseRouteWorldPath(root);
                                if (path != null && path.Length >= 2)
                                {
                                    Debug.Log($"Attempting to create hybrid expert path for {callsign}");
                                    bool pathOk = CreatePath(callsign, path, registerExpertRoute: true);
                                    if (pathOk && !string.IsNullOrEmpty(_pendingHybridLeaderEntityUid))
                                    {
                                        TryStartHybridAwacTraining(_pendingHybridLeaderEntityUid);
                                        _pendingHybridLeaderEntityUid = null;
                                    }
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

        bool CreatePath(string callsignOrUid, RideVector3[] path, bool registerExpertRoute = false)
        {
            string entityKey = callsignOrUid;
            if (!entityList.ContainsKey(entityKey)
                && !AGAMOTTO_HybridAwacHub.TryResolveEntityKey(callsignOrUid, out entityKey))
            {
                Debug.Log(
                    "Entity associated with route does not exist in list; entity either does not exist "
                    + "or has not yet been created; please try again");
                return false;
            }

            if (!entityList.ContainsKey(entityKey))
                return false;

            if (simulationState.State != SimStateType.Idle && simulationState.State != SimStateType.TerrainLoaded)
            {
                Debug.Log("CreatePath refused — simulation not idle.");
                return false;
            }

            if (registerExpertRoute)
            {
                var world = new Vector3[path.Length];
                for (int i = 0; i < path.Length; i++)
                    world[i] = new Vector3((float)path[i].x, (float)path[i].y, (float)path[i].z);
                AGAMOTTO_HybridAwacHub.RegisterExpertRoute(callsignOrUid, world, entityKey);
            }

            Globals.api.movementSystem.MoveGroupPath(entityList[entityKey], path, FormationProcedureType.Wedge);
            AGAMOTTO_HybridAwacHub.MarkExpertPathIssued(callsignOrUid);
            return true;
        }

        bool TryStartHybridAwacTraining(string entityUid)
        {
            if (!entityList.TryGetValue(entityUid, out RideID tacticalGroup))
            {
                Debug.LogWarning($"AGAMOTTO: hybrid AWAC training deferred — unknown entity uid={entityUid}");
                return false;
            }

            Debug.Log($"AGAMOTTO: starting hybrid AWAC training for entity uid={entityUid}");
            return StartRLTraining(tacticalGroup);
        }

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

        RideID CreateUnit(string cotType, string uid, double lat, double lng, bool imitationLearning)
        {
            if(simulationState.State == SimStateType.TerrainLoaded || simulationState.State == SimStateType.Idle)
            {
                string[] segments = cotType.Split('-');
                RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(lat, lng);
                switch (segments[1])
                {
                    case "f":
                        if (!entityList.ContainsKey(uid))
                        {
                            RideID alphaSquad;
                            if (imitationLearning)
                            {
                                alphaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) {prefab = "ChrUsaArmyInfantryOcpMLPrefab", rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Ride.Team.Blue });
                            }
                            else
                            {
                                alphaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) { prefab = "ChrUsaArmyInfantryOcpMLPrefab", rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Ride.Team.Blue });
                            }
                                
                            entityList.Add(uid, alphaSquad);
                            simulationState.State = SimStateType.Idle;
                            return alphaSquad;
                        }
                        break;
                        

                    case "h":
                        if (!entityList.ContainsKey(uid))
                        {
                            // Opposing force: 5–6 dismounts in a single tactical group (not the previous 2×2 four-man squad).
                            int hostileCount = UnityEngine.Random.Range(5, 7);
                            int rows = hostileCount == 6 ? 2 : 1;
                            int cols = hostileCount == 6 ? 3 : 5;
                            RideID insurgents = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams(uid)
                            {
                                rows = rows,
                                cols = cols,
                                position = scenPos,
                                rotation = RideQuaternion.Euler(0, 0, 0),
                                team = Ride.Team.Red
                            });
                            entityList.Add(uid, insurgents);
                            simulationState.State = SimStateType.Idle;
                            Debug.Log($"AGAMOTTO: Red opposing team spawned count={hostileCount} grid={rows}×{cols} uid={uid}.");
                            return insurgents;
                        }
                        break;

                    default:
                        Debug.Log("CoT type invalid - cannot create unit!");
                        break;
                        
                }
                return new RideID(0, 0);

            }
            else
            {
                Debug.Log("Terrain Not Loaded - Cannot Create Units");
                return new RideID(0, 0);
            }

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

            //collect settings specific to ATD, which includes custom ATD agent
            Debug.Log(
                $"AGAMOTTO: Attaching RL policy to friendly leader id={squadLeaderId.id} "
                + $"(tacticalGroup={tacticalGroup.id}) mission={mission}");
            GameObject missionSettings = m_settingsPrefabGameObjects.FirstOrDefault(obj => obj.name == mission);
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


