using UnityEngine;
using Ride.Entities;
using Ride.WorldState;
using System.Collections.Generic;

#if RIDE_ML_AGENTS
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
#endif

namespace Ride.AI.RL
{
#if RIDE_ML_AGENTS

    /// <summary>
    /// Represents a machine learning agent
    /// </summary>
    public abstract class RLAgent : Agent, IRLAgent
    {
        Dictionary<RideID, ActionBuffers> actionBufferDic = new Dictionary<RideID, ActionBuffers>();
        Dictionary<RideID, VectorSensor> vectorSensorDic = new Dictionary<RideID, VectorSensor>();

        /// <summary>
        /// When true, <see cref="Step"/> is not run after <see cref="TakeAction"/> for this decision step
        /// (e.g. terminal reward + <c>EndEpisode()</c> should not also apply per-step punishment).
        /// </summary>
        protected bool suppressStepPunishmentThisAction;


        /// <summary>
        /// The reward given to the agent upon completing the task
        /// </summary>
        public float m_successReward = 5;

        /// <summary>
        /// Penalty given each step to encourage agent to finish task quickly.
        /// </summary>
        protected virtual float stepPunishment { get { return -1f / MaxStep; } }

        /// <summary>
        /// Returns the current step counter within the current episode
        /// </summary>
        public int stepCount { get { return StepCount; } }

        protected RideID agent;//{ get { return GetComponent<IAgent>().agentData.id; } }
        protected IApi api { get; private set; }

        public Transform agentTransform { get; private set; }

        protected AgentMono agentMono { get { return api.agentSystem.GetComponentManipulator(agent).GetComponent<AgentMono>(); } }

        public string behaviourName { get { return GetComponent<BehaviorParameters>().BehaviorName; } }

        /// <summary>
        /// Set the agent that will use this behaviour
        /// </summary>
        /// <param name="agent"></param>
        public void Init(IApi api, RideID agent)
        {
            this.api = api;
            SetupAgent(agent);
        }

        void SetupAgent(RideID agent)
        {
            this.agent = agent;
            agentTransform = api.agentSystem.GetComponentManipulator(agent).GetComponent<Transform>();
        }


        protected virtual void Start()
        {
            api = Globals.api;

            // Mission ML prefabs often sit on a settings GameObject: SetAgentBehaviour + Init(IApi, RideID) bind the
            // real squad leader. Do not stomp that binding with an IAgent on the prefab, or with an unassigned id.
            IAgent iAgent = GetComponent<IAgent>();
            if (iAgent != null)
            {
                RideID fromMono = iAgent.agentData.id;
                bool monoHasRideId = fromMono.id != 0 || fromMono.version != 0;
                bool alreadyBound = this.agent.id != 0 || this.agent.version != 0;
                if (monoHasRideId && !alreadyBound)
                    SetupAgent(fromMono);
            }

            api.worldStateSystem.AddListener<AgentReachedGoalEvent>(WorldEvent.agentReachedGoal, OnAgentReachedGoal);
        }

        void OnAgentReachedGoal(WorldEventMarker marker, AgentReachedGoalEvent e)
        {
            //RideLogSystem.LogFormat("{0}  ----  {1}", e.agent, GetComponentInParent<AgentMono>().agentData.id);
            if (e.agent == agent)
            {
                Debug.LogFormat("Agent {0} reached goal", e.agent);
                OnAgentReachedGoal();
            }
        }

        /// <summary>
        /// called when this agent has reached its goal
        /// </summary>
        protected virtual void OnAgentReachedGoal() { }

        protected virtual void Update() { }

        public virtual void Step()
        {
            AddReward(stepPunishment);
        }

        public void DispatchTrainingResult(bool successful)
        {
            Globals.api.worldStateSystem.DispatchEvent<AgentTrainingEpisodeResultEvent>(WorldEvent.agentTrainingEpisodeResult,
                new AgentTrainingEpisodeResultEvent(agent, successful));
            //SendMessageUpwards("DisplayTrainingSuccess", successful, SendMessageOptions.DontRequireReceiver);
        }

        /// <summary>
        /// Specifies the agent behavior at every step in the training process
        /// based on the provided action that the agent will take
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions)
        {
            suppressStepPunishmentThisAction = false;

            //base.OnActionReceived(actions);

            if (!actionBufferDic.ContainsValue(actions))
            {
                RideID actionBufferID = new RideID();
                actionBufferDic.Add(actionBufferID, actions);
                OnActionReceived(actionBufferID);
                TakeAction(actionBufferID);
            }
            else
            {
                foreach (KeyValuePair<RideID, ActionBuffers> kvp in actionBufferDic)
                {
                    if (kvp.Value.Equals(actions))
                    {
                        OnActionReceived(kvp.Key);
                        TakeAction(kvp.Key);
                    }
                }
            }
            //TakeAction(actions); // Former

            if (!suppressStepPunishmentThisAction)
                Step();
        }

