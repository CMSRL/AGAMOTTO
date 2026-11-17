using Ride;
using System.Collections.Generic;
using AGAMOTTO;
using UnityEngine;

/// <summary>Records original CoT spawn specs and coordinates destroy + fresh respawn each episode.</summary>
public static class AGAMOTTO_EpisodeRespawn
{
    public struct UnitSpawnSpec
    {
        public string Uid;
        public string CotType;
        public double Lat;
        public double Lon;
        public bool ImitationLearning;
        public int HostileRows;
        public int HostileCols;
        public bool IsHostileGrid;
    }

    public struct RespawnResult
    {
        public bool Success;
        public RideID FriendlyTacticalGroup;
        public RideID FriendlyLeader;
        public RideID PrimaryEnemy;
        public RideVector3 FriendlyLeaderSpawn;
        public RideVector3 EnemySpawn;
        public string FriendlyUid;
    }

    static readonly Dictionary<string, UnitSpawnSpec> _specs = new Dictionary<string, UnitSpawnSpec>();
    static string _rlFriendlyUid;

    public static void RegisterSpawn(UnitSpawnSpec spec, bool isRlFriendly)
    {
        if (string.IsNullOrEmpty(spec.Uid))
            return;
        _specs[spec.Uid] = spec;
        if (isRlFriendly)
            _rlFriendlyUid = spec.Uid;
    }

    public static bool HasRegisteredSpawns => _specs.Count > 0;

    public static bool TryGetSpec(string uid, out UnitSpawnSpec spec) =>
        _specs.TryGetValue(uid, out spec);

    public static IEnumerable<string> GetRegisteredUids() => _specs.Keys;

    // Causal trials: a per-unit offset (scene metres) applied on the next spawn of that uid.
    // Empty unless AGAMOTTO_CausalRecorder is running randomized trials, so RL respawns are unaffected.
    static readonly Dictionary<string, Vector2> _sceneOffsets = new Dictionary<string, Vector2>();

    public static void SetSceneOffset(string uid, float dx, float dz)
    {
        if (!string.IsNullOrEmpty(uid))
            _sceneOffsets[uid] = new Vector2(dx, dz);
    }

    public static void ClearSceneOffsets() => _sceneOffsets.Clear();

    public static bool TryGetSceneOffset(string uid, out float dx, out float dz)
    {
        dx = 0f;
        dz = 0f;
        if (string.IsNullOrEmpty(uid) || !_sceneOffsets.TryGetValue(uid, out Vector2 offset))
            return false;
        dx = offset.x;
        dz = offset.y;
        return true;
    }

    public static bool TryRespawnAllUnits(out RespawnResult result, bool attachRlPolicy = true)
    {
        result = default;
        if (AGAMOTTOScenarioCreator.Active == null)
        {
            Debug.LogWarning("AGAMOTTO_EpisodeRespawn: no active ScenarioCreator.");
            return false;
        }

        if (_specs.Count == 0)
        {
            Debug.LogWarning("AGAMOTTO_EpisodeRespawn: no spawn specs registered.");
            return false;
        }

        return AGAMOTTOScenarioCreator.Active.TryRespawnRegisteredUnits(
            _rlFriendlyUid,
            out result,
            attachRlPolicy);
    }
}
