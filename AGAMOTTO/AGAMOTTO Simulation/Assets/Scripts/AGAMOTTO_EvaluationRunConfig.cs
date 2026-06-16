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
        /// <summary>When true, hybrid AWAC uses leader route as expert baseline before online training.</summary>
        public bool awacEnabled = false;
        /// <summary>AWAC temperature λ (advantage weighting); matches paper notation.</summary>
        public float awacLambda = 1.0f;
        /// <summary>Max decision steps in expert-baseline phase per episode before switching to online AWAC.</summary>
        public int expertPhaseMaxSteps = 2000;
        /// <summary>Fraction of online steps that blend toward expert action (0 = off, 1 = full expert).</summary>
        public float expertBlendDuringOnline = 0.0f;
        public string expertTrajectoryPath = "";
    }

    static ActiveRunJson _cached;

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

    static ActiveRunJson LoadConfig()
    {
        if (_cached != null)
            return _cached;
        EnsureLoaded();
        string path = ActiveRunPath;
        if (!File.Exists(path))
            return null;
        try
        {
            _cached = JsonUtility.FromJson<ActiveRunJson>(File.ReadAllText(path));
            return _cached;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_EvaluationRunConfig: could not read {path}: {ex.Message}");
            return null;
        }
    }

    public static string GetDispatchMode()
    {
        var cfg = LoadConfig();
        return string.IsNullOrEmpty(cfg?.dispatchMode) ? "Training" : cfg.dispatchMode;
    }

    public static bool IsAwacHybridEnabled()
    {
        var cfg = LoadConfig();
        if (cfg == null)
            return false;
        return cfg.awacEnabled
            || string.Equals(cfg.dispatchMode, "Hybrid", StringComparison.OrdinalIgnoreCase);
    }

    public static void TryApplyToAgent(AGAMOTTO_ATD_Agent agent)
    {
        if (agent == null) return;
        var cfg = LoadConfig();
        bool hybridDispatch = AGAMOTTO_HybridAwacHub.IsHybridDispatch
            || string.Equals(cfg?.dispatchMode, "Hybrid", StringComparison.OrdinalIgnoreCase);

        if (cfg != null)
        {
            try
            {
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
                Debug.LogWarning($"AGAMOTTO_EvaluationRunConfig: could not apply active_run: {ex.Message}");
            }
        }

        if (cfg != null || hybridDispatch)
        {
            agent.ApplyAwacRunConfig(
                cfg?.awacEnabled ?? hybridDispatch,
                cfg != null && cfg.awacLambda > 0f ? cfg.awacLambda : 1f,
                cfg != null && cfg.expertPhaseMaxSteps > 0 ? cfg.expertPhaseMaxSteps : 2000,
                cfg != null ? cfg.expertBlendDuringOnline : 0f,
                cfg?.expertTrajectoryPath ?? "",
                cfg?.runId ?? "hybrid_awac",
                hybridDispatch);
        }
    }
}
