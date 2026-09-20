using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MiniMetroExtremePlanner
{
    public static class Bootstrap
    {
        private const string ExtremeEnabledPreference = "MiniMetro.ExtremeAutoPlanner.Enabled";
        private const string ClassicEnabledPreference = "MiniMetro.ClassicAutoPlanner.Enabled";
        private static readonly FieldInfo ScheduledScreenField = typeof(Game).GetField(
            "scheduledScreen",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool initialized;
        private static Game currentGame;
        private static float actionTimer;
        private static bool inTick;
        private static City observedCity;
        private static bool gameOverLogged;
        private static float nextWaitLogAt;
        private static string status = "Waiting for Extreme mode";
        private static string lastAction = "Idle";
        private static string objective = "Extreme mode planner";
        private static int actionCount;

        public static bool ExtremeEnabled { get; private set; }
        public static bool ClassicEnabled { get; private set; }
        public static Game CurrentGame { get { return currentGame; } }
        public static string Status { get { return status; } }
        public static string LastAction { get { return lastAction; } }
        public static string Objective { get { return objective; } }
        public static int ActionCount { get { return actionCount; } }

        public static void Tick(Game game, float deltaTime)
        {
            EnsureInitialized();
            currentGame = game;
            ObserveSession(game);

            if (game == null || (game.Mode != GameMode.EXTREME && game.Mode != GameMode.CLASSIC))
            {
                status = "Waiting for Classic or Extreme mode";
                objective = "Automatic network planning";
                actionTimer = 0f;
                return;
            }

            UnlockGoal unlockGoal = null;
            if (game.Mode == GameMode.CLASSIC && game.IsDailyChallenge)
            {
                objective = "Daily Challenge · high-score policy";
            }
            else if (game.Mode == GameMode.CLASSIC && game.City != null && game.City.Definition != null)
            {
                unlockGoal = UnlockInspector.GetGoal(game);
                objective = unlockGoal != null
                    ? unlockGoal.Describe(game)
                    : "EXTREME unlocked · high-score policy";
            }
            else
            {
                objective = "Extreme high-score policy";
            }

            if (!IsEnabled(game.Mode))
            {
                status = "Paused by player";
                return;
            }

            if (inTick)
            {
                return;
            }

            inTick = true;
            try
            {
                if (HandlePendingAwards(game))
                {
                    return;
                }

                if (!CanPlan(game))
                {
                    status = DescribeWait(game);
                    LogWaitState(game);
                    return;
                }

                actionTimer -= Mathf.Max(0f, deltaTime);
                if (actionTimer > 0f)
                {
                    return;
                }

                actionTimer = 0.70f;
                string action;
                if (Planner.TryAct(game, unlockGoal, out action))
                {
                    lastAction = action;
                    status = unlockGoal != null ? "Unlock-first planning" : "High-score planning";
                    actionCount++;
                    Planner.LogAction(game, action);
                }
                else
                {
                    status = "Network balanced";
                }
            }
            catch (Exception exception)
            {
                status = "Recovered: " + exception.GetType().Name;
                lastAction = Shorten(exception.Message, 70);
                actionTimer = 2.0f;
                SafeCancel(game);
                UnityEngine.Debug.LogWarning("[Extreme Auto Planner] " + exception);
            }
            finally
            {
                inTick = false;
            }
        }

        public static bool IsEnabled(GameMode mode)
        {
            if (mode == GameMode.CLASSIC) return ClassicEnabled;
            if (mode == GameMode.EXTREME) return ExtremeEnabled;
            return false;
        }

        public static void SetEnabled(GameMode mode, bool value)
        {
            if (mode == GameMode.CLASSIC)
            {
                ClassicEnabled = value;
                PlayerPrefs.SetInt(ClassicEnabledPreference, value ? 1 : 0);
            }
            else if (mode == GameMode.EXTREME)
            {
                ExtremeEnabled = value;
                PlayerPrefs.SetInt(ExtremeEnabledPreference, value ? 1 : 0);
            }
            PlayerPrefs.Save();
            status = value ? "Running" : "Paused by player";
            lastAction = value ? "Planner enabled" : "Planner disabled";
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            ExtremeEnabled = PlayerPrefs.GetInt(ExtremeEnabledPreference, 1) != 0;
            ClassicEnabled = PlayerPrefs.GetInt(ClassicEnabledPreference, 1) != 0;
            GameObject plannerObject = new GameObject("Mini Metro Extreme Auto Planner");
            plannerObject.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(plannerObject);
            plannerObject.AddComponent<PlannerOverlay>();
        }

        private static bool CanPlan(Game game)
        {
            return game.City != null
                && !game.IsOver
                && !game.IsPaused
                && !game.IsLocked
                && game.Screen == GameScreen.None
                && !game.IsScreenChangeScheduled
                && game.LineBuilder != null
                && !game.LineBuilder.IsBuilding
                && game.AssetBuilder != null
                && !game.AssetBuilder.IsBuilding;
        }

        private static void ObserveSession(Game game)
        {
            if (game == null || game.City == null)
            {
                return;
            }

            if (!System.Object.ReferenceEquals(observedCity, game.City))
            {
                observedCity = game.City;
                gameOverLogged = false;
                nextWaitLogAt = 0f;
                actionCount = 0;
                lastAction = "Session started";
                Planner.ResetSession(game);
            }

            if (game.IsOver && !gameOverLogged)
            {
                gameOverLogged = true;
                Planner.LogSessionEnd(game, actionCount);
            }
        }

        private static string DescribeWait(Game game)
        {
            if (game.IsOver) return "Game over";
            if (game.IsPaused) return "Waiting: game paused";
            if (game.Screen != GameScreen.None || game.IsScreenChangeScheduled) return "Waiting: game screen";
            if (game.IsLocked) return "Waiting: game locked";
            if (game.LineBuilder != null && game.LineBuilder.IsBuilding) return "Waiting: line edit";
            if (game.AssetBuilder != null && game.AssetBuilder.IsBuilding) return "Waiting: asset edit";
            return "Waiting for city";
        }

        private static void LogWaitState(Game game)
        {
            float now = Time.unscaledTime;
            if (now < nextWaitLogAt) return;
            nextWaitLogAt = now + 10f;
            UnityEngine.Debug.Log(
                "[Auto Planner][Wait] over=" + game.IsOver
                + " paused=" + game.IsPaused
                + " locked=" + game.IsLocked
                + " screen=" + game.Screen
                + " scheduledScreen=" + game.IsScreenChangeScheduled
                + " lineBuilder=" + (game.LineBuilder != null)
                + " lineBuilding=" + (game.LineBuilder != null && game.LineBuilder.IsBuilding)
                + " assetBuilder=" + (game.AssetBuilder != null)
                + " assetBuilding=" + (game.AssetBuilder != null && game.AssetBuilder.IsBuilding));
        }

        private static bool HandlePendingAwards(Game game)
        {
            int pending = game.PendingAssetCount;
            if (pending <= 0)
            {
                return false;
            }

            int locomotiveChoices = Mathf.Clamp(game.PendingLocomotiveCount, 0, 12);
            int assetChoices = Mathf.Clamp(pending, 0, 12);
            int granted = 0;
            List<string> selections = new List<string>();

            for (int i = 0; i < locomotiveChoices; i++)
            {
                UpgradeDefinition upgrade = Planner.SelectUpgrade(game, true);
                if (upgrade != null)
                {
                    int count = GrantUpgrade(game, upgrade);
                    granted += count;
                    if (count > 0) selections.Add(upgrade.Type + " x" + count);
                }
            }

            for (int i = 0; i < assetChoices; i++)
            {
                int groupCount = Planner.GetUpgradeGroupCount(game.City.Definition);
                int assetGroupCount = Mathf.Max(1, groupCount - 1);
                int group = 1 + (i % assetGroupCount);
                UpgradeDefinition upgrade = Planner.SelectUpgrade(game, false, group);
                if (upgrade != null)
                {
                    int count = GrantUpgrade(game, upgrade);
                    granted += count;
                    if (count > 0) selections.Add(upgrade.Type + " x" + count);
                }
            }

            bool assetScreenWasOpen = game.Screen == GameScreen.NewAsset;
            game.PendingAssetCount = 0;
            ClearScheduledAssetScreen(game);
            if (assetScreenWasOpen)
            {
                game.Screen = GameScreen.None;
            }
            else
            {
                game.StartWeek();
            }

            lastAction = "Weekly resources: "
                + (selections.Count > 0 ? String.Join(", ", selections.ToArray()) : "none")
                + " (" + granted + ")";
            status = "Running";
            actionCount++;
            Planner.LogAction(game, lastAction);
            actionTimer = 0.25f;
            return true;
        }

        private static int GrantUpgrade(Game game, UpgradeDefinition upgrade)
        {
            int count = Mathf.Max(1, upgrade.Count);
            int granted = 0;
            for (int i = 0; i < count; i++)
            {
                if (game.City.UnlockUpgrade(0, upgrade.Type))
                {
                    granted++;
                }
            }
            return granted;
        }

        private static void ClearScheduledAssetScreen(Game game)
        {
            if (ScheduledScreenField == null)
            {
                return;
            }

            object value = ScheduledScreenField.GetValue(game);
            if (value is GameScreen && (GameScreen)value == GameScreen.NewAsset)
            {
                ScheduledScreenField.SetValue(game, GameScreen.None);
            }
        }

        private static void SafeCancel(Game game)
        {
            try
            {
                Planner.ResetBrokenBuilder(game);
            }
            catch
            {
            }
        }

        private static string Shorten(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "Unknown error";
            return value.Length <= maximum ? value : value.Substring(0, maximum - 3) + "...";
        }
    }

    public sealed class PlannerOverlay : MonoBehaviour
    {
        private void Update()
        {
            Game game = Bootstrap.CurrentGame;
            if (game != null
                && (game.Mode == GameMode.EXTREME || game.Mode == GameMode.CLASSIC)
                && Input.GetKeyDown(KeyCode.F8))
            {
                Bootstrap.SetEnabled(game.Mode, !Bootstrap.IsEnabled(game.Mode));
            }
        }

        private void OnGUI()
        {
            Game game = Bootstrap.CurrentGame;
            if (game == null
                || (game.Mode != GameMode.EXTREME && game.Mode != GameMode.CLASSIC)
                || game.IsOver)
            {
                return;
            }

            float width = 264f;
            float height = 92f;
            Rect panel = new Rect(Mathf.Max(8f, UnityEngine.Screen.width - width - 16f), 16f, width, height);
            Rect toggle = new Rect(panel.x + 166f, panel.y + 6f, 86f, 24f);
            Color oldColor = GUI.color;
            GUI.color = new Color(0.04f, 0.05f, 0.06f, 0.90f);
            GUI.Box(panel, String.Empty);
            GUI.color = Color.white;

            string modeTitle = game.Mode == GameMode.CLASSIC
                ? "CLASSIC AI / 普通自动"
                : "EXTREME AI / 极限自动";
            bool enabled = Bootstrap.IsEnabled(game.Mode);
            GUI.Label(new Rect(panel.x + 12f, panel.y + 7f, 150f, 22f), modeTitle);
            GUI.color = enabled
                ? new Color(0.25f, 1f, 0.55f, 1f)
                : new Color(1f, 0.55f, 0.45f, 1f);
            GUI.Box(toggle, enabled ? "ON  开" : "OFF  关");
            GUI.color = Color.white;

            Event guiEvent = Event.current;
            if (guiEvent != null
                && guiEvent.type == EventType.MouseUp
                && toggle.Contains(guiEvent.mousePosition))
            {
                Bootstrap.SetEnabled(game.Mode, !enabled);
                guiEvent.Use();
            }

            string city = game.City != null && game.City.Definition != null
                ? game.City.Definition.Id
                : "-";
            GUI.Label(new Rect(panel.x + 12f, panel.y + 34f, width - 24f, 20f),
                "City: " + city + "    Score: " + game.Score + "    [F8]");
            GUI.Label(new Rect(panel.x + 12f, panel.y + 55f, width - 24f, 18f),
                Bootstrap.Objective);
            GUI.Label(new Rect(panel.x + 12f, panel.y + 72f, width - 24f, 17f),
                Bootstrap.Status + " · " + Bootstrap.LastAction);
            GUI.color = oldColor;
        }
    }

    internal sealed class UnlockGoal
    {
        public AchievementDefinition Definition;

        public string Describe(Game game)
        {
            if (Definition == null)
            {
                return "Unlocking EXTREME · requirement pending";
            }

            string progress;
            if (Definition.Type == AchievementType.Score)
            {
                progress = "score " + game.Score + "/" + Definition.Score;
            }
            else if (Definition.Type == AchievementType.Day)
            {
                int day = game.City != null && game.City.Clock != null ? game.City.Clock.Day : 0;
                progress = "day " + day + "/" + Definition.Day;
            }
            else
            {
                progress = "city challenge";
            }

            if (Definition.HasRestrictions)
            {
                progress += " · preserving restrictions";
            }
            return "Unlock EXTREME · " + progress;
        }
    }

    internal static class UnlockInspector
    {
        private static readonly FieldInfo RequiredAchievementsField = typeof(CityDefinition).GetField(
            "requiredAchievements",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static UnlockGoal GetGoal(Game game)
        {
            if (game == null || game.City == null || game.City.Definition == null)
            {
                return null;
            }

            CityDefinition city = game.City.Definition;
            try
            {
                if (city.IsUnlocked(GameMode.EXTREME))
                {
                    return null;
                }

                List<List<string>> requirements = RequiredAchievementsField != null
                    ? RequiredAchievementsField.GetValue(city) as List<List<string>>
                    : null;
                int modeIndex = (int)GameMode.EXTREME;
                if (requirements == null || modeIndex < 0 || modeIndex >= requirements.Count)
                {
                    return new UnlockGoal();
                }

                AchievementDefinition best = null;
                float bestCost = Single.MaxValue;
                List<string> ids = requirements[modeIndex];
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (Profile.Instance != null && Profile.Instance.IsAchievementCompleted(id, false))
                    {
                        continue;
                    }

                    AchievementDefinition candidate = AchievementDatabase.Instance[id];
                    if (candidate == null) continue;
                    float cost = GoalCost(city, candidate);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }
                return new UnlockGoal { Definition = best };
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Auto Planner] Unlock inspection recovered: " + exception.Message);
                return new UnlockGoal();
            }
        }

        public static bool Allows(
            Game game,
            UnlockGoal goal,
            RestrictionType type,
            Line line,
            int amount)
        {
            if (goal == null || goal.Definition == null || !goal.Definition.HasRestrictions)
            {
                return true;
            }

            for (int i = 0; i < goal.Definition.RestrictionCount; i++)
            {
                AchievementRestrictionDefinition restriction = goal.Definition.GetRestriction(i);
                if (restriction == null || restriction.Type != type || restriction.Maximum < 0)
                {
                    continue;
                }

                int current = CurrentRestrictedCount(game, restriction.Type, restriction.Scope, line);
                if (current + amount > restriction.Maximum)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool AllowsReallocation(
            Game game,
            UnlockGoal goal,
            RestrictionType type,
            Line target)
        {
            if (goal == null || goal.Definition == null || !goal.Definition.HasRestrictions)
            {
                return true;
            }

            for (int i = 0; i < goal.Definition.RestrictionCount; i++)
            {
                AchievementRestrictionDefinition restriction = goal.Definition.GetRestriction(i);
                if (restriction == null || restriction.Type != type || restriction.Maximum < 0)
                {
                    continue;
                }

                // Moving an existing asset does not increase a city-wide total. A per-line
                // restriction still has to be checked against the receiving line.
                if (restriction.Scope != RestrictionScope.Line) continue;
                int current = CurrentRestrictedCount(game, restriction.Type, restriction.Scope, target);
                if (current + 1 > restriction.Maximum) return false;
            }
            return true;
        }

        private static float GoalCost(CityDefinition city, AchievementDefinition candidate)
        {
            float cost = candidate.HasRestrictions ? 100000f + candidate.RestrictionCount * 1000f : 0f;
            bool sameCity = candidate.CityId == city.Id
                || (!String.IsNullOrEmpty(city.BaseCityId) && candidate.CityId == city.BaseCityId);
            if (!sameCity) cost += 1000000f;
            if (candidate.Type == AchievementType.Score) cost += Mathf.Max(0, candidate.Score);
            else if (candidate.Type == AchievementType.Day) cost += Mathf.Max(0, candidate.Day) * 100f;
            else cost += 500000f;
            return cost;
        }

        private static int CurrentRestrictedCount(
            Game game,
            RestrictionType type,
            RestrictionScope scope,
            Line line)
        {
            if (scope == RestrictionScope.Line && line != null)
            {
                if (type == RestrictionType.Carriage) return line.CarriageCount;
                if (type == RestrictionType.Locomotive) return line.TrainCount;
                if (type == RestrictionType.Station) return line.LiveLinkCount + 1;
                if (type == RestrictionType.Loop) return line.IsLooping ? 1 : 0;
            }

            if (type == RestrictionType.Line) return game.City.LineCount;
            if (type == RestrictionType.Station) return game.City.StationCount;

            int count = 0;
            if (type == RestrictionType.Hub)
            {
                for (int i = 0; i < game.City.StationCount; i++)
                {
                    Station station = game.City.GetStation(i);
                    if (station != null && station.IsInterchange) count++;
                }
                return count;
            }

            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line candidate = game.City.GetLine(i);
                if (candidate == null) continue;
                if (type == RestrictionType.Carriage) count += candidate.CarriageCount;
                else if (type == RestrictionType.Locomotive) count += candidate.TrainCount;
                else if (type == RestrictionType.Loop && candidate.IsLooping) count++;
            }
            return count;
        }
    }

    internal static class Planner
    {
        private sealed class BlockedConnection
        {
            public Station From;
            public Station To;
            public float RetryAfter;
            public float DemandUntil;
            public bool Essential;
        }

        private static readonly FieldInfo BuilderLineField = typeof(LineBuilder).GetField(
            "line", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderLinksField = typeof(LineBuilder).GetField(
            "links", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderPrefixLinksField = typeof(LineBuilder).GetField(
            "prefixLinks", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderFocusField = typeof(LineBuilder).GetField(
            "focus", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderLoopBreakField = typeof(LineBuilder).GetField(
            "loopBreak", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderModifyField = typeof(LineBuilder).GetField(
            "<LinkModify>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo BuilderAllocatedLinesField = typeof(LineBuilder).GetField(
            "allocatedLineIndices", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo HideStationHighlightsMethod = typeof(LineBuilder).GetMethod(
            "HideStationHighlights", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StationInterchangeField = typeof(Station).GetField(
            "isInterchange", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo UpdatePeepContainerMethod = typeof(Station).GetMethod(
            "UpdatePeepContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo RecalculatePeepAlphasMethod = typeof(Station).GetMethod(
            "RecalculatePeepAlphas", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo TrainCurrentStationProperty = typeof(Train).GetProperty(
            "CurrentStation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo TrainNextStationProperty = typeof(Train).GetProperty(
            "NextStation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private sealed class LineMonitor
        {
            public Line Line;
            public int StationCount;
            public int TrainCount;
            public int CarriageCount;
            public int PassengerCount;
            public int SeatCapacity;
            public int CrossingLinks;
            public int OccupiedCrossings;
            public float Length;
            public float LoadRatio;
            public float SpeedRatio;
            public float CycleTime;
            public float Headway;
            public float OperationalPenalty;
        }

        private sealed class StationMonitor
        {
            public Station Station;
            public float QueuePressure;
            public float Risk;
            public float ServiceEta;
            public float Centrality;
            public float Service;
        }

        private sealed class EndpointCandidate
        {
            public Line Line;
            public Terminator Terminator;
            public Station Endpoint;
            public Station Target;
            public float Cost;
        }

        private sealed class TransferCandidate
        {
            public Line Source;
            public Line Other;
            public Terminator Terminator;
            public Station Endpoint;
            public Station Target;
            public float Benefit;
            public int ExistingTransfers;
            public bool ConnectsComponents;
        }

        private sealed class TransferEvaluation
        {
            public Line Source;
            public Line Other;
            public Station Station;
            public float EstimatedBenefit;
            public float PressureBefore;
            public float EvaluateAfter;
            public bool ConnectedComponents;
        }

        private sealed class AssetReallocationCandidate
        {
            public Line Source;
            public Line Target;
            public Railcar Asset;
            public AssetType Type;
            public float Benefit;
            public float SourcePressure;
            public float TargetPressure;
        }

        private sealed class AssetReallocationEvaluation
        {
            public Line Source;
            public Line Target;
            public Railcar MovedAsset;
            public AssetType Type;
            public float Benefit;
            public float SourcePressureBefore;
            public float TargetPressureBefore;
            public float EvaluateAfter;
        }

        private static readonly AssetType[] TrainTypes =
        {
            AssetType.Locomotive,
            AssetType.Shinkansen,
            AssetType.Tram,
            AssetType.Ferry
        };

        private static readonly List<BlockedConnection> BlockedConnections = new List<BlockedConnection>();
        private static readonly List<TransferEvaluation> PendingTransferEvaluations = new List<TransferEvaluation>();
        private static readonly List<AssetReallocationEvaluation> PendingAssetEvaluations =
            new List<AssetReallocationEvaluation>();
        private static readonly Dictionary<Line, LineMonitor> LineMonitors =
            new Dictionary<Line, LineMonitor>();
        private static readonly Dictionary<Station, StationMonitor> StationMonitors =
            new Dictionary<Station, StationMonitor>();
        private static readonly Dictionary<Station, float> InterchangeLowSince =
            new Dictionary<Station, float>();
        private static Game sessionGame;
        private static City sessionCity;
        private static int crossingFailures;
        private static int successfulLines;
        private static int successfulExtensions;
        private static int capacityActions;
        private static int interchangeActions;
        private static int transferActions;
        private static int replanActions;
        private static int reallocationActions;
        private static int reallocationRollbacks;
        private static float transferEffectBias;
        private static float nextTransferAt;
        private static float trainReallocationBias;
        private static float carriageReallocationBias;
        private static float nextReplanAt;
        private static float nextConsolidationAt;
        private static float nextAssetReallocationAt;
        private static Line lastAssetSource;
        private static Line lastAssetTarget;
        private static AssetType lastAssetType;
        private static float reverseAssetBlockUntil;
        private static bool waitingForCrossingResource;
        private static int crossingInventoryAtBlock;
        private static float nextInterchangeActionAt;
        private static Station lastInterchangeFrom;
        private static Station lastInterchangeTo;
        private static float interchangeReverseBlockUntil;
        private static float nextTelemetryAt;

        public static void ResetSession(Game game)
        {
            sessionGame = game;
            sessionCity = game != null ? game.City : null;
            crossingFailures = 0;
            successfulLines = 0;
            successfulExtensions = 0;
            capacityActions = 0;
            interchangeActions = 0;
            transferActions = 0;
            replanActions = 0;
            reallocationActions = 0;
            reallocationRollbacks = 0;
            transferEffectBias = 0f;
            nextTransferAt = 0f;
            trainReallocationBias = 0f;
            carriageReallocationBias = 0f;
            nextReplanAt = 0f;
            nextConsolidationAt = 0f;
            nextAssetReallocationAt = 0f;
            lastAssetSource = null;
            lastAssetTarget = null;
            lastAssetType = AssetType.None;
            reverseAssetBlockUntil = 0f;
            waitingForCrossingResource = false;
            crossingInventoryAtBlock = -1;
            nextInterchangeActionAt = 0f;
            lastInterchangeFrom = null;
            lastInterchangeTo = null;
            interchangeReverseBlockUntil = 0f;
            nextTelemetryAt = 0f;
            BlockedConnections.Clear();
            PendingTransferEvaluations.Clear();
            PendingAssetEvaluations.Clear();
            LineMonitors.Clear();
            StationMonitors.Clear();
            InterchangeLowSince.Clear();
            VisionControlBridge.Reset();
            if (game != null)
            {
                UnityEngine.Debug.Log("[Auto Planner][Session] START " + Snapshot(game));
            }
        }

        public static void LogAction(Game game, string action)
        {
            EnsureSession(game);
            if (!String.IsNullOrEmpty(action))
            {
                if (action.StartsWith("Built line relief", StringComparison.Ordinal))
                {
                    successfulLines++;
                    replanActions++;
                }
                else if (action.StartsWith("Built line", StringComparison.Ordinal)) successfulLines++;
                else if (action.StartsWith("Extended line", StringComparison.Ordinal)) successfulExtensions++;
                else if (action.StartsWith("Added transfer", StringComparison.Ordinal)) transferActions++;
                else if (action.StartsWith("Removed redundant", StringComparison.Ordinal)) replanActions++;
                else if (action.StartsWith("Rehomed", StringComparison.Ordinal)) replanActions++;
                else if (action.StartsWith("Reallocated", StringComparison.Ordinal)) reallocationActions++;
                else if (action.StartsWith("Rolled back", StringComparison.Ordinal))
                {
                    reallocationActions++;
                    reallocationRollbacks++;
                }
                else if (action.IndexOf("carriage", StringComparison.OrdinalIgnoreCase) >= 0
                    || action.IndexOf("train", StringComparison.OrdinalIgnoreCase) >= 0) capacityActions++;
                else if (action.IndexOf("interchange", StringComparison.OrdinalIgnoreCase) >= 0) interchangeActions++;
            }
            UnityEngine.Debug.Log("[Auto Planner][Action] " + action + " | " + Snapshot(game));
        }

        public static void LogSessionEnd(Game game, int actionCount)
        {
            EnsureSession(game);
            UnityEngine.Debug.Log(
                "[Auto Planner][Session] END actions=" + actionCount
                + " built=" + successfulLines
                + " extended=" + successfulExtensions
                + " capacity=" + capacityActions
                + " interchange=" + interchangeActions
                + " transfers=" + transferActions
                + " replans=" + replanActions
                + " reallocations=" + reallocationActions
                + " rollbacks=" + reallocationRollbacks
                + " noCrossing=" + crossingFailures
                + " | " + Snapshot(game));
        }

        public static bool TryAct(Game game, UnlockGoal unlockGoal, out string action)
        {
            action = null;
            RefreshCrossingResourceWait(game);
            // Clear a wait whose formerly blocked station was connected by another
            // route before any opportunistic action consults the global wait flag.
            HasActiveCrossingDemand(game);
            City city = game.City;
            List<Station> stations = ActiveStations(city);
            UpdateNetworkMonitoring(game, stations);
            EmitTelemetry(game, stations);
            EvaluatePendingTransfers(game);
            if (EvaluatePendingAssetReallocations(game, unlockGoal, out action)) return true;
            if (stations.Count < 2)
            {
                return false;
            }

            Station critical = FindMostPressuredStation(stations);
            VisionDecisionState vision = VisionControlBridge.Read(city.StationCount);
            if (vision.HoldsTopology)
            {
                // A fresh visual contradiction may indicate a menu/overlay or a
                // capture/model regression. Preserve only a native emergency
                // capacity action; defer irreversible topology edits until the
                // observer and native state agree again.
                if (critical != null && StationRisk(critical) >= 0.72f
                    && TryAddCapacity(game, critical, unlockGoal, out action))
                {
                    action = "Vision-safe emergency " + action;
                    return true;
                }
                return false;
            }
            if (critical != null && StationRisk(critical) >= 0.64f)
            {
                if (TryUpgradeInterchange(game, critical, unlockGoal, out action)) return true;
                if (TryAddCapacity(game, critical, unlockGoal, out action)) return true;
                if (TryReallocateAssets(game, unlockGoal, critical, out action)) return true;
                if (game.Mode == GameMode.CLASSIC && StationRisk(critical) >= 0.82f)
                {
                    if (TryRemoveRedundantLine(game, stations, unlockGoal, out action)) return true;
                    if (TryConsolidateUnderusedLine(game, stations, unlockGoal, out action)) return true;
                    if (StationRisk(critical) >= 0.90f
                        && TryAddBeneficialTransfer(game, stations, unlockGoal, out action)) return true;
                }
            }

            List<Station> unconnected = new List<Station>();
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].LineCount == 0)
                {
                    unconnected.Add(stations[i]);
                }
            }

            if (unconnected.Count > 0)
            {
                bool preferNewLine = ShouldCreateLine(game, unlockGoal, unconnected.Count);
                if (preferNewLine
                    && TryCreateLine(game, stations, unconnected, unlockGoal, out action))
                {
                    return true;
                }
                if (TryExtendLine(game, stations, unconnected, unlockGoal, out action))
                {
                    return true;
                }
                if (!preferNewLine
                    && TryCreateLine(game, stations, unconnected, unlockGoal, out action))
                {
                    return true;
                }
            }

            if (TryCreateReliefLine(game, stations, unlockGoal, out action))
            {
                return true;
            }

            if (game.Mode == GameMode.CLASSIC
                && TryRemoveRedundantLine(game, stations, unlockGoal, out action))
            {
                return true;
            }

            if (game.Mode == GameMode.CLASSIC
                && TryConsolidateUnderusedLine(game, stations, unlockGoal, out action))
            {
                return true;
            }

            if (TryAddBeneficialTransfer(game, stations, unlockGoal, out action))
            {
                return true;
            }

            if (critical != null && StationRisk(critical) >= 0.58f)
            {
                if (TryAddCapacity(game, critical, unlockGoal, out action)) return true;
                if (TryUpgradeInterchange(game, critical, unlockGoal, out action)) return true;
            }

            if (TryUpgradeInterchange(game, critical, unlockGoal, out action)) return true;
            if (TryReallocateAssets(game, unlockGoal, critical, out action)) return true;
            if (TryBalanceFleet(game, unlockGoal, out action)) return true;
            return false;
        }

        public static UpgradeDefinition SelectUpgrade(Game game, bool locomotive)
        {
            return SelectUpgrade(game, locomotive, -1);
        }

        public static UpgradeDefinition SelectUpgrade(Game game, bool locomotive, int groupFilter)
        {
            RefreshCrossingResourceWait(game);
            UpdateNetworkMonitoring(game, ActiveStations(game.City));
            List<UpgradeDefinition> definitions = UpgradeDefinitions(game.City.Definition, groupFilter);
            UpgradeDefinition best = null;
            float bestScore = Single.MinValue;
            for (int i = 0; i < definitions.Count; i++)
            {
                UpgradeDefinition candidate = definitions[i];
                if (candidate == null || candidate.Type == AssetType.None) continue;
                if (Asset.IsLocomotive(candidate.Type) != locomotive) continue;
                if (!candidate.AnyLeft(game)) continue;

                float score = UpgradeScore(game, candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            if (best != null)
            {
                UnityEngine.Debug.Log(
                    "[Auto Planner][Decision] upgrade=" + best.Type
                    + " score=" + bestScore.ToString("0.0")
                    + " locomotiveGroup=" + locomotive
                    + " pressure=" + MaximumPressure(game.City).ToString("0.00")
                    + " crossingDemand=" + HasActiveCrossingDemand(game)
                    + " crossingWait=" + waitingForCrossingResource
                    + " unconnected=" + CountUnconnected(game.City));
            }
            return best;
        }

        public static int GetUpgradeGroupCount(CityDefinition definition)
        {
            if (definition != null && definition.IsUgc && definition.CustomUpgradeDefinitions != null)
            {
                return Mathf.Max(2, definition.CustomUpgradeDefinitions.Count);
            }
            return 2;
        }

        private static List<UpgradeDefinition> UpgradeDefinitions(CityDefinition definition, int groupFilter)
        {
            List<UpgradeDefinition> result = new List<UpgradeDefinition>();
            if (definition == null) return result;

            int groupCount = GetUpgradeGroupCount(definition);
            int firstGroup = groupFilter >= 0 ? groupFilter : 0;
            int lastGroup = groupFilter >= 0 ? groupFilter + 1 : groupCount;

            for (int group = firstGroup; group < lastGroup && group < groupCount; group++)
            {
                int count;
                try
                {
                    count = definition.GetUnlockableUpgradeCount(group);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < count; i++)
                {
                    UpgradeDefinition item = definition.GetUnlockableUpgrade(group, i);
                    if (item != null && !result.Contains(item)) result.Add(item);
                }
            }
            return result;
        }

        private static float UpgradeScore(Game game, UpgradeDefinition upgrade)
        {
            AssetType type = upgrade.Type;
            float score = upgrade.Weight * 2f + upgrade.Count;
            int available = game.AssetDatabase.GetAvailableAssets(type);
            float maximumPressure = MaximumPressure(game.City);
            bool activeCrossingDemand = HasActiveCrossingDemand(game);
            float interchangeNeed = MaximumInterchangeNeed(game.City);
            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                game.City, ActiveStations(game.City), null, null);

            if (Asset.IsLocomotive(type))
            {
                score += 100f + maximumPressure * 50f;
                score += flow.CongestionPenalty * 45f + flow.AverageCost * 0.08f;
            }
            else if (type == AssetType.Line)
            {
                int unconnected = CountUnconnected(game.City);
                score += (unconnected > 0 ? 115f : 35f) + (available == 0 ? 35f : 0f);
                score += flow.UnreachableRatio * 360f + flow.AverageCost * 0.05f;
                if (game.City.LineCount >= game.City.MaxLineCount) score -= 200f;
                if (AvailableTrainCount(game) <= 0) score -= 180f;
                if (waitingForCrossingResource
                    || (activeCrossingDemand && !HasAvailableCrossing(game))) score -= 110f;
                if (maximumPressure >= 0.85f) score -= 90f;
            }
            else if (type == AssetType.Crossing || type == AssetType.Bridge)
            {
                score += available <= 0 ? 48f : 18f;
                if (activeCrossingDemand)
                {
                    score += waitingForCrossingResource
                        ? 520f + Mathf.Min(120f, crossingFailures * 15f)
                        : available == 0
                        ? 210f + Mathf.Min(75f, crossingFailures * 10f)
                        : 45f;
                }
                else if (CountUnconnected(game.City) == 0) score -= 35f;
            }
            else if (type == AssetType.Carriage)
            {
                score += 55f + maximumPressure * 70f + (available == 0 ? 18f : 0f);
                score += flow.CongestionPenalty * 75f;
                if (maximumPressure >= 0.85f)
                {
                    score += 200f + maximumPressure * 50f;
                }
                if (interchangeNeed >= 260f && available == 0) score -= 70f;
            }
            else if (type == AssetType.Interchange)
            {
                score += 38f + maximumPressure * 85f + (available == 0 ? 12f : 0f);
                score += flow.UnreachableRatio * 180f + flow.AverageCost * 0.04f;
                if (maximumPressure >= 0.75f) score += 230f;
                score += interchangeNeed * 0.65f;
                if (interchangeNeed >= 260f)
                {
                    score += 360f + Mathf.Min(260f, (interchangeNeed - 260f) * 0.8f);
                }
            }
            else
            {
                score += 25f;
            }
            return score;
        }

        private static bool ShouldCreateLine(Game game, UnlockGoal unlockGoal, int unconnectedCount)
        {
            if (game.AssetDatabase.GetAvailableAssets(AssetType.Line) <= 0) return false;
            if (AvailableTrainCount(game) <= 0) return false;
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Line, null, 1)) return false;
            if (game.City.LineCount <= 0) return true;
            if (game.City.LineCount >= game.City.MaxLineCount) return false;
            float stationsPerLine = (float)game.City.StationCount / Mathf.Max(1, game.City.LineCount);
            if (unconnectedCount <= 0) return false;
            int longest = LongestRoute(game.City);
            float maximumPressure = MaximumPressure(game.City);

            // The second line is cheap enough to establish early. A third or later line
            // consumes the last strategic locomotive much more often, so require proof
            // that extension is no longer the safer option.
            if (game.City.LineCount == 1)
            {
                if (unconnectedCount >= 3) return true;
                if (unconnectedCount >= 2 && stationsPerLine >= 4.5f) return true;
                return longest >= 6
                    || stationsPerLine >= 6.0f
                    || maximumPressure >= 0.72f;
            }

            if (unconnectedCount >= 3 && stationsPerLine >= 5.0f) return true;
            if (unconnectedCount >= 2)
            {
                return longest >= 8
                    || stationsPerLine >= 5.5f
                    || maximumPressure >= 0.58f;
            }
            return longest >= 9
                || stationsPerLine >= 6.5f
                || maximumPressure >= 0.72f;
        }

        private static bool ShouldReserveTrainForNewLine(Game game)
        {
            if (game == null || game.City == null || game.AssetDatabase == null) return false;
            if (game.AssetDatabase.GetAvailableAssets(AssetType.Line) <= 0) return false;
            if (game.City.LineCount >= game.City.MaxLineCount) return false;
            if (game.City.LineCount <= 0) return true;

            float stationsPerLine = (float)game.City.StationCount / Mathf.Max(1, game.City.LineCount);
            if (game.City.LineCount == 1)
            {
                return LongestRoute(game.City) >= 6
                    || stationsPerLine >= 5.5f
                    || MaximumPressure(game.City) >= 0.72f;
            }
            return LongestRoute(game.City) >= 9
                || stationsPerLine >= 6.5f
                || MaximumPressure(game.City) >= 0.72f;
        }

        private static bool TryCreateReliefLine(
            Game game,
            List<Station> stations,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game == null || game.City == null || game.City.LineCount < 2) return false;
            if (game.AssetDatabase.GetAvailableAssets(AssetType.Line) <= 0) return false;
            if (AvailableTrainCount(game) <= 0) return false;
            if (game.City.LineCount >= game.City.MaxLineCount) return false;
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Line, null, 1)) return false;

            float maximumPressure = MaximumPressure(game.City);
            int longest = LongestRoute(game.City);
            float minimumPressure = game.Mode == GameMode.EXTREME ? 0.95f : 0.72f;
            int minimumLength = game.Mode == GameMode.EXTREME ? 11 : 9;
            if (maximumPressure < minimumPressure && longest < minimumLength) return false;

            Station start = null;
            float bestStartScore = Single.MinValue;
            for (int i = 0; i < stations.Count; i++)
            {
                Station candidate = stations[i];
                if (candidate.LineCount <= 0 || IsTerminatorOnAnyLine(game.City, candidate)) continue;
                float score = StationRisk(candidate) * 180f + candidate.PeepCount * 12f
                    + candidate.LineCount * 18f;
                if (score > bestStartScore)
                {
                    bestStartScore = score;
                    start = candidate;
                }
            }
            if (start == null) return false;

            List<Station> route = new List<Station>();
            route.Add(start);
            while (route.Count < 3)
            {
                Station next = BestReliefStation(route[route.Count - 1], route, stations);
                if (next == null) break;
                route.Add(next);
            }
            if (route.Count < 3 || RouteContainsBlockedConnection(route)) return false;

            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                game.City, stations, null, null);
            float flowGain = RouteFlowOptimizer.FlowGain(
                game.City, stations, flow, null, route);
            float estimatedBenefit = maximumPressure * 190f
                + longest * 18f
                + CountDistinctShapes(route) * 45f
                + Mathf.Max(0f, flowGain) * 0.55f;
            float routeRisk = 0f;
            for (int routeIndex = 0; routeIndex < route.Count; routeIndex++)
            {
                routeRisk += StationRisk(route[routeIndex]);
            }
            routeRisk /= Mathf.Max(1, route.Count);
            // A relief line with one locomotive is itself a new bottleneck. At the
            // pressure level seen in the 831-point run, spending the last locomotive
            // on a three-station line made the new route start at pressure 2.66.
            // Wait for a second locomotive or use a hub/capacity action first.
            if (AvailableTrainCount(game) <= 1
                && maximumPressure >= 1.15f
                && routeRisk >= 0.70f)
            {
                UnityEngine.Debug.Log(
                    "[Auto Planner][Decision] relief deferred=underpowered"
                    + " routeRisk=" + routeRisk.ToString("0.00")
                    + " maxRisk=" + maximumPressure.ToString("0.00")
                    + " spareTrains=" + AvailableTrainCount(game));
                return false;
            }
            if (!BuildNewLine(game, route, "relief-line")) return false;
            nextReplanAt = Time.unscaledTime + 60f;
            action = "Built line relief through " + route.Count
                + " stations benefit=" + estimatedBenefit.ToString("0")
                + " flowGain=" + flowGain.ToString("0.0")
                + " pressure=" + maximumPressure.ToString("0.00")
                + " longest=" + (longest + 1);
            return true;
        }

        private static Station BestReliefStation(
            Station from,
            List<Station> route,
            List<Station> stations)
        {
            Station best = null;
            float bestScore = Single.MaxValue;
            for (int i = 0; i < stations.Count; i++)
            {
                Station candidate = stations[i];
                if (candidate == null || candidate.LineCount <= 0 || route.Contains(candidate)) continue;
                if (IsConnectionBlocked(from, candidate)) continue;
                int matchingShape = 0;
                for (int r = 0; r < route.Count; r++)
                {
                    if (route[r].Type == candidate.Type) matchingShape++;
                }
                float score = RouteFlowOptimizer.OctilinearDistance(from, candidate)
                    + matchingShape * 120f
                    - StationRisk(candidate) * 125f
                    - candidate.LineCount * 30f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private static bool IsTerminatorOnAnyLine(City city, Station station)
        {
            for (int i = 0; i < city.LineCount; i++)
            {
                Line line = city.GetLine(i);
                if (line == null) continue;
                for (int end = 0; end < 2; end++)
                {
                    Terminator terminator = line.GetTerminator(end);
                    if (terminator != null
                        && terminator.AnchoredStop != null
                        && terminator.AnchoredStop.Station == station)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static int CountDistinctShapes(List<Station> stations)
        {
            List<StationType> shapes = new List<StationType>();
            for (int i = 0; i < stations.Count; i++)
            {
                if (!shapes.Contains(stations[i].Type)) shapes.Add(stations[i].Type);
            }
            return shapes.Count;
        }

        private static bool TryCreateLine(
            Game game,
            List<Station> stations,
            List<Station> unconnected,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game.AssetDatabase.GetAvailableAssets(AssetType.Line) <= 0) return false;
            if (AvailableTrainCount(game) <= 0) return false;
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Line, null, 1)) return false;
            if (game.City.LineCount >= game.City.MaxLineCount) return false;
            if (unconnected.Count == 0) return false;

            float flowGain;
            List<Station> route = RouteFlowOptimizer.FindBestNewLineRoute(
                game.City,
                stations,
                unconnected,
                delegate(Station first, Station second)
                {
                    return IsConnectionBlocked(first, second);
                },
                out flowGain);
            if (route == null || route.Count < 2) return false;

            if (RouteContainsBlockedConnection(route)) return false;

            bool success = BuildNewLine(game, route, "new-line");
            if (success)
            {
                action = "Built line through " + route.Count
                    + " stations flowGain=" + flowGain.ToString("0.0")
                    + " octileLength=" + RouteFlowOptimizer.RouteLength(route).ToString("0");
                return true;
            }
            return false;
        }

        private static bool BuildNewLine(Game game, List<Station> route, string operation)
        {
            LineBuilder builder = game.LineBuilder;
            Line newLine = null;
            bool[] initiallyUnconnected = new bool[route.Count];
            for (int i = 0; i < route.Count; i++)
            {
                initiallyUnconnected[i] = route[i] != null && route[i].LineCount == 0;
            }
            try
            {
                builder.HandleStationTouchBegan(route[0], GlobalPosition(game, route[0]));
                if (!builder.IsBuilding) return false;
                newLine = builder.Line;
                int initialCount = builder.Line != null ? builder.Line.Count : 0;
                builder.HandleStationTouchOut(route[0]);
                for (int i = 1; i < route.Count; i++)
                {
                    builder.HandleTouchMove(GlobalPosition(game, route[i]));
                    builder.HandleStationTouchOver(route[i]);
                    if (BuilderHasNoCrossing(game, builder))
                    {
                        bool essential = String.Equals(operation, "new-line", StringComparison.Ordinal)
                            && (initiallyUnconnected[i - 1] || initiallyUnconnected[i]);
                        NoteCrossingBlocked(game, route[i - 1], route[i], operation, essential);
                        ResetBrokenBuilder(game, newLine, null, true, operation + " NoCrossing");
                        return false;
                    }
                    if (i < route.Count - 1)
                    {
                        builder.HandleStationTouchOut(route[i]);
                    }
                }
                if (builder.Line == null || builder.Line.Count <= initialCount)
                {
                    ResetBrokenBuilder(game, newLine, null, true, "new-line did not grow");
                    return false;
                }
                bool result = builder.HandleTouchEnded();
                if (!result)
                {
                    ResetBrokenBuilder(game, newLine, null, true, "new-line commit rejected");
                }
                return result;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Auto Planner] New line recovered: " + exception);
                ResetBrokenBuilder(game, newLine, null, true, "new-line exception");
                return false;
            }
        }

        private static bool TryExtendLine(
            Game game,
            List<Station> stations,
            List<Station> targets,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                game.City, stations, null, null);
            List<EndpointCandidate> candidates = new List<EndpointCandidate>();
            for (int lineIndex = 0; lineIndex < game.City.LineCount; lineIndex++)
            {
                Line line = game.City.GetLine(lineIndex);
                if (line == null || line.LiveLinkCount <= 0) continue;
                if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Station, line, 1)) continue;

                for (int end = 0; end < 2; end++)
                {
                    Terminator terminator = line.GetTerminator(end);
                    if (terminator == null || terminator.AnchoredStop == null) continue;
                    Station endpoint = terminator.AnchoredStop.Station;
                    if (endpoint == null) continue;

                    for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                    {
                        Station target = targets[targetIndex];
                        if (target == null || line.ContainsStation(target)) continue;
                        if (IsConnectionBlocked(endpoint, target)) continue;
                        candidates.Add(new EndpointCandidate
                        {
                            Line = line,
                            Terminator = terminator,
                            Endpoint = endpoint,
                            Target = target,
                            Cost = ExtensionCost(
                                game.City, line, endpoint, target, stations, flow)
                        });
                    }
                }
            }

            candidates.Sort(delegate(EndpointCandidate a, EndpointCandidate b)
            {
                return a.Cost.CompareTo(b.Cost);
            });

            int attempts = Mathf.Min(1, candidates.Count);
            for (int i = 0; i < attempts; i++)
            {
                EndpointCandidate candidate = candidates[i];
                if (ExtendFromTerminator(game, candidate, "extension"))
                {
                    action = "Extended line " + (candidate.Line.Index + 1) + " to new station";
                    return true;
                }
            }
            return false;
        }

        private static bool ExtendFromTerminator(
            Game game,
            EndpointCandidate candidate,
            string operation)
        {
            LineBuilder builder = game.LineBuilder;
            HashSet<Link> originalLinks = CaptureLineLinks(candidate.Line);
            bool essential = String.Equals(operation, "extension", StringComparison.Ordinal)
                && candidate.Target != null
                && candidate.Target.LineCount == 0;
            try
            {
                int originalCount = candidate.Line.Count;
                candidate.Terminator.HandleTouchBegan(game, GlobalPosition(game, candidate.Endpoint));
                if (!builder.IsBuilding) return false;
                builder.HandleStationTouchOut(candidate.Endpoint);
                builder.HandleTouchMove(GlobalPosition(game, candidate.Target));
                builder.HandleStationTouchOver(candidate.Target);
                if (BuilderHasNoCrossing(game, builder))
                {
                    NoteCrossingBlocked(
                        game,
                        candidate.Endpoint,
                        candidate.Target,
                        operation,
                        essential);
                    ResetBrokenBuilder(
                        game,
                        candidate.Line,
                        originalLinks,
                        false,
                        operation + " NoCrossing");
                    return false;
                }
                if (builder.Line == null || builder.Line.Count <= originalCount)
                {
                    ResetBrokenBuilder(
                        game,
                        candidate.Line,
                        originalLinks,
                        false,
                        "extension did not grow");
                    return false;
                }
                bool result = builder.HandleTouchEnded();
                if (!result)
                {
                    ResetBrokenBuilder(
                        game,
                        candidate.Line,
                        originalLinks,
                        false,
                        "extension commit rejected");
                }
                return result;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Auto Planner] Line extension recovered: " + exception);
                ResetBrokenBuilder(
                    game,
                    candidate.Line,
                    originalLinks,
                    false,
                    "extension exception");
                return false;
            }
        }

        private static float ExtensionCost(
            City city,
            Line line,
            Station endpoint,
            Station target,
            List<Station> stations,
            PassengerFlowAnalysis baseline)
        {
            float cost = RouteFlowOptimizer.OctilinearDistance(endpoint, target);
            cost += line.LiveLinkCount * 14f;
            if (line.LiveLinkCount > 6) cost += (line.LiveLinkCount - 6) * 95f;
            cost += LinePressure(line, stations) * 120f;
            cost -= StationRisk(target) * 85f;

            int matchingType = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].Type == target.Type && line.ContainsStation(stations[i])) matchingType++;
            }
            cost += matchingType * 48f;
            List<Station> candidateRoute = new List<Station>();
            candidateRoute.Add(endpoint);
            candidateRoute.Add(target);
            float flowGain = RouteFlowOptimizer.FlowGain(
                city, stations, baseline, line, candidateRoute);
            cost -= Mathf.Clamp(flowGain, -400f, 800f) * 0.55f;
            return cost;
        }

        private static bool TryAddBeneficialTransfer(
            Game game,
            List<Station> stations,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game == null || game.City == null || game.City.LineCount < 2) return false;
            float now = Time.unscaledTime;
            if (now < nextTransferAt || PendingTransferEvaluations.Count > 0) return false;
            if (waitingForCrossingResource && !HasAvailableCrossing(game)) return false;

            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                game.City, stations, null, null);
            TransferCandidate best = null;
            for (int lineIndex = 0; lineIndex < game.City.LineCount; lineIndex++)
            {
                Line source = game.City.GetLine(lineIndex);
                if (source == null || source.LiveLinkCount <= 0 || source.LiveLinkCount >= 12) continue;
                if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Station, source, 1)) continue;

                for (int end = 0; end < 2; end++)
                {
                    Terminator terminator = source.GetTerminator(end);
                    if (terminator == null || terminator.AnchoredStop == null) continue;
                    Station endpoint = terminator.AnchoredStop.Station;
                    if (endpoint == null) continue;

                    for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
                    {
                        Station target = stations[stationIndex];
                        if (target == null || source.ContainsStation(target) || target.Lines == null) continue;
                        if (IsConnectionBlocked(endpoint, target)) continue;

                        for (int otherIndex = 0; otherIndex < target.Lines.Count; otherIndex++)
                        {
                            Line other = target.Lines[otherIndex];
                            if (other == null || other == source || other.LiveLinkCount <= 0) continue;
                            int shared = CountSharedStations(source, other, stations);
                            bool connected = LinesConnectedByTransfers(game.City, source, other, stations, null);
                            if (shared >= 2 && connected) continue;

                            float benefit = TransferBenefit(
                                game.City,
                                source,
                                other,
                                endpoint,
                                target,
                                stations,
                                shared,
                                !connected,
                                flow);
                            if (best == null || benefit > best.Benefit)
                            {
                                best = new TransferCandidate
                                {
                                    Source = source,
                                    Other = other,
                                    Terminator = terminator,
                                    Endpoint = endpoint,
                                    Target = target,
                                    Benefit = benefit,
                                    ExistingTransfers = shared,
                                    ConnectsComponents = !connected
                                };
                            }
                        }
                    }
                }
            }

            float threshold = (game.Mode == GameMode.EXTREME ? 420f : 260f) + transferEffectBias;
            if (best == null || best.Benefit < threshold) return false;

            float pressureBefore = LinePressure(best.Source, stations) + LinePressure(best.Other, stations);
            EndpointCandidate extension = new EndpointCandidate
            {
                Line = best.Source,
                Terminator = best.Terminator,
                Endpoint = best.Endpoint,
                Target = best.Target,
                Cost = 0f
            };
            if (!ExtendFromTerminator(game, extension, "transfer"))
            {
                // A failed opportunistic transfer must not spend every planner tick
                // probing a different over-water route while the network is in crisis.
                nextTransferAt = now + 12f;
                return false;
            }

            PendingTransferEvaluations.Add(new TransferEvaluation
            {
                Source = best.Source,
                Other = best.Other,
                Station = best.Target,
                EstimatedBenefit = best.Benefit,
                PressureBefore = pressureBefore,
                EvaluateAfter = Time.unscaledTime + 20f,
                ConnectedComponents = best.ConnectsComponents
            });
            nextTransferAt = now + 25f;
            action = "Added transfer L" + (best.Source.Index + 1)
                + "-L" + (best.Other.Index + 1)
                + " benefit=" + best.Benefit.ToString("0")
                + " previous=" + best.ExistingTransfers
                + " connectivity=" + best.ConnectsComponents;
            return true;
        }

        private static float TransferBenefit(
            City city,
            Line source,
            Line other,
            Station endpoint,
            Station target,
            List<Station> stations,
            int sharedStations,
            bool connectsComponents,
            PassengerFlowAnalysis baseline)
        {
            float distance = RouteFlowOptimizer.OctilinearDistance(endpoint, target);
            int newShapes = CountNewShapes(source, other, stations);
            float combinedPressure = LinePressure(source, stations) + LinePressure(other, stations);
            float benefit = connectsComponents ? 520f : 0f;
            benefit += newShapes * 75f;
            benefit += combinedPressure * 105f;
            benefit += StationRisk(target) * 45f;
            benefit += Mathf.Max(0, target.LineCount - 1) * 25f;
            benefit -= distance * 0.32f;
            benefit -= Mathf.Max(0, source.LiveLinkCount - 7) * 55f;
            benefit -= sharedStations * 190f;
            if (source.TrainCount <= 0) benefit -= 220f;
            List<Station> candidateRoute = new List<Station>();
            candidateRoute.Add(endpoint);
            candidateRoute.Add(target);
            float flowGain = RouteFlowOptimizer.FlowGain(
                city, stations, baseline, source, candidateRoute);
            benefit += Mathf.Clamp(flowGain, -400f, 800f) * 0.75f;
            return benefit;
        }

        private static int CountNewShapes(Line source, Line other, List<Station> stations)
        {
            List<StationType> sourceShapes = new List<StationType>();
            List<StationType> newShapes = new List<StationType>();
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                if (source.ContainsStation(station) && !sourceShapes.Contains(station.Type))
                {
                    sourceShapes.Add(station.Type);
                }
            }
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                if (other.ContainsStation(station)
                    && !sourceShapes.Contains(station.Type)
                    && !newShapes.Contains(station.Type))
                {
                    newShapes.Add(station.Type);
                }
            }
            return newShapes.Count;
        }

        private static int CountSharedStations(Line first, Line second, List<Station> stations)
        {
            int count = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (first.ContainsStation(stations[i]) && second.ContainsStation(stations[i])) count++;
            }
            return count;
        }

        private static bool LinesConnectedByTransfers(
            City city,
            Line start,
            Line destination,
            List<Station> stations,
            Line ignored)
        {
            if (start == destination) return true;
            List<Line> visited = new List<Line>();
            List<Line> pending = new List<Line>();
            visited.Add(start);
            pending.Add(start);
            while (pending.Count > 0)
            {
                Line current = pending[0];
                pending.RemoveAt(0);
                for (int i = 0; i < city.LineCount; i++)
                {
                    Line candidate = city.GetLine(i);
                    if (candidate == null || candidate == ignored || visited.Contains(candidate)) continue;
                    if (CountSharedStations(current, candidate, stations) <= 0) continue;
                    if (candidate == destination) return true;
                    visited.Add(candidate);
                    pending.Add(candidate);
                }
            }
            return false;
        }

        private static void EvaluatePendingTransfers(Game game)
        {
            if (game == null || game.City == null) return;
            float now = Time.unscaledTime;
            List<Station> stations = ActiveStations(game.City);
            for (int i = PendingTransferEvaluations.Count - 1; i >= 0; i--)
            {
                TransferEvaluation evaluation = PendingTransferEvaluations[i];
                if (evaluation.EvaluateAfter > now) continue;
                if (evaluation.Source == null || evaluation.Other == null)
                {
                    PendingTransferEvaluations.RemoveAt(i);
                    continue;
                }

                float pressureAfter = LinePressure(evaluation.Source, stations)
                    + LinePressure(evaluation.Other, stations);
                float reduction = evaluation.PressureBefore - pressureAfter;
                bool effective = evaluation.ConnectedComponents
                    || reduction >= 0.12f
                    || pressureAfter <= evaluation.PressureBefore * 0.85f;
                transferEffectBias = Mathf.Clamp(
                    transferEffectBias + (effective ? -20f : 70f),
                    0f,
                    280f);
                nextTransferAt = Mathf.Max(nextTransferAt, now + (effective ? 25f : 75f));
                UnityEngine.Debug.Log(
                    "[Auto Planner][Evaluation] transfer station=" + StationLabel(evaluation.Station)
                    + " estimated=" + evaluation.EstimatedBenefit.ToString("0")
                    + " pressureBefore=" + evaluation.PressureBefore.ToString("0.00")
                    + " pressureAfter=" + pressureAfter.ToString("0.00")
                    + " reduction=" + reduction.ToString("0.00")
                    + " effective=" + effective
                    + " nextThresholdBias=" + transferEffectBias.ToString("0"));
                PendingTransferEvaluations.RemoveAt(i);
            }
        }

        private static bool TryRemoveRedundantLine(
            Game game,
            List<Station> stations,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game == null || game.City == null || !game.CanRemoveTracks) return false;
            if (game.City.LineCount < 3 || Time.unscaledTime < nextReplanAt) return false;

            Line best = null;
            float bestBenefit = Single.MinValue;
            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null || line.LiveLinkCount <= 0 || line.Index < 0) continue;
                if (game.City.Definition != null
                    && line.Index < game.City.Definition.PermanentLineCount) continue;

                int stationCount = 0;
                int exclusiveStations = 0;
                for (int s = 0; s < stations.Count; s++)
                {
                    Station station = stations[s];
                    if (!line.ContainsStation(station)) continue;
                    stationCount++;
                    if (station.LineCount <= 1) exclusiveStations++;
                }
                if (stationCount <= 0 || exclusiveStations > 0) continue;

                float pressure = LinePressure(line, stations);
                if (pressure > 0.55f || line.PeepCount > 2) continue;
                if (!RemainingLineNetworkConnected(game.City, line, stations)) continue;

                float overlapRatio = (float)(stationCount - exclusiveStations) / stationCount;
                float benefit = overlapRatio * 170f
                    + Mathf.Max(0, 5 - stationCount) * 35f
                    + line.TrainCount * 28f
                    + line.CarriageCount * 20f
                    - pressure * 180f;
                if (benefit > bestBenefit)
                {
                    bestBenefit = benefit;
                    best = line;
                }
            }

            if (best == null || bestBenefit < 185f) return false;
            int oldIndex = best.Index;
            int recoveredTrains = best.TrainCount;
            int recoveredCarriages = best.CarriageCount;
            best.Remove();
            nextReplanAt = Time.unscaledTime + 60f;
            action = "Removed redundant line " + (oldIndex + 1)
                + " replanBenefit=" + bestBenefit.ToString("0")
                + " recovered=" + recoveredTrains + " trains/"
                + recoveredCarriages + " carriages";
            return true;
        }

        private static bool TryConsolidateUnderusedLine(
            Game game,
            List<Station> stations,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game == null || game.City == null || !game.CanRemoveTracks) return false;
            if (game.City.LineCount < 3 || AvailableTrainCount(game) > 0) return false;
            if (Time.unscaledTime < nextConsolidationAt || Time.unscaledTime < nextReplanAt) return false;

            float maximumPressure = MaximumPressure(game.City);
            if (maximumPressure < 0.82f) return false;

            Line donor = null;
            List<Station> donorExclusive = null;
            float bestBenefit = Single.MinValue;
            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null || line.LiveLinkCount <= 0 || line.Index < 0) continue;
                if (game.City.Definition != null
                    && line.Index < game.City.Definition.PermanentLineCount) continue;

                List<Station> exclusive = new List<Station>();
                int stationCount = 0;
                for (int s = 0; s < stations.Count; s++)
                {
                    Station candidate = stations[s];
                    if (!line.ContainsStation(candidate)) continue;
                    stationCount++;
                    if (candidate.LineCount <= 1) exclusive.Add(candidate);
                }

                float pressure = LinePressure(line, stations);
                if (stationCount <= 1
                    || stationCount > 4
                    || exclusive.Count <= 0
                    || pressure > 0.42f
                    || maximumPressure - pressure < 0.38f
                    || line.PeepCount > 3) continue;

                float benefit = (maximumPressure - pressure) * 190f
                    + Mathf.Max(0, 5 - stationCount) * 35f
                    + line.TrainCount * 42f
                    + line.CarriageCount * 25f
                    - exclusive.Count * 18f;
                if (benefit > bestBenefit)
                {
                    bestBenefit = benefit;
                    donor = line;
                    donorExclusive = exclusive;
                }
            }

            if (donor == null || donorExclusive == null) return false;
            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                game.City, stations, null, null);
            EndpointCandidate best = null;
            for (int targetIndex = 0; targetIndex < donorExclusive.Count; targetIndex++)
            {
                Station target = donorExclusive[targetIndex];
                for (int lineIndex = 0; lineIndex < game.City.LineCount; lineIndex++)
                {
                    Line receiving = game.City.GetLine(lineIndex);
                    if (receiving == null
                        || receiving == donor
                        || receiving.LiveLinkCount <= 0
                        || receiving.ContainsStation(target)) continue;
                    if (!UnlockInspector.Allows(
                        game, unlockGoal, RestrictionType.Station, receiving, 1)) continue;

                    float receivingPressure = LinePressure(receiving, stations);
                    for (int end = 0; end < 2; end++)
                    {
                        Terminator terminator = receiving.GetTerminator(end);
                        if (terminator == null || terminator.AnchoredStop == null) continue;
                        Station endpoint = terminator.AnchoredStop.Station;
                        if (endpoint == null || IsConnectionBlocked(endpoint, target)) continue;
                        float cost = ExtensionCost(
                            game.City, receiving, endpoint, target, stations, flow)
                            + receivingPressure * 240f
                            + Mathf.Max(0, receiving.LiveLinkCount - 8) * 80f;
                        if (best == null || cost < best.Cost)
                        {
                            best = new EndpointCandidate
                            {
                                Line = receiving,
                                Terminator = terminator,
                                Endpoint = endpoint,
                                Target = target,
                                Cost = cost
                            };
                        }
                    }
                }
            }

            if (best == null || !ExtendFromTerminator(game, best, "rehome")) return false;
            nextConsolidationAt = Time.unscaledTime + 8f;
            action = "Rehomed " + StationLabel(best.Target)
                + " from underused L" + (donor.Index + 1)
                + " to L" + (best.Line.Index + 1)
                + " retirementBenefit=" + bestBenefit.ToString("0");
            return true;
        }

        private static bool RemainingLineNetworkConnected(
            City city,
            Line ignored,
            List<Station> stations)
        {
            Line start = null;
            int remaining = 0;
            for (int i = 0; i < city.LineCount; i++)
            {
                Line line = city.GetLine(i);
                if (line == null || line == ignored || line.LiveLinkCount <= 0) continue;
                remaining++;
                if (start == null) start = line;
            }
            if (remaining <= 1) return true;

            List<Line> visited = new List<Line>();
            List<Line> pending = new List<Line>();
            visited.Add(start);
            pending.Add(start);
            while (pending.Count > 0)
            {
                Line current = pending[0];
                pending.RemoveAt(0);
                for (int i = 0; i < city.LineCount; i++)
                {
                    Line candidate = city.GetLine(i);
                    if (candidate == null || candidate == ignored || visited.Contains(candidate)) continue;
                    if (CountSharedStations(current, candidate, stations) <= 0) continue;
                    visited.Add(candidate);
                    pending.Add(candidate);
                }
            }
            return visited.Count == remaining;
        }

        private static bool TryCloseUsefulLoop(
            Game game,
            List<Station> stations,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Loop, null, 1))
            {
                return false;
            }

            Line bestLine = null;
            Terminator bestTerminator = null;
            Station bestStart = null;
            Station bestEnd = null;
            float bestScore = Single.MaxValue;

            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null || line.IsLooping || line.LiveLinkCount < 3) continue;
                Terminator first = line.GetTerminator(0);
                Terminator second = line.GetTerminator(1);
                if (first == null || second == null
                    || first.AnchoredStop == null || second.AnchoredStop == null)
                {
                    continue;
                }

                Station start = first.AnchoredStop.Station;
                Station end = second.AnchoredStop.Station;
                if (start == null || end == null || start == end) continue;
                float distance = RouteFlowOptimizer.OctilinearDistance(start, end);
                float usefulness = LinePressure(line, stations);
                float score = distance - usefulness * 90f;
                if (distance <= Mathf.Max(150f, line.Length * 0.42f) && score < bestScore)
                {
                    bestScore = score;
                    bestLine = line;
                    bestTerminator = first;
                    bestStart = start;
                    bestEnd = end;
                }
            }

            if (bestLine == null) return false;
            LineBuilder builder = game.LineBuilder;
            HashSet<Link> originalLinks = CaptureLineLinks(bestLine);
            try
            {
                bestTerminator.HandleTouchBegan(game, GlobalPosition(game, bestStart));
                if (!builder.IsBuilding) return false;
                builder.HandleStationTouchOut(bestStart);
                builder.HandleTouchMove(GlobalPosition(game, bestEnd));
                builder.HandleStationTouchOver(bestEnd);
                if (BuilderHasNoCrossing(game, builder))
                {
                    NoteCrossingBlocked(game, bestStart, bestEnd, "loop", false);
                    ResetBrokenBuilder(
                        game,
                        bestLine,
                        originalLinks,
                        false,
                        "loop NoCrossing");
                    return false;
                }
                if (builder.Line == null)
                {
                    ResetBrokenBuilder(
                        game,
                        bestLine,
                        originalLinks,
                        false,
                        "loop lost builder line");
                    return false;
                }
                bool result = builder.HandleTouchEnded();
                if (!result)
                {
                    ResetBrokenBuilder(
                        game,
                        bestLine,
                        originalLinks,
                        false,
                        "loop commit rejected");
                }
                if (!result) return false;
                action = "Closed useful loop on line " + (bestLine.Index + 1);
                return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[Auto Planner] Loop edit recovered: " + exception);
                ResetBrokenBuilder(
                    game,
                    bestLine,
                    originalLinks,
                    false,
                    "loop exception");
                return false;
            }
        }

        public static void ResetBrokenBuilder(Game game)
        {
            ResetBrokenBuilder(game, null, null, false, "generic recovery");
        }

        private static HashSet<Link> CaptureLineLinks(Line line)
        {
            HashSet<Link> links = new HashSet<Link>();
            if (line == null) return links;
            for (int i = 0; i < line.Count; i++)
            {
                Link link = line[i];
                if (link != null) links.Add(link);
            }
            return links;
        }

        private static void ResetBrokenBuilder(
            Game game,
            Line editedLine,
            HashSet<Link> originalLinks,
            bool removeEditedLine,
            string reason)
        {
            if (game == null || game.LineBuilder == null) return;
            LineBuilder builder = game.LineBuilder;
            Line activeLine = null;
            try
            {
                activeLine = builder.Line;
            }
            catch
            {
            }

            if (activeLine == null) activeLine = editedLine;
            int countBefore = activeLine != null ? activeLine.Count : 0;
            int removedBuilderLinks = 0;
            bool nativeEditEnded = false;

            // A failed drag still owns temporary DirectedLinks. Clearing the fields alone
            // strands those links in the Line and leaves the red dotted NoCrossing edit on
            // screen. Remove only links created by this edit, then let the game's normal
            // end-of-edit path release UI errors, highlights, haptics and edit state.
            try
            {
                DirectedLink[] links = BuilderLinksField != null
                    ? BuilderLinksField.GetValue(builder) as DirectedLink[]
                    : null;
                if (links != null)
                {
                    for (int i = 0; i < links.Length; i++)
                    {
                        DirectedLink directed = links[i];
                        if (directed.IsNull) continue;
                        Link link = directed.Link;
                        bool belongsToFailedEdit = removeEditedLine
                            || originalLinks == null
                            || link == null
                            || !originalLinks.Contains(link);
                        if (belongsToFailedEdit)
                        {
                            directed.Remove();
                            removedBuilderLinks++;
                        }
                        links[i] = DirectedLink.Null;
                    }
                }

                ClearArrayField(BuilderPrefixLinksField, builder);
                if (BuilderFocusField != null) BuilderFocusField.SetValue(builder, null);
                if (BuilderLoopBreakField != null) BuilderLoopBreakField.SetValue(builder, null);
                if (activeLine != null) activeLine.RollbackMiddleEdit();

                if (builder.IsBuilding)
                {
                    builder.HandleTouchCanceled();
                    nativeEditEnded = !builder.IsBuilding;
                }
                else
                {
                    nativeEditEnded = true;
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "[Auto Planner][Rollback] Native cleanup recovered: "
                    + exception.GetType().Name + ": " + exception.Message);
            }

            if (removeEditedLine)
            {
                if (activeLine != null && ContainsLine(game.City, activeLine))
                {
                    try
                    {
                        game.City.RemoveLine(activeLine);
                    }
                    catch (Exception exception)
                    {
                        UnityEngine.Debug.LogWarning(
                            "[Auto Planner][Rollback] Failed to remove rejected new line: "
                            + exception.Message);
                    }
                }
            }
            else if (activeLine != null && originalLinks != null)
            {
                RemoveUnexpectedLinks(activeLine, originalLinks);
            }

            // Reflection is retained strictly as a last-resort guard for an exception in
            // the native cleanup path. It must run only after temporary links were removed.
            if (builder.IsBuilding)
            {
                try
                {
                    if (HideStationHighlightsMethod != null)
                    {
                        HideStationHighlightsMethod.Invoke(builder, null);
                    }
                }
                catch
                {
                }

                ClearArrayField(BuilderLinksField, builder);
                ClearArrayField(BuilderPrefixLinksField, builder);
                if (BuilderFocusField != null) BuilderFocusField.SetValue(builder, null);
                if (BuilderLoopBreakField != null) BuilderLoopBreakField.SetValue(builder, null);
                if (BuilderLineField != null) BuilderLineField.SetValue(builder, null);
                if (BuilderModifyField != null)
                {
                    BuilderModifyField.SetValue(builder, Enum.ToObject(BuilderModifyField.FieldType, 0));
                }
            }

            if (BuilderAllocatedLinesField != null)
            {
                List<int> allocated = BuilderAllocatedLinesField.GetValue(builder) as List<int>;
                if (allocated != null) allocated.Clear();
            }

            int countAfter = activeLine != null && !removeEditedLine ? activeLine.Count : 0;
            UnityEngine.Debug.Log(
                "[Auto Planner][Rollback] reason=" + reason
                + " nativeEnded=" + nativeEditEnded
                + " removedBuilderLinks=" + removedBuilderLinks
                + " lineLinks=" + countBefore + "->" + countAfter
                + " removedWholeLine=" + removeEditedLine);
        }

        private static void RemoveUnexpectedLinks(Line line, HashSet<Link> originalLinks)
        {
            try
            {
                for (int i = line.Count - 1; i >= 0; i--)
                {
                    Link link = line[i];
                    if (link != null && !originalLinks.Contains(link))
                    {
                        line.RemoveLink(link, true);
                    }
                }
                line.RollbackMiddleEdit();
                line.RefreshTerminators();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "[Auto Planner][Rollback] Snapshot restore recovered: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static bool ContainsLine(City city, Line line)
        {
            if (city == null || line == null) return false;
            for (int i = 0; i < city.LineCount; i++)
            {
                if (System.Object.ReferenceEquals(city.GetLine(i), line)) return true;
            }
            return false;
        }

        private static void ClearArrayField(FieldInfo field, object subject)
        {
            if (field == null) return;
            Array array = field.GetValue(subject) as Array;
            if (array != null) Array.Clear(array, 0, array.Length);
        }

        private static Vector2 GlobalPosition(Game game, Station station)
        {
            if (game != null && game.City != null && game.City.CityLayer != null)
            {
                return game.City.CityLayer.LocalToGlobal(station.Position);
            }
            return station.Position;
        }

        private static bool TryReallocateAssets(
            Game game,
            UnlockGoal unlockGoal,
            Station critical,
            out string action)
        {
            action = null;
            if (game == null || game.City == null || game.Mode != GameMode.CLASSIC) return false;
            if (!game.CanMoveAssets || Time.unscaledTime < nextAssetReallocationAt) return false;
            if (PendingAssetEvaluations.Count > 0) return false;

            List<Station> stations = ActiveStations(game.City);
            AssetReallocationCandidate best = null;
            for (int targetIndex = 0; targetIndex < game.City.LineCount; targetIndex++)
            {
                Line target = game.City.GetLine(targetIndex);
                if (target == null || target.LiveLinkCount <= 0 || target.TrainCount <= 0) continue;
                if (critical != null
                    && StationRisk(critical) >= 0.58f
                    && !target.ContainsStation(critical)) continue;

                float targetPressure = LinePressure(target, stations);
                float targetNeed = LineCapacityNeed(target, targetPressure);
                for (int sourceIndex = 0; sourceIndex < game.City.LineCount; sourceIndex++)
                {
                    Line source = game.City.GetLine(sourceIndex);
                    if (source == null || source == target || source.LiveLinkCount <= 0) continue;

                    float sourcePressure = LinePressure(source, stations);
                    // The previous run moved assets at 0.27->0.46 and 0.54->0.72,
                    // then the monitored pressure got worse. Require a clear service
                    // gap and a genuinely cool source before taking an asset away.
                    if (sourcePressure > 0.28f || targetPressure < sourcePressure + 0.24f) continue;
                    float sourceNeed = LineCapacityNeed(source, sourcePressure);

                    AssetReallocationCandidate carriage = BuildCarriageReallocationCandidate(
                        game,
                        unlockGoal,
                        source,
                        target,
                        sourcePressure,
                        targetPressure,
                        sourceNeed,
                        targetNeed);
                    if (carriage != null && (best == null || carriage.Benefit > best.Benefit))
                    {
                        best = carriage;
                    }

                    AssetReallocationCandidate train = BuildTrainReallocationCandidate(
                        game,
                        unlockGoal,
                        source,
                        target,
                        sourcePressure,
                        targetPressure,
                        sourceNeed,
                        targetNeed);
                    if (train != null && (best == null || train.Benefit > best.Benefit))
                    {
                        best = train;
                    }
                }
            }

            if (best == null || best.Asset == null) return false;
            Railcar oldAsset = best.Asset;
            if (!best.Target.ApplyAsset(
                best.Type,
                null,
                LineDirection.FORWARDS,
                oldAsset,
                true,
                false,
                false))
            {
                nextAssetReallocationAt = Time.unscaledTime + 8f;
                return false;
            }

            Railcar movedAsset = oldAsset.LinkedRailcar;
            PendingAssetEvaluations.Add(new AssetReallocationEvaluation
            {
                Source = best.Source,
                Target = best.Target,
                MovedAsset = movedAsset,
                Type = best.Type,
                Benefit = best.Benefit,
                SourcePressureBefore = best.SourcePressure,
                TargetPressureBefore = best.TargetPressure,
                EvaluateAfter = Time.unscaledTime + 20f
            });
            lastAssetSource = best.Source;
            lastAssetTarget = best.Target;
            lastAssetType = best.Type;
            reverseAssetBlockUntil = Time.unscaledTime + 55f;
            nextAssetReallocationAt = Time.unscaledTime + 24f;

            string kind = best.Type == AssetType.Carriage ? "carriage" : "train";
            action = "Reallocated " + kind
                + " L" + (best.Source.Index + 1)
                + "->L" + (best.Target.Index + 1)
                + " benefit=" + best.Benefit.ToString("0.00")
                + " pressure=" + best.SourcePressure.ToString("0.00")
                + "->" + best.TargetPressure.ToString("0.00");
            return true;
        }

        private static AssetReallocationCandidate BuildCarriageReallocationCandidate(
            Game game,
            UnlockGoal unlockGoal,
            Line source,
            Line target,
            float sourcePressure,
            float targetPressure,
            float sourceNeed,
            float targetNeed)
        {
            if (IsReverseAssetMove(source, target, AssetType.Carriage)) return null;
            if (!UnlockInspector.AllowsReallocation(
                game, unlockGoal, RestrictionType.Carriage, target)) return null;

            int minimumSourceCarriages = sourcePressure >= 0.32f
                ? Mathf.Max(0, source.TrainCount - 1)
                : 0;
            if (source.CarriageCount <= minimumSourceCarriages) return null;

            int desiredTargetCarriages = target.TrainCount * (targetPressure >= 0.70f ? 2 : 1);
            if (target.CarriageCount >= desiredTargetCarriages && targetPressure < 0.92f) return null;
            if (targetPressure < 0.90f) return null;

            Railcar carriage = FindMovableCarriage(source);
            if (carriage == null) return null;

            float shortage = Mathf.Max(0, desiredTargetCarriages - target.CarriageCount);
            float benefit = targetNeed - sourceNeed
                + shortage * 0.30f
                + targetPressure * 0.45f
                - sourcePressure * 0.35f;
            if (benefit < 0.78f + carriageReallocationBias) return null;
            return new AssetReallocationCandidate
            {
                Source = source,
                Target = target,
                Asset = carriage,
                Type = AssetType.Carriage,
                Benefit = benefit,
                SourcePressure = sourcePressure,
                TargetPressure = targetPressure
            };
        }

        private static AssetReallocationCandidate BuildTrainReallocationCandidate(
            Game game,
            UnlockGoal unlockGoal,
            Line source,
            Line target,
            float sourcePressure,
            float targetPressure,
            float sourceNeed,
            float targetNeed)
        {
            if (target.TrainCount >= game.MaxLocomotivesPerRoute) return null;
            int sourceFloor = Mathf.Max(1, 1 + source.LiveLinkCount / 7);
            if (sourcePressure >= 0.42f) sourceFloor++;
            if (source.TrainCount <= sourceFloor) return null;

            Railcar locomotive = FindMovableLocomotive(source);
            if (locomotive == null || IsReverseAssetMove(source, target, locomotive.AssetType)) return null;
            if (!UnlockInspector.AllowsReallocation(
                game, unlockGoal, RestrictionType.Locomotive, target)) return null;

            int desiredTargetTrains = Mathf.Clamp(
                1 + target.LiveLinkCount / 4 + (targetPressure >= 0.68f ? 1 : 0),
                1,
                game.MaxLocomotivesPerRoute);
            float shortage = Mathf.Max(0, desiredTargetTrains - target.TrainCount);
            if (shortage <= 0f && targetPressure < 0.90f) return null;
            if (targetPressure < 0.80f) return null;

            float benefit = targetNeed - sourceNeed
                + shortage * 0.42f
                + targetPressure * 0.55f
                - sourcePressure * 0.40f;
            if (benefit < 0.92f + trainReallocationBias) return null;
            return new AssetReallocationCandidate
            {
                Source = source,
                Target = target,
                Asset = locomotive,
                Type = locomotive.AssetType,
                Benefit = benefit,
                SourcePressure = sourcePressure,
                TargetPressure = targetPressure
            };
        }

        private static Railcar FindMovableCarriage(Line line)
        {
            Railcar best = null;
            int bestTrainLoad = Int32.MaxValue;
            for (int trainIndex = 0; trainIndex < line.TrainCount; trainIndex++)
            {
                Train train = line.GetTrain(trainIndex);
                if (train == null || train.HasMothballedRailcar) continue;
                for (int railcarIndex = train.RailcarCount - 1; railcarIndex >= 1; railcarIndex--)
                {
                    Railcar railcar = train.GetRailcar(railcarIndex);
                    if (railcar == null
                        || railcar.AssetType != AssetType.Carriage
                        || railcar.PeepCount > 0
                        || railcar.LinkedRailcar != null) continue;
                    if (train.PeepCount < bestTrainLoad)
                    {
                        best = railcar;
                        bestTrainLoad = train.PeepCount;
                    }
                }
            }
            return best;
        }

        private static Railcar FindMovableLocomotive(Line line)
        {
            Railcar best = null;
            int bestRailcarCount = Int32.MaxValue;
            for (int trainIndex = 0; trainIndex < line.TrainCount; trainIndex++)
            {
                Train train = line.GetTrain(trainIndex);
                if (train == null
                    || train.Locomotive == null
                    || train.PeepCount > 0
                    || train.HasMothballedRailcar
                    || train.Locomotive.LinkedRailcar != null) continue;
                if (train.RailcarCount < bestRailcarCount)
                {
                    best = train.Locomotive;
                    bestRailcarCount = train.RailcarCount;
                }
            }
            return best;
        }

        private static float LineCapacityNeed(Line line, float pressure)
        {
            float onboardPerTrain = (float)line.PeepCount / Mathf.Max(1, line.TrainCount);
            return pressure * 1.75f
                + (line.LiveLinkCount + 1) * 0.075f
                + onboardPerTrain * 0.035f
                - line.TrainCount * 0.42f
                - line.CarriageCount * 0.16f;
        }

        private static bool IsReverseAssetMove(Line source, Line target, AssetType type)
        {
            if (Time.unscaledTime >= reverseAssetBlockUntil) return false;
            if (!System.Object.ReferenceEquals(source, lastAssetTarget)
                || !System.Object.ReferenceEquals(target, lastAssetSource)) return false;
            if (type == AssetType.Carriage) return lastAssetType == AssetType.Carriage;
            return Asset.IsLocomotive(type) && Asset.IsLocomotive(lastAssetType);
        }

        private static bool EvaluatePendingAssetReallocations(
            Game game,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (game == null || game.City == null) return false;
            List<Station> stations = ActiveStations(game.City);
            for (int i = PendingAssetEvaluations.Count - 1; i >= 0; i--)
            {
                AssetReallocationEvaluation evaluation = PendingAssetEvaluations[i];
                if (Time.unscaledTime < evaluation.EvaluateAfter) continue;

                float sourceAfter = LinePressure(evaluation.Source, stations);
                float targetAfter = LinePressure(evaluation.Target, stations);
                float improvement = evaluation.TargetPressureBefore - targetAfter
                    - Mathf.Max(0f, sourceAfter - evaluation.SourcePressureBefore) * 0.75f;
                bool effective = improvement >= 0.035f
                    || targetAfter <= evaluation.TargetPressureBefore * 0.88f;
                bool harmful = improvement < -0.08f
                    && sourceAfter >= Mathf.Max(0.70f, evaluation.SourcePressureBefore + 0.16f);

                if (harmful
                    && TryRollbackAssetReallocation(game, unlockGoal, evaluation, sourceAfter, targetAfter, out action))
                {
                    PendingAssetEvaluations.RemoveAt(i);
                    nextAssetReallocationAt = Time.unscaledTime + 40f;
                    IncreaseAssetReallocationBias(evaluation.Type, 0.18f);
                    return true;
                }

                if (effective) IncreaseAssetReallocationBias(evaluation.Type, -0.05f);
                else IncreaseAssetReallocationBias(evaluation.Type, 0.12f);
                UnityEngine.Debug.Log(
                    "[Auto Planner][ReallocationEffect] type=" + evaluation.Type
                    + " L" + (evaluation.Source.Index + 1)
                    + "->L" + (evaluation.Target.Index + 1)
                    + " estimated=" + evaluation.Benefit.ToString("0.00")
                    + " improvement=" + improvement.ToString("0.00")
                    + " source=" + evaluation.SourcePressureBefore.ToString("0.00")
                    + "->" + sourceAfter.ToString("0.00")
                    + " target=" + evaluation.TargetPressureBefore.ToString("0.00")
                    + "->" + targetAfter.ToString("0.00")
                    + " effective=" + effective);
                PendingAssetEvaluations.RemoveAt(i);
            }
            return false;
        }

        private static bool TryRollbackAssetReallocation(
            Game game,
            UnlockGoal unlockGoal,
            AssetReallocationEvaluation evaluation,
            float sourceAfter,
            float targetAfter,
            out string action)
        {
            action = null;
            Railcar moved = evaluation.MovedAsset;
            if (!game.CanMoveAssets
                || moved == null
                || moved.Train == null
                || moved.Train.Line != evaluation.Target
                || moved.PeepCount > 0
                || evaluation.Source == null
                || evaluation.Source.LiveLinkCount <= 0) return false;

            RestrictionType restriction = evaluation.Type == AssetType.Carriage
                ? RestrictionType.Carriage
                : RestrictionType.Locomotive;
            if (!UnlockInspector.AllowsReallocation(
                game, unlockGoal, restriction, evaluation.Source)) return false;
            if (!evaluation.Source.ApplyAsset(
                evaluation.Type,
                null,
                LineDirection.FORWARDS,
                moved,
                true,
                false,
                false)) return false;

            lastAssetSource = evaluation.Target;
            lastAssetTarget = evaluation.Source;
            lastAssetType = evaluation.Type;
            reverseAssetBlockUntil = Time.unscaledTime + 70f;
            string kind = evaluation.Type == AssetType.Carriage ? "carriage" : "train";
            action = "Rolled back " + kind
                + " reallocation L" + (evaluation.Target.Index + 1)
                + "->L" + (evaluation.Source.Index + 1)
                + " sourcePressure=" + sourceAfter.ToString("0.00")
                + " targetPressure=" + targetAfter.ToString("0.00");
            return true;
        }

        private static void IncreaseAssetReallocationBias(AssetType type, float delta)
        {
            if (type == AssetType.Carriage)
            {
                carriageReallocationBias = Mathf.Clamp(carriageReallocationBias + delta, 0f, 0.65f);
            }
            else
            {
                trainReallocationBias = Mathf.Clamp(trainReallocationBias + delta, 0f, 0.65f);
            }
        }

        private static bool TryUpgradeInterchange(
            Game game,
            Station station,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Hub, null, 1)) return false;
            float now = Time.unscaledTime;
            if (now < nextInterchangeActionAt) return false;

            List<Station> stations = ActiveStations(game.City);
            Station best = null;
            float bestBenefit = Single.MinValue;
            for (int i = 0; i < stations.Count; i++)
            {
                Station candidate = stations[i];
                if (candidate == null || candidate.IsInterchange || candidate.LineCount <= 0) continue;
                float benefit = InterchangeBenefit(candidate);
                if (candidate == station) benefit += 24f;
                if (benefit > bestBenefit)
                {
                    bestBenefit = benefit;
                    best = candidate;
                }
            }
            int available = game.AssetDatabase.GetAvailableAssets(AssetType.Interchange);
            if (available > 0 && best != null && bestBenefit >= 110f)
            {
                if (!best.ApplyAsset(AssetType.Interchange, true)) return false;
                InterchangeLowSince.Remove(best);
                nextInterchangeActionAt = now + 20f;
                lastInterchangeFrom = null;
                lastInterchangeTo = best;
                action = "Upgraded interchange at " + StationLabel(best)
                    + " expectedBenefit=" + bestBenefit.ToString("0");
                return true;
            }

            if (available <= 0 && best != null && bestBenefit >= 170f && game.CanMoveAssets)
            {
                Station donor = null;
                float donorBenefit = Single.MaxValue;
                for (int i = 0; i < stations.Count; i++)
                {
                    Station candidate = stations[i];
                    if (candidate == null || !candidate.IsInterchange || candidate == best) continue;
                    if (candidate.NumPeepsOverCapacity > 0 || candidate.PeepCount > 5) continue;
                    float benefit = InterchangeBenefit(candidate);
                    if (benefit < donorBenefit)
                    {
                        donorBenefit = benefit;
                        donor = candidate;
                    }
                }

                bool reverseMove = donor != null
                    && System.Object.ReferenceEquals(donor, lastInterchangeTo)
                    && System.Object.ReferenceEquals(best, lastInterchangeFrom)
                    && now < interchangeReverseBlockUntil;
                if (!reverseMove && donor != null && bestBenefit >= donorBenefit + 120f
                    && RemoveInterchangeAsset(game, donor))
                {
                    if (best.ApplyAsset(AssetType.Interchange, true))
                    {
                        InterchangeLowSince.Remove(donor);
                        InterchangeLowSince.Remove(best);
                        lastInterchangeFrom = donor;
                        lastInterchangeTo = best;
                        interchangeReverseBlockUntil = now + 180f;
                        nextInterchangeActionAt = now + 90f;
                        action = "Relocated interchange " + StationLabel(donor)
                            + "->" + StationLabel(best)
                            + " benefit=" + donorBenefit.ToString("0")
                            + "->" + bestBenefit.ToString("0");
                        return true;
                    }

                    // The asset was released before applying to the target. If the target
                    // rejected it, restore the original hub instead of losing protection.
                    donor.ApplyAsset(AssetType.Interchange, true);
                }
            }

            if (available <= 0 && (station == null || StationRisk(station) < 0.42f)
                && game.CanMoveAssets)
            {
                for (int i = 0; i < stations.Count; i++)
                {
                    Station candidate = stations[i];
                    if (candidate == null || !candidate.IsInterchange) continue;
                    bool safelyIdle = candidate.PeepCount <= 1
                        && candidate.NumPeepsOverCapacity <= 0
                        && StationRisk(candidate) < 0.22f
                        && candidate.LineCount <= 2;
                    if (!safelyIdle)
                    {
                        InterchangeLowSince.Remove(candidate);
                        continue;
                    }

                    float lowSince;
                    if (!InterchangeLowSince.TryGetValue(candidate, out lowSince))
                    {
                        InterchangeLowSince[candidate] = now;
                        continue;
                    }
                    if (now - lowSince < 120f) continue;
                    if (!RemoveInterchangeAsset(game, candidate)) continue;
                    InterchangeLowSince.Remove(candidate);
                    lastInterchangeFrom = candidate;
                    lastInterchangeTo = null;
                    interchangeReverseBlockUntil = now + 180f;
                    nextInterchangeActionAt = now + 120f;
                    action = "Removed underused interchange at " + StationLabel(candidate);
                    return true;
                }
            }
            return false;
        }

        private static float InterchangeBenefit(Station station)
        {
            float benefit = StationRisk(station) * 205f
                + station.PeepCount * 8f
                + Mathf.Max(0, station.LineCount - 1) * 78f
                + Mathf.Clamp(station.Centrality, 0f, 3f) * 24f;
            if (station.NumPeepsOverCapacity > 0)
            {
                benefit += 210f + station.ExpiryTimerCompletion * 180f;
            }
            if (station.LineCount >= 3) benefit += 35f;
            return benefit;
        }

        private static float MaximumInterchangeNeed(City city)
        {
            if (city == null) return 0f;
            float maximum = 0f;
            for (int i = 0; i < city.StationCount; i++)
            {
                Station station = city.GetStation(i);
                if (station == null || !station.IsActive || station.LineCount <= 0
                    || station.IsInterchange) continue;
                maximum = Mathf.Max(maximum, InterchangeBenefit(station));
            }
            return maximum;
        }

        private static bool RemoveInterchangeAsset(Game game, Station station)
        {
            if (game == null || station == null || !station.IsInterchange
                || StationInterchangeField == null || station.PeepCount > 5
                || station.NumPeepsOverCapacity > 0) return false;
            try
            {
                StationInterchangeField.SetValue(station, false);
                station.InterchangeScale = 1f;
                if (UpdatePeepContainerMethod != null)
                {
                    UpdatePeepContainerMethod.Invoke(station, null);
                }
                if (RecalculatePeepAlphasMethod != null)
                {
                    RecalculatePeepAlphasMethod.Invoke(station, new object[] { 0 });
                }
                game.AssetDatabase.ReleaseAsset(AssetType.Interchange);
                return !station.IsInterchange;
            }
            catch (Exception exception)
            {
                StationInterchangeField.SetValue(station, true);
                UnityEngine.Debug.LogWarning(
                    "[Auto Planner][Interchange] Removal recovered: " + exception.Message);
                return false;
            }
        }

        private static bool TryAddCapacity(
            Game game,
            Station station,
            UnlockGoal unlockGoal,
            out string action)
        {
            action = null;
            if (station == null || station.Lines == null || station.Lines.Count == 0) return false;

            Line best = null;
            float bestScore = Single.MinValue;
            for (int i = 0; i < station.Lines.Count; i++)
            {
                Line line = station.Lines[i];
                if (line == null) continue;
                float score = LinePressure(line, ActiveStations(game.City)) * 120f
                    + line.PeepCount * 4f
                    - line.TrainCount * 8f
                    - line.CarriageCount * 3f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = line;
                }
            }
            if (best == null) return false;

            int desiredCarriages = Mathf.Max(1, best.TrainCount) * (StationRisk(station) >= 0.78f ? 2 : 1);
            if (best.CarriageCount < desiredCarriages
                && game.AssetDatabase.GetAvailableAssets(AssetType.Carriage) > 0
                && UnlockInspector.Allows(game, unlockGoal, RestrictionType.Carriage, best, 1)
                && best.ApplyAsset(AssetType.Carriage, null, LineDirection.FORWARDS, null, true, false, false))
            {
                action = "Added carriage to line " + (best.Index + 1);
                return true;
            }

            bool reserveLastTrain = AvailableTrainCount(game) <= 1
                && ShouldReserveTrainForNewLine(game)
                && StationRisk(station) < 1.0f;
            if (!reserveLastTrain
                && best.TrainCount < game.MaxLocomotivesPerRoute
                && TryApplyTrain(game, best, unlockGoal))
            {
                action = "Added train to line " + (best.Index + 1);
                return true;
            }
            return false;
        }

        private static bool TryBalanceFleet(Game game, UnlockGoal unlockGoal, out string action)
        {
            action = null;
            Line bestTrainLine = null;
            float bestTrainNeed = 0f;
            Line bestCarriageLine = null;
            float bestCarriageNeed = 0f;
            List<Station> stations = ActiveStations(game.City);

            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null || line.LiveLinkCount <= 0) continue;
                float pressure = LinePressure(line, stations);
                int desiredTrains = Mathf.Clamp(
                    1 + line.LiveLinkCount / 5 + (pressure > 0.78f ? 1 : 0),
                    1,
                    game.MaxLocomotivesPerRoute);
                float trainShortage = Mathf.Max(0, desiredTrains - line.TrainCount);
                float routeCoverageGap = Mathf.Max(0, line.LiveLinkCount - line.TrainCount * 5);
                float trainNeed = pressure * 2.4f
                    + trainShortage * 0.75f
                    + routeCoverageGap * 0.06f
                    + line.PeepCount * 0.04f
                    - line.TrainCount * 0.12f;
                if (line.TrainCount < game.MaxLocomotivesPerRoute && trainNeed > bestTrainNeed)
                {
                    bestTrainNeed = trainNeed;
                    bestTrainLine = line;
                }

                int desiredCarriages = line.TrainCount * (pressure > 0.62f ? 2 : 1);
                float carriageNeed = desiredCarriages - line.CarriageCount + pressure;
                if (carriageNeed > bestCarriageNeed)
                {
                    bestCarriageNeed = carriageNeed;
                    bestCarriageLine = line;
                }
            }

            if (bestCarriageLine != null
                && bestCarriageNeed > 0.35f
                && game.AssetDatabase.GetAvailableAssets(AssetType.Carriage) > 0
                && UnlockInspector.Allows(game, unlockGoal, RestrictionType.Carriage, bestCarriageLine, 1)
                && bestCarriageLine.ApplyAsset(AssetType.Carriage, null, LineDirection.FORWARDS, null, true, false, false))
            {
                action = "Balanced carriage capacity on line " + (bestCarriageLine.Index + 1);
                return true;
            }

            bool reserveLastTrain = AvailableTrainCount(game) <= 1
                && ShouldReserveTrainForNewLine(game);
            if (!reserveLastTrain
                && bestTrainLine != null
                && bestTrainNeed > 0.35f
                && bestTrainLine.TrainCount < game.MaxLocomotivesPerRoute
                && TryApplyTrain(game, bestTrainLine, unlockGoal))
            {
                action = "Balanced trains on line " + (bestTrainLine.Index + 1);
                return true;
            }
            return false;
        }

        private static bool TryApplyTrain(Game game, Line line, UnlockGoal unlockGoal)
        {
            if (!UnlockInspector.Allows(game, unlockGoal, RestrictionType.Locomotive, line, 1))
            {
                return false;
            }
            AssetType preferred = game.AssetDatabase.AvailableLocomotiveAsset;
            if (preferred != AssetType.None
                && game.AssetDatabase.GetAvailableAssets(preferred) > 0
                && line.ApplyAsset(preferred, null, LineDirection.FORWARDS, null, true, false, false))
            {
                return true;
            }

            for (int i = 0; i < TrainTypes.Length; i++)
            {
                AssetType type = TrainTypes[i];
                if (type == preferred || game.AssetDatabase.GetAvailableAssets(type) <= 0) continue;
                if (line.ApplyAsset(type, null, LineDirection.FORWARDS, null, true, false, false)) return true;
            }
            return false;
        }

        private static Station BestNextStation(Station from, List<Station> route, List<Station> candidates)
        {
            Station best = null;
            float bestScore = Single.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                Station candidate = candidates[i];
                if (IsConnectionBlocked(from, candidate)) continue;
                float score = RouteFlowOptimizer.OctilinearDistance(from, candidate)
                    - StationRisk(candidate) * 90f;
                int sameShape = 0;
                for (int r = 0; r < route.Count; r++)
                {
                    if (route[r].Type == candidate.Type) sameShape++;
                }
                score += sameShape * 75f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private static bool BuilderHasNoCrossing(Game game, LineBuilder builder)
        {
            if (game == null || game.City == null || game.City.LinkErrorSystem == null || builder == null)
            {
                return false;
            }

            Line line = builder.Line;
            if (line == null) return false;
            for (int i = 0; i < line.Count; i++)
            {
                Link link = line[i];
                if (link != null && game.City.LinkErrorSystem.HasError(link, typeof(NoCrossing)))
                {
                    return true;
                }
            }
            return false;
        }

        private static void NoteCrossingBlocked(
            Game game,
            Station from,
            Station to,
            string operation,
            bool essential)
        {
            EnsureSession(game);
            crossingFailures++;
            float now = Time.unscaledTime;
            float retryAfter = now + Mathf.Min(45f, 12f + crossingFailures * 4f);
            float demandUntil = now + 180f;
            BlockedConnection existing = null;
            for (int i = 0; i < BlockedConnections.Count; i++)
            {
                BlockedConnection item = BlockedConnections[i];
                if ((System.Object.ReferenceEquals(item.From, from) && System.Object.ReferenceEquals(item.To, to))
                    || (System.Object.ReferenceEquals(item.From, to) && System.Object.ReferenceEquals(item.To, from)))
                {
                    existing = item;
                    break;
                }
            }
            if (existing == null)
            {
                BlockedConnections.Add(new BlockedConnection
                {
                    From = from,
                    To = to,
                    RetryAfter = retryAfter,
                    DemandUntil = demandUntil,
                    Essential = essential
                });
            }
            else
            {
                existing.RetryAfter = retryAfter;
                existing.DemandUntil = demandUntil;
                existing.Essential = existing.Essential || essential;
            }

            int crossingInventory = CrossingResourceCount(game);
            if (essential && !waitingForCrossingResource)
            {
                waitingForCrossingResource = true;
                crossingInventoryAtBlock = crossingInventory;
                UnityEngine.Debug.LogWarning(
                    "[Auto Planner][Infrastructure] WAIT crossingStock=" + crossingInventory
                    + " reason=NoCrossing operation=" + operation
                    + " resumeWhenStockAbove=" + crossingInventoryAtBlock);
            }

            UnityEngine.Debug.LogWarning(
                "[Auto Planner][Blocked] NoCrossing operation=" + operation
                + " from=" + StationLabel(from)
                + " to=" + StationLabel(to)
                + " failures=" + crossingFailures
                + " infrastructureWait=" + waitingForCrossingResource
                + " | " + Snapshot(game));
        }

        private static void RefreshCrossingResourceWait(Game game)
        {
            if (!waitingForCrossingResource || game == null || game.AssetDatabase == null) return;
            int current = CrossingResourceCount(game);
            if (current <= crossingInventoryAtBlock) return;

            waitingForCrossingResource = false;
            int previous = crossingInventoryAtBlock;
            crossingInventoryAtBlock = -1;
            float now = Time.unscaledTime;
            for (int i = 0; i < BlockedConnections.Count; i++)
            {
                BlockedConnections[i].RetryAfter = 0f;
                BlockedConnections[i].DemandUntil = Mathf.Max(
                    BlockedConnections[i].DemandUntil,
                    now + 180f);
            }
            UnityEngine.Debug.Log(
                "[Auto Planner][Infrastructure] READY crossingStock=" + current
                + " previous=" + previous
                + " track construction resumed");
        }

        private static bool IsConnectionBlocked(Station from, Station to)
        {
            float now = Time.unscaledTime;
            for (int i = BlockedConnections.Count - 1; i >= 0; i--)
            {
                BlockedConnection item = BlockedConnections[i];
                if (!waitingForCrossingResource && item.DemandUntil <= now)
                {
                    BlockedConnections.RemoveAt(i);
                    continue;
                }
                if ((System.Object.ReferenceEquals(item.From, from) && System.Object.ReferenceEquals(item.To, to))
                    || (System.Object.ReferenceEquals(item.From, to) && System.Object.ReferenceEquals(item.To, from)))
                {
                    return (waitingForCrossingResource && item.Essential)
                        || item.RetryAfter > now;
                }
            }
            return false;
        }

        private static bool HasActiveCrossingDemand(Game game)
        {
            if (game == null || game.City == null) return false;
            float now = Time.unscaledTime;
            bool activeDemand = false;
            for (int i = BlockedConnections.Count - 1; i >= 0; i--)
            {
                BlockedConnection item = BlockedConnections[i];
                if (item.DemandUntil <= now || item.From == null || item.To == null)
                {
                    BlockedConnections.RemoveAt(i);
                    continue;
                }

                if (!item.Essential) continue;
                bool fromNeedsConnection = item.From.IsActive
                    && item.From.CanBeConnected
                    && item.From.LineCount == 0;
                bool toNeedsConnection = item.To.IsActive
                    && item.To.CanBeConnected
                    && item.To.LineCount == 0;
                if (fromNeedsConnection || toNeedsConnection) activeDemand = true;
            }
            if (!activeDemand && waitingForCrossingResource)
            {
                waitingForCrossingResource = false;
                crossingInventoryAtBlock = -1;
                UnityEngine.Debug.Log(
                    "[Auto Planner][Infrastructure] CLEARED no essential blocked station remains");
            }
            return activeDemand;
        }

        private static bool RouteContainsBlockedConnection(List<Station> route)
        {
            for (int i = 1; i < route.Count; i++)
            {
                if (IsConnectionBlocked(route[i - 1], route[i])) return true;
            }
            return false;
        }

        private static bool HasAvailableCrossing(Game game)
        {
            return CrossingResourceCount(game) > 0;
        }

        private static int CrossingResourceCount(Game game)
        {
            return game != null && game.AssetDatabase != null
                ? game.AssetDatabase.GetAvailableAssets(AssetType.Crossing)
                    + game.AssetDatabase.GetAvailableAssets(AssetType.Bridge)
                : 0;
        }

        private static void EnsureSession(Game game)
        {
            if (!System.Object.ReferenceEquals(sessionGame, game)
                || !System.Object.ReferenceEquals(sessionCity, game != null ? game.City : null))
            {
                ResetSession(game);
            }
        }

        private static string Snapshot(Game game)
        {
            if (game == null || game.City == null) return "no-city";
            City city = game.City;
            string cityId = city.Definition != null ? city.Definition.Id : "unknown";
            int day = city.Clock != null ? city.Clock.Day : 0;
            List<Station> activeStations = ActiveStations(city);
            Station critical = FindMostPressuredStation(activeStations);
            PassengerFlowAnalysis flow = RouteFlowOptimizer.Analyze(
                city, activeStations, null, null);
            string criticalText = critical != null
                ? StationLabel(critical) + " queue=" + critical.PeepCount + "/" + critical.PeepCapacity
                    + " pressure=" + Pressure(critical).ToString("0.00")
                    + " risk=" + StationRisk(critical).ToString("0.00")
                    + " eta=" + StationServiceEta(critical).ToString("0.0")
                    + " hub=" + critical.IsInterchange
                : "none";
            return "city=" + cityId
                + " mode=" + game.Mode
                + " daily=" + game.IsDailyChallenge
                + " score=" + game.Score
                + " day=" + day
                + " stations=" + city.StationCount
                + " unconnected=" + CountUnconnected(city)
                + " lines=" + city.LineCount
                + " spare[line/train/carriage/crossing/hub]="
                + game.AssetDatabase.GetAvailableAssets(AssetType.Line) + "/"
                + AvailableTrainCount(game) + "/"
                + game.AssetDatabase.GetAvailableAssets(AssetType.Carriage) + "/"
                + (game.AssetDatabase.GetAvailableAssets(AssetType.Crossing)
                    + game.AssetDatabase.GetAvailableAssets(AssetType.Bridge)) + "/"
                + game.AssetDatabase.GetAvailableAssets(AssetType.Interchange)
                + " crossingWait=" + waitingForCrossingResource
                + " vision=" + VisionControlBridge.Current.Summary
                + " activeHubs=" + CountInterchanges(city)
                + " flow=[" + flow.Summary + "]"
                + " critical=" + criticalText
                + " routes=" + RouteSummary(game);
        }

        private static string RouteSummary(Game game)
        {
            if (game == null || game.City == null) return "none";
            List<string> routes = new List<string>();
            List<Station> stations = ActiveStations(game.City);
            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null) continue;
                int exclusiveStations = 0;
                int waitingPeeps = 0;
                for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
                {
                    Station station = stations[stationIndex];
                    if (!line.ContainsStation(station)) continue;
                    waitingPeeps += station.PeepCount;
                    if (station.LineCount <= 1) exclusiveStations++;
                }
                routes.Add(
                    "L" + (line.Index + 1)
                    + "[s" + (line.LiveLinkCount + 1)
                    + ",t" + line.TrainCount
                    + ",c" + line.CarriageCount
                    + ",p" + LinePressure(line, stations).ToString("0.00")
                    + ",q" + waitingPeeps
                    + ",e" + exclusiveStations
                    + LineMonitorSummary(line) + "]");
            }
            return routes.Count > 0 ? String.Join(",", routes.ToArray()) : "none";
        }

        private static string LineMonitorSummary(Line line)
        {
            LineMonitor monitor;
            if (!LineMonitors.TryGetValue(line, out monitor)) return String.Empty;
            return ",load" + monitor.LoadRatio.ToString("0.00")
                + ",spd" + monitor.SpeedRatio.ToString("0.00")
                + ",hw" + monitor.Headway.ToString("0")
                + ",x" + monitor.CrossingLinks + "/" + monitor.OccupiedCrossings
                + ",op" + monitor.OperationalPenalty.ToString("0.00");
        }

        private static float StationServiceEta(Station station)
        {
            StationMonitor monitor;
            return station != null && StationMonitors.TryGetValue(station, out monitor)
                ? monitor.ServiceEta
                : 999f;
        }

        private static int CountInterchanges(City city)
        {
            int count = 0;
            if (city == null) return count;
            for (int i = 0; i < city.StationCount; i++)
            {
                Station station = city.GetStation(i);
                if (station != null && station.IsInterchange) count++;
            }
            return count;
        }

        private static int AvailableTrainCount(Game game)
        {
            int count = 0;
            for (int i = 0; i < TrainTypes.Length; i++)
            {
                count += game.AssetDatabase.GetAvailableAssets(TrainTypes[i]);
            }
            return count;
        }

        private static string StationLabel(Station station)
        {
            if (station == null) return "none";
            return station.Type + "@(" + station.Position.x.ToString("0") + ","
                + station.Position.y.ToString("0") + ")";
        }

        private static Station BestTransferHub(List<Station> stations, Station from, List<Station> route)
        {
            Station best = null;
            float bestScore = Single.MaxValue;
            for (int i = 0; i < stations.Count; i++)
            {
                Station candidate = stations[i];
                if (candidate.LineCount <= 0 || route.Contains(candidate)) continue;
                int matchingShape = 0;
                for (int r = 0; r < route.Count; r++)
                {
                    if (route[r].Type == candidate.Type) matchingShape++;
                }
                float score = RouteFlowOptimizer.OctilinearDistance(from, candidate)
                    - candidate.LineCount * 55f
                    - StationRisk(candidate) * 35f
                    + matchingShape * 85f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private static Station NearestOtherStation(Station station, List<Station> stations)
        {
            Station best = null;
            float bestDistance = Single.MaxValue;
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i] == station) continue;
                float distance = RouteFlowOptimizer.OctilinearDistance(station, stations[i]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = stations[i];
                }
            }
            return best;
        }

        private static Station HighestPriorityStation(List<Station> stations)
        {
            Station best = stations[0];
            float bestScore = Single.MinValue;
            for (int i = 0; i < stations.Count; i++)
            {
                float score = stations[i].PeepCount * 20f + Pressure(stations[i]) * 100f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = stations[i];
                }
            }
            return best;
        }

        private static Station FindMostPressuredStation(List<Station> stations)
        {
            Station best = null;
            float bestPressure = -1f;
            for (int i = 0; i < stations.Count; i++)
            {
                if (stations[i].LineCount <= 0) continue;
                float pressure = StationRisk(stations[i]);
                if (pressure > bestPressure)
                {
                    bestPressure = pressure;
                    best = stations[i];
                }
            }
            return best;
        }

        private static List<Station> ActiveStations(City city)
        {
            List<Station> result = new List<Station>();
            if (city == null) return result;
            for (int i = 0; i < city.StationCount; i++)
            {
                Station station = city.GetStation(i);
                if (station != null && station.IsActive && station.CanBeConnected) result.Add(station);
            }
            return result;
        }

        private static int CountUnconnected(City city)
        {
            int count = 0;
            for (int i = 0; i < city.StationCount; i++)
            {
                Station station = city.GetStation(i);
                if (station != null && station.IsActive && station.CanBeConnected && station.LineCount == 0) count++;
            }
            return count;
        }

        private static int LongestRoute(City city)
        {
            int longest = 0;
            if (city == null) return longest;
            for (int i = 0; i < city.LineCount; i++)
            {
                Line line = city.GetLine(i);
                if (line != null) longest = Mathf.Max(longest, line.LiveLinkCount);
            }
            return longest;
        }

        private static float MaximumPressure(City city)
        {
            float maximum = 0f;
            for (int i = 0; i < city.StationCount; i++)
            {
                Station station = city.GetStation(i);
                if (station != null && station.IsActive)
                {
                    maximum = Mathf.Max(maximum, StationRisk(station));
                }
            }
            return maximum;
        }

        private static float Pressure(Station station)
        {
            if (station == null) return 0f;
            int capacity = Mathf.Max(1, station.PeepCapacity);
            float queue = (float)station.PeepCount / capacity;
            float overflow = station.NumPeepsOverCapacity > 0
                ? 0.35f + station.ExpiryTimerCompletion
                : 0f;
            return queue + overflow;
        }

        private static float StationRisk(Station station)
        {
            if (station == null) return 0f;
            StationMonitor monitor;
            return StationMonitors.TryGetValue(station, out monitor)
                ? monitor.Risk
                : Pressure(station);
        }

        private static float LinePressure(Line line, List<Station> stations)
        {
            float maximum = 0f;
            float total = 0f;
            int count = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (!line.ContainsStation(stations[i])) continue;
                float pressure = Pressure(stations[i]);
                maximum = Mathf.Max(maximum, pressure);
                total += pressure;
                count++;
            }
            float average = count > 0 ? total / count : 0f;
            LineMonitor monitor;
            float monitored = 0f;
            if (LineMonitors.TryGetValue(line, out monitor))
            {
                monitored = monitor.OperationalPenalty
                    + Mathf.Max(0f, monitor.LoadRatio - 0.55f) * 0.28f;
            }
            return maximum * 0.7f + average * 0.3f
                + line.PeepCount * 0.025f + monitored;
        }

        private static void UpdateNetworkMonitoring(Game game, List<Station> stations)
        {
            if (game == null || game.City == null) return;
            Dictionary<Line, LineMonitor> updatedLines = new Dictionary<Line, LineMonitor>();
            for (int i = 0; i < game.City.LineCount; i++)
            {
                Line line = game.City.GetLine(i);
                if (line == null || line.LiveLinkCount <= 0) continue;
                LineMonitor previous;
                LineMonitors.TryGetValue(line, out previous);
                updatedLines[line] = BuildLineMonitor(line, stations, previous);
            }
            LineMonitors.Clear();
            foreach (KeyValuePair<Line, LineMonitor> item in updatedLines)
            {
                LineMonitors[item.Key] = item.Value;
            }

            StationMonitors.Clear();
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                float queuePressure = Pressure(station);
                float serviceEta = EstimateStationServiceEta(station);
                float bestOperationalPenalty = 0.75f;
                bool foundLine = false;
                if (station.Lines != null)
                {
                    for (int lineIndex = 0; lineIndex < station.Lines.Count; lineIndex++)
                    {
                        LineMonitor lineMonitor;
                        if (!LineMonitors.TryGetValue(station.Lines[lineIndex], out lineMonitor)) continue;
                        if (!foundLine || lineMonitor.OperationalPenalty < bestOperationalPenalty)
                        {
                            bestOperationalPenalty = lineMonitor.OperationalPenalty;
                            foundLine = true;
                        }
                    }
                }
                if (!foundLine) bestOperationalPenalty = 0.75f;

                float demandWeight = Mathf.Clamp01(0.25f + queuePressure);
                float etaPenalty = serviceEta >= 999f
                    ? 0.40f
                    : Mathf.Min(0.38f, serviceEta / 180f) * demandWeight;
                float operationalPenalty = bestOperationalPenalty
                    * (queuePressure > 0.25f ? 0.22f : 0.08f);
                StationMonitors[station] = new StationMonitor
                {
                    Station = station,
                    QueuePressure = queuePressure,
                    Risk = queuePressure + etaPenalty + operationalPenalty,
                    ServiceEta = serviceEta,
                    Centrality = station.Centrality,
                    Service = station.Service
                };
            }
        }

        private static LineMonitor BuildLineMonitor(
            Line line,
            List<Station> stations,
            LineMonitor previous)
        {
            int stationCount = 0;
            int crossingLinks = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (line.ContainsStation(stations[i])) stationCount++;
            }
            for (int i = 0; i < line.Count; i++)
            {
                Link link = line[i];
                if (link != null && link.State != LinkState.MOTHBALLED && link.HasCrossing)
                {
                    crossingLinks++;
                }
            }

            int capacity = 0;
            int passengers = 0;
            int occupiedCrossings = 0;
            float speedRatioTotal = 0f;
            float cycleTotal = 0f;
            float topSpeedTotal = 0f;
            int speedSamples = 0;
            int cycleSamples = 0;
            for (int trainIndex = 0; trainIndex < line.TrainCount; trainIndex++)
            {
                Train train = line.GetTrain(trainIndex);
                if (train == null) continue;
                passengers += train.PeepCount;
                for (int railcarIndex = 0; railcarIndex < train.RailcarCount; railcarIndex++)
                {
                    Railcar railcar = train.GetRailcar(railcarIndex);
                    if (railcar != null) capacity += railcar.Capacity;
                }
                if (train.Locomotive != null && train.Definition != null)
                {
                    float topSpeed = Mathf.Max(0.01f, train.Definition.Speed);
                    speedRatioTotal += Mathf.Clamp01(train.Locomotive.Speed / topSpeed);
                    topSpeedTotal += topSpeed;
                    speedSamples++;
                }
                if (train.EstimatedCycleTime > 0f)
                {
                    cycleTotal += train.EstimatedCycleTime;
                    cycleSamples++;
                }
                if (!train.Link.IsNull && train.Link.Link != null && train.Link.Link.HasCrossing)
                {
                    occupiedCrossings++;
                }
            }

            float speedSample = speedSamples > 0 ? speedRatioTotal / speedSamples : 0f;
            float speedRatio = previous != null
                ? Mathf.Lerp(previous.SpeedRatio, speedSample, 0.22f)
                : speedSample;
            float averageTopSpeed = speedSamples > 0 ? topSpeedTotal / speedSamples : 1f;
            float estimatedCycle = cycleSamples > 0
                ? cycleTotal / cycleSamples
                : line.Length / Mathf.Max(1f, averageTopSpeed)
                    * (line.IsLooping ? 1.15f : 2f) + stationCount * 2.5f;
            float cycleTime = previous != null && previous.CycleTime > 0f
                ? Mathf.Lerp(previous.CycleTime, estimatedCycle, 0.25f)
                : estimatedCycle;
            float headway = line.TrainCount > 0 ? cycleTime / line.TrainCount : 999f;
            float loadRatio = capacity > 0 ? (float)passengers / capacity : 1f;
            float lengthPerTrain = line.Length / Mathf.Max(1, line.TrainCount);
            float coveragePenalty = Mathf.Min(0.75f, Mathf.Max(0f, lengthPerTrain - 420f) / 900f);
            float headwayPenalty = Mathf.Min(0.65f, Mathf.Max(0f, headway - 35f) / 120f);
            float speedPenalty = line.PeepCount > 0
                ? Mathf.Max(0f, 0.62f - speedRatio) * 0.30f
                : 0f;
            float crossingPenalty = crossingLinks * 0.025f + occupiedCrossings * 0.045f;

            return new LineMonitor
            {
                Line = line,
                StationCount = stationCount,
                TrainCount = line.TrainCount,
                CarriageCount = line.CarriageCount,
                PassengerCount = passengers,
                SeatCapacity = capacity,
                CrossingLinks = crossingLinks,
                OccupiedCrossings = occupiedCrossings,
                Length = line.Length,
                LoadRatio = loadRatio,
                SpeedRatio = speedRatio,
                CycleTime = cycleTime,
                Headway = headway,
                OperationalPenalty = coveragePenalty + headwayPenalty
                    + speedPenalty + crossingPenalty
            };
        }

        private static float EstimateStationServiceEta(Station station)
        {
            if (station == null || station.LineCount <= 0) return 999f;
            if (station.IsTrainAtStation) return 0f;
            float best = 999f;
            for (int lineIndex = 0; lineIndex < station.Lines.Count; lineIndex++)
            {
                Line line = station.Lines[lineIndex];
                if (line == null) continue;
                LineMonitor monitor;
                if (LineMonitors.TryGetValue(line, out monitor))
                {
                    best = Mathf.Min(best, monitor.Headway);
                }
                for (int trainIndex = 0; trainIndex < line.TrainCount; trainIndex++)
                {
                    Train train = line.GetTrain(trainIndex);
                    if (train == null || train.Definition == null) continue;
                    if (TrainServesStationSoon(train, station))
                    {
                        best = Mathf.Min(best, 2f);
                        continue;
                    }
                    float distance = Vector2.Distance(train.FrontPosition, station.Position);
                    float eta = distance / Mathf.Max(1f, train.Definition.Speed);
                    best = Mathf.Min(best, eta);
                }
            }
            return best;
        }

        private static bool TrainServesStationSoon(Train train, Station station)
        {
            try
            {
                Station current = TrainCurrentStationProperty != null
                    ? TrainCurrentStationProperty.GetValue(train, null) as Station
                    : null;
                Station next = TrainNextStationProperty != null
                    ? TrainNextStationProperty.GetValue(train, null) as Station
                    : null;
                return current == station || next == station;
            }
            catch
            {
                return false;
            }
        }

        private static void EmitTelemetry(Game game, List<Station> stations)
        {
            float now = Time.unscaledTime;
            if (now < nextTelemetryAt) return;
            nextTelemetryAt = now + 15f;

            foreach (KeyValuePair<Line, LineMonitor> item in LineMonitors)
            {
                LineMonitor monitor = item.Value;
                UnityEngine.Debug.Log(
                    "[Auto Planner][Monitor][Line] L" + (monitor.Line.Index + 1)
                    + " stations=" + monitor.StationCount
                    + " trains=" + monitor.TrainCount
                    + " carriages=" + monitor.CarriageCount
                    + " onboard=" + monitor.PassengerCount + "/" + monitor.SeatCapacity
                    + " load=" + monitor.LoadRatio.ToString("0.00")
                    + " speed=" + monitor.SpeedRatio.ToString("0.00")
                    + " cycle=" + monitor.CycleTime.ToString("0.0")
                    + " headway=" + monitor.Headway.ToString("0.0")
                    + " crossings=" + monitor.CrossingLinks
                    + " occupiedCrossings=" + monitor.OccupiedCrossings
                    + " op=" + monitor.OperationalPenalty.ToString("0.00"));
            }

            List<string> stationDetails = new List<string>();
            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                StationMonitor monitor;
                if (!StationMonitors.TryGetValue(station, out monitor)) continue;
                stationDetails.Add(
                    StationLabel(station)
                    + " q=" + station.PeepCount + "/" + station.PeepCapacity
                    + " p=" + monitor.QueuePressure.ToString("0.00")
                    + " risk=" + monitor.Risk.ToString("0.00")
                    + " eta=" + monitor.ServiceEta.ToString("0.0")
                    + " lines=" + station.LineCount
                    + " hub=" + station.IsInterchange
                    + " svc=" + monitor.Service.ToString("0.00")
                    + " ctr=" + monitor.Centrality.ToString("0.00"));
            }
            UnityEngine.Debug.Log(
                "[Auto Planner][Monitor][Stations] "
                + String.Join("; ", stationDetails.ToArray()));
        }
    }
}
