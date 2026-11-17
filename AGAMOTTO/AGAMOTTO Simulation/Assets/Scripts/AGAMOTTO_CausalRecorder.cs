using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AGAMOTTO;
using Ride;
using Ride.Combat;
using Ride.Entities;
using Ride.Movement;
using Ride.Terrain;
using Ride.WorldState;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Records what RIDE's own combat model does, for causal analysis of terrain effects.
/// No ML-Agents: it reads RIDE systems and world events only, so it works on units
/// spawned from TAK in plain Evaluation mode.
///
/// Two ways to collect data:
///   observational - record whatever happens to the units already in the scene;
///   randomized    - respawn the TAK units at a seeded random offset from their TAK
///                   points, once per trial, so position is set by the experiment.
///
/// Output is one JSONL file per Play session under [project]/CausalLogs, one row per
/// line, typed by "kind": trial_begin, agent, dyad, shot, death, trial_end.
/// Fit it with "AGAMOTTO Server/causal/fit_graph.py".
/// </summary>
public class AGAMOTTO_CausalRecorder : MonoBehaviour
{
    public enum MovementMode { Hold, BlueAdvances }
    public enum RandomizeSide { Both, BlueOnly, RedOnly }
    public enum PlaceMode { NearPins, CoverArea }

    [Header("Sampling")]
    [Tooltip("Seconds of simulation time between agent/dyad samples.")]
    public float sampleIntervalS = 1f;
    [Tooltip("Half-width of the stencil used for local slope (m).")]
    public float slopeStencilM = 5f;
    [Tooltip("Ring radius for local prominence: ground height here minus mean height on the ring (m).")]
    public float prominenceRadiusM = 30f;

    [Header("Randomized trials")]
    public int trialCount = 20;
    [Tooltip("Trial i uses seed baseSeed + i, so a run can be repeated exactly.")]
    public int baseSeed = 1;
    [Tooltip("Near pins: disk radius around each TAK point (m). Cover area: padding added to the TAK bounding box.")]
    public float offsetRadiusM = 150f;
    public RandomizeSide randomize = RandomizeSide.Both;
    [Tooltip("Near pins = jitter around each TAK pin. Cover area = move the whole layout across that box so trials cover terrain.")]
    public PlaceMode place = PlaceMode.CoverArea;
    public MovementMode movement = MovementMode.Hold;
    public float maxTrialSeconds = 60f;
    [Tooltip("Pause after respawn, with firing off, before the trial clock starts.")]
    public float settleSeconds = 1f;
    [Tooltip("RIDE sim speed during collection (ITimeSimulationSystem.SetSpeed). 1 = real time. "
             + "Above ~8, targeting/physics can diverge from 1x.")]
    public float trialTimeScale = 4f;

    [Header("Panel")]
    public bool showPanel = true;

    public bool IsRecording => _recording;
    public bool IsRunningTrials => _trialsRunning;
    public string LogPath => _logPath;

    sealed class Tracked
    {
        public RideID id;
        public Team team;
        public string unit;
        public bool alive = true;
        public bool sampled;
        public bool hasLast;
        public Vector3 lastPos;
        public float lastT;
        public Vector3 pos;
        public Vector3 view;
        public float elev = float.NaN;
        public float slopeDeg = float.NaN;
        public float prom = float.NaN;
    }

    struct UnitPlan
    {
        public string uid;
        public bool blue;
        public float baseX, baseZ, dx, dz;
        public int tries;
        public bool randomized;
        public bool offsetOk;
    }

    const AttackSystemFlags CombatOn = AttackSystemFlags.Auto_Attack | AttackSystemFlags.Acquire_Targets;
    const AttackSystemFlags CombatHeld = AttackSystemFlags.Acquire_Targets;
    const int FlushEveryLines = 256;
    const float LosClearEpsilonM = 0.35f;

    readonly List<Tracked> _roster = new List<Tracked>();
    readonly Dictionary<RideID, Tracked> _byId = new Dictionary<RideID, Tracked>();
    readonly List<string> _pending = new List<string>();
    readonly int[] _shots = new int[2];
    readonly int[] _hits = new int[2];
    readonly int[] _losses = new int[2];
    readonly float[] _firstShotT = { float.NaN, float.NaN };

    IEngagementSystem _engagements;
    bool _engagementLookupDone;
    bool _listening;
    bool _recording;
    bool _passive;
    bool _trialsRunning;
    bool _abortRequested;
    int _trialId;
    int _trialsDone;
    int _sampleIndex;
    float _trialT0;
    float _nextSampleT;
    float _savedTimeScale = 1f;
    bool _simRateApplied;
    string _sessionId;
    string _logPath;
    string _message = "Idle.";
    int _warnings;
    string _trialCountText, _radiusText, _maxSecondsText, _seedText, _rateText;

    // ---------------------------------------------------------------- lifecycle

    void OnEnable()
    {
        _trialCountText = trialCount.ToString(CultureInfo.InvariantCulture);
        _radiusText = offsetRadiusM.ToString("0.#", CultureInfo.InvariantCulture);
        _maxSecondsText = maxTrialSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        _seedText = baseSeed.ToString(CultureInfo.InvariantCulture);
        _rateText = trialTimeScale.ToString("0.#", CultureInfo.InvariantCulture);
    }

    void OnDisable()
    {
        if (_recording)
            EndTrial(_trialsRunning ? "aborted" : "stopped");
        if (_trialsRunning)
        {
            // A disabled component keeps its coroutines, so stop the trial loop explicitly.
            _abortRequested = true;
            StopAllCoroutines();
            FinishTrials();
        }
        else
            RestoreSimRate();
        StopListening();
        Flush();
    }

    void OnApplicationQuit() => Flush();

    void Update()
    {
        if (!_recording)
            return;

        PollDeaths();

        float t = TrialTime;
        if (t >= _nextSampleT)
        {
            _nextSampleT = t + Mathf.Max(0.05f, sampleIntervalS);
            if (_passive)
                RefreshRoster(false);
            SamplePass(t);
        }

        if (_passive && (Alive(Team.Blue) == 0 || Alive(Team.Red) == 0))
        {
            EndTrial(OutcomeFromAlive());
            _message = "Observational recording ended: one side has no one left.";
        }
    }

    float TrialTime => Time.time - _trialT0;

    // ---------------------------------------------------------------- public controls

    /// <summary>Record the units already in the scene without touching them.</summary>
    public bool StartObservational()
    {
        if (_recording || _trialsRunning)
            return Fail("Already recording.");
        if (Globals.api == null)
            return Fail("RIDE is not ready yet.");

        RefreshRoster(true);
        if (Alive(Team.Blue) == 0 || Alive(Team.Red) == 0)
            return Fail("Need at least one live Blue and one live Red unit. Send map data from TAK first.");

        _passive = true;
        ApplySimRate();
        BeginTrial("observational", -1, null);
        _message = "Recording what the current units do.";
        return true;
    }

