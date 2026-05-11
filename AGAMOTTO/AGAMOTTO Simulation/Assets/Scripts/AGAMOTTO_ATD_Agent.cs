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
    public RideVector3 m_startingPosition;
    public RideVector3 m_enemyStartingPosition;
    private Vector3 previousPosition;
    private int episodeCounter = 0;
    private const float MinMoveDirectionSqr = 1e-8f;

    public override void OnEpisodeBegin()
    {
        base.OnEpisodeBegin();

        episodeCounter++;
        Debug.Log($"Episode {episodeCounter}...");
        IAgentSystem agentSystem = Globals.api.agentSystem;
        IMovementSystem movementSystem = Globals.api.movementSystem;

        Debug.Log($"Episode reset, moving agent from {agentTransform.position} to start; enemy to {m_enemyStartingPosition}");
        transform.localPosition = m_startingPosition;

        // Prevent spurious first-step distance-delta reward based on an uninitialized previousPosition.
        previousPosition = agentTransform.position;

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

        // Reset enemy so a new episode does not begin with the enemy already dead (instant terminal success).
        if (agentSystem.IsAgentDead(m_enemy))
            agentSystem.ReviveAgent(m_enemy);
        movementSystem.MoveTeleport(m_enemy, m_enemyStartingPosition);
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

        // Unit direction from agent toward enemy (in world XZ plane for stability on terrain).
        Vector3 agentPos = agentTransform.position;
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

        float xmov = actions.ContinuousActions[0];
        float zmov = actions.ContinuousActions[1];

        Vector3 rawMove = new Vector3(xmov, 0f, zmov);
        if (rawMove.sqrMagnitude >= MinMoveDirectionSqr)
        {
            Vector3 direction = rawMove.normalized;

            var maxSpeed = Globals.api.agentSystem.GetAgentMaxSpeed(agent);
            float deltaTime = Time.deltaTime;

            Vector3 newPos = agentTransform.position + direction * maxSpeed * deltaTime;
            agentTransform.rotation = Quaternion.LookRotation(direction);

            if (api.movementSystem.IsPathValid(agentTransform.position, new RideVector3(newPos)))
            {
                api.movementSystem.MoveTeleport(agent, new RideVector3(newPos));
                //var agentTacticalGroup = Globals.api.groupSystem.GetGroupMemberships(agent);
                //api.movementSystem.MoveToPosition(agentTacticalGroup, new RideVector3(newPos), FormationProcedureType.Wedge);
                //api.movementSystem.MoveToPosition(agent, new RideVector3(newPos), 5f);
                //api.movementSystem.MoveToPositionInFormation(agent, new RideVector3(newPos), FormationProcedureType.Wedge);
            }
        }

        RideVector3 enemyPos = api.GetSystem<ITransformSystem>().GetPosition(m_enemy);
        float enemyDist = Vector3.Distance(agentTransform.position, enemyPos);

        float previousDistance = Vector3.Distance(previousPosition, enemyPos);


        // Distance shaping should be small enough that terminal reward is still meaningful.
        if (enemyDist < previousDistance)
            AddReward(+0.002f);
        else
            AddReward(-0.0008f);

        if(stepCount > MaxStep)
        {
            DispatchTrainingResult(false);
            Debug.Log($"Episode reset at step: {stepCount}");
            EndEpisode();
        }

        previousPosition = agentTransform.position;

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
