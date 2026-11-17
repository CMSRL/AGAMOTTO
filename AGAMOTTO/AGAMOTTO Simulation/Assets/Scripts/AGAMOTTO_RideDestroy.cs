using Ride;
using Ride.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>RIDE entity removal for episode respawn (squads include nested fireteams).</summary>
public static class AGAMOTTO_RideDestroy
{
    public static void DetachBehaviour(RideID agentId)
    {
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(agentId))
            return;
        try
        {
            Globals.api.agentSystem.SetAgentBehaviour(agentId, null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_RideDestroy: SetAgentBehaviour(null) failed id={agentId.id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a tactical group created by <see cref="IUnitCreatorSystem.CreateSquad"/> /
    /// <see cref="IUnitCreatorSystem.CreateFireTeam"/> (recursive subgroups + all soldiers).
    /// </summary>
    public static void TryDestroyTacticalGroup(RideID tacticalGroup)
    {
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup))
            return;

        IGroupSystem groupSystem = Globals.api.groupSystem;
        IAgentSystem agentSystem = Globals.api.agentSystem;
        if (groupSystem == null || agentSystem == null)
            return;

        try
        {
            if (groupSystem.DoesGroupExist(tacticalGroup))
            {
                RideID leader = groupSystem.GetGroupLeader(tacticalGroup);
                if (AGAMOTTO_ATD_Agent.IsValidRideId(leader))
                    DetachBehaviour(leader);

                bool removed = groupSystem.RemoveGroup(tacticalGroup, true, true);
                if (removed)
                    return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"AGAMOTTO_RideDestroy: RemoveGroup failed id={tacticalGroup.id}: {ex.Message}");
        }

        foreach (RideID member in CollectGroupMembers(tacticalGroup))
        {
            DetachBehaviour(member);
            TryRemoveAgent(member);
        }
    }

    public static void DetachMlAgentsFromAllCombatTeams()
    {
        DetachBehavioursOnTeam(Team.Blue);
        DetachBehavioursOnTeam(Team.Red);
    }

    public static void StripMlComponentsOnTacticalGroup(RideID tacticalGroup)
    {
        foreach (RideID member in CollectGroupMembers(tacticalGroup))
            StripMlComponentsOnRideEntity(member);
    }

    public static void SuppressMlAgentsOnTacticalGroup(RideID tacticalGroup) =>
        StripMlComponentsOnTacticalGroup(tacticalGroup);

    public static void StripMlComponentsOnRideEntity(RideID rideId)
    {
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(rideId))
            return;

        try
        {
            var manip = Globals.api?.agentSystem?.GetComponentManipulator(rideId) as Component;
            if (manip == null)
                return;
            StripMlComponentsOnTransform(manip.transform);
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"AGAMOTTO_RideDestroy: StripMlComponentsOnRideEntity failed id={rideId.id}: {ex.Message}");
        }
    }

    public static void SuppressMlAgentsOnRideEntity(RideID rideId) =>
        StripMlComponentsOnRideEntity(rideId);

    static void StripMlComponentsOnTransform(Transform root)
    {
        if (root == null || IsProtectedMlAgentRoot(root))
            return;

        foreach (var dr in root.GetComponentsInChildren<Unity.MLAgents.DecisionRequester>(true))
        {
            if (dr == null || IsProtectedMlAgentRoot(dr.transform))
                continue;
            // Never strip the live training agent's own DecisionRequester — without it the
            // agent stops requesting decisions (no movement, MaxStep-only episode churn).
            if (dr.GetComponent<AGAMOTTO_ATD_Agent>() != null)
                continue;
            UnityEngine.Object.Destroy(dr);
        }

        foreach (BehaviorParameters bp in root.GetComponentsInChildren<BehaviorParameters>(true))
        {
            if (bp == null || IsProtectedMlAgentRoot(bp.transform))
                continue;
            if (bp.GetComponent<AGAMOTTO_ATD_Agent>() != null)
                continue;
            UnityEngine.Object.Destroy(bp);
        }

        foreach (Agent agent in root.GetComponentsInChildren<Agent>(true))
        {
            if (agent == null || IsProtectedMlAgentRoot(agent.transform))
                continue;
            if (agent is AGAMOTTO_ATD_Agent)
                continue;
            agent.enabled = false;
        }
    }

    /// <summary>
    /// Removes Unity ML-Agents components from infantry prefab instances (Evaluation spawn).
    /// RIDE combat behaviours are left intact (unlike <see cref="DetachBehaviour"/>).
    /// </summary>
    public static void StripEmbeddedMlAgentsInScene()
    {
        var agents = UnityEngine.Object.FindObjectsOfType<Agent>(true);
        foreach (Agent agent in agents)
        {
            if (agent == null || IsProtectedMlAgentRoot(agent.transform))
                continue;
            StripMlComponentsOnTransform(agent.transform);
        }
    }

    public static void SuppressEmbeddedMlAgentsInScene() => StripEmbeddedMlAgentsInScene();

    static bool IsProtectedMlAgentRoot(Transform transform) => false;

    static void DetachBehavioursOnTeam(Team team)
    {
        IEnumerable<RideID> roster = Globals.api.scenarioSystem.GetAgents(team);
        if (roster == null)
            return;

        foreach (RideID id in roster.ToList())
            DetachBehaviour(id);
    }

    /// <summary>Removes every Blue/Red agent still registered with the scenario.</summary>
    public static void SweepAllCombatTeams()
    {
        SweepTeamAgents(Team.Blue);
        SweepTeamAgents(Team.Red);
    }

    /// <summary>
    /// Destroys registered CoT squads, then sweeps any leftover Blue/Red agents (orphans from failed prior respawns).
    /// </summary>
    public static void DestroyAllCombatants(IEnumerable<RideID> registeredTacticalGroups)
    {
        var groups = new HashSet<int>();
        if (registeredTacticalGroups != null)
        {
            foreach (RideID groupId in registeredTacticalGroups)
            {
                if (!AGAMOTTO_ATD_Agent.IsValidRideId(groupId))
                    continue;
                if (groups.Add(groupId.id))
                    TryDestroyTacticalGroup(groupId);
            }
        }

        SweepTeamAgents(Team.Blue);
        SweepTeamAgents(Team.Red);
        SweepTeamAgents(Team.Blue);
        SweepTeamAgents(Team.Red);
    }

    static void SweepTeamAgents(Team team)
    {
        IAgentSystem agentSystem = Globals.api.agentSystem;
        if (agentSystem == null)
            return;

        IEnumerable<RideID> roster = Globals.api.scenarioSystem.GetAgents(team);
        if (roster == null)
            return;

        foreach (RideID id in roster.ToList())
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id))
                continue;
            DetachBehaviour(id);
            TryRemoveAgent(id);
        }
    }

    public static List<RideID> CollectGroupMembers(RideID tacticalGroup)
    {
        var members = new List<RideID>();
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup))
            return members;

        IGroupSystem gs = Globals.api.groupSystem;
        if (gs == null)
            return members;

        var seen = new HashSet<int>();
        void TryAdd(RideID id)
        {
            if (!AGAMOTTO_ATD_Agent.IsValidRideId(id) || !seen.Add(id.id))
                return;
            members.Add(id);
        }

        try
        {
            foreach (RideID id in gs.GetMembers(tacticalGroup, false, true))
                TryAdd(id);
        }
        catch
        {
            /* optional API */
        }

        TryAdd(gs.GetGroupLeader(tacticalGroup));
        return members;
    }

    static void TryRemoveAgent(RideID rideId)
    {
        if (!AGAMOTTO_ATD_Agent.IsValidRideId(rideId))
            return;

        try
        {
            Globals.api.agentSystem.RemoveAgent(rideId);
            return;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_RideDestroy: RemoveAgent failed id={rideId.id}: {ex.Message}");
        }

        try
        {
            Globals.api.scenarioSystem.RemoveAgent(rideId);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_RideDestroy: scenario RemoveAgent failed id={rideId.id}: {ex.Message}");
        }
    }
}
