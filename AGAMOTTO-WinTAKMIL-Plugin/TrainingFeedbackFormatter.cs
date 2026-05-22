using System;
using System.Text.RegularExpressions;

namespace AGAMOTTO.WinTAKMIL
{
    /// <summary>
    /// Turns JSON payloads from Unity RL training into short lines for the Assessment list.
    /// </summary>
    internal static class TrainingFeedbackFormatter
    {
        public static string FormatForAssessment(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return payload;

            var p = payload.TrimStart();
            if (p.Length < 2 || p[0] != '{')
                return payload;
            if (p.IndexOf("rl_training_episode", StringComparison.OrdinalIgnoreCase) < 0)
                return payload;

            bool success = Regex.IsMatch(p, "\"missionSuccess\"\\s*:\\s*true\\b", RegexOptions.IgnoreCase);
            string steps = MatchQuotedNumber(p, "stepCount");
            string reward = MatchQuotedNumber(p, "cumulativeReward");
            string outcome = MatchQuotedString(p, "outcome");

            return $"RL episode {(success ? "SUCCESS" : "FAILURE")} | steps={steps} | return={reward} | outcome={outcome}";
        }

        static string MatchQuotedNumber(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "?";
        }

        static string MatchQuotedString(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
