using EtcAbduction;
using Google.Protobuf.WellKnownTypes;
using Ride;
using Ride.AI;
using Ride.AI.RL;
using Ride.Entities;
using Ride.Movement;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

#if RIDE_ML_AGENTS
public class AGAMOTTO_ATD_Agent : RLAgent
{

    public RideID m_enemy;
    /// <summary>Friendly tactical group id from RL init (reserved for group-level APIs).</summary>
    public RideID m_squadTacticalGroup;
    public RideVector3 m_startingPosition;
    public RideVector3 m_enemyStartingPosition;
    /// <summary>World spawn poses captured once at RL init; used to reset every Blue unit each episode.</summary>
    List<(RideID id, RideVector3 world)> _friendlySpawnWorld;
    /// <summary>World spawn poses for every Red agent at RL init; closest enemy remains <see cref="m_enemy"/> for rewards.</summary>
    List<(RideID id, RideVector3 world)> _enemySpawnWorld;

    [Header("Observation v2b (normalized ego-centric)")]
    [Tooltip("Scale for horizontal offsets and distance (meters).")]
    public float observationMapRadiusM = 2500f;
    [Tooltip("Scale for vertical offset enemy.y - agent.y (meters).")]
    public float observationHeightScaleM = 50f;

    [Header("WinTAK post-training feedback")]
    [Tooltip("Send episode results to the AGAMOTTO WinTAK plugin Assessment listener (127.0.0.1:65433, length-prefixed UTF-8).")]
    public bool sendTrainingFeedbackToTak = true;
    public string takFeedbackHost = "127.0.0.1";
    public int takFeedbackPort = 65433;

    [Header("Squad combat rewards")]
    [Tooltip("Positive reward when a non-primary enemy in the tracked enemy squad dies (primary target still uses mission success reward).")]
    public float rewardPerEnemySquadMemberKilled = 0.45f;
    [Tooltip("Negative reward when a friendly squad member other than the RL leader dies.")]
    public float penaltyPerFriendlyWingmanDeath = 0.5f;

    readonly HashSet<(int id, int version)> _enemyAlivePrevStep = new HashSet<(int, int)>();
    readonly HashSet<(int id, int version)> _friendlyAlivePrevStep = new HashSet<(int, int)>();

    /// <summary>Tracked hostiles killed this episode (TensorBoard: AGAMOTTO/Enemies Killed Per Episode).</summary>
    int _episodeEnemiesKilled;
    /// <summary>Wingmen + RL leader deaths this episode (TensorBoard: AGAMOTTO/Friendlies Lost Per Episode).</summary>
    int _episodeFriendliesLost;

    private Vector3 previousPosition;
    private int episodeCounter = 0;
    private const float MinMoveDirectionSqr = 1e-8f;
    /// <summary>Minimum leader waypoint segment length (meters) per RL decision when issuing squad paths.</summary>
    const float MinMoveStepMeters = 0.15f;
    /// <summary>Floor for decision delta so <c>maxSpeed * dt</c> is not zero in batched / headless steps.</summary>
    const float MinDecisionDeltaSeconds = 1f / 20f;

    public static bool IsValidRideId(RideID id) => id.id != 0 || id.version != 0;

    static bool HasAssignedEnemy(RideID enemy) => IsValidRideId(enemy);

    static (int, int) AliveKey(RideID id) => (id.id, id.version);

    static bool SameRideId(RideID a, RideID b) => a.id == b.id && a.version == b.version;

    /// <summary>Same underlying sim entity as the RL leader (id match); snapshot rows can lag <see cref="RideID.version"/>.</summary>
    static bool SameRideEntityId(RideID a, RideID b) => a.id == b.id;

    void NotifyTakTrainingOutcome(bool missionSuccess, string outcome)
    {
        AGAMOTTO_TakTrainingFeedback.TrySend(
            sendTrainingFeedbackToTak,
            takFeedbackHost,
            takFeedbackPort,
            missionSuccess,
            stepCount,
            GetCumulativeReward(),
            MaxStep,
            outcome);
    }

    [Header("Squad movement")]
    [Tooltip("When false (eval default), squad moves via teleport each RL step. When true, uses MoveGroupPath (often fails off-mesh).")]
    public bool useSquadNavMeshPaths = false;
    [Tooltip("Formation used with MoveGroupPath when useSquadNavMeshPaths is enabled.")]
    public FormationProcedureType squadPathFormation = FormationProcedureType.Wedge;
    [Tooltip("When no RL spawn snapshot exists, include same-team agents within this horizontal distance (m) of the leader for formation teleport.")]
    public float squadFormationFallbackMaxHorizontalM = 800f;
    [Tooltip("Max horizontal distance (m) between friendly squad members on each teleport step.")]
    public float squadMaxMemberSpacingM = 20f;

