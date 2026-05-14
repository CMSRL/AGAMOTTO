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
    [Tooltip("Formation used with MoveGroupPath when moving the friendly tactical group during RL.")]
    public FormationProcedureType squadPathFormation = FormationProcedureType.Wedge;
    [Tooltip("When no RL spawn snapshot exists, include same-team agents within this horizontal distance (m) of the leader for formation teleport.")]
    public float squadFormationFallbackMaxHorizontalM = 800f;

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

    /// <summary>
    /// Episode reset / fallback only: teleports the RL friendly squad with the leader (same world delta).
    /// In-episode control uses <see cref="IssueFriendlySquadLeaderStepPath"/> instead.
    /// </summary>
    void TeleportFriendlySquadPreservingFormation(RideVector3 leaderDestination)
    {
        ITransformSystem transformSystem = api.GetSystem<ITransformSystem>();
        IMovementSystem movementSystem = api.movementSystem;
        IAgentSystem agentSystem = Globals.api.agentSystem;

        RideVector3 leaderFrom = transformSystem.GetPosition(agent);
        double dx = leaderDestination.x - leaderFrom.x;
        double dy = leaderDestination.y - leaderFrom.y;
        double dz = leaderDestination.z - leaderFrom.z;

        void TeleportIfAlive(RideID id, RideVector3 dest)
        {
            if (!IsValidRideId(id))
                return;
            if (agentSystem.IsAgentDead(id))
                return;
            movementSystem.MoveTeleport(id, dest);
        }

        // Prefer tracked RL squad (same IDs as episode reset) so wingmen always move with the leader.
        if (_friendlySpawnWorld != null && _friendlySpawnWorld.Count > 0)
        {
            TeleportIfAlive(agent, leaderDestination);

            foreach (var pair in _friendlySpawnWorld)
            {
                if (!IsValidRideId(pair.id) || SameRideEntityId(pair.id, agent))
                    continue;
                RideVector3 p = transformSystem.GetPosition(pair.id);
                RideVector3 dest = new RideVector3(
                    (float)(p.x + dx),
                    (float)(p.y + dy),
                    (float)(p.z + dz));
                TeleportIfAlive(pair.id, dest);
            }

            return;
        }

        float maxR = Mathf.Max(squadFormationFallbackMaxHorizontalM, 1f);
        float maxSq = maxR * maxR;
        Team friendTeam = agentSystem.GetAgentTeam(agent);
        IEnumerable<RideID> mates = api.scenarioSystem.GetAgents(friendTeam);
        if (mates == null)
        {
            movementSystem.MoveTeleport(agent, leaderDestination);
            return;
        }

        foreach (RideID id in mates)
        {
            if (!IsValidRideId(id))
                continue;
            if (agentSystem.IsAgentDead(id))
                continue;

            RideVector3 p = transformSystem.GetPosition(id);
            bool isLeader = SameRideEntityId(id, agent);
            if (!isLeader)
            {
                float hdx = (float)(p.x - leaderFrom.x);
                float hdz = (float)(p.z - leaderFrom.z);
                if (hdx * hdx + hdz * hdz > maxSq)
                    continue;
            }

            RideVector3 dest = new RideVector3(
                (float)(p.x + dx),
                (float)(p.y + dy),
                (float)(p.z + dz));
            movementSystem.MoveTeleport(id, dest);
        }
    }

    /// <summary>
    /// Called from <see cref="AGAMOTTO_RL_Behavior"/> after units exist. Merges sim positions into spawn caches so
    /// later snapshots (e.g. delayed) add units without dropping IDs from an earlier pass.
    /// <see cref="OnEpisodeBegin"/> uses these to reset full squads.
    /// </summary>
    public void SetEpisodeSpawnSnapshots(IEnumerable<RideID> friendlyIds, IEnumerable<RideID> hostileIds)
    {
        ITransformSystem ts = api.GetSystem<ITransformSystem>();

        _friendlySpawnWorld = MergeSpawnSnapshot(_friendlySpawnWorld, friendlyIds, ts);
        _enemySpawnWorld = MergeSpawnSnapshot(_enemySpawnWorld, hostileIds, ts);

        Debug.Log($"AGAMOTTO_ATD_Agent: Spawn snapshot merge → {_friendlySpawnWorld.Count} friendlies, {_enemySpawnWorld.Count} hostiles tracked.");
    }

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

        EnsureLeaderInFriendlySpawnSnapshot();

        // Revive RL leader before movement so RIDE does not ignore teleports on a dead handle.
        if (agentSystem.IsAgentDead(agent))
            agentSystem.ReviveAgent(agent);

        // Reset full squads to RL-init sim poses (merged snapshots + leader-last teleport).
        if (_friendlySpawnWorld != null && _friendlySpawnWorld.Count > 0)
        {
            TeleportTeamToSpawnWorld(_friendlySpawnWorld);
            Debug.Log($"AGAMOTTO_ATD_Agent: Teleported {_friendlySpawnWorld.Count} friendlies to spawn snapshots.");
        }
        else
        {
            Debug.LogWarning("AGAMOTTO_ATD_Agent: No friendly spawn snapshot; using formation delta to m_startingPosition.");
            TeleportFriendlySquadPreservingFormation(m_startingPosition);
        }

        if (_enemySpawnWorld != null && _enemySpawnWorld.Count > 0)
            TeleportTeamToSpawnWorld(_enemySpawnWorld);
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

        // Enemy squad already reset above when _enemySpawnWorld is set; single-enemy fallback only if no snapshot.
        if ((_enemySpawnWorld == null || _enemySpawnWorld.Count == 0) && !HasAssignedEnemy(m_enemy))
            Debug.LogWarning("AGAMOTTO_ATD_Agent: m_enemy is not assigned; skipping enemy reset.");

        RefreshSquadAliveBaselines(agentSystem);
    }

    /// <summary>Resets per-episode tracking so squad death rewards only fire on transitions after spawn.</summary>
    void RefreshSquadAliveBaselines(IAgentSystem agentSystem)
    {
        _enemyAlivePrevStep.Clear();
        if (_enemySpawnWorld != null)
        {
            foreach (var pair in _enemySpawnWorld)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _enemyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }

        _friendlyAlivePrevStep.Clear();
        if (_friendlySpawnWorld != null)
        {
            foreach (var pair in _friendlySpawnWorld)
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
        if (_enemySpawnWorld != null)
        {
            foreach (var pair in _enemySpawnWorld)
            {
                RideID id = pair.id;
                if (!IsValidRideId(id))
                    continue;
                bool aliveNow = !agentSystem.IsAgentDead(id);
                bool alivePrev = _enemyAlivePrevStep.Contains(AliveKey(id));
                if (!alivePrev || aliveNow)
                    continue;
                bool isPrimary = HasAssignedEnemy(m_enemy) && SameRideId(id, m_enemy);
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
            foreach (var pair in _enemySpawnWorld)
            {
                if (!IsValidRideId(pair.id))
                    continue;
                if (!agentSystem.IsAgentDead(pair.id))
                    _enemyAlivePrevStep.Add(AliveKey(pair.id));
            }
        }

        if (_friendlySpawnWorld != null)
        {
            foreach (var pair in _friendlySpawnWorld)
            {
                RideID id = pair.id;
                if (!IsValidRideId(id))
                    continue;
                bool isLeader = SameRideEntityId(id, agent);
                bool aliveNow = !agentSystem.IsAgentDead(id);
                bool alivePrev = _friendlyAlivePrevStep.Contains(AliveKey(id));
                if (!alivePrev || aliveNow || isLeader)
                    continue;
                if (penaltyPerFriendlyWingmanDeath != 0f)
                    AddReward(-penaltyPerFriendlyWingmanDeath);
                Debug.Log($"AGAMOTTO_ATD_Agent: Friendly wingman killed id={id.id} v={id.version} penalty={penaltyPerFriendlyWingmanDeath}.");
            }

            _friendlyAlivePrevStep.Clear();
            foreach (var pair in _friendlySpawnWorld)
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
        if (_enemySpawnWorld == null || _enemySpawnWorld.Count == 0)
            return 0f;
        int alive = 0;
        foreach (var pair in _enemySpawnWorld)
        {
            if (!IsValidRideId(pair.id))
                continue;
            if (!agentSystem.IsAgentDead(pair.id))
                alive++;
        }

        return (float)alive / _enemySpawnWorld.Count;
    }

    float GetFriendlyWingmanAliveRatio(IAgentSystem agentSystem)
    {
        if (_friendlySpawnWorld == null || _friendlySpawnWorld.Count == 0)
            return 0f;
        int wing = 0;
        int alive = 0;
        foreach (var pair in _friendlySpawnWorld)
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

        ApplySquadCombatDeathRewards(agentSystem);

        if (HasAssignedEnemy(m_enemy) && agentSystem.IsAgentDead(m_enemy))
        {
            Debug.Log($"AGAMOTTO_ATD_Agent: Episode success — primary enemy eliminated id={m_enemy.id} v={m_enemy.version}.");
            AddReward(m_successReward);
            NotifyTakTrainingOutcome(true, "primary_enemy_killed");
            DispatchTrainingResult(true);
            suppressStepPunishmentThisAction = true;
            EndEpisode();
            return;
        }

        if (agentSystem.IsAgentDead(agent))
        {
            Debug.Log($"AGAMOTTO_ATD_Agent: Episode failure — RL leader killed id={agent.id} v={agent.version}.");
            AddReward(-1 * m_successReward);
            NotifyTakTrainingOutcome(false, "leader_killed");
            DispatchTrainingResult(false);
            suppressStepPunishmentThisAction = true;
            EndEpisode();
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
            EndEpisode();
            return;
        }

        previousPosition = GetAgentWorldPosition();

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
