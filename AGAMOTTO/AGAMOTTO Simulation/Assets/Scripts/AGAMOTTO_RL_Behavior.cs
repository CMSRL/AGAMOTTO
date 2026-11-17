using Ride;
using Ride.AI;
using Ride.AI.RL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unity.MLAgents.Policies;
using UnityEngine;

public class AGAMOTTO_RL_Behavior : MLBehaviour
{
    static int s_initGeneration;
    int _initGeneration;
    /// <summary>
    /// Max distance from the learning agent when querying candidate enemies (world units).
    /// </summary>
    public float maxEnemyDistance = 1000f;

    /// <summary>
    /// Horizontal radius (meters) around the friendly leader used to decide which Blue agents belong to this squad for spawn snapshots.
    /// </summary>
    public float friendlySquadSpawnCaptureRadiusM = 2500f;

    /// <summary>
    /// Horizontal radius (meters) around the assigned enemy anchor used for Red spawn snapshots.
    /// </summary>
    public float enemySquadSpawnCaptureRadiusM = 2500f;

    /// <summary>
    /// After the first spawn snapshot, wait this long (ms) and capture again so late-spawned units are included.
    /// </summary>
    public int delayedSpawnSnapshotMs = 500;

    const int MaxFriendlySnapshotAgents = 10;
    const int MaxHostileSnapshotAgents = 7;

    /// <summary>
    /// Poll interval while waiting for at least one enemy (milliseconds).
    /// </summary>
    public int enemySpawnPollIntervalMs = 100;

    /// <summary>
    /// Stop waiting after this many polls (total wait ~= max * interval).
    /// </summary>
    public int enemySpawnWaitMaxPolls = 300;

    /// <summary>Friendly squad / tactical group id (passed to the ATD agent for future group logic).</summary>
    readonly RideID _friendlyTacticalGroup;

    /// <summary>Alternates ML-Agents <c>TeamId</c> (0/1) per installed squad so two friendly training groups map to two trainer teams while sharing <c>AGAMOTTO_RL</c>.</summary>
    static int s_nextMlAgentsTeamId;

    public AGAMOTTO_RL_Behavior(UnityEngine.GameObject behaviourSettings, float maxEnemyDistance, RideID friendlyTacticalGroup = default)
        : base(behaviourSettings)
    {
        this.maxEnemyDistance = maxEnemyDistance;
        _friendlyTacticalGroup = friendlyTacticalGroup;
    }