    [Header("Episode reset")]
    [Tooltip("Steps after episode reset where leader-death terminal and squad death rewards are suppressed (avoids spawn crossfire).")]
    public int combatTerminalWarmupSteps = 12;
    [Tooltip("End episode if the squad leader moves less than leaderStationaryMoveThresholdM for more than this many consecutive steps.")]
    public int leaderStationaryMaxSteps = 100;
    [Tooltip("Horizontal movement (m) per step below which the leader counts as stationary.")]
    public float leaderStationaryMoveThresholdM = 0.1f;
    List<(RideID id, RideVector3 world)> _lockedFriendlySpawnWorld;
    List<(RideID id, RideVector3 world)> _lockedEnemySpawnWorld;
    /// <summary>Wingman horizontal offsets from leader (m), keyed by entity id; capped to <see cref="squadMaxMemberSpacingM"/>.</summary>
    List<(int entityId, double offsetX, double offsetY, double offsetZ)> _friendlySquadOffsets;
    bool _loggedSquadMoveThisEpisode;
    Vector3 _leaderStationaryCheckPosition;
    int _leaderStationarySteps;

    /// <summary>
    /// Issues a short leader path for the whole friendly tactical group via <see cref="IMovementSystem.MoveGroupPath"/> (RIDE grouped pathing).
    /// Falls back to <see cref="TeleportFriendlySquadPreservingFormation"/> if <see cref="m_squadTacticalGroup"/> is not set.
    /// </summary>
    void IssueFriendlySquadLeaderStepPath(RideVector3 leaderDestination)
    {
        ITransformSystem transformSystem = api.GetSystem<ITransformSystem>();
        IMovementSystem movementSystem = api.movementSystem;
        RideVector3 leaderFrom = transformSystem.GetPosition(agent);

        if (IsValidRideId(m_squadTacticalGroup))
        {
            RideVector3[] path = { leaderFrom, leaderDestination };
            movementSystem.MoveGroupPath(m_squadTacticalGroup, path, squadPathFormation);
            return;
        }

        TeleportFriendlySquadPreservingFormation(leaderDestination);
    }

    static int EntityIdKey(RideID id) => id.id;

    void BuildFriendlySquadOffsetsFromSpawns()
    {
        _friendlySquadOffsets = new List<(int, double, double, double)>();
        var spawns = _lockedFriendlySpawnWorld ?? _friendlySpawnWorld;
        if (spawns == null || spawns.Count == 0)
            return;

        RideVector3 leaderWorld = api.GetSystem<ITransformSystem>().GetPosition(agent);
        foreach (var pair in spawns)
        {
            if (SameRideEntityId(pair.id, agent))
            {
                leaderWorld = pair.world;
                break;
            }
        }

        foreach (var pair in spawns)
        {
            if (!IsValidRideId(pair.id) || SameRideEntityId(pair.id, agent))
                continue;
            _friendlySquadOffsets.Add((
                EntityIdKey(pair.id),
                pair.world.x - leaderWorld.x,
                pair.world.y - leaderWorld.y,
                pair.world.z - leaderWorld.z));
        }

        EnforceMaxSquadSpacing(_friendlySquadOffsets, Mathf.Max(squadMaxMemberSpacingM, 1f));
    }

    static void EnforceMaxSquadSpacing(
        List<(int entityId, double offsetX, double offsetY, double offsetZ)> offsets,
        float maxM)
    {
        if (offsets == null || offsets.Count == 0)
            return;

        for (int pass = 0; pass < 12; pass++)
        {
            bool changed = false;
            for (int i = 0; i < offsets.Count; i++)
            {
                var a = offsets[i];
                double ax = a.offsetX;
                double az = a.offsetZ;
                double distLeader = System.Math.Sqrt(ax * ax + az * az);
                if (distLeader > maxM)
                {
                    double scale = maxM / distLeader;
                    offsets[i] = (a.entityId, ax * scale, a.offsetY, az * scale);
                    changed = true;
                }

                for (int j = i + 1; j < offsets.Count; j++)
                {
                    var b = offsets[j];
                    double dx = b.offsetX - offsets[i].offsetX;
                    double dz = b.offsetZ - offsets[i].offsetZ;
                    double dist = System.Math.Sqrt(dx * dx + dz * dz);
                    if (dist <= maxM || dist < 1e-3)
                        continue;
                    double scale = maxM / dist;
                    var ai = offsets[i];
                    var bj = offsets[j];
                    double midX = (ai.offsetX + bj.offsetX) * 0.5;
                    double midZ = (ai.offsetZ + bj.offsetZ) * 0.5;
                    double halfDx = dx * 0.5 * scale;
                    double halfDz = dz * 0.5 * scale;
                    offsets[i] = (ai.entityId, midX - halfDx, ai.offsetY, midZ - halfDz);
                    offsets[j] = (bj.entityId, midX + halfDx, bj.offsetY, midZ + halfDz);
                    changed = true;
                }
            }

            if (!changed)
                break;
        }
    }

