using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Optional per-run overrides written to StreamingAssets/evaluation/active_run.json.
/// </summary>
public static class AGAMOTTO_EvaluationRunConfig
{
    [Serializable]
    sealed class ActiveRunJson
    {
        public int version = 1;
        public string runId;
        public string phase = "training";
        public string trainerType = "sac";
        public bool sendTrainingFeedbackToTak = true;
        public float rewardPerEnemySquadMemberKilled = float.NaN;
        public float penaltyPerFriendlyWingmanDeath = float.NaN;
        public bool useSquadNavMeshPaths = false;
        public float squadMaxMemberSpacingM = 20f;
        public string dispatchMode = "Training";
        public int trainingFriendlyLimit = 1;
        public bool inferenceEnabled = false;
        public bool pythonDrivesInference = false;
        public string inferencePolicyPath = "policies/AGAMOTTO_RL.onnx";
        public string leaderCallsign = "leader";
        public int inferenceRouteSampleSteps = 8;
    }

    static ActiveRunJson _cached;
    static bool _loaded;
    static int _rlSlotsClaimed;
    static bool _respawnReattachBypass;

    public static void BeginRespawnReattach() => _respawnReattachBypass = true;
    public static void EndRespawnReattach() => _respawnReattachBypass = false;

    static string ActiveRunPath =>
        Path.Combine(Application.streamingAssetsPath, "evaluation", "active_run.json");

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        _rlSlotsClaimed = 0;
    }

    public static bool TryClaimRlTrainingSlot()
    {
        if (_respawnReattachBypass)
            return true;
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

    public static void InvalidateCache()
    {
        _cached = null;
    }

    public static string GetDispatchMode()
    {
        var cfg = LoadConfig();
        string mode = string.IsNullOrEmpty(cfg?.dispatchMode) ? "Training" : cfg.dispatchMode;
        if (string.Equals(mode, "Hybrid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "AWAC", StringComparison.OrdinalIgnoreCase))
            return "Training";
        return mode;
    }

    /// <summary>
    /// Overwrite leftover run files so SAC from WinTAK attaches as Training.
    /// </summary>
    public static void WriteSacTrainingRun()
    {
        var cfg = new ActiveRunJson
        {
            version = 1,
            runId = "sac",
            phase = "training",
            trainerType = "sac",
            dispatchMode = "Training",
            sendTrainingFeedbackToTak = true,
            inferenceEnabled = false,
            pythonDrivesInference = false,
            trainingFriendlyLimit = 1,
        };
        try
        {
            string dir = Path.GetDirectoryName(ActiveRunPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ActiveRunPath, JsonUtility.ToJson(cfg, true));
            _cached = cfg;
            _loaded = true;
            Debug.Log("AGAMOTTO_EvaluationRunConfig: wrote SAC training active_run.json");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_EvaluationRunConfig: could not write SAC active_run: {ex.Message}");
            _cached = cfg;
            _loaded = true;
        }
    }

    public static bool IsInferencePhase()
    {
        var cfg = LoadConfig();
        if (cfg == null)
            return false;
        return cfg.inferenceEnabled
            || string.Equals(cfg.phase, "inference", StringComparison.OrdinalIgnoreCase);
    }

    public static bool PythonDrivesInference()
    {
        var cfg = LoadConfig();
        return cfg != null && cfg.pythonDrivesInference;
    }

    public static string GetTrainerType()
    {
        var cfg = LoadConfig();
        return string.IsNullOrEmpty(cfg?.trainerType) ? "sac" : cfg.trainerType;
    }

    public static string GetRunId()
    {
        var cfg = LoadConfig();
        return string.IsNullOrEmpty(cfg?.runId) ? "inference" : cfg.runId;
    }

    public static string GetInferencePolicyRelativePath()
    {
        var cfg = LoadConfig();
        return string.IsNullOrEmpty(cfg?.inferencePolicyPath)
            ? "policies/AGAMOTTO_RL.onnx"
            : cfg.inferencePolicyPath;
    }

    public static string GetLeaderCallsign()
    {
        var cfg = LoadConfig();
        return string.IsNullOrEmpty(cfg?.leaderCallsign) ? "leader" : cfg.leaderCallsign;
    }

    public static int GetInferenceRouteSampleSteps()
    {
        var cfg = LoadConfig();
        return cfg != null && cfg.inferenceRouteSampleSteps > 0 ? cfg.inferenceRouteSampleSteps : 8;
    }

    public static void TryApplyToAgent(AGAMOTTO_ATD_Agent agent)
    {
        if (agent == null) return;
        var cfg = LoadConfig();

        if (cfg != null)
        {
            try
            {
                if (!string.IsNullOrEmpty(cfg.runId))
                    Debug.Log($"AGAMOTTO_EvaluationRunConfig: active run '{cfg.runId}' phase={cfg.phase}");

                agent.sendTrainingFeedbackToTak = cfg.sendTrainingFeedbackToTak;

                if (!float.IsNaN(cfg.rewardPerEnemySquadMemberKilled))
                    agent.rewardPerEnemySquadMemberKilled = cfg.rewardPerEnemySquadMemberKilled;
                if (!float.IsNaN(cfg.penaltyPerFriendlyWingmanDeath))
                    agent.penaltyPerFriendlyWingmanDeath = cfg.penaltyPerFriendlyWingmanDeath;
                agent.useSquadNavMeshPaths = cfg.useSquadNavMeshPaths;
                if (cfg.squadMaxMemberSpacingM > 0f)
                    agent.squadMaxMemberSpacingM = cfg.squadMaxMemberSpacingM;

                if (IsInferencePhase())
                {
                    agent.ConfigureInferenceRun(
                        cfg.trainerType,
                        cfg.runId,
                        cfg.leaderCallsign,
                        cfg.inferenceRouteSampleSteps);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AGAMOTTO_EvaluationRunConfig: could not apply active_run: {ex.Message}");
            }
        }

    }
}
