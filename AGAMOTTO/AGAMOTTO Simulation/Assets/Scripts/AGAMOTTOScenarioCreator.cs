using PimDeWitte.UnityMainThreadDispatcher;
using Ride;
using Ride.AI;
using Ride.Entities;
using Ride.Examples;
using Ride.IO;
using Ride.Movement;
using Ride.Terrain;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEngine;


namespace AGAMOTTO
{
    public class AGAMOTTOScenarioCreator : RideBaseMinimal
    {

        public bool NavMeshBuilt { get; private set; } = false;
        protected TerrainLoader m_terrainLoader;
        protected string[] m_terrainPathsTileNames;
        protected string[] m_terrainPathsLmabNames;
        protected List<Ride.Terrain.ITerrain> m_terrains = new List<Ride.Terrain.ITerrain>();

        protected bool m_loadTrees { get => m_terrainLoader.m_loadTrees; set => m_terrainLoader.m_loadTrees = value; }
        protected bool m_cameraStartPosition { get => m_terrainLoader.m_cameraStartPosition; set => m_terrainLoader.m_cameraStartPosition = value; }
        protected int m_loadRadius { get => m_terrainLoader.m_renderRadius; set => m_terrainLoader.m_renderRadius = value; }
        protected float m_cpuPriority { get => m_terrainLoader.m_cpuPriority; set => m_terrainLoader.m_cpuPriority = value; }

        protected int m_tileEntrySelected;
        protected int m_tileLodSelected;
        protected int m_lmabEntrySelected;
        protected int m_lmabLodSelected;
        protected int m_colliderSelected;

        protected Dictionary<int, TerrainSO> tileMap = new Dictionary<int, TerrainSO>();
        protected Dictionary<int, TerrainSO> lmabMap = new Dictionary<int, TerrainSO>();

        float m_terrainLoadProgress = 0;
        float m_startTime = 0;

        protected override void Start()
        {

            base.Start();

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

            Globals.api.attackSystem.flags = Ride.Combat.AttackSystemFlags.Auto_Attack;
            //create a watcher to monitor input file directory for new or modified scenario files
            string filePath = Application.streamingAssetsPath;
            Debug.Log(filePath);
            StartCoroutine(CreateWatcher(filePath));
            StartCoroutine(CreateNavMesh());


            //build test nav mesh
            // NavMeshSurface surface = GameObject.Find("Plane").AddComponent<NavMeshSurface>();
            //surface.BuildNavMesh();
            //bool success = CreateUnit("a-f-G", 0, 0);

        }

        protected override void Update()
        {
            base.Update();

        }

        bool CreateUnit(string cotType, float lat, float lng)
        {
            //RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(lat, lng);
            //(double lat, double lon) = Globals.api.terrainSystem.ConvertToLatLng(hit.hitInfo.point, 5, true);
            //RideLogSystem.LogFormat("Scene Point {0} == lat/lon {1}:{2}", hit.hitInfo.point, lat.ToString("f2"), lon.ToString("f2"));
            RideVector3 scenPos = Globals.api.terrainSystem.ConvertToScenePosition(35.3308510842807, -116.5211896686935);
            //35.3308510842807, -116.5211896686935
            Debug.Log(scenPos);
            //RideLogSystem.LogFormat("lat/lon {0}:{1} ==  Scene Point {2}", lat.ToString("f2"), lng.ToString("f2"), scenPos);


            RideID alphaTeamAlphaSquad = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams("Alpha Team, Alpha Squad") { rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            RideID bravoTeamAlphaSquad = Globals.api.agentSystem.CreateFireTeam(new UnitCreationParams("Bravo Team, Alpha Squad") { rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            //RideID alphaSquadGlobals.api.agentSystem.CreateSquad(new UnitCreationParams("Alpha Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 15), rotation = RideQuaternion.Euler(0, -90, 0), team = Team.Blue });
            //RideID betaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Beta Squad") { rows = 2, cols = 2, position = new RideVector3(-25, 0, -5), rotation = RideQuaternion.Euler(0, 90, 0), team = Team.Blue });
            //RideID gammaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Gamma Squad") { rows = 2, cols = 2, position = new RideVector3(-10, 0, -25), rotation = RideQuaternion.Euler(0, 180, 0), team = Team.Blue });
            //RideID deltaSquad = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Delta Squad") { rows = 2, cols = 2, position = new RideVector3(25, 0, 5), rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Blue });
            RideID insurgents = Globals.api.agentSystem.CreateSquad(new UnitCreationParams("Insurgents") { rows = 2, cols = 2, position = scenPos, rotation = RideQuaternion.Euler(0, 0, 0), team = Team.Red });
            RideID alphaSquad = Globals.api.groupSystem.CreateGroup("Force", null, new RideID[] { alphaTeamAlphaSquad, bravoTeamAlphaSquad });
            //RideID platoon = Globals.api.groupSystem.CreateGroup("Force", null, new RideID[] { alphaSquad, deltaSquad, betaSquad, gammaSquad, });
            Globals.api.agentSystem.SetAgentGroupBehaviour(alphaTeamAlphaSquad, new FireTeamWedge());
            Globals.api.agentSystem.SetAgentGroupBehaviour(bravoTeamAlphaSquad, new FireTeamWedge());
            //  Globals.api.agentSystem.SetAgentGroupBehaviour(alphaTeamAlphaSquad, new GroupMoveToPositionInFormationBehaviour() { destination = new RideVector3(0, 0, 0), formationDistance = 0.5f, formationType = FormationProcedureType.Wedge });
            return false;
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

            //build test nav mesh
            NavMeshSurface surface = GameObject.Find("TerrainRoot").AddComponent<NavMeshSurface>();
            surface.BuildNavMesh();
            bool success = CreateUnit("a-f-G", 0, 0);
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
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                bool success = CreateUnit("a-f-G", 0, 0);
            });
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            string value = $"Created: {e.FullPath}";
            Debug.Log(value);
            
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
      {
                bool success = CreateUnit("a-f-G", 0, 0);
            }); 
           
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


