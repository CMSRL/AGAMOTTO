using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Records (observation, policy action, expert action, mission reward) transitions during hybrid expert baseline.
/// Python AWAC offline trainer reads <see cref="DefaultBufferFileName"/> under StreamingAssets/awac/.
/// </summary>
public static class AGAMOTTO_ExpertTrajectoryRecorder
{
    public const int ObservationSize = 10;
    public const int ActionSize = 2;
    public const string DefaultBufferFileName = "expert_buffer.jsonl";
    public const string ManifestFileName = "expert_manifest.json";

    static readonly List<string> PendingLines = new List<string>();
    static string _runId = "hybrid_awac";
    static string _leaderCallsign = "leader";
    static int _episodeId;
    static int _transitionCount;
    static bool _enabled;

    public static bool IsEnabled => _enabled;
    public static int TransitionCount => _transitionCount;

    public static void ResetSession()
    {
        PendingLines.Clear();
        _episodeId = 0;
        _transitionCount = 0;
        _enabled = false;
    }

    public static void Configure(string runId, string leaderCallsign, bool enabled)
    {
        _runId = string.IsNullOrWhiteSpace(runId) ? "hybrid_awac" : runId;
        _leaderCallsign = string.IsNullOrWhiteSpace(leaderCallsign) ? "leader" : leaderCallsign;
        _enabled = enabled;
        EnsureDirectory();
    }

    public static void OnExpertEpisodeBegin()
    {
        if (!_enabled)
            return;
        _episodeId++;
    }

    public static void RecordTransition(
        int step,
        float[] observation,
        float[] policyAction,
        float[] expertAction,
        float reward,
        bool done,
        float cumulativeReward)
    {
        if (!_enabled)
            return;
        if (observation == null || observation.Length != ObservationSize)
            return;
        if (expertAction == null || expertAction.Length != ActionSize)
            return;

        float[] policy = policyAction ?? new float[ActionSize];
        var sb = new StringBuilder(512);
        sb.Append('{');
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"run_id\":\"{0}\",", EscapeJson(_runId));
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"leader_callsign\":\"{0}\",", EscapeJson(_leaderCallsign));
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"episode_id\":{0},", _episodeId);
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"step\":{0},", step);
        sb.Append(JsonArray("observation", observation));
        sb.Append(',');
        sb.Append(JsonArray("policy_action", policy, ActionSize));
        sb.Append(',');
        sb.Append(JsonArray("expert_action", expertAction, ActionSize));
        sb.Append(',');
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"reward\":{0},", JsonFloat(reward));
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"cumulative_reward\":{0},", JsonFloat(cumulativeReward));
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"done\":{0}", done ? "true" : "false");
        sb.Append('}');
        PendingLines.Add(sb.ToString());
        _transitionCount++;
    }

    public static void FlushAndWriteManifest()
    {
        if (!_enabled && PendingLines.Count == 0)
            return;

        EnsureDirectory();
        string bufferPath = Path.Combine(AGAMOTTO_HybridAwacHub.ExpertBufferDirectory, DefaultBufferFileName);
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllLines(bufferPath, PendingLines, utf8NoBom);

        string manifestPath = Path.Combine(AGAMOTTO_HybridAwacHub.ExpertBufferDirectory, ManifestFileName);
        string manifest =
            "{\n"
            + $"  \"run_id\": \"{EscapeJson(_runId)}\",\n"
            + $"  \"leader_callsign\": \"{EscapeJson(_leaderCallsign)}\",\n"
            + $"  \"transitions\": {_transitionCount},\n"
            + $"  \"episodes\": {_episodeId},\n"
            + $"  \"observation_size\": {ObservationSize},\n"
            + $"  \"action_size\": {ActionSize},\n"
            + $"  \"buffer_path\": \"{EscapeJson(bufferPath)}\"\n"
            + "}\n";
        File.WriteAllText(manifestPath, manifest, utf8NoBom);
        Debug.Log(
            $"AGAMOTTO_ExpertTrajectoryRecorder: wrote {PendingLines.Count} transitions to {bufferPath}");
    }

    static void EnsureDirectory()
    {
        string dir = AGAMOTTO_HybridAwacHub.ExpertBufferDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    static string JsonArray(string key, float[] values, int expectedLength = -1)
    {
        int n = expectedLength > 0 ? expectedLength : values.Length;
        var sb = new StringBuilder();
        sb.AppendFormat(CultureInfo.InvariantCulture, "\"{0}\":[", key);
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                sb.Append(',');
            float v = i < values.Length ? values[i] : 0f;
            sb.Append(JsonFloat(v));
        }
        sb.Append(']');
        return sb.ToString();
    }

    static string JsonFloat(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v))
            return "0";
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    static string EscapeJson(string s) =>
        (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}
