using Ride;
using Ride.AI;
using Ride.AI.RL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.MLAgents.Policies;
using UnityEngine;

public class AGAMOTTO_RL_Behavior : MLBehaviour
{
    /// <summary>
    /// Max distance from the learning agent when querying candidate enemies (world units).
    /// </summary>
    public float maxEnemyDistance = 1000f;

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

    public AGAMOTTO_RL_Behavior(UnityEngine.GameObject behaviourSettings, float maxEnemyDistance, RideID friendlyTacticalGroup = default)
        : base(behaviourSettings)
    {
        this.maxEnemyDistance = maxEnemyDistance;
        _friendlyTacticalGroup = friendlyTacticalGroup;
    }

    async protected override void InitMLBehaviour()
    {
        try
        {
            base.InitMLBehaviour();
            AGAMOTTO_ATD_Agent mlagent = GetRLAgent<AGAMOTTO_ATD_Agent>();
            // Ensure RIDE id + Unity transform track the behaviour entity (squad leader), not the mission prefab root.
            mlagent.Init(api, entity);
            mlagent.gameObject.GetComponent<BehaviorParameters>().BehaviorName = "AGAMOTTO_RL";
            mlagent.m_squadTacticalGroup = _friendlyTacticalGroup;

            bool enemiesReady = await WaitUntilEnemySpawn(mlagent);
            if (!enemiesReady)
            {
                Debug.LogError("AGAMOTTO_RL_Behavior: Timed out waiting for an enemy. Agent enemy target was not set.");
                return;
            }

            // Snapshot start pose from sim (leader entity), not the mission prefab transform.
            mlagent.m_startingPosition = api.GetSystem<ITransformSystem>().GetPosition(entity);

            Team team = api.agentSystem.GetAgentTeam(entity) == Team.Blue ? Team.Red : Team.Blue;
            IEnumerable<RideID> enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
            if (!TryAssignClosestEnemy(mlagent, enemies))
                Debug.LogError("AGAMOTTO_RL_Behavior: No enemies found after wait; cannot assign m_enemy.");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
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

    /// <summary>
    /// Waits until <see cref="IScenarioSystem.GetAgents"/> returns at least one enemy, or until timeout.
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