    public void StopObservational()
    {
        if (_recording && _passive)
        {
            EndTrial("stopped");
            RestoreSimRate();
            _message = "Observational recording stopped.";
        }
    }

    /// <summary>Respawn the TAK units at seeded random offsets and record each engagement.</summary>
    public bool StartRandomizedTrials()
    {
        if (_recording || _trialsRunning)
            return Fail("Already recording.");
        if (Globals.api == null || AGAMOTTOScenarioCreator.Active == null)
            return Fail("RIDE is not ready yet.");
        if (!AGAMOTTO_EpisodeRespawn.HasRegisteredSpawns)
            return Fail("No TAK units yet. Send map data from TAK (Evaluation) first.");

        // Trials respawn the units without an RL policy, which would break a live training run.
        string mode = AGAMOTTOScenarioCreator.Active.LastDispatchMode;
        if (!string.Equals(mode, "Evaluation", StringComparison.OrdinalIgnoreCase))
            return Fail($"Last TAK send was '{mode}'. Trials need units sent in Evaluation mode.");

        bool haveBlue = false, haveRed = false;
        foreach (string uid in AGAMOTTO_EpisodeRespawn.GetRegisteredUids())
        {
            if (!AGAMOTTO_EpisodeRespawn.TryGetSpec(uid, out var spec))
                continue;
            if (IsBlueCot(spec.CotType)) haveBlue = true;
            else if (IsRedCot(spec.CotType)) haveRed = true;
        }
        if (!haveBlue || !haveRed)
            return Fail("Trials need at least one friendly and one hostile TAK unit.");

        _message = $"Starting {Mathf.Max(1, trialCount)} trials ({place}, {movement}).";
        StartCoroutine(RunTrials());
        return true;
    }

    public void AbortTrials()
    {
        if (_trialsRunning)
            _abortRequested = true;
    }

    bool Fail(string why)
    {
        _message = why;
        Debug.LogWarning("AGAMOTTO_CausalRecorder: " + why);
        return false;
    }

