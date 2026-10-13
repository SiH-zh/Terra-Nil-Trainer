using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Model;
using Model.State;
using Data;
using UnityEngine;
using Controller;
using Controller.Map;
using View.Handlers.Selection;
using Object = System.Object;

namespace TerraNilTrainer
{
    public sealed class AnimalPrediction
    {
        public Tile Start;
        public float Score, DistanceSquared;
        public int TerritoryCount;
        public AnimalNeed.State MoodState;
        public Dictionary<AnimalNeed, float> Needs;
        public Vector3 WorldPosition;
        public bool HasWorldPosition;
        public int PoorNeeds { get { return Needs.Count(p => p.Key.GetStateForValue(p.Value) == AnimalNeed.State.Poor); } }
        public string DescribeNeeds()
        {
            return String.Join("；", Needs.Select(p => AnimalModule.NeedName(p.Key.type) + "：" + AnimalModule.StateName(p.Key.GetStateForValue(p.Value))).ToArray());
        }
    }
    public sealed class AnimalModule
    {
        internal static bool LockMood;
        internal static int ActivityFactor = 1;
        [ThreadStatic] private static int rawScoreDepth;
        private static readonly FieldInfo DespawnTime = AccessTools.Field(typeof(AnimalGroup), "_despawnAtTime");
        private static readonly MethodInfo HappinessListeners = AccessTools.Method(typeof(AnimalGroup), "UpdateHappinessListeners");
        private readonly TrainerPlugin host;
        private AnimalGroup selected;
        private GameState scanState;
        private AnimalGroup scanGroup;
        private Coroutine scanCoroutine;
        private bool scanning, fillAllQueued, fillSelectedQueued;
        private float nextLockRefresh;
        private int processed, total, legalCount, failedCount;
        private string message = "先进入有动物的关卡，选择兽群后使用。";
        private string scanMessage = "迁移推荐尚未计算。";
        private AnimalPrediction baseline, marker;
        private readonly List<AnimalPrediction> best = new List<AnimalPrediction>();
        private readonly List<AnimalPrediction> bestPoints = new List<AnimalPrediction>();
        private float highestScore = -1f;
        private long completedSignature;
        private bool stale, showMarkers = true, showGroupList = true;
        private float nextValidityCheck;
        private string groupFilter = "";
        private Vector2 groupScroll;
        private static readonly FieldInfo GameplayCamera = AccessTools.Field(typeof(CameraController), "_camera");

