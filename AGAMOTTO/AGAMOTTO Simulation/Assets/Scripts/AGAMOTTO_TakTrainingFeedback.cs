#if RIDE_ML_AGENTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Sends ML-Agents episode outcomes to the AGAMOTTO WinTAK plugin results listener (127.0.0.1:65433 by default),
/// using the same length-prefixed UTF-8 framing as <c>Send_to_WinTAK.py</c> / <c>TemplateDockPane</c>.
/// </summary>
public static class AGAMOTTO_TakTrainingFeedback
{
    const int MaxPayloadBytes = 10_000_000;

    [Serializable]
    public class RoutePoint
    {
        public double lat;
        public double lon;
        public double alt;
    }

    [Serializable]
    class AgamottoRlEpisodeFeedback
    {
        public string kind;
        public bool missionSuccess;
        public int stepCount;
        public float cumulativeReward;
        public int maxStep;
        public string outcome;
    }

    [Serializable]
    class AgamottoInferenceCompleteFeedback
    {
        public string kind = "inference_complete";
        public string trainer;
        public string runId;
        public bool missionSuccess;
        public string outcome;
        public int stepCount;
        public float cumulativeReward;
        public string leaderCallsign;
        public string routeName;
        public string routeUid;
        public RoutePoint[] routePoints;
    }

    /// <summary>Fire-and-forget TCP send on a background thread (does not block the simulation step).</summary>
    public static void TrySend(
        bool enabled,
        string host,
        int port,
        bool missionSuccess,
        int stepCount,
        float cumulativeReward,
        int maxStep,
        string outcome)
    {
        if (!enabled)
            return;
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            return;

        var payload = new AgamottoRlEpisodeFeedback
        {
            kind = "rl_training_episode",
            missionSuccess = missionSuccess,
            stepCount = stepCount,
            cumulativeReward = cumulativeReward,
            maxStep = maxStep,
            outcome = outcome ?? ""
        };
        QueueSend(host, port, JsonUtility.ToJson(payload));
    }

    /// <summary>Post-training inference episode summary with leader path for WinTAK map overlay.</summary>
    public static void TrySendInferenceComplete(
        bool enabled,
        string host,
        int port,
        string trainer,
        string runId,
        bool missionSuccess,
        int stepCount,
        float cumulativeReward,
        string outcome,
        string leaderCallsign,
        string routeName,
        IReadOnlyList<RoutePoint> routePoints)
    {
        if (!enabled)
            return;
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            return;

        var payload = new AgamottoInferenceCompleteFeedback
        {
            trainer = trainer ?? "sac",
            runId = runId ?? "inference",
            missionSuccess = missionSuccess,
            outcome = outcome ?? "",
            stepCount = stepCount,
            cumulativeReward = cumulativeReward,
            leaderCallsign = string.IsNullOrWhiteSpace(leaderCallsign) ? "leader" : leaderCallsign,
            routeName = string.IsNullOrWhiteSpace(routeName) ? "Policy route" : routeName,
            routeUid = Guid.NewGuid().ToString(),
            routePoints = routePoints != null ? new List<RoutePoint>(routePoints).ToArray() : Array.Empty<RoutePoint>()
        };
        QueueSend(host, port, JsonUtility.ToJson(payload));
    }

    static void QueueSend(string host, int port, string json) =>
        Task.Run(() => SendFrame(host.Trim(), port, json));

    static void SendFrame(string host, int port, string json)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (body.Length > MaxPayloadBytes)
                return;

            using (var client = new TcpClient())
            {
                client.NoDelay = true;
                client.Connect(host, port);
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] len = BitConverter.GetBytes(body.Length);
                    stream.Write(len, 0, 4);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"AGAMOTTO_TakTrainingFeedback: send failed ({host}:{port}): {ex.Message}");
        }
    }
}
#endif
