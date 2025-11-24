using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json.Utilities;
using Ride;
using Ride.AI;
using Ride.AI.RL;
using Ride.Examples;
using Ride.WorldState;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.MLAgents.Policies;
using UnityEngine;
using static VHWayPointNavigator;


public class AGAMOTTO_RL_Behavior: MLBehaviour
{
    /// <summary>
    /// The max distance of an enemy from the agent before it is disregarded
    /// </summary>
    public float maxEnemyDistance = RideUtils.Infinity;

    public AGAMOTTO_RL_Behavior(UnityEngine.GameObject behaviourSettings, float maxEnemyDistance) : base(behaviourSettings)
    {
        this.maxEnemyDistance = maxEnemyDistance;
    }

    async protected override void InitMLBehaviour()
    {
        base.InitMLBehaviour();
        AGAMOTTO_ATD_Agent mlagent = GetRLAgent<AGAMOTTO_ATD_Agent>();
        mlagent.gameObject.GetComponent<BehaviorParameters>().BehaviorName = "AGAMOTTO_RL";
        mlagent.m_startingPosition = mlagent.gameObject.transform.position;
        await WaitUntilEnemySpawn(mlagent);
        Team team = api.agentSystem.GetAgentTeam(entity) == Team.Blue ? Team.Red : Team.Blue;
        IEnumerable<RideID> enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
        foreach (RideID id in enemies)
        {
            mlagent.m_enemy = id;
            mlagent.m_enemyStartingPosition = api.GetSystem<ITransformSystem>().GetPosition(id);
        }

    }



    public async Task WaitUntilEnemySpawn(AGAMOTTO_ATD_Agent mlagent)
    {
        
        
        Team team = api.agentSystem.GetAgentTeam(entity) == Team.Blue ? Team.Red : Team.Blue;
        IEnumerable<RideID> enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
        Debug.Log("Waiting for condition...");

        while (enemies.Count() == 0)
        {
            // Do something or just wait
            await Task.Delay(100); // non-blocking wait
            enemies = api.scenarioSystem.GetAgents(mlagent.agentTransform.position, maxEnemyDistance, team);
        }

        Debug.Log("Condition met!");
    }



}