        public AnimalModule(TrainerPlugin plugin) { host = plugin; }
        internal static void TerritoryLimitPostfix(AnimalGroup __instance, ref int __result)
        {
            if (!TrainerPlugin.Ready || ActivityFactor == 1 || __instance.WantsToDespawnNowEvenInZenMode) return;
            int mapSize = AnimalGroup.MapState.size;
            __result = ScaleActivityLimit(__result, ActivityFactor, mapSize);
        }
        public static int ScaleActivityLimit(int original, int factor, int mapSize)
        {
            if (original <= 0 || factor <= 1 || mapSize <= 0) return original;
            long area = (long)original * Math.Min(5, factor) * Math.Min(5, factor);
            return (int)Math.Min(Int32.MaxValue, Math.Max(original, Math.Min((long)mapSize * mapSize, area)));
        }
        public static string GroupName(AnimalGroup group)
        {
            if (group == null) return "尚未选择种群";
            try { string name = group.Name; if (!String.IsNullOrWhiteSpace(name)) return name; }
            catch { }
            return "未命名种群";
        }
        public static string GroupLabel(AnimalGroup group)
        { return group == null ? "尚未选择种群" : SpeciesName(group.type) + " · 「" + GroupName(group) + "」"; }
        public static AnimalGroup[] SortGroups(IEnumerable<AnimalGroup> groups)
        { return groups.OrderBy(g => SpeciesName(g.type), StringComparer.Ordinal).ThenBy(GroupName, StringComparer.Ordinal).ThenBy(g => g.TerritoryCenter.x).ThenBy(g => g.TerritoryCenter.y).ToArray(); }
        internal static void MoodCalculationPostfix(bool forSpawning, ref AnimalNeed.State happinessState, ref float __result)
        {
            if (!TrainerPlugin.Ready || !LockMood || forSpawning || rawScoreDepth > 0) return;
            __result = 1f;
            happinessState = AnimalNeed.State.Ideal;
        }
        internal static void MoodUpdatePostfix(AnimalGroup __instance)
        {
            if (TrainerPlugin.Ready && LockMood) SetFullMood(__instance, true);
        }
        public static bool SetFullMood(AnimalGroup group, bool updateVisuals)
        {
            // 不复活被玩家遣散或已经失去领地的兽群。
            if (group == null || group.WantsToDespawnNowEvenInZenMode) return false;
            AnimalNeed.State previous = group.GetCurrentHappinessState();
            group.SetHappiness(1f, 1f);
            if (DespawnTime != null) DespawnTime.SetValue(group, Single.MaxValue);
            group.MarkUiDirty(true);
            if (updateVisuals && previous != AnimalNeed.State.Ideal && AnimalController.InstanceExists() && MapController.InstanceExists())
                HappinessListeners.Invoke(group, new object[] { AnimalNeed.State.Ideal });
            return true;
        }
        public static AnimalPrediction ScoreTerritory(AnimalGroup group, IEnumerable<Tile> territory, Tile start)
        {
            HashSet<Tile> area = new HashSet<Tile>(territory);
            Dictionary<AnimalNeed, float> values = new Dictionary<AnimalNeed, float>();
            AnimalNeed.State state;
            float score;
            rawScoreDepth++;
            try { score = group.Data.CalculateNeedsAndHappiness(area, values, out state, false); }
            finally { rawScoreDepth--; }
            if (Single.IsNaN(score) || Single.IsInfinity(score)) throw new InvalidOperationException("动物需求计算返回无效值。");
            Vector2 center = group.TerritoryCenter;
            float dx = start != null ? start.x - center.x : 0, dz = start != null ? start.z - center.y : 0;
            return new AnimalPrediction { Start = start, Score = Mathf.Clamp01(score), MoodState = state, TerritoryCount = area.Count,
                Needs = values, DistanceSquared = dx * dx + dz * dz };
        }
        public static AnimalPrediction Predict(AnimalGroup group, Tile start, int baseLimit, float poorFactor, float adequateFactor)
        {
            // 迁移后初始心情为适中；按预测心情调整领地上限，最多模拟四轮。
            int limit = Math.Max(1, Mathf.CeilToInt(baseLimit * adequateFactor));
            HashSet<int> visitedLimits = new HashSet<int>();
            AnimalPrediction conservative = null;
            for (int round = 0; round < 4; round++)
            {
                if (!visitedLimits.Add(limit)) return conservative;
                HashSet<Tile> territory = BuildCandidateTerritory(group, start, limit);
                AnimalPrediction prediction = ScoreTerritory(group, territory, start);
                if (conservative == null || prediction.Score < conservative.Score) conservative = prediction;
                float factor = prediction.MoodState == AnimalNeed.State.Ideal ? 1f :
                    prediction.MoodState == AnimalNeed.State.Adequate ? adequateFactor : poorFactor;
                int next = Math.Max(1, Mathf.CeilToInt(baseLimit * factor));
                if (next == limit) return prediction;
                limit = next;
            }
            return conservative;
        }
        public static HashSet<Tile> BuildCandidateTerritory(AnimalGroup group, Tile start, int limit)
        {
            // 严格按实际扩张的邻格规则过滤，并在每格加入前限制容量；不改写动物或地块。
            HashSet<Tile> area = new HashSet<Tile>();
            if (start == null || limit <= 0 || start.IsUnsafeForAnimals) return area;
            Queue<Tile> frontier = new Queue<Tile>();
            area.Add(start); frontier.Enqueue(start);
            var rules = group.Data.territory;
            while (frontier.Count > 0 && area.Count < limit)
            {
                Tile from = frontier.Dequeue();
                if (!rules.IsExpandableFrom(from.type)) continue;
                foreach (Tile to in from.Get4Neighbours())
                {
                    if (to == null || area.Contains(to) || to.IsUnsafeForAnimals || !rules.CanExpandWithHeightChange(from, to) ||
                        to.HeightForAnimals < rules.ExpandHeightBottom || to.HeightForAnimals > rules.ExpandHeightTop ||
                        !rules.IsExpandableFromTo(from.type, to.type) || to.HasAnimalGroupsOfTypeInRange(group.type, group) ||
                        group.AnimalState.IsTileSonicPulsed(to)) continue;
                    if (GameController.Instance.Mission == Model.Mission.AridCanyon && to.Get4Neighbours().Any(t => t != null && t.isOnFire)) continue;
                    area.Add(to); frontier.Enqueue(to);
                    if (area.Count >= limit) break;
                }
            }
            return area;
        }
        public static int ComparePredictions(AnimalPrediction a, AnimalPrediction b)
        {
            int order = b.Score.CompareTo(a.Score);
            if (order == 0) order = a.PoorNeeds.CompareTo(b.PoorNeeds);
            if (order == 0) order = a.DistanceSquared.CompareTo(b.DistanceSquared);
            if (order == 0) order = b.TerritoryCount.CompareTo(a.TerritoryCount);
            if (order == 0) order = a.Start.x.CompareTo(b.Start.x);
            if (order == 0) order = a.Start.z.CompareTo(b.Start.z);
            return order;
        }
        public static bool IsLegalStart(GameState state, AnimalGroup group, Tile tile)
        {
            if (tile == null || tile.IsUnsafeForAnimals || state == null || state.animalState == null) return false;
            AnimalTerritoryData rules = group.Data.territory;
            // 与原游戏迁移起点限制一致，同类兽群当前领地继续排除。
            return rules.IsExpandThrough(tile.type) && tile.height >= rules.ExpandHeightBottom && tile.height <= rules.ExpandHeightTop &&
                !tile.HasAnimalGroupsOfTypeInRange(group.type) && !state.animalState.IsTileSonicPulsed(tile);
        }
        public static long EnvironmentSignature(GameState state)
        {
            unchecked
            {
                long value = 1469598103934665603L;
                value = (value ^ ActivityFactor) * 1099511628211L;
                value = (value ^ TrainerPlugin.BuildingFactor ^ (TrainerPlugin.IrrigationFactor << 4) ^ (TrainerPlugin.RecyclingFactor << 8)) * 1099511628211L;
                foreach (Tile tile in state.mapState.map)
                {
                    if (tile == null) { value = (value ^ -1) * 1099511628211L; continue; }
                    value = (value ^ (int)tile.type) * 1099511628211L;
                    value = (value ^ tile.height) * 1099511628211L;
                    value = (value ^ tile.radioactivity) * 1099511628211L;
                    value = (value ^ (tile.isOnFire ? 1 : 0)) * 1099511628211L;
                    value = (value ^ tile.isPipe ^ (tile.isAqueduct << 8)) * 1099511628211L;
                    value = (value ^ tile.concreteHeight) * 1099511628211L;
                    if (state.animalState != null) value = (value ^ (state.animalState.IsTileSonicPulsed(tile) ? 1 : 0)) * 1099511628211L;
                }
                if (state.mapState.BuildingsDict != null)
                    foreach (var entry in state.mapState.BuildingsDict.OrderBy(p => p.Key.x).ThenBy(p => p.Key.y))
                        value = (value ^ entry.Key.x ^ ((long)entry.Key.y << 16) ^ (entry.Value == null ? 0 : entry.Value.GetType().FullName.GetHashCode())) * 1099511628211L;
                foreach (AnimalGroup group in state.animalState.GetGroups())
                {
                    value = (value ^ (int)group.type) * 1099511628211L;
                    value = (value ^ group.IndividualCount) * 1099511628211L;
                    int territoryHash = 0;
                    foreach (Tile tile in group.GetTerritory()) territoryHash ^= tile.x * 397 ^ tile.z;
                    value = (value ^ territoryHash ^ group.TerritoryCount) * 1099511628211L;
                }
                if (state.climateState != null && state.climateState.ClimateParameters != null)
                    foreach (var p in state.climateState.ClimateParameters.OrderBy(p => p.Key.GetInstanceID()))
                        value = (value ^ p.Key.GetInstanceID() ^ p.Value.GetHashCode()) * 1099511628211L;
                if (Global.CampaignStateManager.InstanceExists())
                    value = (value ^ Global.CampaignStateManager.Instance.PlayerProfileState.difficultyState.animalNeedsMultiplier.GetHashCode()) * 1099511628211L;
                return value;
            }
        }
        private AnimalGroup[] Groups()
        {
            GameState state = TrainerPlugin.CurrentState();
            return state != null && state.animalState != null ? SortGroups(state.animalState.GetGroups().Where(g => g != null && !g.WantsToDespawnNowEvenInZenMode)) : new AnimalGroup[0];
        }
        public void Update()
        {
            if (scanState != null && !Object.ReferenceEquals(scanState, TrainerPlugin.CurrentState())) ResetScan("关卡已切换，旧推荐已清除。");
            if (!scanning && scanState != null && Time.realtimeSinceStartup >= nextValidityCheck)
            {
                nextValidityCheck = Time.realtimeSinceStartup + 2f;
                if (!scanState.animalState.ContainsGroup(scanGroup)) ResetScan("原种群已离开或合并，请按名字重新选择。");
                else if (completedSignature != EnvironmentSignature(scanState)) { stale = true; scanMessage = "地图或领地已变化：标记是旧预测，请重新扫描。"; }
            }
            if (fillAllQueued || (LockMood && Time.realtimeSinceStartup >= nextLockRefresh))
            {
                int count = 0;
                foreach (AnimalGroup group in Groups()) if (SetFullMood(group, true)) count++;
                if (fillAllQueued) message = count > 0 ? "已将当前 " + count + " 个兽群心情设置为 100%。" : "当前关卡没有可修改的兽群。";
                fillAllQueued = false;
                nextLockRefresh = Time.realtimeSinceStartup + 0.5f;
            }
            if (fillSelectedQueued)
            {
                fillSelectedQueued = false;
                message = Groups().Contains(selected) && SetFullMood(selected, true) ? GroupLabel(selected) + " 心情已设置为 100%。" : "选中兽群已经离开，请重新选择。";
            }
        }
        public void Reset()
        {
            LockMood = fillAllQueued = fillSelectedQueued = false;
            ActivityFactor = 1;
            ResetScan("动物修改已关闭，推荐及地图标记已清除。");
            selected = null;
        }
        private void ResetScan(string text)
        {
            if (scanCoroutine != null) host.StopCoroutine(scanCoroutine);
            scanCoroutine = null;
            scanning = false;
            scanState = null; scanGroup = null; baseline = marker = null;
            best.Clear(); bestPoints.Clear(); highestScore = -1f; stale = false;
            scanMessage = text;
        }
        private void StartSearch()
        {
            if (selected == null || !Groups().Contains(selected)) { scanMessage = "请先选择有效兽群。"; return; }
            ResetScan("正在扫描全部合法迁移起点……");
            scanState = TrainerPlugin.CurrentState(); scanGroup = selected;
            scanning = true; processed = legalCount = failedCount = 0;
            total = scanState.mapState.map.Length;
            scanCoroutine = host.StartCoroutine(RunSearch());
        }
        private IEnumerator RunSearch()
        {
            IEnumerator inner = SearchCore();
            bool failed = false;
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception ex) { scanMessage = "扫描未完成：" + ex.GetBaseException().Message; failed = true; break; }
                if (!more) break;
                yield return inner.Current;
            }
            IDisposable disposable = inner as IDisposable;
            if (disposable != null) disposable.Dispose();
            if (failed) { best.Clear(); bestPoints.Clear(); marker = null; }
            scanning = false; scanCoroutine = null;
        }
        private IEnumerator SearchCore()
        {
            if (!AnimalController.InstanceExists() || !MapController.InstanceExists()) throw new InvalidOperationException("动物或地图控制器尚未就绪。");
            long signature = EnvironmentSignature(scanState);
            baseline = ScoreTerritory(scanGroup, scanGroup.GetTerritory(), null);
            int limit = scanGroup.GetTerritoryMaxTilesBase();
            float poorFactor = AnimalController.Instance.territoryMaxLimitFactorPoor;
            float adequateFactor = AnimalController.Instance.territoryMaxLimitFactorAdequate;
            Stopwatch budget = Stopwatch.StartNew();
            foreach (Tile tile in scanState.mapState.map)
            {
                if (!scanState.animalState.ContainsGroup(scanGroup)) throw new InvalidOperationException("兽群已消失，请重新选择。");
                processed++;
                if (IsLegalStart(scanState, scanGroup, tile))
                {
                    var visibleTile = MapController.Instance.GetGameplayTile(tile);
                    if (visibleTile != null && !visibleTile.IsUnsafeForAnimals)
                    {
                        legalCount++;
                        try
                        {
                            AnimalPrediction p = Predict(scanGroup, tile, limit, poorFactor, adequateFactor);
                            p.WorldPosition = visibleTile.GetSurfacePosition() + Vector3.up * 0.25f; p.HasWorldPosition = true;
                            if (p.Score > highestScore) { highestScore = p.Score; bestPoints.RemoveAll(c => c.Score < highestScore - 0.0001f); }
                            if (p.Score >= highestScore - 0.0001f) bestPoints.Add(p);
                        }
                        catch { failedCount++; }
                    }
                }
                // Unity（游戏引擎）对象只在主线程读取；分帧执行，避免长时间占用一帧。
                if (budget.ElapsedMilliseconds >= 6)
                {
                    scanMessage = "扫描进度 " + (100 * processed / Math.Max(1, total)) + "%（已评分 " + legalCount + " 个合法起点）";
                    yield return null;
                    budget.Restart();
                }
            }
            completedSignature = signature;
            stale = signature != EnvironmentSignature(scanState);
            bestPoints.Sort(ComparePredictions);
            best.AddRange(ChooseRepresentatives(bestPoints, 5));
            if (best.Count == 0) scanMessage = "没有找到能成功评分的合法迁移位置。";
            else
            {
                scanMessage = "推荐对象：" + GroupLabel(scanGroup) + "。扫描 " + legalCount + " 个合法起点，最佳同分点 " + bestPoints.Count + " 个；以下为分散代表点。";
                if (best[0].Score <= baseline.Score + 0.0001f) scanMessage += " 未找到比当前领地评分更高的位置。";
                if (failedCount > 0) scanMessage += " " + failedCount + " 个起点评分失败，结果不代表全部起点最优。";
                if (stale) scanMessage += " 扫描期间环境改变，标记仅为旧预测，请重新计算。";
                marker = best[0];
            }
        }
        public static List<AnimalPrediction> ChooseRepresentatives(IEnumerable<AnimalPrediction> candidates, int count)
        {
            var chosen = new List<AnimalPrediction>();
            foreach (var p in candidates)
            {
                float gap = Math.Max(3f, (float)Math.Sqrt(Math.Max(1, p.TerritoryCount)) * 0.6f);
                if (chosen.Any(c => (c.Start.x - p.Start.x) * (c.Start.x - p.Start.x) + (c.Start.z - p.Start.z) * (c.Start.z - p.Start.z) < gap * gap)) continue;
                chosen.Add(p); if (chosen.Count >= count) break;
            }
            return chosen;
        }
        public void DrawPanel()
        {
            AnimalGroup[] groups = Groups();
            if (selected != null && !groups.Contains(selected)) { selected = null; ResetScan("原种群已经离开，请按名字重新选择。"); }
            GUILayout.Label("动物心情与迁移　｜　关卡中 " + groups.Length + " 个兽群");
            GUILayout.Label(message);
            GUI.enabled = TrainerPlugin.Ready && groups.Length > 0;
            if (GUILayout.Button("当前所有兽群心情一键拉满", GUILayout.Height(34))) fillAllQueued = true;
            GUI.enabled = TrainerPlugin.Ready;
            bool keep = GUILayout.Toggle(LockMood, " 持续保持所有兽群满心情（包含新出现的兽群）", GUILayout.Height(32));
            if (keep != LockMood) { LockMood = keep; if (keep) fillAllQueued = true; message = keep ? "持续满心情已开启。" : "持续满心情已关闭，后续按原规则变化。"; }
            GUILayout.Label("只改心情，不伪造实际需求；关闭持续保持后会按环境重新计算。");
            GUILayout.Space(5);
            GUILayout.Label("动物活动范围：" + ActivityFactor + " 倍（领地容量约 " + (ActivityFactor * ActivityFactor) + " 倍）");
            int nextFactor = GUILayout.Toolbar(ActivityFactor - 1, new string[] { "1", "2", "3", "4", "5" }) + 1;
            if (nextFactor != ActivityFactor)
            {
                ActivityFactor = nextFactor;
                foreach (var group in groups) group.MarkUiDirty(true);
                ResetScan("活动范围已改变，请按新范围重新扫描。");
            }
            GUILayout.Label("所有种群按原栖息地规则逐步扩大领地；不是固定圆形范围。1 倍恢复原上限，已经扩张的领地不会立即回退。");
            GUILayout.Space(5);
            GUI.enabled = TrainerPlugin.Ready && groups.Length > 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(showGroupList ? "收起种群名单" : "按名字选择种群")) showGroupList = !showGroupList;
            if (GUILayout.Button("采用游戏中选中种群"))
            {
                var view = SelectionHandler.InstanceExists() ? SelectionHandler.Instance.CurrentlySelectedAnimalGroup : null;
                if (view != null && groups.Contains(view.AnimalGroup)) { selected = view.AnimalGroup; ResetScan("已采用 " + GroupLabel(selected) + "，请扫描候选位置。"); }
                else message = "请在游戏地图或动物列表中选中具体种群，再同步选择。";
            }
            GUILayout.EndHorizontal();
            if (showGroupList)
            {
                GUILayout.Label("按游戏中的种群名字选择；可输入名字或动物种类筛选：");
                groupFilter = GUILayout.TextField(groupFilter);
                groupScroll = GUILayout.BeginScrollView(groupScroll, GUILayout.Height(140));
                foreach (var group in groups.Where(g => String.IsNullOrEmpty(groupFilter) || GroupLabel(g).IndexOf(groupFilter, StringComparison.CurrentCultureIgnoreCase) >= 0))
                    if (GUILayout.Button((Object.ReferenceEquals(group, selected) ? "✓ " : "") + GroupLabel(group) + "　" + group.IndividualCount + " 只"))
                    { selected = group; ResetScan("已选择 " + GroupLabel(selected) + "，请扫描候选位置。"); }
                GUILayout.EndScrollView();
            }
            if (selected != null)
            {
                Vector2 center = selected.TerritoryCenter;
                GUILayout.Label("推荐对象：" + GroupLabel(selected) +
                    "　心情 " + (100 * selected.GetCurrentHappiness()).ToString("0") + "%\n领地中心（" + center.x.ToString("0") + ", " + center.y.ToString("0") + "），" + selected.IndividualCount + " 只");
                GUILayout.Label("当前活动领地 " + selected.TerritoryCount + " 格，上限 " + selected.GetTerritoryMaxTilesWithModifiers() + " 格。");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("上一群")) SelectAnother(groups, -1);
                if (GUILayout.Button("下一群")) SelectAnother(groups, 1);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("仅将选中兽群心情拉满")) fillSelectedQueued = true;
            }
            GUILayout.Space(5);
            GUI.enabled = TrainerPlugin.Ready && selected != null && !scanning;
            if (GUILayout.Button("扫描当前兽群的最佳迁移候选", GUILayout.Height(36))) StartSearch();
            GUI.enabled = true;
            if (scanning && GUILayout.Button("取消扫描")) ResetScan("扫描已取消。");
            GUILayout.Label(scanMessage);
            showMarkers = GUILayout.Toggle(showMarkers, " 在地图显示全部最佳同分点（菜单展开时也显示）");
            if (baseline != null) GUILayout.Label("当前领地原始评分：" + (baseline.Score * 100).ToString("0.0") + "%　" + baseline.DescribeNeeds());
            for (int index = 0; index < best.Count; index++)
            {
                AnimalPrediction p = best[index];
                GUILayout.Label("候选 " + (index + 1) + "　" + GroupLabel(scanGroup) + "\n推荐迁移起点（" + p.Start.x + ", " + p.Start.z + "），海拔 " + p.Start.height + "；扩张预估 " +
                    (p.Score * 100).ToString("0.0") + "%（" + StateName(p.MoodState) + "），领地 " + p.TerritoryCount + " 格\n" + p.DescribeNeeds());
                GUI.enabled = !scanning;
                if (GUILayout.Button("定位并标记候选 " + (index + 1))) Locate(p);
                GUI.enabled = true;
            }
            if (bestPoints.Count > 0 && GUILayout.Button("清除地图推荐与标记")) ResetScan("推荐已清除。");
            GUILayout.Label("地图小点为全部预测最佳同分起点；数字标记为最多 5 个分散代表点。\n推荐使用原始需求评分，持续满心情不会把评分伪造成满分。\n动物实际扩张会按需求和随机顺序择地；这里是满足扩张限制的估算，不保证最终心情。使用游戏原有迁移操作移到标记格。");
            GUI.enabled = true;
        }
        private void SelectAnother(AnimalGroup[] groups, int delta)
        {
            if (groups.Length == 0) return;
            int index = Array.IndexOf(groups, selected);
            selected = groups[(index + delta + groups.Length) % groups.Length];
            ResetScan("已切换兽群，请重新扫描。");
        }
        private void Locate(AnimalPrediction p)
        {
            if (scanState == null || !Object.ReferenceEquals(scanState, TrainerPlugin.CurrentState()) || scanGroup == null ||
                !scanState.animalState.ContainsGroup(scanGroup) || !IsLegalStart(scanState, scanGroup, p.Start))
            { ResetScan("原候选已不再适用，请重新扫描。"); return; }
            if (stale || completedSignature != EnvironmentSignature(scanState)) { stale = true; scanMessage = "地图或动物领地已变化，请先重新扫描；旧推荐不再用于定位。"; return; }
            marker = p;
            var tile = MapController.Instance.GetGameplayTile(p.Start);
            if (tile != null && CameraController.InstanceExists())
            {
                var camera = CameraController.Instance;
                Vector3 point = tile.GetSurfacePosition();
                Vector3 target = camera.targetPivotPos;
                target.x = point.x; target.z = point.z;
                camera.targetPivotPos = target;
                if (camera.pivot != null) camera.pivot.position = target;
                scanMessage = "已定位 " + GroupLabel(scanGroup) + " 的候选（" + p.Start.x + ", " + p.Start.z + "）；按原游戏迁移到对应标记格。";
            }
        }
        public void DrawMarker()
        {
            if (!showMarkers || bestPoints.Count == 0 || scanState == null || scanning || !Object.ReferenceEquals(scanState, TrainerPlugin.CurrentState())) return;
            Camera camera = CameraController.InstanceExists() && GameplayCamera != null ? GameplayCamera.GetValue(CameraController.Instance) as Camera : null;
            if (camera == null) camera = Camera.main;
            if (camera == null || Event.current.type != EventType.Repaint) return;
            Color previous = GUI.color;
            try
            {
                GUI.color = stale ? new Color(1f, 0.65f, 0.15f, 0.9f) : new Color(0.3f, 1f, 0.65f, 0.8f);
                foreach (var p in bestPoints)
                {
                    if (!p.HasWorldPosition) continue;
                    Vector3 point = camera.WorldToScreenPoint(p.WorldPosition);
                    if (point.z <= 0 || point.x < 0 || point.x > Screen.width || point.y < 0 || point.y > Screen.height) continue;
                    GUI.DrawTexture(new Rect(point.x - 2, Screen.height - point.y - 2, 4, 4), Texture2D.whiteTexture);
                }
                var boxes = new List<Rect>();
                Rect panelBounds = TrainerPlugin.VisiblePanelBounds;
                for (int i = 0; i < best.Count; i++)
                {
                    var p = best[i];
                    if (!p.HasWorldPosition) continue;
                    Vector3 point = camera.WorldToScreenPoint(p.WorldPosition);
                    if (point.z <= 0 || point.x < 0 || point.x > Screen.width || point.y < 0 || point.y > Screen.height) continue;
                    float y = Screen.height - point.y;
                    GUI.DrawTexture(new Rect(point.x - 15, y - 2, 30, 4), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(point.x - 2, y - 15, 4, 30), Texture2D.whiteTexture);
                    Rect label = new Rect(Mathf.Clamp(point.x - 130, 0, Math.Max(0, Screen.width - 280)), Mathf.Clamp(y - 90, 0, Math.Max(0, Screen.height - 80)), 280, 78);
                    if (label.Overlaps(panelBounds) && panelBounds.xMax + 288 <= Screen.width) label.x = panelBounds.xMax + 8;
                    else if (label.Overlaps(panelBounds) && panelBounds.xMin >= 288) label.x = panelBounds.xMin - 288;
                    for (int shift = 0; shift < 5 && boxes.Any(b => b.Overlaps(label)); shift++) label.y = Mathf.Clamp(label.y + 82, 0, Math.Max(0, Screen.height - 80));
                    boxes.Add(label);
                    GUI.Box(label, (stale ? "旧预测 · " : "候选 ") + (i + 1) + "　" + GroupLabel(scanGroup) + "\n起点（" + p.Start.x + ", " + p.Start.z + "）\n预估心情 " + (p.Score * 100).ToString("0.0") + "%");
                }
            }
            finally { GUI.color = previous; }
        }
        public static string NeedName(AnimalNeedType type)
        {
            switch (type)
            {
                case AnimalNeedType.Space: return "空间";
                case AnimalNeedType.Shelter: return "庇护";
                case AnimalNeedType.Water: return "水源";
                case AnimalNeedType.Food: return "食物";
                case AnimalNeedType.Climate: return "气候";
                case AnimalNeedType.Air: return "空气";
                case AnimalNeedType.Habitat: return "栖息地";
                default: return "其他需求";
            }
        }
        public static string StateName(AnimalNeed.State state)
        { return state == AnimalNeed.State.Ideal ? "理想" : state == AnimalNeed.State.Adequate ? "适中" : "不足"; }
        public static string SpeciesName(AnimalType type)
        {
            if (Controller.StringsController.InstanceExists())
            {
                string text = Controller.StringsController.Instance.GetStringOrDefault("Animal." + type + ".Name", false);
                if (!String.IsNullOrWhiteSpace(text) && !text.StartsWith("Animal.", StringComparison.Ordinal))
                    return text.Any(c => c >= 0x4e00 && c <= 0x9fff) ? text : text + "（" + FallbackSpeciesName(type) + "）";
            }
            return FallbackSpeciesName(type);
        }
        private static string FallbackSpeciesName(AnimalType type)
        {
            switch (type)
            {
                case AnimalType.Deer: return "鹿"; case AnimalType.Frog: return "青蛙"; case AnimalType.Bear: return "熊";
                case AnimalType.Trout: return "鳟鱼"; case AnimalType.Goose: return "鹅"; case AnimalType.Parrot: return "鹦鹉";
                case AnimalType.Penguin: return "企鹅"; case AnimalType.Tortoise: return "陆龟"; case AnimalType.MantaRay: return "蝠鲼";
                case AnimalType.Flamingo: return "火烈鸟"; case AnimalType.PolarBear: return "北极熊"; case AnimalType.SnowyOwl: return "雪鸮";
                case AnimalType.DebugDeer: return "测试鹿"; case AnimalType.Narwhal: return "独角鲸"; case AnimalType.ReefShark: return "礁鲨";
                case AnimalType.Whale: return "鲸"; case AnimalType.SeaOtter: return "海獭"; case AnimalType.ArcticFox: return "北极狐";
                case AnimalType.Elk: return "麋鹿"; case AnimalType.Peacock: return "孔雀"; case AnimalType.RedFox: return "赤狐";
                case AnimalType.Beaver: return "海狸"; case AnimalType.Wolf: return "狼"; case AnimalType.Boar: return "野猪";
                case AnimalType.Panda: return "熊猫"; case AnimalType.Turtle: return "海龟"; case AnimalType.Eagle: return "鹰";
                case AnimalType.Jaguar: return "美洲豹"; case AnimalType.Giraffe: return "长颈鹿"; case AnimalType.Rhino: return "犀牛";
                case AnimalType.Elephant: return "大象"; case AnimalType.Zebra: return "斑马"; case AnimalType.Lion: return "狮子";
                case AnimalType.Springbok: return "跳羚"; case AnimalType.Crocodile: return "鳄鱼"; case AnimalType.Hippo: return "河马";
                case AnimalType.Hornbill: return "犀鸟"; case AnimalType.Vulture: return "秃鹫"; case AnimalType.Hyena: return "鬣狗";
                case AnimalType.AfricanPenguin: return "非洲企鹅"; default: return "动物";
            }
        }
    }
}
