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
        public int PoorNeeds { get { return Needs.Count(p => p.Key.GetStateForValue(p.Value) == AnimalNeed.State.Poor); } }
        public string DescribeNeeds()
        {
            return String.Join("；", Needs.Select(p => AnimalModule.NeedName(p.Key.type) + "：" + AnimalModule.StateName(p.Key.GetStateForValue(p.Value))).ToArray());
        }
    }
    public sealed class AnimalModule
    {
        internal static bool LockMood;
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

        public AnimalModule(TrainerPlugin plugin) { host = plugin; }
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
                HashSet<Tile> territory = group.Data.DoFloodFill(start, limit, group);
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
                foreach (Tile tile in state.mapState.map)
                {
                    if (tile == null) { value = (value ^ -1) * 1099511628211L; continue; }
                    value = (value ^ (int)tile.type) * 1099511628211L;
                    value = (value ^ tile.height) * 1099511628211L;
                    value = (value ^ tile.radioactivity) * 1099511628211L;
                    value = (value ^ (tile.isOnFire ? 1 : 0)) * 1099511628211L;
                    value = (value ^ tile.isPipe ^ (tile.isAqueduct << 8)) * 1099511628211L;
                    value = (value ^ tile.concreteHeight) * 1099511628211L;
                }
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
                return value;
            }
        }
        private AnimalGroup[] Groups()
        {
            GameState state = TrainerPlugin.CurrentState();
            return state != null && state.animalState != null ? state.animalState.GetGroups().Where(g => g != null && !g.WantsToDespawnNowEvenInZenMode).ToArray() : new AnimalGroup[0];
        }
        public void Update()
        {
            if (scanState != null && !Object.ReferenceEquals(scanState, TrainerPlugin.CurrentState())) ResetScan("关卡已切换，旧推荐已清除。");
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
                message = Groups().Contains(selected) && SetFullMood(selected, true) ? "选中兽群心情已设置为 100%。" : "选中兽群已经离开，请重新选择。";
            }
        }
        public void Reset()
        {
            LockMood = fillAllQueued = fillSelectedQueued = false;
            ResetScan("动物修改已关闭，推荐及地图标记已清除。");
            selected = null;
        }
        private void ResetScan(string text)
        {
            if (scanCoroutine != null) host.StopCoroutine(scanCoroutine);
            scanCoroutine = null;
            scanning = false;
            scanState = null; scanGroup = null; baseline = marker = null;
            best.Clear();
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
            if (failed) { best.Clear(); marker = null; }
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
                            best.Add(p); best.Sort(ComparePredictions);
                            if (best.Count > 3) best.RemoveAt(3);
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
            bool changed = signature != EnvironmentSignature(scanState);
            if (best.Count == 0) scanMessage = "没有找到能成功评分的合法迁移位置。";
            else
            {
                scanMessage = "扫描完成：" + legalCount + " 个合法起点；候选按原始需求评分排序。";
                if (best[0].Score <= baseline.Score + 0.0001f) scanMessage += " 未找到比当前领地评分更高的位置。";
                if (failedCount > 0) scanMessage += " " + failedCount + " 个起点评分失败，结果不代表全部起点最优。";
                if (changed) scanMessage += " 扫描期间环境改变，请重新计算。";
                marker = best[0];
            }
        }
        public void DrawPanel()
        {
            AnimalGroup[] groups = Groups();
            if (!groups.Contains(selected)) selected = groups.FirstOrDefault();
            GUILayout.Label("动物心情与迁移　｜　关卡中 " + groups.Length + " 个兽群");
            GUILayout.Label(message);
            GUI.enabled = TrainerPlugin.Ready && groups.Length > 0;
            if (GUILayout.Button("当前所有兽群心情一键拉满", GUILayout.Height(34))) fillAllQueued = true;
            GUI.enabled = TrainerPlugin.Ready;
            bool keep = GUILayout.Toggle(LockMood, " 持续保持所有兽群满心情（包含新出现的兽群）", GUILayout.Height(32));
            if (keep != LockMood) { LockMood = keep; if (keep) fillAllQueued = true; message = keep ? "持续满心情已开启。" : "持续满心情已关闭，后续按原规则变化。"; }
            GUILayout.Label("只改心情，不伪造实际需求；关闭持续保持后会按环境重新计算。");
            GUILayout.Space(5);
            GUI.enabled = TrainerPlugin.Ready && groups.Length > 0;
            if (selected != null)
            {
                Vector2 center = selected.TerritoryCenter;
                GUILayout.Label("当前选择：" + SpeciesName(selected.type) + "兽群 " + (Array.IndexOf(groups, selected) + 1) +
                    "　心情 " + (100 * selected.GetCurrentHappiness()).ToString("0") + "%\n领地中心（" + center.x.ToString("0") + ", " + center.y.ToString("0") + "），" + selected.IndividualCount + " 只");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("上一群")) SelectAnother(groups, -1);
                if (GUILayout.Button("下一群")) SelectAnother(groups, 1);
                if (GUILayout.Button("采用游戏中选中兽群"))
                {
                    var view = SelectionHandler.InstanceExists() ? SelectionHandler.Instance.CurrentlySelectedAnimalGroup : null;
                    if (view != null && groups.Contains(view.AnimalGroup)) { selected = view.AnimalGroup; ResetScan("选择已同步，请扫描迁移位置。"); }
                    else message = "先用面板快捷键隐藏，在游戏中选中动物，再打开面板同步。";
                }
                GUILayout.EndHorizontal();
                if (GUILayout.Button("仅将选中兽群心情拉满")) fillSelectedQueued = true;
            }
            GUILayout.Space(5);
            GUI.enabled = TrainerPlugin.Ready && selected != null && !scanning;
            if (GUILayout.Button("扫描当前兽群的最佳迁移候选", GUILayout.Height(36))) StartSearch();
            GUI.enabled = true;
            if (scanning && GUILayout.Button("取消扫描")) ResetScan("扫描已取消。");
            GUILayout.Label(scanMessage);
            if (baseline != null) GUILayout.Label("当前领地原始评分：" + (baseline.Score * 100).ToString("0.0") + "%　" + baseline.DescribeNeeds());
            for (int index = 0; index < best.Count; index++)
            {
                AnimalPrediction p = best[index];
                GUILayout.Label("候选 " + (index + 1) + "：（" + p.Start.x + ", " + p.Start.z + "） 预计 " +
                    (p.Score * 100).ToString("0.0") + "%（" + StateName(p.MoodState) + "），领地 " + p.TerritoryCount + " 格\n" + p.DescribeNeeds());
                GUI.enabled = !scanning;
                if (GUILayout.Button("定位并标记候选 " + (index + 1))) Locate(p);
                GUI.enabled = true;
            }
            if (marker != null && GUILayout.Button("清除地图推荐标记")) marker = null;
            GUILayout.Label("推荐使用原始需求评分，持续满心情不会把评分伪造成满分。\n结果是当前地图的领地扩张估算，不能保证迁移后或未来状态。\n隐藏面板后使用游戏原有迁移操作移到标记格。扫描不迁移动物、不扣货币。");
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
                scanMessage = "已定位候选（" + p.Start.x + ", " + p.Start.z + "）；隐藏面板后按原游戏迁移到绿色标记格。";
            }
        }
        public void DrawMarker()
        {
            if (marker == null || scanState == null || !Object.ReferenceEquals(scanState, TrainerPlugin.CurrentState()) || !MapController.InstanceExists()) return;
            Camera camera = Camera.main;
            var tile = MapController.Instance.GetGameplayTile(marker.Start);
            if (camera == null || tile == null) return;
            Vector3 point = camera.WorldToScreenPoint(tile.GetSurfacePosition() + Vector3.up * 0.25f);
            if (point.z <= 0 || point.x < 0 || point.x > Screen.width || point.y < 0 || point.y > Screen.height) return;
            float y = Screen.height - point.y;
            Color previous = GUI.color;
            GUI.color = Color.green;
            GUI.DrawTexture(new Rect(point.x - 18, y - 2, 36, 4), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(point.x - 2, y - 18, 4, 36), Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Box(new Rect(Mathf.Clamp(point.x - 100, 0, Mathf.Max(0, Screen.width - 240)), y - 65, 240, 42),
                "迁移候选（" + marker.Start.x + ", " + marker.Start.z + "）\n预计心情 " + (marker.Score * 100).ToString("0.0") + "%");
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