    RideVector3 WingmanDestinationFromOffset(
        RideVector3 leaderDestination,
        int entityId,
        RideVector3 fallbackSpawnWorld)
    {
        if (_friendlySquadOffsets != null)
        {
            foreach (var o in _friendlySquadOffsets)
            {
                if (o.entityId != entityId)
                    continue;
                return new RideVector3(
                    (float)(leaderDestination.x + o.offsetX),
                    (float)(leaderDestination.y + o.offsetY),
                    (float)(leaderDestination.z + o.offsetZ));
            }
        }

        RideVector3 leaderSpawn = m_startingPosition;
        var friendlySpawns = FriendlySpawnsForReset();
        if (friendlySpawns != null)
        {
            foreach (var pair in friendlySpawns)
            {
                if (SameRideEntityId(pair.id, agent))
                {
                    leaderSpawn = pair.world;
                    break;
                }
            }
        }

        return new RideVector3(
            (float)(leaderDestination.x + (fallbackSpawnWorld.x - leaderSpawn.x)),
            (float)(leaderDestination.y + (fallbackSpawnWorld.y - leaderSpawn.y)),
            (float)(leaderDestination.z + (fallbackSpawnWorld.z - leaderSpawn.z)));
    }

    /// <summary>
    /// Teleports the RL leader and every friendly using locked formation offsets (≤ squadMaxMemberSpacingM apart).
    /// </summary>
    void TeleportFriendlySquadPreservingFormation(RideVector3 leaderDestination)
    {
        ITransformSystem transformSystem = api.GetSystem<ITransformSystem>();
        IMovementSystem movementSystem = api.movementSystem;
        IAgentSystem agentSystem = Globals.api.agentSystem;
        const Team friendlyTeam = Team.Blue;

        if (agentSystem.GetAgentTeam(agent) != friendlyTeam)
        {
            Debug.LogError(
                $"AGAMOTTO_ATD_Agent: Refusing squad teleport — RL agent team is "
                + $"{agentSystem.GetAgentTeam(agent)}, expected {friendlyTeam}.");
            return;
        }

        if (_friendlySquadOffsets == null || _friendlySquadOffsets.Count == 0)
            BuildFriendlySquadOffsetsFromSpawns();

        int moved = 0;

        void TeleportIfAlive(RideID id, RideVector3 dest)
        {
            if (!IsValidRideId(id))
                return;
            if (agentSystem.GetAgentTeam(id) != friendlyTeam)
                return;
            if (agentSystem.IsAgentDead(id))
                return;
            movementSystem.MoveTeleport(id, dest);
            moved++;
        }

        TeleportIfAlive(agent, leaderDestination);

        var friendlySpawns = FriendlySpawnsForReset();
        if (friendlySpawns != null)
        {
            foreach (var pair in friendlySpawns)
            {
                if (!IsValidRideId(pair.id) || SameRideEntityId(pair.id, agent))
                    continue;
                RideVector3 dest = WingmanDestinationFromOffset(
                    leaderDestination,
                    EntityIdKey(pair.id),
                    pair.world);
                TeleportIfAlive(pair.id, dest);
            }
        }
        else
        {
            float maxR = Mathf.Max(squadFormationFallbackMaxHorizontalM, 1f);
            float maxSq = maxR * maxR;
            RideVector3 leaderFrom = transformSystem.GetPosition(agent);
            IEnumerable<RideID> mates = api.scenarioSystem.GetAgents(friendlyTeam);
            if (mates != null)
            {
                foreach (RideID id in mates)
                {
                    if (!IsValidRideId(id) || SameRideEntityId(id, agent))
                        continue;
                    RideVector3 p = transformSystem.GetPosition(id);
                    float hdx = (float)(p.x - leaderFrom.x);
                    float hdz = (float)(p.z - leaderFrom.z);
                    if (hdx * hdx + hdz * hdz > maxSq)
                        continue;
                    RideVector3 dest = WingmanDestinationFromOffset(
                        leaderDestination,
                        EntityIdKey(id),
                        p);
                    TeleportIfAlive(id, dest);
                }
            }
        }

        if (!_loggedSquadMoveThisEpisode && moved > 1)
        {
            _loggedSquadMoveThisEpisode = true;
            Debug.Log(
                $"AGAMOTTO_ATD_Agent: Squad moved together (teleport): {moved} live friendlies "
                + $"(max spacing={squadMaxMemberSpacingM:F0}m).");
        }
    }

    /// <summary>Replaces spawn caches (used when RL init captures squads).</summary>
    public void ReplaceEpisodeSpawnSnapshots(IEnumerable<RideID> friendlyIds, IEnumerable<RideID> hostileIds)
    {
        ITransformSystem ts = api.GetSystem<ITransformSystem>();
        _friendlySpawnWorld = BuildSpawnSnapshot(friendlyIds, ts);
        _enemySpawnWorld = BuildSpawnSnapshot(hostileIds, ts);
        BuildFriendlySquadOffsetsFromSpawns();
        Debug.Log(
            $"AGAMOTTO_ATD_Agent: Spawn snapshot replace → {_friendlySpawnWorld.Count} friendlies, "
            + $"{_enemySpawnWorld.Count} hostiles.");
    }

