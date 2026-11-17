using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AGAMOTTO.WinTAKMIL
{
    /// <summary>
    /// Turns JSON payloads from Unity / Python into Assessment lines and route vertices.
    /// </summary>
    internal static class TrainingFeedbackFormatter
    {
        public sealed class InferenceRoutePayload
        {
            public string DisplayLine;
            public string RouteUid;
            public string RouteName;
            public List<(double lat, double lon, double? alt)> Vertices = new List<(double, double, double?)>();
        }

        public static string FormatForAssessment(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return payload;

            var p = payload.TrimStart();
            if (p.Length < 2 || p[0] != '{')
                return payload;

            if (p.IndexOf("inference_complete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool success = Regex.IsMatch(p, "\"missionSuccess\"\\s*:\\s*true\\b", RegexOptions.IgnoreCase);
                string trainer = MatchQuotedString(p, "trainer");
                string steps = MatchQuotedNumber(p, "stepCount");
                string reward = MatchQuotedNumber(p, "cumulativeReward");
                string outcome = MatchQuotedString(p, "outcome");
                int points = CountRoutePoints(p);
                return
                    $"INFERENCE {(success ? "SUCCESS" : "FAILURE")} ({trainer}) | steps={steps} | return={reward} | outcome={outcome} | route_pts={points}";
            }

            if (p.IndexOf("training_complete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string trainer = MatchQuotedString(p, "trainer");
                string runId = MatchQuotedString(p, "runId");
                string steps = MatchQuotedNumber(p, "totalSteps");
                return $"TRAINING COMPLETE ({trainer}) run={runId} steps={steps}";
            }

            if (p.IndexOf("rl_training_episode", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bool success = Regex.IsMatch(p, "\"missionSuccess\"\\s*:\\s*true\\b", RegexOptions.IgnoreCase);
                string steps = MatchQuotedNumber(p, "stepCount");
                string reward = MatchQuotedNumber(p, "cumulativeReward");
                string outcome = MatchQuotedString(p, "outcome");
                return $"RL episode {(success ? "SUCCESS" : "FAILURE")} | steps={steps} | return={reward} | outcome={outcome}";
            }

            return payload;
        }

        public static bool TryParseInferenceRoute(string payload, out InferenceRoutePayload parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(payload))
                return false;
            var p = payload.TrimStart();
            if (p.IndexOf("inference_complete", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var verts = new List<(double, double, double?)>();
            var pointPattern = new Regex(
                "\\{\\s*\"lat\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)\\s*,\\s*\"lon\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)(?:\\s*,\\s*\"alt\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?))?",
                RegexOptions.IgnoreCase);
            foreach (Match m in pointPattern.Matches(p))
            {
                if (!m.Success)
                    continue;
                double lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double? alt = null;
                if (m.Groups[3].Success)
                    alt = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                verts.Add((lat, lon, alt));
            }

            if (verts.Count < 2)
                return false;

            parsed = new InferenceRoutePayload
            {
                DisplayLine = FormatForAssessment(payload),
                RouteUid = MatchQuotedString(p, "routeUid"),
                RouteName = MatchQuotedString(p, "routeName"),
                Vertices = verts
            };
            if (string.IsNullOrWhiteSpace(parsed.RouteUid))
                parsed.RouteUid = Guid.NewGuid().ToString();
            if (string.IsNullOrWhiteSpace(parsed.RouteName))
                parsed.RouteName = "Policy route";
            return true;
        }

        static int CountRoutePoints(string json)
        {
            return Regex.Matches(json, "\"lat\"\\s*:", RegexOptions.IgnoreCase).Count;
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
