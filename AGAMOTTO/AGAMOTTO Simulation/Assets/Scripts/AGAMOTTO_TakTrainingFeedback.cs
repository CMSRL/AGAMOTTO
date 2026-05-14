#if RIDE_ML_AGENTS
using System;
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
        string json = JsonUtility.ToJson(payload);

        Task.Run(() => SendFrame(host.Trim(), port, json));
    }

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
}
#endif