    /// <summary>
    /// Merges sim positions into spawn caches (delayed capture pass).
    /// <see cref="OnEpisodeBegin"/> uses locked anchors when <see cref="TryLockEpisodeSpawnAnchors"/> succeeded.
    /// </summary>
    public void SetEpisodeSpawnSnapshots(IEnumerable<RideID> friendlyIds, IEnumerable<RideID> hostileIds)
    {
        ITransformSystem ts = api.GetSystem<ITransformSystem>();
        _friendlySpawnWorld = MergeSpawnSnapshot(_friendlySpawnWorld, friendlyIds, ts);
        _enemySpawnWorld = MergeSpawnSnapshot(_enemySpawnWorld, hostileIds, ts);
        Debug.Log(
            $"AGAMOTTO_ATD_Agent: Spawn snapshot merge → {_friendlySpawnWorld.Count} friendlies, "
            + $"{_enemySpawnWorld.Count} hostiles tracked.");
    }

    /// <summary>Freeze RL-init poses so episode reset does not drift from live positions.</summary>
    public void TryLockEpisodeSpawnAnchors()
    {
        if (_lockedFriendlySpawnWorld != null && _lockedEnemySpawnWorld != null)
            return;
        if (_friendlySpawnWorld == null || _friendlySpawnWorld.Count < 4)
            return;
        if (_enemySpawnWorld == null || _enemySpawnWorld.Count < 1)
            return;

        _lockedFriendlySpawnWorld = CloneSpawnList(_friendlySpawnWorld);
        _lockedEnemySpawnWorld = CloneSpawnList(_enemySpawnWorld);
        BuildFriendlySquadOffsetsFromSpawns();
        Debug.Log(
            $"AGAMOTTO_ATD_Agent: Locked episode spawn anchors "
            + $"({_lockedFriendlySpawnWorld.Count} friendlies, {_lockedEnemySpawnWorld.Count} hostiles, "
            + $"{_friendlySquadOffsets?.Count ?? 0} offsets ≤{squadMaxMemberSpacingM:F0}m).");
    }

    static List<(RideID id, RideVector3 world)> CloneSpawnList(List<(RideID id, RideVector3 world)> source)
    {
        var copy = new List<(RideID id, RideVector3 world)>(source.Count);
        foreach (var pair in source)
            copy.Add(pair);
        return copy;
    }

    static List<(RideID id, RideVector3 world)> BuildSpawnSnapshot(
        IEnumerable<RideID> ids,
        ITransformSystem ts)
    {
        var list = new List<(RideID id, RideVector3 world)>();
        if (ids == null)
            return list;
        foreach (RideID id in ids)
        {
            if (!IsValidRideId(id))
                continue;
            list.Add((id, ts.GetPosition(id)));
        }
        return list;
    }

    public void EnableRlCombatEngagement()
    {
        Globals.api.attackSystem.flags =
            Ride.Combat.AttackSystemFlags.Auto_Attack | Ride.Combat.AttackSystemFlags.Acquire_Targets;
    }

    static void SuppressCombatDuringReset()
    {
        Globals.api.attackSystem.flags = Ride.Combat.AttackSystemFlags.Acquire_Targets;
    }

    IEnumerable<(RideID id, RideVector3 world)> FriendlySpawnsForReset() =>
        _lockedFriendlySpawnWorld ?? _friendlySpawnWorld;

    IEnumerable<(RideID id, RideVector3 world)> EnemySpawnsForReset() =>
        _lockedEnemySpawnWorld ?? _enemySpawnWorld;

    static List<(RideID id, RideVector3 world)> MergeSpawnSnapshot(
        List<(RideID id, RideVector3 world)> prior,
        IEnumerable<RideID> incoming,
        ITransformSystem ts)
    {
        var dict = new Dictionary<(int, int), (RideID id, RideVector3 world)>();
        if (prior != null)
        {
            foreach (var pair in prior)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                dict[AliveKey(pair.id)] = pair;
            }
        }

        if (incoming != null)
        {
            foreach (RideID id in incoming)
            {
                if (!IsValidRideId(id))
                    continue;
                dict[AliveKey(id)] = (id, ts.GetPosition(id));
            }
        }