        /// <summary>
        /// In the editor, if "Reset On Done" is checked then AgentReset() will be
        /// called automatically anytime we mark done = true in an agent script.
        /// </summary>
        public override void OnEpisodeBegin()
        {
            base.OnEpisodeBegin();

            Globals.api.worldStateSystem.DispatchEvent<AgentTrainingEpisodeBeginEvent>(WorldEvent.agentTrainingEpisodeBegin,
                new AgentTrainingEpisodeBeginEvent(agent));
        }


        /// <summary>
        /// Collect the vector observations of the agent to be used in the learning environment for training the agent
        /// </summary>
        /// <param name="sensor"></param>
        public override void CollectObservations(VectorSensor sensor)
        {
            if (!vectorSensorDic.ContainsValue(sensor))
            {
                RideID sensorID = new RideID(sensor.GetHashCode());
                vectorSensorDic.Add(sensorID, sensor);
                CollectObservations(sensorID);
            }
            else
            {
                foreach (KeyValuePair<RideID, VectorSensor> kvp in vectorSensorDic)
                {
                    if (kvp.Value.Equals(sensor))
                    {
                        CollectObservations(kvp.Key);
                    }
                }
            }
            //base.CollectObservations(sensor);
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            if (!actionBufferDic.ContainsValue(actionsOut))
            {
                RideID actionsOutID = new RideID(actionsOut.GetHashCode());
                actionBufferDic.Add(actionsOutID, actionsOut);
                Heuristic(actionsOutID);
            }
            else
            {
                foreach (KeyValuePair<RideID, ActionBuffers> kvp in actionBufferDic)
                {
                    if (kvp.Value.Equals(actionsOut))
                    {
                        Heuristic(kvp.Key);
                    }
                }
            }
            //base.Heuristic(actionsOut);
        }

        /// <summary>
        /// Call when an action is taken by the agent. Each behaviour needs to handle actions in its own way based on the use case
        /// </summary>
        /// <param name="vectorAction"></param>
        abstract protected void TakeAction(ActionBuffers actionBuffers);

        public void OnActionReceived(RideID actions)
        {
            base.OnActionReceived(actionBufferDic[actions]);
        }

        public void CollectObservations(RideID sensor)
        {
            base.CollectObservations(vectorSensorDic[sensor]);
        }

        public void Heuristic(RideID actionsOut)
        {
            base.Heuristic(actionBufferDic[actionsOut]);
        }

        public void TakeAction(RideID actionBuffers)
        {
            TakeAction(actionBufferDic[actionBuffers]);
        }
    }

#else
/// <summary>
/// Represents a machine learning agent
/// </summary>
abstract public class RLAgent : MonoBehaviour
    {
        public float m_successReward = 5;
        //public float m_stepPunishment;

        /// <summary>
        /// Penalty given each step to encourage agent to finish task quickly.
        /// </summary>
        protected virtual float stepPunishment { get { return -1f / 5000; } }

        public int stepCount { get { return 0; } }

        //protected virtual void Start() { base.sta}

        //protected virtual void Update() { }
        public Transform agentTransform { get; private set; }
        public void AddReward(float reward) { }

        public virtual void AchievedSuccess() { }

        public virtual void Step() { }

        /// </summary>
        /// <summary>
        /// Specifies the agent behavior at every step in the training process
        /// based on the provided action that the agent will take
        /// </summary>
        public virtual void OnActionReceived(float[] vectorAction)
        {
            TakeAction(vectorAction);

            Step();
        }

        /// <summary>
        /// In the editor, if "Reset On Done" is checked then AgentReset() will be
        /// called automatically anytime we mark done = true in an agent script.
        /// </summary>
        public virtual void OnEpisodeBegin() { }

        public virtual void EndEpisode() { }

        /// <summary>
        /// Collect the vector observations of the agent to be used in the learning environment for training the agent
        /// </summary>
        /// <param name="sensor"></param>
        public virtual void CollectObservations() {  }

        /// <summary>
        /// Call when an action is taken by the agent. Each behaviour needs to handle actions in its own way based on the use case
        /// </summary>
        /// <param name="vectorAction"></param>
        abstract protected void TakeAction(float[] vectorAction);
    }
#endif
}