    static ITimeSimulationSystem TryTimeSim()
    {
        if (Globals.api == null)
            return null;
        try
        {
            return Globals.api.GetSystem<ITimeSimulationSystem>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    void ApplySimRate()
    {
        float rate = Mathf.Clamp(trialTimeScale, 0.25f, 20f);
        trialTimeScale = rate;
        ITimeSimulationSystem ts = TryTimeSim();
        if (ts != null)
        {
            _savedTimeScale = ts.speed > 0.01f ? ts.speed : 1f;
            if (ts.isPaused)
                ts.Play();
            ts.SetSpeed(rate);
        }
        else
        {
            _savedTimeScale = Time.timeScale > 0.01f ? Time.timeScale : 1f;
            Time.timeScale = rate;
        }

        _simRateApplied = true;
        Debug.Log($"AGAMOTTO_CausalRecorder: RIDE sim rate {rate:0.#}x (timeScale={Time.timeScale:0.#}).");
    }

    void RestoreSimRate()
    {
        if (!_simRateApplied)
            return;
        _simRateApplied = false;
        float restore = _savedTimeScale > 0.01f ? _savedTimeScale : 1f;
        try
        {
            ITimeSimulationSystem ts = TryTimeSim();
            if (ts != null)
            {
                if (ts.isPaused)
                    ts.Play();
                ts.SetSpeed(restore);
                return;
            }
        }
        catch (Exception)
        {
            // leaving Play
        }

        Time.timeScale = restore;
    }

    // ---------------------------------------------------------------- randomized trials

    IEnumerator RunTrials()
    {
        _trialsRunning = true;
        _abortRequested = false;
        _trialsDone = 0;
        ApplySimRate();
        int total = Mathf.Max(1, trialCount);

        for (int i = 0; i < total && !_abortRequested; i++)
        {
            int seed = baseSeed + i;
            List<UnitPlan> plans = PlanOffsets(new System.Random(seed), i);

            AGAMOTTO_EpisodeRespawn.ClearSceneOffsets();
            foreach (UnitPlan plan in plans)
                AGAMOTTO_EpisodeRespawn.SetSceneOffset(plan.uid, plan.dx, plan.dz);

            bool respawned;
            try
            {
                respawned = AGAMOTTO_EpisodeRespawn.TryRespawnAllUnits(out _, attachRlPolicy: false);
            }
            finally
            {
                AGAMOTTO_EpisodeRespawn.ClearSceneOffsets();
            }

            if (!respawned)
            {
                _message = $"Trial {i + 1}: respawn failed. Stopping.";
                Debug.LogError("AGAMOTTO_CausalRecorder: " + _message);
                break;
            }

            // Respawn leaves firing off; close to weapon range before the trial clock starts
            // so Max s is combat time, not walking time.
            Globals.api.attackSystem.flags = CombatHeld;
            yield return new WaitForSeconds(Mathf.Max(0.1f, settleSeconds));

            RefreshRoster(true);
            if (Alive(Team.Blue) == 0 || Alive(Team.Red) == 0)
            {
                _message = $"Trial {i + 1}: units missing after respawn. Stopping.";
                Debug.LogError("AGAMOTTO_CausalRecorder: " + _message);
                break;
            }

            float approachLimit = Mathf.Max(120f, maxTrialSeconds * 3f);
            yield return CloseToContact(approachLimit);
            if (_abortRequested)
                break;

            _passive = false;
            BeginTrial("randomized", seed, plans);
            Globals.api.attackSystem.flags = CombatOn;
            SeedEngagements();
            if (movement == MovementMode.BlueAdvances)
                OrderBlueAdvance();

            string outcome = null;
            float nextNudge = 1f;
            while (outcome == null)
            {
                yield return null;
                if (_abortRequested) outcome = "aborted";
                else if (Alive(Team.Blue) == 0 || Alive(Team.Red) == 0) outcome = OutcomeFromAlive();
                else if (TrialTime >= maxTrialSeconds)
                    outcome = (float.IsNaN(_firstShotT[0]) && float.IsNaN(_firstShotT[1])) ? "no_contact" : "timeout";
                else if (movement == MovementMode.BlueAdvances && TrialTime >= nextNudge)
                {
                    OrderBlueAdvance();
                    nextNudge = TrialTime + 2f;
                }
            }

            EndTrial(outcome);
            Globals.api.attackSystem.flags = CombatHeld;
            _trialsDone = i + 1;
            _message = $"Trial {_trialsDone}/{total} done ({outcome}).";
        }

        FinishTrials();
        if (_trialsDone == total)
            _message = $"Finished {total} randomized trials.";
        else if (_abortRequested)
            _message = $"Aborted after {_trialsDone} complete trial(s).";
    }

    void FinishTrials()
    {
        _trialsRunning = false;
        AGAMOTTO_EpisodeRespawn.ClearSceneOffsets();
        RestoreSimRate();
        try
        {
            if (Globals.api != null)
                Globals.api.attackSystem.flags = CombatOn;
        }
        catch (Exception)
        {
            // RIDE already torn down (leaving Play mode mid-trial)
        }
        Flush();
    }

    List<UnitPlan> PlanOffsets(System.Random rng, int trialIndex)
    {
        var plans = new List<UnitPlan>();
        var uids = new List<string>(AGAMOTTO_EpisodeRespawn.GetRegisteredUids());
        uids.Sort(StringComparer.Ordinal); // fixed order so a seed always maps to the same draw

        foreach (string uid in uids)
        {
            if (!AGAMOTTO_EpisodeRespawn.TryGetSpec(uid, out var spec))
                continue;
            bool blue = IsBlueCot(spec.CotType);
            if (!blue && !IsRedCot(spec.CotType))
                continue;

            RideVector3 basePos = Globals.api.terrainSystem.ConvertToScenePosition(spec.Lat, spec.Lon);
            var plan = new UnitPlan { uid = uid, blue = blue, baseX = basePos.x, baseZ = basePos.z, offsetOk = true };
            plan.randomized = randomize == RandomizeSide.Both
                              || (randomize == RandomizeSide.BlueOnly && blue)
                              || (randomize == RandomizeSide.RedOnly && !blue);
            plans.Add(plan);
        }

        if (place == PlaceMode.CoverArea)
            ApplyCoverAreaOffsets(plans, rng, trialIndex);
        else
            ApplyNearPinOffsets(plans, rng);

        return plans;
    }

    void ApplyNearPinOffsets(List<UnitPlan> plans, System.Random rng)
    {
        if (offsetRadiusM <= 0f)
            return;

        for (int i = 0; i < plans.Count; i++)
        {
            UnitPlan plan = plans[i];
            if (!plan.randomized)
                continue;

            var basePos = new Vector3(plan.baseX, 0f, plan.baseZ);
            TryTerrainHeight(plan.baseX, plan.baseZ, out float baseH);
            basePos.y = baseH;
            bool useNavMesh = NavMesh.SamplePosition(basePos, out _, 10f, NavMesh.AllAreas);
            plan.offsetOk = false;
            for (int attempt = 1; attempt <= 40 && !plan.offsetOk; attempt++)
            {
                plan.tries = attempt;
                double r = offsetRadiusM * Math.Sqrt(rng.NextDouble());
                double theta = 2.0 * Math.PI * rng.NextDouble();
                float dx = (float)(r * Math.Cos(theta));
                float dz = (float)(r * Math.Sin(theta));
                if (!TryTerrainHeight(plan.baseX + dx, plan.baseZ + dz, out float h))
                    continue;
                var candidate = new Vector3(plan.baseX + dx, h, plan.baseZ + dz);
                if (useNavMesh && !NavMesh.SamplePosition(candidate, out _, 4f, NavMesh.AllAreas))
                    continue;
                plan.dx = dx;
                plan.dz = dz;
                plan.offsetOk = true;
            }

            plans[i] = plan;
        }
    }

    /// <summary>
    /// Moves the TAK layout so this trial occupies a jittered grid cell over the TAK bounding box
    /// plus <see cref="offsetRadiusM"/> padding. Relative Blue/Red geometry is kept.
    /// </summary>
    void ApplyCoverAreaOffsets(List<UnitPlan> plans, System.Random rng, int trialIndex)
    {
        if (plans.Count == 0)
            return;

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        float originX = 0f, originZ = 0f;
        bool useNavMesh = false;
        foreach (UnitPlan plan in plans)
        {
            minX = Mathf.Min(minX, plan.baseX);
            maxX = Mathf.Max(maxX, plan.baseX);
            minZ = Mathf.Min(minZ, plan.baseZ);
            maxZ = Mathf.Max(maxZ, plan.baseZ);
            originX += plan.baseX;
            originZ += plan.baseZ;
            TryTerrainHeight(plan.baseX, plan.baseZ, out float h);
            if (NavMesh.SamplePosition(new Vector3(plan.baseX, h, plan.baseZ), out _, 10f, NavMesh.AllAreas))
                useNavMesh = true;
        }

        originX /= plans.Count;
        originZ /= plans.Count;
        float pad = Mathf.Max(0f, offsetRadiusM);
        minX -= pad;
        maxX += pad;
        minZ -= pad;
        maxZ += pad;
        float minSpan = Mathf.Max(2f * pad, 80f);
        if (maxX - minX < minSpan)
        {
            minX = originX - minSpan * 0.5f;
            maxX = originX + minSpan * 0.5f;
        }
        if (maxZ - minZ < minSpan)
        {
            minZ = originZ - minSpan * 0.5f;
            maxZ = originZ + minSpan * 0.5f;
        }

        int n = Mathf.Max(1, trialCount);
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(n)));
        int rows = Mathf.Max(1, Mathf.CeilToInt(n / (float)cols));
        int cells = rows * cols;
        int idx = ((trialIndex % cells) + cells) % cells;
        int col = idx % cols;
        int row = idx / cols;
        float cellMinX = minX + (maxX - minX) * col / cols;
        float cellMaxX = minX + (maxX - minX) * (col + 1) / cols;
        float cellMinZ = minZ + (maxZ - minZ) * row / rows;
        float cellMaxZ = minZ + (maxZ - minZ) * (row + 1) / rows;

        bool placed = false;
        float dx = 0f, dz = 0f;
        int tries = 0;
        for (int attempt = 1; attempt <= 40 && !placed; attempt++)
        {
            tries = attempt;
            float sampleX = cellMinX + (float)rng.NextDouble() * Mathf.Max(1f, cellMaxX - cellMinX);
            float sampleZ = cellMinZ + (float)rng.NextDouble() * Mathf.Max(1f, cellMaxZ - cellMinZ);
            dx = sampleX - originX;
            dz = sampleZ - originZ;
            bool ok = true;
            foreach (UnitPlan plan in plans)
            {
                if (!plan.randomized)
                    continue;
                if (!TryTerrainHeight(plan.baseX + dx, plan.baseZ + dz, out float h))
                {
                    ok = false;
                    break;
                }
                var candidate = new Vector3(plan.baseX + dx, h, plan.baseZ + dz);
                if (useNavMesh && !NavMesh.SamplePosition(candidate, out _, 6f, NavMesh.AllAreas))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                placed = true;
        }

        for (int i = 0; i < plans.Count; i++)
        {
            UnitPlan plan = plans[i];
            if (!plan.randomized)
                continue;
            plan.tries = tries;
            if (placed)
            {
                plan.dx = dx;
                plan.dz = dz;
                plan.offsetOk = true;
            }
            else
                plan.offsetOk = false;
            plans[i] = plan;
        }

        if (!placed)
            Warn($"cover-area trial {trialIndex + 1}: no on-mesh placement in cell; using TAK points.");
    }

    IEnumerator CloseToContact(float limit)
    {
        float t0 = Time.time;
        int tick = 0;
        while (!_abortRequested && Time.time - t0 < limit)
        {
            RefreshRoster(false);
            if (AnyPairInRange())
            {
                _message = $"In range after {Time.time - t0:0.0}s. Starting trial.";
                yield break;
            }

            if (tick == 16 && !AnyPairInRange())
            {
                Warn("navmesh close stalled; placing Blue at weapon standoff.");
                TeleportBlueToStandoff();
            }
            else if (tick % 2 == 0)
                OrderBlueTowardContact();
            tick++;
            float dist = BlueRedDistance();
            _message = float.IsNaN(dist)
                ? "Closing to contact..."
                : $"Closing to contact ({Time.time - t0:0}s) dist={dist:0}m";
            yield return new WaitForSeconds(0.5f);
        }

        RefreshRoster(false);
        if (!AnyPairInRange())
            Warn($"not in weapon range after {limit:0}s close; starting trial anyway.");
    }

    bool AnyPairInRange()
    {
        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != Team.Blue)
                continue;
            foreach (Tracked b in _roster)
            {
                if (!b.alive || b.team != Team.Red)
                    continue;
                if (InRange(a.id, b.id) == true)
                    return true;
            }
        }

