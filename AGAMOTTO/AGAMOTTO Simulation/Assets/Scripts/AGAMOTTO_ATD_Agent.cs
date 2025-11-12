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
    public RideVector3 m_enemyStartingPosition;
    private Vector3 previousPosition;
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

        // direction to enemy
        Vector3 direction = (agentTransform.position - enemyPos).normalized;
        sensor.AddObservation(direction.x);
        sensor.AddObservation(direction.y);
        sensor.AddObservation(direction.z);
        //sensor.AddObservation(m_goal.transform.position.z);

        //where agent is facing
        sensor.AddObservation(transform.forward);

    }
    private void OnCollisionEnter(Collision collision)
    {

        AddReward(-1.0f); // Penalty for hitting obstacle
        Debug.Log("Obstacle!");
        DispatchTrainingResult(false);
        //EndEpisode();

    }
    protected override void TakeAction(ActionBuffers actions)
    {
        IAgentSystem agentSystem = Globals.api.agentSystem;
        IMovementSystem movementSystem = Globals.api.movementSystem;

        var dirToGo = Vector3.zero;
        var rotateDir = RideVector3.zero;

        float dirToGoDegrees = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);

        float angleRadians = dirToGoDegrees * Mathf.Deg2Rad;

        Vector3 direction = new Vector3(Mathf.Cos(angleRadians), 0, Mathf.Sin(angleRadians));

        Vector3 newPos = agentTransform.position + (direction.normalized * api.movementSystem.GetMoverSpeed(agent));

        if (api.movementSystem.IsPathValid(agentTransform.position, new RideVector3(newPos)))
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
            //AddReward(-1);
            DispatchTrainingResult(false);
            //EndEpisode();
        }
        RideVector3 enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        float enemyDist = Vector3.Distance(agentTransform.position, enemyPos);
        //float originalEnemyDistance = Vector3.Distance(m_enemyStartingPosition, enemyPos);

        float previousDistance = Vector3.Distance(previousPosition, enemyPos);
        float currentDistance = Vector3.Distance(transform.position, enemyPos);

        //AddReward(enemyDist - originalEnemyDistance + (-1f / MaxStep));


        float progressReward = previousDistance - enemyDist;

        AddReward(progressReward * 1f); // Scale as needed

        // 2. Small reward for moving (to discourage standing still)
        float movement = Vector3.Distance(transform.position, previousPosition);
        AddReward(movement * 1f);

        // 3. Penalty for moving away from the enemy
        if (progressReward < 0)
        {
            AddReward(progressReward * 0.5f); // Negative reward
        }

        previousPosition = transform.position;
        //AddReward(-1f / MaxStep);
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
