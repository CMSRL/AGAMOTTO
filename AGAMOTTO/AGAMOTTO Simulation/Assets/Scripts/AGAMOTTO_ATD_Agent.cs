using EtcAbduction;
using Google.Protobuf.WellKnownTypes;
using Ride;
using Ride.AI;
using Ride.AI.RL;
using Ride.Entities;
using Ride.Movement;
using System.Collections;
using System.Collections.Generic;
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
    private Vector3 previousPosition;
    private int episodeCounter = 0;
    private const float MinMoveDirectionSqr = 1e-8f;
    /// <summary>Minimum leader step (meters) per RL decision when using teleport motion.</summary>
    const float MinMoveStepMeters = 0.15f;
    /// <summary>Floor for decision delta so <c>maxSpeed * dt</c> is not zero in batched / headless steps.</summary>
    const float MinDecisionDeltaSeconds = 1f / 20f;

    public static bool IsValidRideId(RideID id) => id.id != 0 || id.version != 0;

    static bool HasAssignedEnemy(RideID enemy) => IsValidRideId(enemy);

    /// <summary>Max horizontal distance from leader for a Blue agent to count as part of this squad when teleporting.</summary>
    const float SquadTeleportMaxHorizontalMeters = 50f;

    /// <summary>
    /// Teleports the squad leader to <paramref name="leaderDestination"/> and applies the same world delta to nearby
    /// friendly (Blue) agents so the wedge stays coherent. Uses <c>MoveTeleport</c> only (no <c>MoveGroupPath</c>).
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

        float maxSq = SquadTeleportMaxHorizontalMeters * SquadTeleportMaxHorizontalMeters;
        IEnumerable<RideID> blues = api.scenarioSystem.GetAgents(Team.Blue);
        if (blues == null)
        {
            movementSystem.MoveTeleport(agent, leaderDestination);
            return;
        }

        foreach (RideID id in blues)
        {
            if (!IsValidRideId(id))
                continue;
            if (agentSystem.IsAgentDead(id))
                continue;

            RideVector3 p = transformSystem.GetPosition(id);
            bool isLeader = id.id == agent.id && id.version == agent.version;
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
    /// Called from <see cref="AGAMOTTO_RL_Behavior"/> after units exist. Stores every Blue/Red sim position so
    /// <see cref="OnEpisodeBegin"/> can reset the full squads, not only the leader and closest enemy.
    /// </summary>
    public void SetEpisodeSpawnSnapshots(IEnumerable<RideID> blueIds, IEnumerable<RideID> redIds)
    {
        ITransformSystem ts = api.GetSystem<ITransformSystem>();

        _friendlySpawnWorld = new List<(RideID, RideVector3)>();
        if (blueIds != null)
        {
            var seen = new HashSet<(int, int)>();
            foreach (RideID id in blueIds)
            {
                if (!IsValidRideId(id) || !seen.Add((id.id, id.version)))
                    continue;
                // Include dead units: episode reset revives before teleport; skipping dead dropped squads from the cache.
                _friendlySpawnWorld.Add((id, ts.GetPosition(id)));
            }
        }

        _enemySpawnWorld = new List<(RideID, RideVector3)>();
        if (redIds != null)
        {
            var seen = new HashSet<(int, int)>();
            foreach (RideID id in redIds)
            {
                if (!IsValidRideId(id) || !seen.Add((id.id, id.version)))
                    continue;
                _enemySpawnWorld.Add((id, ts.GetPosition(id)));
            }
        }
    }

    void TeleportTeamToSpawnWorld(IEnumerable<(RideID id, RideVector3 world)> spawns)
    {
        if (spawns == null)
            return;
        IMovementSystem movementSystem = api.movementSystem;
        IAgentSystem agentSystem = Globals.api.agentSystem;
        foreach (var pair in spawns)
        {
            if (!IsValidRideId(pair.id))
                continue;
            if (agentSystem.IsAgentDead(pair.id))
                agentSystem.ReviveAgent(pair.id);
            movementSystem.MoveTeleport(pair.id, pair.world);
        }
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

        // Reset full squads to RL-init sim poses (leader-only + 50 m wedge filter misses distant friendlies;
        // m_enemy is only one Red — other Reds never moved with the old path).
        if (_friendlySpawnWorld != null && _friendlySpawnWorld.Count > 0)
            TeleportTeamToSpawnWorld(_friendlySpawnWorld);
        else
            TeleportFriendlySquadPreservingFormation(m_startingPosition);

        if (_enemySpawnWorld != null && _enemySpawnWorld.Count > 0)
            TeleportTeamToSpawnWorld(_enemySpawnWorld);
        else if (HasAssignedEnemy(m_enemy))
        {
            if (agentSystem.IsAgentDead(m_enemy))
                agentSystem.ReviveAgent(m_enemy);
            movementSystem.MoveTeleport(m_enemy, m_enemyStartingPosition);
        }

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
            Debug.Log($"Episode reset and agent dead at step: {stepCount}");
            agentSystem.ReviveAgent(agent);
        }

        // Enemy squad already reset above when _enemySpawnWorld is set; single-enemy fallback only if no snapshot.
        if ((_enemySpawnWorld == null || _enemySpawnWorld.Count == 0) && !HasAssignedEnemy(m_enemy))
            Debug.LogWarning("AGAMOTTO_ATD_Agent: m_enemy is not assigned; skipping enemy reset.");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        base.CollectObservations(sensor);

        Vector3 agentPos = GetAgentWorldPosition();
        // agent x and z (RIDE sim position so policy matches teleport motion state)
        sensor.AddObservation(agentPos.x);
        sensor.AddObservation(agentPos.z);

        RideVector3 enemyPos;
        if (HasAssignedEnemy(m_enemy))
            enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        else
            enemyPos = new RideVector3(agentPos.x, agentPos.y, agentPos.z);

        // enemy x and z
        sensor.AddObservation(enemyPos.x);
        sensor.AddObservation(enemyPos.z);

        // Unit direction from agent toward enemy (in world XZ plane for stability on terrain).
        Vector3 enemyWorld = new Vector3(enemyPos.x, enemyPos.y, enemyPos.z);
        Vector3 toEnemy = enemyWorld - agentPos;
        toEnemy.y = 0f;
        if (toEnemy.sqrMagnitude < MinMoveDirectionSqr)
            toEnemy = Vector3.forward;
        Vector3 direction = toEnemy.normalized;
        sensor.AddObservation(direction.x);
        sensor.AddObservation(direction.y);
        sensor.AddObservation(direction.z);
        //sensor.AddObservation(m_goal.transform.position.z);

        //where agent is facing
        //sensor.AddObservation(transform.forward);

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

        if (HasAssignedEnemy(m_enemy) && agentSystem.IsAgentDead(m_enemy))
        {
            AddReward(m_successReward);
            DispatchTrainingResult(true);
            suppressStepPunishmentThisAction = true;
            EndEpisode();
            return;
        }

        if (agentSystem.IsAgentDead(agent))
        {
            AddReward(-1 * m_successReward);
            DispatchTrainingResult(false);
            suppressStepPunishmentThisAction = true;
            EndEpisode();
            return;
        }

        float xmov = actions.ContinuousActions[0];
        float zmov = actions.ContinuousActions[1];

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
        if (Input.GetKey(KeyCode.D))
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = 3;  //actionsOut[0] = 3;
        }
        if (Input.GetKey(KeyCode.W))
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = 1;  //actionsOut[0] = 1;
        }
        if (Input.GetKey(KeyCode.A))
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = 4;  //actionsOut[0] = 4;
        }
        if (Input.GetKey(KeyCode.S))
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = 2;  //actionsOut[0] = 2;
        }
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
