using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ride;
using UnityEngine;

/// <summary>
/// Coordinates hybrid AWAC: leader route as expert example, then online RL.
/// Routes are keyed by tactical leader callsign and resolved to entity uids via <see cref="RegisterEntity"/>.
/// </summary>
public static class AGAMOTTO_HybridAwacHub
{
    public enum AwacPhase
    {
        None,
        ExpertBaseline,
        OnlineAwac,
    }

    sealed class ExpertRoute
    {
        public string callsign;
        public string entityUid;
        public RideID tacticalGroup;
        public Vector3[] waypointsWorld;
        public bool pathIssued;
    }

    static readonly Dictionary<string, ExpertRoute> RoutesByCallsign = new Dictionary<string, ExpertRoute>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> CallsignToEntityUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, RideID> UidToTacticalGroup = new Dictionary<string, RideID>(StringComparer.OrdinalIgnoreCase);

    static AwacPhase _phase = AwacPhase.None;
    static string _activeLeaderCallsign;
    static bool _hybridDispatchRequested;
    static bool _sessionExpertBaselineComplete;

    public static AwacPhase CurrentPhase => _phase;
    public static bool IsHybridDispatch => _hybridDispatchRequested;
    public static bool SessionExpertBaselineComplete => _sessionExpertBaselineComplete;
    public static string ActiveLeaderCallsign => _activeLeaderCallsign;

    public static void ResetForNewScenario()
    {
        RoutesByCallsign.Clear();
        CallsignToEntityUid.Clear();
        UidToTacticalGroup.Clear();
        _phase = AwacPhase.None;
        _activeLeaderCallsign = null;
        _hybridDispatchRequested = false;
        _sessionExpertBaselineComplete = false;
        AGAMOTTO_ExpertTrajectoryRecorder.ResetSession();
    }

    public static void EnableHybridDispatch()
    {
        _hybridDispatchRequested = true;
        _phase = AwacPhase.ExpertBaseline;
    }

    public static void RegisterEntity(string uid, string callsign, RideID tacticalGroup)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return;
        UidToTacticalGroup[uid] = tacticalGroup;
        if (!string.IsNullOrWhiteSpace(callsign))
            CallsignToEntityUid[callsign] = uid;
    }

    public static bool TryResolveEntityKey(string callsignOrUid, out string entityUid)
    {
        entityUid = null;
        if (string.IsNullOrWhiteSpace(callsignOrUid))
            return false;
        if (UidToTacticalGroup.ContainsKey(callsignOrUid))
        {
            entityUid = callsignOrUid;
            return true;
        }
        if (CallsignToEntityUid.TryGetValue(callsignOrUid, out entityUid))
            return true;
        return false;
    }

    public static void RegisterExpertRoute(string callsign, Vector3[] waypointsWorld, string linkedEntityUid = null)
    {
        if (waypointsWorld == null || waypointsWorld.Length < 2)
        {
            Debug.LogWarning($"AGAMOTTO_HybridAwacHub: route for '{callsign}' needs at least 2 waypoints.");
            return;
        }

        string entityUid = linkedEntityUid;
        if (string.IsNullOrWhiteSpace(entityUid))
            TryResolveEntityKey(callsign, out entityUid);

        RideID tacticalGroup = default;
        if (!string.IsNullOrWhiteSpace(entityUid))
            UidToTacticalGroup.TryGetValue(entityUid, out tacticalGroup);

        var route = new ExpertRoute
        {
            callsign = callsign,
            entityUid = entityUid,
            tacticalGroup = tacticalGroup,
            waypointsWorld = waypointsWorld,
        };
        RoutesByCallsign[callsign] = route;
        _activeLeaderCallsign = callsign;
        Debug.Log(
            $"AGAMOTTO_HybridAwacHub: registered expert route callsign={callsign} "
            + $"entityUid={entityUid ?? "unresolved"} waypoints={waypointsWorld.Length}");
    }

    public static bool TryGetActiveExpertWaypoints(out Vector3[] waypoints)
    {
        waypoints = null;
        if (string.IsNullOrWhiteSpace(_activeLeaderCallsign))
            return false;
        if (!RoutesByCallsign.TryGetValue(_activeLeaderCallsign, out var route))
            return false;
        waypoints = route.waypointsWorld;
        return waypoints != null && waypoints.Length > 0;
    }

    public static bool TryGetActiveTacticalGroup(out RideID tacticalGroup)
    {
        tacticalGroup = default;
        if (string.IsNullOrWhiteSpace(_activeLeaderCallsign))
            return false;
        if (!RoutesByCallsign.TryGetValue(_activeLeaderCallsign, out var route))
            return false;
        tacticalGroup = route.tacticalGroup;
        return AGAMOTTO_ATD_Agent.IsValidRideId(tacticalGroup);
    }

    public static void MarkExpertPathIssued(string callsign)
    {
        if (RoutesByCallsign.TryGetValue(callsign, out var route))
            route.pathIssued = true;
    }

    public static void RestoreExpertBaselinePhaseIfNeeded()
    {
        if (_sessionExpertBaselineComplete)
            return;
        _phase = AwacPhase.ExpertBaseline;
    }

    public static void BeginOnlineAwacPhase()
    {
        if (_phase == AwacPhase.OnlineAwac)
            return;
        _phase = AwacPhase.OnlineAwac;
        _sessionExpertBaselineComplete = true;
        AGAMOTTO_ExpertTrajectoryRecorder.FlushAndWriteManifest();
        Debug.Log("AGAMOTTO_HybridAwacHub: expert baseline complete — online AWAC phase.");
    }

    public static string ExpertBufferDirectory =>
        Path.Combine(Application.streamingAssetsPath, "awac");

    public static string DefaultExpertBufferPath =>
        Path.Combine(ExpertBufferDirectory, "expert_buffer.jsonl");
}