        var result = new List<(RideID id, RideVector3 world)>(dict.Count);
        foreach (var pair in dict.Values)
            result.Add(pair);
        return result;
    }

    /// <summary>
    /// Revives everyone first, then teleports (wingmen then leader). Uses <see cref="agent"/> for leader ops when
    /// snapshot <see cref="RideID.version"/> lags RIDE, otherwise dead-leader teleports are skipped and revive never sticks.
    /// </summary>
    void TeleportTeamToSpawnWorld(IEnumerable<(RideID id, RideVector3 world)> spawns)
    {
        if (spawns == null)
            return;
        IMovementSystem movementSystem = api.movementSystem;
        IAgentSystem agentSystem = Globals.api.agentSystem;
        var list = spawns as IList<(RideID id, RideVector3 world)> ?? spawns.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            var pair = list[i];
            if (!IsValidRideId(pair.id))
                continue;
            RideID opId = SameRideEntityId(pair.id, agent) ? agent : pair.id;
            if (agentSystem.IsAgentDead(opId))
                agentSystem.ReviveAgent(opId);
        }

        for (int i = 0; i < list.Count; i++)
        {
            var pair = list[i];
            if (!IsValidRideId(pair.id) || SameRideEntityId(pair.id, agent))
                continue;
            movementSystem.MoveTeleport(pair.id, pair.world);
        }

        for (int i = 0; i < list.Count; i++)
        {
            var pair = list[i];
            if (!IsValidRideId(pair.id) || !SameRideEntityId(pair.id, agent))
                continue;
            if (agentSystem.IsAgentDead(agent))
                agentSystem.ReviveAgent(agent);
            movementSystem.MoveTeleport(agent, pair.world);
        }
    }

    /// <summary>Ensures the RL-controlled leader is in the snapshot so squad reset always moves them to <see cref="m_startingPosition"/>.</summary>
    void EnsureLeaderInFriendlySpawnSnapshot()
    {
        if (_friendlySpawnWorld == null)
            _friendlySpawnWorld = new List<(RideID id, RideVector3 world)>();
        foreach (var pair in _friendlySpawnWorld)
        {
            if (SameRideEntityId(pair.id, agent))
                return;
        }

        _friendlySpawnWorld.Add((agent, m_startingPosition));
        Debug.Log($"AGAMOTTO_ATD_Agent: Appended RL leader to friendly spawn snapshot (id={agent.id} v={agent.version}).");
    }

    void SyncLeaderUnityTransformFromRide()
    {
        RideVector3 p = api.GetSystem<ITransformSystem>().GetPosition(agent);
        agentTransform.position = new Vector3((float)p.x, (float)p.y, (float)p.z);
    }

    /// <summary>Leader world position from RIDE sim state (Unity transform can lag).</summary>
    Vector3 GetAgentWorldPosition()
    {
        RideVector3 p = api.GetSystem<ITransformSystem>().GetPosition(agent);
        return new Vector3((float)p.x, (float)p.y, (float)p.z);
    }

    public override void OnEpisodeBegin()
    {
        base.OnEpisodeBegin();

        episodeCounter++;
        Debug.Log($"Episode {episodeCounter}...");
        IAgentSystem agentSystem = Globals.api.agentSystem;
        IMovementSystem movementSystem = Globals.api.movementSystem;

        Debug.Log($"Episode reset, moving agent from {agentTransform.position} to start; enemy to {m_enemyStartingPosition}");

        SuppressCombatDuringReset();

        var friendlySpawns = FriendlySpawnsForReset();
        var enemySpawns = EnemySpawnsForReset();

        // Friendlies first (revive + teleport), then hostiles — avoids crossfire during reset.
        if (friendlySpawns != null && friendlySpawns.Any())
        {
            TeleportTeamToSpawnWorld(friendlySpawns);
            Debug.Log($"AGAMOTTO_ATD_Agent: Teleported friendly squad to spawn anchors.");
        }
        else
        {
            EnsureLeaderInFriendlySpawnSnapshot();
            if (agentSystem.IsAgentDead(agent))
                agentSystem.ReviveAgent(agent);
            Debug.LogWarning("AGAMOTTO_ATD_Agent: No friendly spawn snapshot; using formation delta to m_startingPosition.");
            TeleportFriendlySquadPreservingFormation(m_startingPosition);
        }

        if (enemySpawns != null && enemySpawns.Any())
        {
            TeleportTeamToSpawnWorld(enemySpawns);
            Debug.Log($"AGAMOTTO_ATD_Agent: Teleported hostile squad to spawn anchors.");
        }
        else if (HasAssignedEnemy(m_enemy))
        {
            if (agentSystem.IsAgentDead(m_enemy))
                agentSystem.ReviveAgent(m_enemy);
            movementSystem.MoveTeleport(m_enemy, m_enemyStartingPosition);
        }

        SyncLeaderUnityTransformFromRide();

        // Align reward / obs bookkeeping with simulation pose (not the ML-Agents Unity transform).
        previousPosition = GetAgentWorldPosition();

        Rigidbody rb = agentTransform.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (agentSystem.IsAgentDead(agent))
        {
            Debug.LogWarning($"AGAMOTTO_ATD_Agent: Leader still dead after reset at step {stepCount}; forcing revive and teleport to m_startingPosition.");
            agentSystem.ReviveAgent(agent);
            movementSystem.MoveTeleport(agent, m_startingPosition);
        }

        if ((enemySpawns == null || !enemySpawns.Any()) && !HasAssignedEnemy(m_enemy))
            Debug.LogWarning("AGAMOTTO_ATD_Agent: m_enemy is not assigned; skipping enemy reset.");

        EnableRlCombatEngagement();
        RefreshSquadAliveBaselines(agentSystem);
        ResetEpisodeKillCounters();
        _loggedSquadMoveThisEpisode = false;
        _leaderStationaryCheckPosition = GetAgentWorldPosition();
        _leaderStationarySteps = 0;
    }

    /// <summary>
    /// Tracks consecutive RL leader steps with negligible horizontal movement; ends episode after <see cref="leaderStationaryMaxSteps"/>.
    /// </summary>
    /// <returns>True if the episode was ended due to stationary leader.</returns>
    bool CheckLeaderStationaryTermination()
    {
        if (leaderStationaryMaxSteps <= 0)
            return false;

        Vector3 leaderPos = GetAgentWorldPosition();
        float hdx = leaderPos.x - _leaderStationaryCheckPosition.x;
        float hdz = leaderPos.z - _leaderStationaryCheckPosition.z;
        float horizontalDelta = Mathf.Sqrt(hdx * hdx + hdz * hdz);
        float threshold = Mathf.Max(leaderStationaryMoveThresholdM, 1e-4f);

        if (horizontalDelta < threshold)
            _leaderStationarySteps++;
        else
            _leaderStationarySteps = 0;

        _leaderStationaryCheckPosition = leaderPos;

        if (_leaderStationarySteps <= leaderStationaryMaxSteps)
            return false;

        Debug.Log(
            $"AGAMOTTO_ATD_Agent: Episode failure — leader stationary for {_leaderStationarySteps} steps "
            + $"(limit={leaderStationaryMaxSteps}, last_delta={horizontalDelta:F3}m).");
        AddReward(-0.25f * m_successReward);
        NotifyTakTrainingOutcome(false, "leader_stationary");
        DispatchTrainingResult(false);
        suppressStepPunishmentThisAction = true;
        EndEpisodeWithTrainingStats("leader_stationary");
        return true;
    }

    void ResetEpisodeKillCounters()
    {
        _episodeEnemiesKilled = 0;
        _episodeFriendliesLost = 0;
    }

    /// <summary>Unity log + TensorBoard scalars for eval summarization.</summary>
    void LogAndRecordEpisodeKillStats(string outcome)
    {
        Debug.Log(
            $"AGAMOTTO_ATD_Agent: Episode {episodeCounter} end ({outcome}) — "
            + $"enemies_killed={_episodeEnemiesKilled}, friendlies_lost={_episodeFriendliesLost}, "
            + $"step={stepCount}, cumulative_reward={GetCumulativeReward():F3}");

        if (Academy.Instance?.StatsRecorder == null)
            return;
        Academy.Instance.StatsRecorder.Add(
            "AGAMOTTO/Enemies Killed Per Episode",
            _episodeEnemiesKilled,
            StatAggregationMethod.MostRecent);
        Academy.Instance.StatsRecorder.Add(
            "AGAMOTTO/Friendlies Lost Per Episode",
            _episodeFriendliesLost,
            StatAggregationMethod.MostRecent);
    }

    void EndEpisodeWithTrainingStats(string outcome)
    {
        LogAndRecordEpisodeKillStats(outcome);
        EndEpisode();
    }

    /// <summary>Resets per-episode tracking so squad death rewards only fire on transitions after spawn.</summary>
    void RefreshSquadAliveBaselines(IAgentSystem agentSystem)
    {
        _enemyAlivePrevStep.Clear();
        var enemySpawns = EnemySpawnsForReset();
        if (enemySpawns != null)
        {
            foreach (var pair in enemySpawns)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _enemyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }

        _friendlyAlivePrevStep.Clear();
        var friendlySpawns = FriendlySpawnsForReset();
        if (friendlySpawns != null)
        {
            foreach (var pair in friendlySpawns)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _friendlyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }
    }

    /// <summary>Rewards enemy squad losses (except primary <see cref="m_enemy"/>, which uses mission success) and penalizes friendly wingman deaths.</summary>
    void ApplySquadCombatDeathRewards(IAgentSystem agentSystem)
    {
        var enemySpawns = EnemySpawnsForReset();
        if (enemySpawns != null)
        {
            foreach (var pair in enemySpawns)
            {
                RideID id = pair.id;
                if (!IsValidRideId(id))
                    continue;
                bool aliveNow = !agentSystem.IsAgentDead(id);
                bool alivePrev = _enemyAlivePrevStep.Contains(AliveKey(id));
                if (!alivePrev || aliveNow)
                    continue;
                bool isPrimary = HasAssignedEnemy(m_enemy) && SameRideId(id, m_enemy);
                _episodeEnemiesKilled++;
                if (isPrimary)
                    Debug.Log($"AGAMOTTO_ATD_Agent: Enemy (primary / mission target) killed id={id.id} v={id.version}.");
                else
                {
                    if (rewardPerEnemySquadMemberKilled != 0f)
                        AddReward(rewardPerEnemySquadMemberKilled);
                    Debug.Log($"AGAMOTTO_ATD_Agent: Enemy squad member killed id={id.id} v={id.version} reward={rewardPerEnemySquadMemberKilled}.");
                }
            }

            _enemyAlivePrevStep.Clear();
            foreach (var pair in enemySpawns)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _enemyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }

        var friendlySpawns = FriendlySpawnsForReset();
        if (friendlySpawns != null)
        {
            foreach (var pair in friendlySpawns)
            {
                RideID id = pair.id;
                if (!IsValidRideId(id))
                    continue;
                bool isLeader = SameRideEntityId(id, agent);
                bool aliveNow = !agentSystem.IsAgentDead(id);
                bool alivePrev = _friendlyAlivePrevStep.Contains(AliveKey(id));
                if (!alivePrev || aliveNow || isLeader)
                    continue;
                _episodeFriendliesLost++;
                if (penaltyPerFriendlyWingmanDeath != 0f)
                    AddReward(-penaltyPerFriendlyWingmanDeath);
                Debug.Log($"AGAMOTTO_ATD_Agent: Friendly wingman killed id={id.id} v={id.version} penalty={penaltyPerFriendlyWingmanDeath}.");
            }

            _friendlyAlivePrevStep.Clear();
            foreach (var pair in friendlySpawns)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _friendlyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }
    }

    float GetEnemySquadAliveRatio(IAgentSystem agentSystem)
    {
        var enemySpawns = EnemySpawnsForReset();
        if (enemySpawns == null)
            return 0f;
        var list = enemySpawns as IList<(RideID id, RideVector3 world)> ?? enemySpawns.ToList();
        if (list.Count == 0)
            return 0f;
        int alive = 0;
        foreach (var pair in list)
        {
            if (!IsValidRideId(pair.id))
                continue;
            if (!agentSystem.IsAgentDead(pair.id))
                alive++;
        }

        return (float)alive / list.Count;
    }

    float GetFriendlyWingmanAliveRatio(IAgentSystem agentSystem)
    {
        var friendlySpawns = FriendlySpawnsForReset();
        if (friendlySpawns == null)
            return 0f;
        int wing = 0;
        int alive = 0;
        foreach (var pair in friendlySpawns)
        {
            if (!IsValidRideId(pair.id) || SameRideEntityId(pair.id, agent))
                continue;
            wing++;
            if (!agentSystem.IsAgentDead(pair.id))
                alive++;
        }

        if (wing == 0)
            return 1f;
        return (float)alive / wing;
    }

    /// <summary>
    /// v2b vector (10): rel enemy xz (2), rel height (1), dist primary (1), bearing sin/cos (2),
    /// primary alive (1), enemy squad alive ratio (1), wingman alive ratio (1), step fraction (1).
    /// Must match <c>BehaviorParameters.VectorObservationSize</c> (plus any base <see cref="RLAgent"/> extras).
    /// </summary>
    public override void CollectObservations(VectorSensor sensor)
    {
        base.CollectObservations(sensor);

        IAgentSystem agentSystem = Globals.api.agentSystem;
        Vector3 agentPos = GetAgentWorldPosition();
        float R = Mathf.Max(observationMapRadiusM, 1f);
        float H = Mathf.Max(observationHeightScaleM, 1f);

        RideVector3 enemyRv;
        if (HasAssignedEnemy(m_enemy))
            enemyRv = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        else
            enemyRv = new RideVector3(agentPos.x, agentPos.y, agentPos.z);

        Vector3 enemyWorld = new Vector3((float)enemyRv.x, (float)enemyRv.y, (float)enemyRv.z);
        float relX = enemyWorld.x - agentPos.x;
        float relZ = enemyWorld.z - agentPos.z;
        float relY = enemyWorld.y - agentPos.y;

        sensor.AddObservation(Mathf.Clamp(relX / R, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(relZ / R, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(relY / H, -1f, 1f));

        float dist = Vector3.Distance(agentPos, enemyWorld);
        sensor.AddObservation(Mathf.Clamp01(dist / R));

        float theta = Mathf.Atan2(relZ, relX);
        sensor.AddObservation(Mathf.Sin(theta));
        sensor.AddObservation(Mathf.Cos(theta));

        float primaryAlive = HasAssignedEnemy(m_enemy) && !agentSystem.IsAgentDead(m_enemy) ? 1f : 0f;
        sensor.AddObservation(primaryAlive);

        sensor.AddObservation(GetEnemySquadAliveRatio(agentSystem));
        sensor.AddObservation(GetFriendlyWingmanAliveRatio(agentSystem));

        float stepFrac = MaxStep > 0 ? Mathf.Clamp01((float)stepCount / MaxStep) : 0f;
        sensor.AddObservation(stepFrac);
    }
    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log("Obstacle!");
        // Light penalty so the policy learns to avoid clutter in large environments without ending every bump.
        AddReward(-0.002f);
    }
    protected override void TakeAction(ActionBuffers actions)
    {
        IAgentSystem agentSystem = Globals.api.agentSystem;
        bool inCombatWarmup = stepCount <= combatTerminalWarmupSteps;

        if (!inCombatWarmup)
            ApplySquadCombatDeathRewards(agentSystem);

        if (!inCombatWarmup && HasAssignedEnemy(m_enemy) && agentSystem.IsAgentDead(m_enemy))
        {
            Debug.Log($"AGAMOTTO_ATD_Agent: Episode success — primary enemy eliminated id={m_enemy.id} v={m_enemy.version}.");
            AddReward(m_successReward);
            NotifyTakTrainingOutcome(true, "primary_enemy_killed");
            DispatchTrainingResult(true);
            suppressStepPunishmentThisAction = true;
            EndEpisodeWithTrainingStats("primary_enemy_killed");
            return;
        }

        if (!inCombatWarmup && agentSystem.IsAgentDead(agent))
        {
            Debug.Log($"AGAMOTTO_ATD_Agent: Episode failure — RL leader killed id={agent.id} v={agent.version}.");
            _episodeFriendliesLost++;
            AddReward(-1 * m_successReward);
            NotifyTakTrainingOutcome(false, "leader_killed");
            DispatchTrainingResult(false);
            suppressStepPunishmentThisAction = true;
            EndEpisodeWithTrainingStats("leader_killed");
            return;
        }

        float xmov = (float)System.Math.Tanh(actions.ContinuousActions[0]);
        float zmov = (float)System.Math.Tanh(actions.ContinuousActions[1]);

        Vector3 rawMove = new Vector3(xmov, 0f, zmov);
        if (rawMove.sqrMagnitude >= MinMoveDirectionSqr)
        {
            Vector3 direction = rawMove.normalized;

            var maxSpeed = Globals.api.agentSystem.GetAgentMaxSpeed(agent);
            float deltaTime = Mathf.Max(Time.deltaTime, MinDecisionDeltaSeconds);
            float stepDistance = Mathf.Max(maxSpeed * deltaTime, MinMoveStepMeters);

            RideVector3 fromRide = api.GetSystem<ITransformSystem>().GetPosition(agent);
            Vector3 from = new Vector3((float)fromRide.x, (float)fromRide.y, (float)fromRide.z);
            Vector3 newPos = from + direction * stepDistance;

            RideVector3 dest = new RideVector3(newPos.x, newPos.y, newPos.z);
            if (useSquadNavMeshPaths)
                IssueFriendlySquadLeaderStepPath(dest);
            else
                TeleportFriendlySquadPreservingFormation(dest);
        }

        if (!HasAssignedEnemy(m_enemy))
            return;

        RideVector3 enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        Vector3 agentWorld = GetAgentWorldPosition();
        Vector3 enemyWorldV = new Vector3((float)enemyPos.x, (float)enemyPos.y, (float)enemyPos.z);
        float enemyDist = Vector3.Distance(agentWorld, enemyWorldV);

        float previousDistance = Vector3.Distance(previousPosition, enemyWorldV);


        // Distance shaping should be small enough that terminal reward is still meaningful.
        if (enemyDist < previousDistance)
            AddReward(+0.002f);
        else
            AddReward(-0.0008f);

        if(stepCount > MaxStep)
        {
            NotifyTakTrainingOutcome(false, "max_step");
            DispatchTrainingResult(false);
            Debug.Log($"Episode reset at step: {stepCount}");
            suppressStepPunishmentThisAction = true;
            EndEpisodeWithTrainingStats("max_step");
            return;
        }

        previousPosition = GetAgentWorldPosition();

        if (!inCombatWarmup && CheckLeaderStationaryTermination())
            return;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuous = actionsOut.ContinuousActions;
        continuous[0] = 0f;
        continuous[1] = 0f;
        if (Input.GetKey(KeyCode.D))
            continuous[0] = 1f;
        if (Input.GetKey(KeyCode.A))
            continuous[0] = -1f;
        if (Input.GetKey(KeyCode.W))
            continuous[1] = 1f;
        if (Input.GetKey(KeyCode.S))
            continuous[1] = -1f;
    }
}
#else
    public class TakeCoverMLAgent : RLAgent
    {
        public GoalArea m_goal;
        public RideID m_enemy;

        public bool isCovered;

        protected override void TakeAction(float[] vectorAction) { Debug.LogFormat("TakeAction() - vectorAction.Length: {0}", vectorAction.Length); }
    }
#endif
