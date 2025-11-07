using Amazon.Rekognition.Model;
using BllipParser.DotNet.Vanilla;
using Fusion;
using Pathfinding;
using Photon.Realtime;
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
using System.Security.AccessControl;
using System.Xml;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.Serialization;
using static UnityEngine.UI.CanvasScaler;
using System.Linq;

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

        public bool NavMeshBuilt { get; private set; } = false;
        protected TerrainLoader m_terrainLoader;
        protected string[] m_terrainPathsTileNames;
        protected string[] m_terrainPathsLmabNames;
        protected List<Ride.Terrain.ITerrain> m_terrains = new List<Ride.Terrain.ITerrain>();
        protected Dictionary<string, RideID> entityList;
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

        [FormerlySerializedAs("sceneGameObjects")]
        public GameObject[] m_settingsPrefabGameObjects;


        protected override void Start()
        {
            simulationState = new SimState();
            simulationState.State = SimStateType.LoadingTerrain;
            base.Start();
            entityList = new Dictionary<string, RideID>();
            worldStateSystem = Globals.api.worldStateSystem;
            worldStateSystem.AddListener<TerrainLoadedEvent>(WorldEvent.terrainLoaded, OnTerrainLoaded);
            LoadTerrain();

            //change later, gives all agents authority to attack at will
            Globals.api.attackSystem.flags = Ride.Combat.AttackSystemFlags.Auto_Attack;

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
        protected virtual void OnTerrainLoaded(WorldEventMarker marker, TerrainLoadedEvent e)
        {
            Debug.Log("Terrain Finished Loading");
            

        }

        bool CreatePath(string callsign, RideVector3[] path)
        {
            //set path for agent
            //WARNING: Agent has to exist in the entityList; possible race condition
            if (entityList.ContainsKey(callsign) && simulationState.State == SimStateType.Idle)
            {
                Globals.api.movementSystem.MoveGroupPath(entityList[callsign], path, FormationProcedureType.Wedge);

            }
            else
            {
                Debug.Log("Entity Associated with Route does not exist in list; Entity either does not exist or has not yet been created; please try again");
            }


                return true;
        }

        bool CreateUnit(string cotType, string uid, double lat, double lng)
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
                            RideID alphaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) { rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
                            entityList.Add(uid, alphaSquad);   
                        }
                        simulationState.State = SimStateType.Idle;
                        return true;
                        //break;

                    case "h":
                        if (!entityList.ContainsKey(uid))
                        {
                            RideID insurgents = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) { rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Red });
                            entityList.Add(uid, insurgents);
                        }
                        simulationState.State = SimStateType.Idle;
                        return true;

                }
                return true;
            }
            else
            {
                Debug.Log("Terrain Not Loaded - Cannot Create Units");
                return false;
            }

        }

        bool CreateMLUnit(string cotType, string uid, double lat, double lng)
        {
            if (simulationState.State == SimStateType.TerrainLoaded || simulationState.State == SimStateType.Idle)
            {
                string[] segments = cotType.Split('-');
                RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(lat, lng);
                IGroupSystem groupSystem = Globals.api.groupSystem;
                switch (segments[1])
                {
                    case "f":
                        if (!entityList.ContainsKey(uid))
                        {
                            //create custom ML-enabled prefab 
                            RideID alphaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) {prefab = "ChrUsaArmyInfantryOcpMLPrefab", rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
                            RideID groupLeaderId = groupSystem.GetGroupLeader(alphaSquad);
                            IEnumerable<RideID> mlAgents = Globals.api.scenarioSystem.GetAgents(Team.Blue);

                            var goSystem = Globals.api.GetSystem<IGameObjectSystem>();

                            //collect settings specific to ATD, which includes custom ATD agent
                            GameObject settings = m_settingsPrefabGameObjects.FirstOrDefault(obj => obj.name == "AttackToDestroyMLSettings");
                            AGAMOTTO_RL_Behavior RLBehavior= new AGAMOTTO_RL_Behavior(settings, RideUtils.Infinity);
                            foreach (var agent in mlAgents)
                            {
                                Globals.api.agentSystem.SetAgentBehaviour(agent, RLBehavior);
                            }
                            //Globals.api.agentSystem.SetAgentBehaviour(groupLeaderId, attackToDestroy);


                            entityList.Add(uid, alphaSquad);
                        }
                        


                        simulationState.State = SimStateType.Idle;
                        return true;
                    //break;

                    case "h":
                        if (!entityList.ContainsKey(uid))
                        {
                            RideID insurgents = Globals.api.agentSystem.CreateSquad(new UnitCreationParams(uid) {prefab = "ChrIrqInsurgentMleAdultAvg01Prefab", rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Red });
                            RideID groupLeaderId = groupSystem.GetGroupLeader(insurgents);
                            //goSystem.AddComponent<AGAMOTTOMLAgent>(groupLeaderId);
                            entityList.Add(uid, insurgents);
                        }
                        simulationState.State = SimStateType.Idle;
                        return true;

                }
                return true;
            }
            else
            {
                Debug.Log("Terrain Not Loaded - Cannot Create Units");
                return false;
            }

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
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
            {
                return;
            }

            Debug.Log($"Changed: {e.FullPath}");
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.Load(e.FullPath);
                //Debug.Log(doc.InnerText);
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
                        if (mode.Equals("training"))
                        {
                            if (type != null && type.StartsWith("a"))
                            {
                                bool success = CreateMLUnit(type, uid, double.Parse(lat), double.Parse(lon));
                            }

                        }
                        else
                        {
                            if (type != null && type.StartsWith("a"))
                            {
                                bool success = CreateUnit(type, uid, double.Parse(lat), double.Parse(lon));
                            }

                            if (type != null && type.EndsWith("r"))
                            {
                                //Debug.Log("Received a route");
                                //Debug.Log(type);
                                XmlNode routeNode = root.SelectSingleNode("detail/route");

                                int routeSize = 0;

                                // inelegant; replace
                                foreach (XmlNode point in routeNode)
                                {
                                    routeSize++;
                                }
                                RideVector3[] path = new RideVector3[routeSize];

                                //Debug.Log(routeSize);
                                int counter = 0;
                                foreach (XmlNode point in routeNode)
                                {
                                    string lat = point.Attributes["lat"]?.Value;
                                    string lon = point.Attributes["lon"]?.Value;
                                    string hae = point.Attributes["hae"]?.Value;
                                    RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(Convert.ToDouble(lat), Convert.ToDouble(lon));
                                    path[counter] = scenPos;
                                    //Debug.Log($"Point - Lat: {lat}, Lon: {lon}, HAE: {hae}");
                                    counter++;
                                }
                                Debug.Log($"Attempting to create path for {callsign}");
                                //associate with existing entity
                                bool success = CreatePath(callsign, path);
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

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            string value = $"Created: {e.FullPath}";
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.Load(e.FullPath);
                XmlNode root = doc.SelectSingleNode("/root");

                if (root != null)
                {
                    string callsign = root.SelectSingleNode("callsign")?.InnerText;
                    string ce = root.SelectSingleNode("ce")?.InnerText;
                    string hae = root.SelectSingleNode("hae")?.InnerText;
                    string how = root.SelectSingleNode("how")?.InnerText;
                    string lat = root.SelectSingleNode("lat")?.InnerText;
                    string le = root.SelectSingleNode("le")?.InnerText;
                    string lon = root.SelectSingleNode("lon")?.InnerText;
                    string stale = root.SelectSingleNode("stale")?.InnerText;
                    string start = root.SelectSingleNode("start")?.InnerText;
                    string time = root.SelectSingleNode("time")?.InnerText;
                    string type = root.SelectSingleNode("type")?.InnerText;
                    string uid = root.SelectSingleNode("uid")?.InnerText;

                    /*Debug.Log("----- Parsed CoT Message -----");
                    Debug.Log($"Callsign: {callsign}");
                    Debug.Log($"CE: {ce}");
                    Debug.Log($"HAE: {hae}");
                    Debug.Log($"How: {how}");
                    Debug.Log($"Lat: {lat}");
                    Debug.Log($"Lon: {lon}");
                    Debug.Log($"LE: {le}");
                    Debug.Log($"Time: {time}");
                    Debug.Log($"Start: {start}");
                    Debug.Log($"Stale: {stale}");
                    Debug.Log($"Type: {type}");
                    Debug.Log($"UID: {uid}");*/

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        //a = atomic, meaning it is a physical entity
                        if (type.StartsWith("a"))
                        {
                            bool success = CreateUnit(type, callsign, double.Parse(lat), double.Parse(lon));
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


