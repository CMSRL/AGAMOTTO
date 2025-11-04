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

#if RIDE_ML_AGENTS
public class AGAMOTTO_ATD_Agent : RLAgent
{

    public RideID m_enemy;
    public RideVector3 m_startingPosition;

    public override void OnEpisodeBegin()
    {
        
        base.OnEpisodeBegin();
        api.movementSystem.MoveTeleport(agent, m_startingPosition);
        if (api != null)
        {
            api.agentSystem.SetAgentState(agent, StateMachineUtils.MoveState);
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        base.CollectObservations(sensor);

        // agent x and z
        sensor.AddObservation(agentTransform.position.x);
        sensor.AddObservation(agentTransform.position.z);

        // enemy x and z
        RideVector3 enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        sensor.AddObservation(enemyPos.x);
        sensor.AddObservation(enemyPos.z);

        // cover x and z
        //sensor.AddObservation(m_goal.transform.position.x);
        //sensor.AddObservation(m_goal.transform.position.z);
    }

    protected override void TakeAction(ActionBuffers actions)
    {
        IAgentSystem agentSystem = Globals.api.agentSystem;
        IMovementSystem movementSystem = Globals.api.movementSystem;

        var dirToGo = Vector3.zero;
        var rotateDir = RideVector3.zero;
        var action = actions.DiscreteActions[0];

        switch (action)
        {
            case 0:
                dirToGo = agentTransform.forward * 1f;
                break;
            case 1:
                dirToGo = agentTransform.forward * -1f;
                break;
            case 2:
                dirToGo = agentTransform.right * -0.75f;
                break;
            case 3:
                dirToGo = agentTransform.right * 0.75f;
                break;
            case 4:
                break;
            case 5:
                break;
            case 6:
                break;
        }

        Vector3 newPos = agentTransform.position + (dirToGo.normalized * api.movementSystem.GetMoverSpeed(agent));

        if (api.movementSystem.IsPathValid(agentTransform.position, newPos))
        {
            api.movementSystem.MoveToPosition(agent, new RideVector3(newPos));
            //api.movementSystem.MoveToPositionInFormation(agent, new RideVector3(newPos), FormationProcedureType.Wedge);
        }

        agentSystem.SetAgentState(agent, StateMachineUtils.MoveState);

        if (agentSystem.IsAgentDead(m_enemy))
        {
            AddReward(m_successReward);
            DispatchTrainingResult(true);
            EndEpisode();
        }

        else if (agentSystem.IsAgentDead(agent))
        {
            AddReward(-1 * m_successReward);
            DispatchTrainingResult(false);
            EndEpisode();
        }

        else if (stepCount >= MaxStep)
        {
            AddReward(-1 * m_successReward);
            DispatchTrainingResult(false);
            EndEpisode();
        }
        RideVector3 enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        float enemyDist = (agentTransform.position - enemyPos).magnitude;
        AddReward(1f / enemyDist);
        AddReward(-1f / MaxStep);
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
