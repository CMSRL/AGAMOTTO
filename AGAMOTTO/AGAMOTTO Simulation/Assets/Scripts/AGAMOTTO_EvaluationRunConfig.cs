using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Optional per-run overrides written by the Python evaluation suite to
/// StreamingAssets/evaluation/active_run.json before training starts.
/// </summary>
public static class AGAMOTTO_EvaluationRunConfig
{
    [Serializable]
    sealed class ActiveRunJson
    {
        public int version = 1;
        public string runId;
        public bool sendTrainingFeedbackToTak = true;
        public float rewardPerEnemySquadMemberKilled = float.NaN;
        public float penaltyPerFriendlyWingmanDeath = float.NaN;
        /// <summary>false = teleport squad per RL step (reliable in eval); true = NavMesh MoveGroupPath.</summary>
        public bool useSquadNavMeshPaths = false;
        public float squadMaxMemberSpacingM = 20f;
        public string dispatchMode = "Training";
        public int trainingFriendlyLimit = 1;
    }

    static bool _loaded;
    static int _rlSlotsClaimed;

    static string ActiveRunPath =>
        Path.Combine(Application.streamingAssetsPath, "evaluation", "active_run.json");

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        _rlSlotsClaimed = 0;
    }

    /// <summary>Only friendly squad leaders may claim an RL policy slot (hostiles never train).</summary>
    public static bool TryClaimRlTrainingSlot()
    {
        EnsureLoaded();
        string path = ActiveRunPath;
        if (!File.Exists(path))
            return true;
        try
        {
            var cfg = JsonUtility.FromJson<ActiveRunJson>(File.ReadAllText(path));
            if (cfg == null || cfg.trainingFriendlyLimit <= 0)
                return true;
            if (_rlSlotsClaimed >= cfg.trainingFriendlyLimit)
                return false;
            _rlSlotsClaimed++;
            return true;
        }
        catch
        {
            return true;
        }
    }

    public static void TryApplyToAgent(AGAMOTTO_ATD_Agent agent)
    {
        if (agent == null) return;
        string path = ActiveRunPath;
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            var cfg = JsonUtility.FromJson<ActiveRunJson>(json);
            if (cfg == null) return;

            if (!string.IsNullOrEmpty(cfg.runId))
                Debug.Log($"AGAMOTTO_EvaluationRunConfig: active run '{cfg.runId}'");

            agent.sendTrainingFeedbackToTak = cfg.sendTrainingFeedbackToTak;

            if (!float.IsNaN(cfg.rewardPerEnemySquadMemberKilled))
                agent.rewardPerEnemySquadMemberKilled = cfg.rewardPerEnemySquadMemberKilled;
            if (!float.IsNaN(cfg.penaltyPerFriendlyWingmanDeath))
                agent.penaltyPerFriendlyWingmanDeath = cfg.penaltyPerFriendlyWingmanDeath;
            agent.useSquadNavMeshPaths = cfg.useSquadNavMeshPaths;
            if (cfg.squadMaxMemberSpacingM > 0f)
                agent.squadMaxMemberSpacingM = cfg.squadMaxMemberSpacingM;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_EvaluationRunConfig: could not read {path}: {ex.Message}");
        }
    }
}