        return false;
    }

    float BlueRedDistance()
    {
        if (!TryCentroid(Team.Blue, null, out Vector3 blue) || !TryCentroid(Team.Red, null, out Vector3 red))
            return float.NaN;
        blue.y = red.y = 0f;
        return Vector3.Distance(blue, red);
    }

    void OrderBlueTowardContact()
    {
        if (!TryCentroid(Team.Red, null, out Vector3 redC) || !TryCentroid(Team.Blue, null, out Vector3 blueC))
            return;

        float range = FirstOver(Team.Blue, WeaponRange);
        if (float.IsNaN(range) || range < 30f)
            range = 250f;
        Vector3 delta = blueC - redC;
        delta.y = 0f;
        float dist = delta.magnitude;
        float standoff = Mathf.Clamp(range * 0.55f, 40f, range * 0.85f);
        if (dist > 1f && dist <= standoff)
            return;

        Vector3 goal = dist < 1f ? redC : redC + delta.normalized * standoff;
        IssueBlueMove(goal);
    }

    void OrderBlueAdvance()
    {
        if (!TryCentroid(Team.Red, null, out Vector3 goal))
            return;
        IssueBlueMove(goal);
    }

    void TeleportBlueToStandoff()
    {
        if (!TryCentroid(Team.Red, null, out Vector3 redC) || !TryCentroid(Team.Blue, null, out Vector3 blueC))
            return;
        float range = FirstOver(Team.Blue, WeaponRange);
        if (float.IsNaN(range) || range < 30f)
            range = 250f;
        Vector3 delta = blueC - redC;
        delta.y = 0f;
        if (delta.sqrMagnitude < 1f)
            delta = Vector3.forward;
        float standoff = Mathf.Clamp(range * 0.55f, 40f, range * 0.85f);
        Vector3 goal = redC + delta.normalized * standoff;
        Vector3 shift = goal - blueC;
        shift.y = 0f;
        ITransformSystem ts;
        try { ts = Globals.api.GetSystem<ITransformSystem>(); }
        catch (Exception) { return; }
        if (ts == null)
            return;

        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != Team.Blue || !TryPosition(a.id, out Vector3 p))
                continue;
            float x = p.x + shift.x;
            float z = p.z + shift.z;
            if (!TryTerrainHeight(x, z, out float h))
                h = p.y;
            try { ts.SetPosition(a.id, new RideVector3(x, h, z)); }
            catch (Exception) { /* skip this soldier */ }
        }
    }

    void IssueBlueMove(Vector3 goal)
    {
        var creator = AGAMOTTOScenarioCreator.Active;
        if (creator == null || creator.SpawnedGroups == null)
            return;
        if (!TryTerrainHeight(goal.x, goal.z, out float h))
            h = goal.y;
        var dest = new RideVector3(goal.x, h, goal.z);
        var movers = Globals.api.movementSystem;

        foreach (KeyValuePair<string, RideID> kv in creator.SpawnedGroups)
        {
            if (!AGAMOTTO_EpisodeRespawn.TryGetSpec(kv.Key, out var spec) || !IsBlueCot(spec.CotType))
                continue;
            try
            {
                movers.MoveToPositionInFormation(kv.Value, dest, FormationProcedureType.Wedge);
            }
            catch (Exception)
            {
                try
                {
                    if (!TryCentroid(Team.Blue, kv.Key, out Vector3 start))
                        continue;
                    movers.MoveGroupPath(
                        kv.Value,
                        new RideVector3[] { start, dest },
                        FormationProcedureType.Wedge);
                }
                catch (Exception ex)
                {
                    Warn("advance order failed: " + ex.Message);
                }
            }
        }
    }

    void SeedEngagements()
    {
        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != Team.Blue)
                continue;
            Tracked best = null;
            float bestDist = float.PositiveInfinity;
            foreach (Tracked b in _roster)
            {
                if (!b.alive || b.team != Team.Red)
                    continue;
                if (InRange(a.id, b.id) != true)
                    continue;
                if (!TryPosition(a.id, out Vector3 ap) || !TryPosition(b.id, out Vector3 bp))
                    continue;
                float d = Vector3.Distance(ap, bp);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b;
                }
            }

            if (best == null)
                continue;
            try
            {
                Globals.api.attackSystem.AddEngagement(a.id, best.id);
            }
            catch (Exception)
            {
                // already engaged or agent not in attack system yet
            }
        }
    }

    // ---------------------------------------------------------------- trial bookkeeping

    void BeginTrial(string assignment, int seed, List<UnitPlan> plans)
    {
        EnsureLog();
        StartListening();
        if (!_engagementLookupDone)
        {
            _engagementLookupDone = true;
            try { _engagements = Globals.api.GetSystem<IEngagementSystem>(); }
            catch (Exception) { _engagements = null; }
        }

        _trialId++;
        _sampleIndex = 0;
        for (int i = 0; i < 2; i++)
        {
            _shots[i] = 0;
            _hits[i] = 0;
            _losses[i] = 0;
            _firstShotT[i] = float.NaN;
        }

        foreach (Tracked a in _roster)
        {
            a.hasLast = false;
            if (a.alive)
                TryReadAgent(a);
        }

        bool haveBlue = TryCentroid(Team.Blue, null, out Vector3 blueC);
        bool haveRed = TryCentroid(Team.Red, null, out Vector3 redC);
        float blueElev = MeanOver(Team.Blue, a => a.elev);
        float redElev = MeanOver(Team.Red, a => a.elev);

        var row = new Row("trial_begin")
            .S("session", _sessionId)
            .I("trial", _trialId)
            .S("assignment", assignment)
            .I("seed", seed)
            .S("movement", assignment == "randomized" ? movement.ToString() : "AsOrdered")
            .S("randomize", assignment == "randomized" ? randomize.ToString() : "None")
            .S("place", assignment == "randomized" ? place.ToString() : "AsPlaced")
            .F("offset_radius_m", assignment == "randomized" ? offsetRadiusM : 0f)
            .F("max_s", assignment == "randomized" ? maxTrialSeconds : float.NaN)
            .F("sample_interval_s", sampleIntervalS)
            .F("time_scale", Time.timeScale)
            .F("sim_rate", trialTimeScale)
            .I("blue_n", Alive(Team.Blue))
            .I("red_n", Alive(Team.Red))
            .F("init_dist", haveBlue && haveRed ? Vector3.Distance(blueC, redC) : float.NaN)
            .F("init_height_adv", blueElev - redElev)
            .F("blue_elev", blueElev)
            .F("red_elev", redElev)
            .F("blue_prom", MeanOver(Team.Blue, a => a.prom))
            .F("red_prom", MeanOver(Team.Red, a => a.prom))
            .F("blue_slope_deg", MeanOver(Team.Blue, a => a.slopeDeg))
            .F("red_slope_deg", MeanOver(Team.Red, a => a.slopeDeg))
            .F("blue_weapon_range", FirstOver(Team.Blue, WeaponRange))
            .F("red_weapon_range", FirstOver(Team.Red, WeaponRange))
            .F("blue_agent_range", FirstOver(Team.Blue, AgentRange))
            .F("red_agent_range", FirstOver(Team.Red, AgentRange));

        if (plans != null)
        {
            var units = new StringBuilder("[");
            for (int i = 0; i < plans.Count; i++)
            {
                UnitPlan p = plans[i];
                if (i > 0) units.Append(',');
                units.Append(new Row(null)
                    .S("uid", p.uid)
                    .B("blue", p.blue)
                    .F("base_x", p.baseX)
                    .F("base_z", p.baseZ)
                    .F("dx", p.dx)
                    .F("dz", p.dz)
                    .B("randomized", p.randomized)
                    .B("offset_ok", p.offsetOk)
                    .I("tries", p.tries)
                    .End());
            }
            units.Append(']');
            row.Raw("units", units.ToString());
        }

        Write(row.End());

        _trialT0 = Time.time;
        _nextSampleT = 0f;
        _recording = true;
    }

    void EndTrial(string outcome)
    {
        if (!_recording)
            return;
        float t = TrialTime;
        SamplePass(t);
        _recording = false;
        _passive = false;

        int blueShots = _shots[0];
        int blueHits = _hits[0];
        bool firstShot = !float.IsNaN(_firstShotT[0]);
        bool enemyKilled = _losses[1] > 0 || outcome == "red_destroyed";
        float hitRate = blueShots > 0 ? (float)blueHits / blueShots : float.NaN;
        Write(new Row("trial_end")
            .I("trial", _trialId)
            .F("t", t)
            .S("outcome", outcome)
            .I("blue_alive", Alive(Team.Blue))
            .I("red_alive", Alive(Team.Red))
            .I("blue_losses", _losses[0])
            .I("red_losses", _losses[1])
            .I("blue_shots", blueShots)
            .I("red_shots", _shots[1])
            .I("blue_hits", blueHits)
            .I("red_hits", _hits[1])
            .F("blue_first_shot_t", _firstShotT[0])
            .F("red_first_shot_t", _firstShotT[1])
            .I("enemy_killed", enemyKilled ? 1 : 0)
            .I("first_shot", firstShot ? 1 : 0)
            .F("blue_hit_rate", hitRate)
            .End());
        Flush();
    }

    string OutcomeFromAlive()
    {
        bool blueGone = Alive(Team.Blue) == 0;
        bool redGone = Alive(Team.Red) == 0;
        if (blueGone && redGone) return "both_destroyed";
        if (blueGone) return "blue_destroyed";
        if (redGone) return "red_destroyed";
        return "stopped";
    }

    // ---------------------------------------------------------------- roster

    void RefreshRoster(bool reset)
    {
        if (reset)
        {
            _roster.Clear();
            _byId.Clear();
        }

        var unitOf = new Dictionary<RideID, string>();
        var creator = AGAMOTTOScenarioCreator.Active;
        if (creator != null && creator.SpawnedGroups != null)
        {
            foreach (KeyValuePair<string, RideID> kv in creator.SpawnedGroups)
            {
                try
                {
                    foreach (RideID member in Globals.api.groupSystem.GetMembers(kv.Value))
                        unitOf[member] = kv.Key;
                }
                catch (Exception)
                {
                    // group already destroyed; its agents are picked up by team below
                }
            }
        }

        AddTeam(Team.Blue, unitOf);
        AddTeam(Team.Red, unitOf);
    }

    void AddTeam(Team team, Dictionary<RideID, string> unitOf)
    {
        List<RideID> ids;
        try
        {
            IEnumerable<RideID> found = Globals.api.scenarioSystem.GetAgents(team);
            if (found == null)
                return;
            ids = new List<RideID>(found);
        }
        catch (Exception)
        {
            return;
        }

        foreach (RideID id in ids)
        {
            if (_byId.ContainsKey(id) || !IsLive(id))
                continue;
            unitOf.TryGetValue(id, out string unit);
            var tracked = new Tracked { id = id, team = team, unit = unit };
            _roster.Add(tracked);
            _byId[id] = tracked;
        }
    }

    static bool IsLive(RideID id)
    {
        if (id == RideID.Null)
            return false;
        try
        {
            IAgentSystem agents = Globals.api.agentSystem;
            return agents.AgentExists(id) && !agents.IsAgentDead(id);
        }
        catch (Exception)
        {
            return false;
        }
    }

    int Alive(Team team)
    {
        int n = 0;
        foreach (Tracked a in _roster)
            if (a.alive && a.team == team)
                n++;
        return n;
    }

    void PollDeaths()
    {
        IAgentSystem agents = Globals.api.agentSystem;
        foreach (Tracked a in _roster)
        {
            if (!a.alive)
                continue;
            bool exists;
            bool dead;
            try
            {
                exists = agents.AgentExists(a.id);
                dead = exists && agents.IsAgentDead(a.id);
            }
            catch (Exception)
            {
                exists = false;
                dead = false;
            }

            if (exists && !dead)
                continue;

            a.alive = false;
            _losses[Side(a.team)]++;
            Write(new Row("death")
                .I("trial", _trialId)
                .F("t", TrialTime)
                .I("id", a.id.id)
                .B("blue", a.team == Team.Blue)
                .S("cause", dead ? "killed" : "removed")
                .End());
        }
    }

    // ---------------------------------------------------------------- sampling

    void SamplePass(float t)
    {
        _sampleIndex++;
        foreach (Tracked a in _roster)
        {
            a.sampled = false;
            if (!a.alive || !TryReadAgent(a))
                continue;
            a.sampled = true;
            WriteAgentRow(a, t);
        }

        foreach (Tracked a in _roster)
        {
            if (!a.sampled)
                continue;
            foreach (Tracked b in _roster)
            {
                if (!b.sampled || a.team == b.team)
                    continue;
                WriteDyadRow(a, b, t);
            }
        }

        if (_pending.Count >= FlushEveryLines)
            Flush();
    }

    bool TryReadAgent(Tracked a)
    {
        try
        {
            IAgentSystem agents = Globals.api.agentSystem;
            a.pos = agents.GetAgentPosition(a.id);
            a.view = agents.GetAgentViewPosition(a.id);
            TryTerrainHeight(a.pos.x, a.pos.z, out a.elev);
            a.slopeDeg = SlopeDeg(a.pos.x, a.pos.z);
            a.prom = Prominence(a.pos.x, a.pos.z, a.elev);
            return true;
        }
        catch (Exception ex)
        {
            Warn($"could not read agent {a.id.id}: {ex.Message}");
            return false;
        }
    }

    void WriteAgentRow(Tracked a, float t)
    {
        IAgentSystem agents = Globals.api.agentSystem;
        float dispRate = float.NaN;
        float climbRate = float.NaN;
        if (a.hasLast && t - a.lastT > 1e-3f)
        {
            float dt = t - a.lastT;
            float dx = a.pos.x - a.lastPos.x;
            float dz = a.pos.z - a.lastPos.z;
            dispRate = Mathf.Sqrt(dx * dx + dz * dz) / dt;
            climbRate = (a.pos.y - a.lastPos.y) / dt;
        }
        a.lastPos = a.pos;
        a.lastT = t;
        a.hasLast = true;

        Write(new Row("agent")
            .I("trial", _trialId)
            .F("t", t)
            .I("s", _sampleIndex)
            .I("id", a.id.id)
            .B("blue", a.team == Team.Blue)
            .S("unit", a.unit)
            .F("x", a.pos.x)
            .F("y", a.pos.y)
            .F("z", a.pos.z)
            .F("elev", a.elev)
            .F("slope_deg", a.slopeDeg)
            .F("prom", a.prom)
            .F("speed", Safe(() => agents.GetAgentCurrentSpeed(a.id)))
            .F("max_speed", Safe(() => agents.GetAgentSpeed(a.id)))
            .F("disp_rate", dispRate)
            .F("climb_rate", climbRate)
            .F("health", Safe(() => agents.GetAgentHealth(a.id)))
            .F("suppression", Safe(() => agents.GetAgentSuppression(a.id)))
            .S("posture", PostureName(a.id))
            .End());
    }

    void WriteDyadRow(Tracked a, Tracked b, float t)
    {
        Write(new Row("dyad")
            .I("trial", _trialId)
            .F("t", t)
            .I("s", _sampleIndex)
            .I("a", a.id.id)
            .I("b", b.id.id)
            .B("a_blue", a.team == Team.Blue)
            .F("dist", Vector3.Distance(a.pos, b.pos))
            .F("height_adv", a.pos.y - b.pos.y)
            .F("a_elev", a.elev)
            .F("b_elev", b.elev)
            .F("a_prom", a.prom)
            .F("b_prom", b.prom)
            .B("terrain_los", TerrainLos(a.view, b.view))
            .B("visible", RideVisible(a, b))
            .B("in_range", InRange(a.id, b.id))
            .B("engaged", Engaged(a.id, b.id))
            .End());
    }

    // ---------------------------------------------------------------- shots

    void StartListening()
    {
        if (_listening || Globals.api == null)
            return;
        Globals.api.worldStateSystem.AddListener<AgentAttackedByAgentEvent>(WorldEvent.agentAttackedAgent, OnAgentAttacked);
        _listening = true;
    }

    void StopListening()
    {
        if (!_listening)
            return;
        _listening = false;
        try
        {
            Globals.api?.worldStateSystem?.RemoveListener<AgentAttackedByAgentEvent>(WorldEvent.agentAttackedAgent, OnAgentAttacked);
        }
        catch (Exception)
        {
            // RIDE already torn down
        }
    }

    void OnAgentAttacked(WorldEventMarker marker, AgentAttackedByAgentEvent e)
    {
        if (!_recording || e == null)
            return;
        try
        {
            // RIDE raises this twice for each AI shot: once from HandleWeaponFired with shooter and
            // target swapped and isHit forced true, once from ReviewAttackResults in the right order.
            // Keep the one whose first agent owns the weapon, and take hit from damage.
            RideID shooter = e.agent;
            RideID target = e.attackee;
            bool roleOk = OwnsWeapon(shooter, e.weapon);
            if (!roleOk && OwnsWeapon(target, e.weapon))
                return;

            if (!_byId.TryGetValue(shooter, out Tracked a) || !_byId.TryGetValue(target, out Tracked b))
                return;

            IAgentSystem agents = Globals.api.agentSystem;
            float t = TrialTime;
            bool hit = e.damage > 0f;
            int side = Side(a.team);
            if (roleOk)
            {
                _shots[side]++;
                if (hit) _hits[side]++;
                if (float.IsNaN(_firstShotT[side])) _firstShotT[side] = t;
            }

            bool havePos = TryPosition(shooter, out Vector3 aPos) & TryPosition(target, out Vector3 bPos);
            float maxSpeed = Safe(() => agents.GetAgentSpeed(shooter));
            float relSpeed = maxSpeed > 0f ? Safe(() => agents.GetAgentCurrentSpeed(shooter)) / maxSpeed : 0f;
            string posture = PostureName(target);

            Write(new Row("shot")
                .I("trial", _trialId)
                .F("t", t)
                .I("a", shooter.id)
                .I("b", target.id)
                .B("a_blue", a.team == Team.Blue)
                .B("role_ok", roleOk)
                .F("dist", havePos ? Vector3.Distance(aPos, bPos) : float.NaN)
                .F("height_adv", havePos ? aPos.y - bPos.y : float.NaN)
                .B("visible", RideVisible(a, b, refresh: true))
                .B("in_range", InRange(shooter, target))
                .B("hit", hit)
                .F("damage", e.damage)
                .F("p_hit_sim", Safe(() => Globals.api.attackSystem.CalculateAccuracy(Attack.None, shooter, e.weapon, target)))
                .F("skill", Safe(() => agents.GetAgentSkill(shooter)))
                .F("suppression", Safe(() => agents.GetAgentSuppression(shooter)))
                .F("rel_speed", relSpeed)
                .S("b_posture", posture)
                .F("b_exposure", PostureExposure(posture))
                .F("b_health", Safe(() => agents.GetAgentHealth(target)))
                .F("weapon_range", Safe(() => Globals.api.equipmentSystem.GetWeapon(e.weapon).range))
                .End());
        }
        catch (Exception ex)
        {
            Warn("shot row failed: " + ex.Message);
        }
    }

    static bool OwnsWeapon(RideID agent, RideID weapon)
    {
        try
        {
            IWeapon w = Globals.api.equipmentSystem.GetWeapon(weapon);
            if (w != null && w.owner == agent)
                return true;
        }
        catch (Exception)
        {
            // fall through to the primary-weapon test
        }

        try
        {
            IAgentSystem agents = Globals.api.agentSystem;
            return agents.AgentExists(agent) && agents.GetPrimaryWeapon(agent) == weapon;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- RIDE readouts

    /// <summary>The same occlusion test RIDE uses to acquire and to drop a target.</summary>
    bool? RideVisible(Tracked a, Tracked b, bool refresh = false)
    {
        try
        {
            IAgentSystem agents = Globals.api.agentSystem;
            IComponentManipulator manipulator = agents.GetComponentManipulator(b.id);
            ISpatialObject spatial = manipulator != null ? manipulator.GetComponent<ISpatialObject>() : null;
            if (spatial == null)
                return null;
            RideVector3 viewer = refresh ? agents.GetAgentViewPosition(a.id) : (RideVector3)a.view;
            RideVector3 eyeOffset = refresh
                ? agents.GetAgentViewPosition(b.id) - agents.GetAgentPosition(b.id)
                : (RideVector3)(b.view - b.pos);
            return !RideUtils.IsObjectOccluded(viewer, spatial, eyeOffset);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Eye-to-eye sight line against the terrain layer only (no trees, buildings or bodies).</summary>
    static bool? TerrainLos(Vector3 from, Vector3 to)
    {
        try
        {
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.5f)
                return true;
            Vector3 dir = delta / dist;
            var ray = new RideRay(new RideVector3(from.x, from.y, from.z), new RideVector3(dir.x, dir.y, dir.z));
            if (!Globals.api.terrainSystem.RaycastTerrain(ray, out RideRaycastHit hit) || !hit.isHit)
                return true;
            return hit.hitInfo.distance >= dist - LosClearEpsilonM;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static bool? InRange(RideID a, RideID b)
    {
        try
        {
            RideID weapon = Globals.api.agentSystem.GetPrimaryWeapon(a);
            return Globals.api.attackSystem.IsInAttackRange(a, b, weapon);
        }
        catch (Exception)
        {
            return null;
        }
    }

    bool? Engaged(RideID a, RideID b)
    {
        if (_engagements == null)
            return null;
        try
        {
            return _engagements.Exists(a, b, RideID.Null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static bool TryPosition(RideID id, out Vector3 pos)
    {
        try
        {
            pos = Globals.api.agentSystem.GetAgentPosition(id);
            return true;
        }
        catch (Exception)
        {
            pos = default;
            return false;
        }
    }

    static string PostureName(RideID id)
    {
        try
        {
            return Globals.api.agentSystem.GetAgentPosture(id).ToString().ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Target-posture factor in BasicAttackCalculator: stand 1, crouch 0.25, prone 0.1.</summary>
    static float PostureExposure(string posture)
    {
        if (posture == null) return float.NaN;
        if (posture == "crouch") return 0.25f;
        if (posture == "prone") return 0.1f;
        return 1f;
    }

    static float WeaponRange(Tracked a) =>
        Safe(() => Globals.api.equipmentSystem.GetWeapon(Globals.api.agentSystem.GetPrimaryWeapon(a.id)).range);

    static float AgentRange(Tracked a) => Safe(() => Globals.api.agentSystem.GetAgentRange(a.id));

    static float Safe(Func<float> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return float.NaN;
        }
    }

    // ---------------------------------------------------------------- terrain

    static bool TryTerrainHeight(float x, float z, out float height)
    {
        height = float.NaN;
        try
        {
            var ray = new RideRay(new RideVector3(x, 10000f, z), RideVector3.down);
            if (!Globals.api.terrainSystem.RaycastTerrain(ray, out RideRaycastHit hit) || !hit.isHit)
                return false;
            height = hit.hitInfo.point.y;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    float SlopeDeg(float x, float z)
    {
        float d = Mathf.Max(0.5f, slopeStencilM);
        if (!TryTerrainHeight(x + d, z, out float hx1) || !TryTerrainHeight(x - d, z, out float hx0)
            || !TryTerrainHeight(x, z + d, out float hz1) || !TryTerrainHeight(x, z - d, out float hz0))
            return float.NaN;
        float gx = (hx1 - hx0) / (2f * d);
        float gz = (hz1 - hz0) / (2f * d);
        return Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;
    }

    float Prominence(float x, float z, float elev)
    {
        if (float.IsNaN(elev))
            return float.NaN;
        float r = Mathf.Max(1f, prominenceRadiusM);
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.PI / 4f;
            if (TryTerrainHeight(x + r * Mathf.Cos(angle), z + r * Mathf.Sin(angle), out float h))
            {
                sum += h;
                n++;
            }
        }
        return n > 0 ? elev - sum / n : float.NaN;
    }

    bool TryCentroid(Team team, string unit, out Vector3 centroid)
    {
        centroid = Vector3.zero;
        int n = 0;
        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != team)
                continue;
            if (unit != null && a.unit != unit)
                continue;
            if (!TryPosition(a.id, out Vector3 p))
                continue;
            centroid += p;
            n++;
        }
        if (n == 0)
            return false;
        centroid /= n;
        return true;
    }

    float MeanOver(Team team, Func<Tracked, float> value)
    {
        float sum = 0f;
        int n = 0;
        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != team)
                continue;
            float v = value(a);
            if (float.IsNaN(v))
                continue;
            sum += v;
            n++;
        }
        return n > 0 ? sum / n : float.NaN;
    }

    float FirstOver(Team team, Func<Tracked, float> value)
    {
        foreach (Tracked a in _roster)
        {
            if (!a.alive || a.team != team)
                continue;
            float v = value(a);
            if (!float.IsNaN(v))
                return v;
        }
        return float.NaN;
    }

    static int Side(Team team) => team == Team.Blue ? 0 : 1;

    static bool IsBlueCot(string cotType) => CotAffiliation(cotType) == 'f';
    static bool IsRedCot(string cotType) => CotAffiliation(cotType) == 'h';

    static char CotAffiliation(string cotType)
    {
        if (string.IsNullOrEmpty(cotType))
            return '?';
        string[] segments = cotType.Split('-');
        return segments.Length >= 2 && segments[0] == "a" && segments[1].Length > 0 ? segments[1][0] : '?';
    }

    // ---------------------------------------------------------------- output

    void EnsureLog()
    {
        if (_logPath != null)
            return;
        _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        // Outside Assets so Unity does not import every log.
        string root = Directory.GetParent(Application.dataPath).FullName;
        string dir = Path.Combine(root, "CausalLogs");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, $"causal_{_sessionId}.jsonl");
        Debug.Log("AGAMOTTO_CausalRecorder: logging to " + _logPath);
    }

    void Write(string line) => _pending.Add(line);

    void Flush()
    {
        if (_pending.Count == 0 || _logPath == null)
            return;
        try
        {
            File.AppendAllLines(_logPath, _pending, new UTF8Encoding(false));
            _pending.Clear();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("AGAMOTTO_CausalRecorder: could not write log: " + ex.Message);
        }
    }

    void Warn(string what)
    {
        if (_warnings++ < 10)
            Debug.LogWarning("AGAMOTTO_CausalRecorder: " + what);
    }

    sealed class Row
    {
        readonly StringBuilder _sb = new StringBuilder(256);
        bool _first = true;

        public Row(string kind)
        {
            _sb.Append('{');
            if (kind != null)
                S("kind", kind);
        }

        void Key(string key)
        {
            if (!_first)
                _sb.Append(',');
            _first = false;
            _sb.Append('"').Append(key).Append("\":");
        }

        public Row S(string key, string value)
        {
            Key(key);
            if (value == null)
                _sb.Append("null");
            else
                _sb.Append('"').Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            return this;
        }

        public Row I(string key, int value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Row F(string key, float value)
        {
            Key(key);
            if (float.IsNaN(value) || float.IsInfinity(value))
                _sb.Append("null");
            else
                _sb.Append(value.ToString("0.####", CultureInfo.InvariantCulture));
            return this;
        }

        public Row B(string key, bool? value)
        {
            Key(key);
            _sb.Append(value.HasValue ? (value.Value ? "1" : "0") : "null");
            return this;
        }

        public Row Raw(string key, string json)
        {
            Key(key);
            _sb.Append(json);
            return this;
        }

        public string End()
        {
            _sb.Append('}');
            return _sb.ToString();
        }
    }

    // ---------------------------------------------------------------- panel

    void OnGUI()
    {
        Event current = Event.current;
        if (current.type == EventType.KeyDown && current.keyCode == KeyCode.F8)
        {
            showPanel = !showPanel;
            current.Use();
        }

        if (!showPanel)
            return;

        const float width = 380f;
        const float height = 310f;
        GUILayout.BeginArea(new Rect(10f, Screen.height - height - 10f, width, height), GUI.skin.box);
        GUILayout.Label("Causal recorder (F8 hides)");

        if (_recording)
        {
            string what = _passive ? "Recording current units" : $"Randomized trial {_trialsDone + 1}/{Mathf.Max(1, trialCount)}";
            GUILayout.Label($"{what}  t={TrialTime:0.0}s");
            GUILayout.Label(
                $"Blue {Alive(Team.Blue)} alive, {_shots[0]} shots, {_hits[0]} hits   "
                + $"Red {Alive(Team.Red)} alive, {_shots[1]} shots, {_hits[1]} hits");
        }
        else
        {
            GUILayout.Label(_trialsRunning ? (_message ?? "Respawning for the next trial...") : "Not recording.");
        }

        bool busy = _recording || _trialsRunning;
        GUI.enabled = !busy;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Trials", GUILayout.Width(40f));
        _trialCountText = GUILayout.TextField(_trialCountText, GUILayout.Width(40f));
        GUILayout.Label(place == PlaceMode.CoverArea ? "Pad m" : "Radius m", GUILayout.Width(58f));
        _radiusText = GUILayout.TextField(_radiusText, GUILayout.Width(40f));
        GUILayout.Label("Max s", GUILayout.Width(40f));
        _maxSecondsText = GUILayout.TextField(_maxSecondsText, GUILayout.Width(40f));
        GUILayout.Label("Seed", GUILayout.Width(34f));
        _seedText = GUILayout.TextField(_seedText, GUILayout.Width(40f));
        GUILayout.Label("Rate", GUILayout.Width(34f));
        _rateText = GUILayout.TextField(_rateText, GUILayout.Width(36f));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Place", GUILayout.Width(40f));
        place = (PlaceMode)GUILayout.Toolbar((int)place, new[] { "Near pins", "Cover area" });
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Move", GUILayout.Width(40f));
        movement = (MovementMode)GUILayout.Toolbar((int)movement, new[] { "Hold", "Blue advances" });
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Vary", GUILayout.Width(40f));
        randomize = (RandomizeSide)GUILayout.Toolbar((int)randomize, new[] { "Both", "Blue only", "Red only" });
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Run randomized trials"))
        {
            ApplyPanelText();
            StartRandomizedTrials();
        }
        if (GUILayout.Button("Record current units"))
        {
            ApplyPanelText();
            StartObservational();
        }
        GUILayout.EndHorizontal();
        GUI.enabled = true;

        if (busy && GUILayout.Button(_trialsRunning ? "Abort trials" : "Stop recording"))
        {
            if (_trialsRunning) AbortTrials();
            else StopObservational();
        }

        GUILayout.Label(_message);
        if (_logPath != null)
            GUILayout.Label("Log: CausalLogs/" + Path.GetFileName(_logPath));
        GUILayout.EndArea();
    }

    void ApplyPanelText()
    {
        if (int.TryParse(_trialCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            trialCount = Mathf.Clamp(n, 1, 10000);
        if (float.TryParse(_radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
            offsetRadiusM = Mathf.Clamp(r, 0f, 2000f);
        if (float.TryParse(_maxSecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
            maxTrialSeconds = Mathf.Clamp(s, 1f, 3600f);
        if (int.TryParse(_seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            baseSeed = seed;
        if (float.TryParse(_rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out float rate))
            trialTimeScale = Mathf.Clamp(rate, 0.25f, 20f);
        _trialCountText = trialCount.ToString(CultureInfo.InvariantCulture);
        _radiusText = offsetRadiusM.ToString("0.#", CultureInfo.InvariantCulture);
        _maxSecondsText = maxTrialSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        _rateText = trialTimeScale.ToString("0.#", CultureInfo.InvariantCulture);
        _seedText = baseSeed.ToString(CultureInfo.InvariantCulture);
    }
}