    async protected override void InitMLBehaviour()
    {
        int generation = ++s_initGeneration;
        _initGeneration = generation;
        try
        {
            base.InitMLBehaviour();
            if (_initGeneration != generation)
                return;

            AGAMOTTO_ATD_Agent mlagent = GetRLAgent<AGAMOTTO_ATD_Agent>();
            if (mlagent == null)
            {
                Debug.LogError(
                    "AGAMOTTO_RL_Behavior: AGAMOTTO_ATD_Agent not found on mission settings prefab "
                    + "(AttackToDestroy). RL is attached via SetAgentBehaviour, not the infantry prefab.");
                return;
            }

            BehaviorParameters bp = mlagent.gameObject.GetComponent<BehaviorParameters>();

            Team leaderTeam = api.agentSystem.GetAgentTeam(entity);
            if (leaderTeam != Team.Blue)
            {
                Debug.LogError(
                    $"AGAMOTTO_RL_Behavior: ML-Agents policy is on team {leaderTeam}, expected Blue (friendly). "
                    + "Check CoT types (a-f-* friendly, a-h-* hostile) and active_run trainingFriendlyLimit.");
                Globals.api.agentSystem.SetAgentBehaviour(entity, null);
                return;
            }
            // Ensure RIDE id + Unity transform track the behaviour entity (squad leader), not the mission prefab root.
            mlagent.Init(api, entity);
            AGAMOTTO_EvaluationRunConfig.TryApplyToAgent(mlagent);
            bp.BehaviorName = "AGAMOTTO_RL";
            // Two squads → two ML-Agents teams for cooperative multi-squad training (same behavior name, shared policy file).
            bp.TeamId = s_nextMlAgentsTeamId & 1;
            if (s_nextMlAgentsTeamId < int.MaxValue - 1)
                s_nextMlAgentsTeamId++;
            mlagent.m_squadTacticalGroup = _friendlyTacticalGroup;

            bool enemiesReady = await WaitUntilEnemySpawn(mlagent);
            if (_initGeneration != generation)
                return;
            if (!enemiesReady)
            {
                Debug.LogError("AGAMOTTO_RL_Behavior: Timed out waiting for an enemy. Agent enemy target was not set.");
                return;
            }

            // Snapshot start pose from sim (leader entity), not the mission prefab transform.
            if (AGAMOTTO_ATD_Agent.TryGetRidePosition(entity, out RideVector3 startPos))
                mlagent.m_startingPosition = startPos;

            Team team = api.agentSystem.GetAgentTeam(entity) == Team.Blue ? Team.Red : Team.Blue;
            IEnumerable<RideID> enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
            if (!TryAssignClosestEnemy(mlagent, enemies))
                Debug.LogError("AGAMOTTO_RL_Behavior: No enemies found after wait; cannot assign m_enemy.");

            CaptureAndReplaceSpawnSnapshots(mlagent);
            if (delayedSpawnSnapshotMs > 0)
            {
                await Task.Delay(delayedSpawnSnapshotMs);
                if (_initGeneration != generation)
                    return;
                CaptureAndMergeSpawnSnapshots(mlagent);
            }

            if (_initGeneration != generation)
                return;

            mlagent.TryLockEpisodeSpawnAnchors();

            TryEnableInferenceMode(mlagent, bp);
            mlagent.EnableRlCombatEngagement();

            Debug.Log(
                $"AGAMOTTO_RL_Behavior: RL ready — teleport={!mlagent.useSquadNavMeshPaths}, "
                + $"combat=Auto_Attack+Acquire_Targets.");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    void CaptureAndReplaceSpawnSnapshots(AGAMOTTO_ATD_Agent mlagent)
    {
        List<RideID> friendlies = CollectFriendlySquadForSnapshots();
        List<RideID> hostiles = CollectEnemySquadNearAnchor(mlagent, Team.Red, enemySquadSpawnCaptureRadiusM);
        mlagent.ReplaceEpisodeSpawnSnapshots(friendlies, hostiles);
        Debug.Log(
            $"AGAMOTTO_RL_Behavior: spawn snapshot replace — {friendlies.Count} friendlies, {hostiles.Count} hostiles.");
    }

    void CaptureAndMergeSpawnSnapshots(AGAMOTTO_ATD_Agent mlagent)
    {
        List<RideID> friendlies = CollectFriendlySquadForSnapshots();
        List<RideID> hostiles = CollectEnemySquadNearAnchor(mlagent, Team.Red, enemySquadSpawnCaptureRadiusM);
        mlagent.SetEpisodeSpawnSnapshots(friendlies, hostiles);
        mlagent.TryLockEpisodeSpawnAnchors();
        Debug.Log(
            $"AGAMOTTO_RL_Behavior: spawn snapshot merge — {friendlies.Count} friendlies, {hostiles.Count} hostiles.");
    }

    /// <summary>Prefer tactical-group roster (full 2×2 squad); fall back to spatial query near leader.</summary>
    List<RideID> CollectFriendlySquadForSnapshots()
    {
        List<RideID> fromGroup = CollectAgentsInTacticalGroup(_friendlyTacticalGroup);
        if (fromGroup.Count >= 1)
            return fromGroup;
        List<RideID> spatial = CollectSquadAgentsNear(entity, Team.Blue, Mathf.Min(friendlySquadSpawnCaptureRadiusM, 80f));
        return spatial.Count > fromGroup.Count ? spatial : fromGroup;
    }

    /// <summary>Enumerate soldiers in a RIDE tactical group (CreateSquad / CreateFireTeam handle).</summary>
    List<RideID> CollectAgentsInTacticalGroup(RideID tacticalGroup)
    {
        var result = new List<RideID>();
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup))
            return result;

        var gs = api.groupSystem;
        if (gs == null)
            return result;

        var seen = new HashSet<int>();
        void TryAdd(RideID id)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add(id.id))
                return;
            result.Add(id);
        }

        foreach (string methodName in new[]
        {
            "GetGroupMembers",
            "GetAgentsInGroup",
            "GetGroupAgents",
            "EnumerateGroupMembers",
            "GetMembers",
        })
        {
            try
            {
                MethodInfo method = gs.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(RideID) },
                    null);
                if (method == null)
                    continue;
                if (method.Invoke(gs, new object[] { tacticalGroup }) is IEnumerable<RideID> ids)
                {
                    foreach (RideID id in ids)
                        TryAdd(id);
                }
            }
            catch
            {
                /* optional API */
            }
        }

        RideID leader = gs.GetGroupLeader(tacticalGroup);
        TryAdd(leader);
        TryAdd(entity);

        if (result.Count < 2)
        {
            foreach (RideID id in CollectAgentsSharingTacticalGroup(tacticalGroup))
                TryAdd(id);
        }

        return result;
    }

    /// <summary>All team agents whose tactical group matches <paramref name="tacticalGroup"/>.</summary>
    List<RideID> CollectAgentsSharingTacticalGroup(RideID tacticalGroup)
    {
        var result = new List<RideID>();
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup))
            return result;

        var gs = api.groupSystem;
        if (gs == null)
            return result;

        var seen = new HashSet<int>();
        void TryAdd(RideID id)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add(id.id))
                return;
            result.Add(id);
        }

        IEnumerable<RideID> blues = api.scenarioSystem.GetAgents(Team.Blue);
        if (blues == null)
            return result;

        foreach (RideID id in blues)
        {
            RideID group = TryResolveAgentTacticalGroup(gs, id);
            if (group.id == tacticalGroup.id)
                TryAdd(id);
        }

        return result;
    }

    static RideID TryResolveAgentTacticalGroup(object groupSystem, RideID agentId)
    {
        if (groupSystem == null || !AGAMOTTO_ATD_Agent.IsValidRideId(agentId))
            return default;

        foreach (string methodName in new[]
        {
            "GetAgentGroup",
            "GetGroupForAgent",
            "GetAgentsGroup",
            "GetTacticalGroup",
        })
        {
            try
            {
                MethodInfo method = groupSystem.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(RideID) },
                    null);
                if (method == null)
                    continue;
                if (method.Invoke(groupSystem, new object[] { agentId }) is RideID group
                    && AGAMOTTO_ATD_Agent.IsValidRideId(group))
                    return group;
            }
            catch
            {
                /* optional API */
            }
        }

        return default;
    }

    /// <summary>
    /// Blues near this behaviour's <paramref name="leaderEntity"/> (not every Blue on the map), merged from spatial query and horizontal distance filter.
    /// </summary>
    List<RideID> CollectSquadAgentsNear(RideID leaderEntity, Team team, float horizontalRadiusM)
    {
        var ss = api.scenarioSystem;
        if (!AGAMOTTO_ATD_Agent.TryGetRidePosition(leaderEntity, out RideVector3 anchorRv))
            return new List<RideID>();
        var anchor = new Vector3((float)anchorRv.x, (float)anchorRv.y, (float)anchorRv.z);
        float r2 = horizontalRadiusM * horizontalRadiusM;

        var seen = new HashSet<int>();
        var result = new List<RideID>();

        void TryAdd(RideID id)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add(id.id))
                return;
            if (!AGAMOTTO_ATD_Agent.TryGetRidePosition(id, out RideVector3 p))
                return;
            float hdx = (float)(p.x - anchorRv.x);
            float hdz = (float)(p.z - anchorRv.z);
            if (hdx * hdx + hdz * hdz > r2)
                return;
            result.Add(id);
        }

        TryAdd(leaderEntity);

        IEnumerable<RideID> spatial = ss.GetAgents(anchor, horizontalRadiusM, team);
        if (spatial != null)
        {
            foreach (RideID id in spatial)
                TryAdd(id);
        }

        IEnumerable<RideID> allTeam = ss.GetAgents(team);
        if (allTeam != null)
        {
            foreach (RideID id in allTeam)
                TryAdd(id);
        }

        if (result.Count > MaxFriendlySnapshotAgents)
            return result.GetRange(0, MaxFriendlySnapshotAgents);
        return result;
    }

    /// <summary>
    /// Reds near <see cref="AGAMOTTO_ATD_Agent.m_enemy"/> when set; otherwise all reds (single-team fallback).
    /// </summary>
    List<RideID> CollectEnemySquadNearAnchor(AGAMOTTO_ATD_Agent mlagent, Team team, float horizontalRadiusM)
    {
        var ss = api.scenarioSystem;

        if (!AGAMOTTO_ATD_Agent.TryGetRidePosition(mlagent.m_enemy, out RideVector3 anchorRv))
            return ToDedupedList(ss.GetAgents(team));

        var anchor = new Vector3((float)anchorRv.x, (float)anchorRv.y, (float)anchorRv.z);
        float r2 = horizontalRadiusM * horizontalRadiusM;

        var seen = new HashSet<(int, int)>();
        var result = new List<RideID>();

        void TryAdd(RideID id)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add((id.id, id.version)))
                return;
            if (!AGAMOTTO_ATD_Agent.TryGetRidePosition(id, out RideVector3 p))
                return;
            float hdx = (float)(p.x - anchorRv.x);
            float hdz = (float)(p.z - anchorRv.z);
            if (hdx * hdx + hdz * hdz > r2)
                return;
            result.Add(id);
        }

        IEnumerable<RideID> spatial = ss.GetAgents(anchor, horizontalRadiusM, team);
        if (spatial != null)
        {
            foreach (RideID id in spatial)
                TryAdd(id);
        }

        IEnumerable<RideID> allTeam = ss.GetAgents(team);
        if (allTeam != null)
        {
            foreach (RideID id in allTeam)
                TryAdd(id);
        }

        if (result.Count > MaxHostileSnapshotAgents)
            return result.GetRange(0, MaxHostileSnapshotAgents);
        return result;
    }

    static List<RideID> ToDedupedList(IEnumerable<RideID> source)
    {
        var seen = new HashSet<(int, int)>();
        var result = new List<RideID>();
        if (source == null)
            return result;
        foreach (RideID id in source)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add((id.id, id.version)))
                continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Picks the enemy closest to the ML agent and stores it plus that enemy's starting position.
    /// </summary>
    bool TryAssignClosestEnemy(AGAMOTTO_ATD_Agent mlagent, IEnumerable<RideID> enemies)
    {
        ITransformSystem transformSystem = api.GetSystem<ITransformSystem>();
        Vector3 agentPos = mlagent.agentTransform.position;

        RideID bestId = default;
        RideVector3 bestPos = default;
        float bestSqr = float.PositiveInfinity;

        foreach (RideID id in enemies)
        {
            RideVector3 p = transformSystem.GetPosition(id);
            var dx = (float)(p.x - agentPos.x);
            var dy = (float)(p.y - agentPos.y);
            var dz = (float)(p.z - agentPos.z);
            float sqr = dx * dx + dy * dy + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestId = id;
                bestPos = p;
            }
        }

        if (float.IsPositiveInfinity(bestSqr))
            return false;

        mlagent.m_enemy = bestId;
        mlagent.m_enemyStartingPosition = bestPos;
        return true;
    }

    void TryEnableInferenceMode(AGAMOTTO_ATD_Agent mlagent, BehaviorParameters bp)
    {
        if (!AGAMOTTO_EvaluationRunConfig.IsInferencePhase())
            return;

        string trainer = AGAMOTTO_EvaluationRunConfig.GetTrainerType();
        if (AGAMOTTO_EvaluationRunConfig.PythonDrivesInference())
        {
            Debug.Log(
                $"AGAMOTTO_RL_Behavior: {trainer.ToUpperInvariant()} post-training inference — "
                + "Python drives actions; Unity records route for WinTAK.");
            return;
        }

        string rel = AGAMOTTO_EvaluationRunConfig.GetInferencePolicyRelativePath();
        string onnxPath = Path.Combine(Application.streamingAssetsPath, rel.Replace('\\', '/'));
        if (!File.Exists(onnxPath))
        {
            Debug.LogWarning(
                $"AGAMOTTO_RL_Behavior: inference ONNX not found at {onnxPath}. "
                + "Copy exported AGAMOTTO_RL.onnx into StreamingAssets/policies/.");
            return;
        }

        bp.BehaviorType = BehaviorType.InferenceOnly;
        Debug.Log(
            $"AGAMOTTO_RL_Behavior: inference mode enabled. "
            + $"Assign ONNX model from {onnxPath} to BehaviorParameters if not already set.");
    }

    /// <summary>
    /// Waits until the scenario system's agent query returns at least one enemy, or until timeout.
    /// </summary>
    /// <returns>True if at least one enemy is available; false on timeout.</returns>
    public async Task<bool> WaitUntilEnemySpawn(AGAMOTTO_ATD_Agent mlagent)
    {
        Team team = api.agentSystem.GetAgentTeam(entity) == Team.Blue ? Team.Red : Team.Blue;
        IEnumerable<RideID> enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
        Debug.Log("AGAMOTTO_RL_Behavior: Waiting for enemy spawn...");

        int polls = 0;
        while (!enemies.Any())
        {
            if (polls >= enemySpawnWaitMaxPolls)
            {
                Debug.LogError($"AGAMOTTO_RL_Behavior: No enemy after {polls * enemySpawnPollIntervalMs} ms (max polls={enemySpawnWaitMaxPolls}).");
                return false;
            }

            await Task.Delay(enemySpawnPollIntervalMs);
            polls++;
            enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
        }

        Debug.Log("AGAMOTTO_RL_Behavior: Enemy spawn condition met.");
        return true;
    }
}
