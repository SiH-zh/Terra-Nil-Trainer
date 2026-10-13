using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Controller;
using Controller.Map;
using Model;
using Model.Buildings;
using Model.State;
using View.Handlers;
using View.Handlers.Selection;
using Type = System.Type;

namespace TerraNilTrainer
{
    [BepInPlugin(Id, "伊始之地中文修改器", "1.1.0")]
    [BepInProcess("Terra Nil.exe")]
    public sealed class TrainerPlugin : BaseUnityPlugin
    {
        public const string Id = "local.terranil.chinese.trainer";
        public const string GameHash = "FF04732FEBC096F2C0296953D8477C16170E0B6752A8967B50051748EC7AED23";
        internal static TrainerPlugin Instance;
        internal static bool Ready, AnyPosition, IgnoreConditions, Visible = true;
        internal static int IrrigationFactor = 1, RecyclingFactor = 1, BuildingFactor = 1;
        private Harmony harmony;
        private string status = "正在检查版本……", currencyInput = "99999";
        private bool currencyQueued;
        private int queuedCurrency;
        private Rect panel = new Rect(30, 60, 510, 565);
        private Font chineseFont;
        private GUISkin skin;
        private Vector2 scroll;
        private int panelTab;
        private AnimalModule animals;
        private Lib.Recyclers.LandRecycler rover;
        private int originalRoverRange;
        private static readonly FieldInfo RoverRange = AccessTools.Field(typeof(Lib.Recyclers.LandRecycler), "recyclingRange");
        private static readonly MethodInfo HeightCheck = AccessTools.Method(typeof(MapController), "BuildingHeightCheck");
        private static readonly MethodInfo RestrictedCheck = AccessTools.Method(typeof(MapController), "CheckRestrictedTiles");
        private static GameState selfTestState;

        private void Awake()
        {
            Instance = this;
            animals = new AnimalModule(this);
            try
            {
                string path = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (hash != GameHash) throw new InvalidOperationException("游戏版本不匹配，已停止全部修改。适配构建：22566272。");
                }
                harmony = new Harmony(Id);
                AddPatch(typeof(Building), "get_Range", "RangePostfix", false);
                AddPatch(typeof(Model.Buildings.Tier1.SolarArray), "get_Range", "RangePostfix", false);
                // 废弃核设施属于地图危险源，不扩大其污染范围。
                AddPatch(typeof(Model.Buildings.Tier1.Irrigator), "GetArea", "IrrigationPostfix", false);
                AddPatch(typeof(MapController), "CanPlaceBuildingAtLocation", "PlacementPostfix", false);
                AddPatch(typeof(Model.Helpers.ClimateEffectivenessHelper), "EvaluateCurrentClimate", "ClimatePrefix", true);
                AddPatch(typeof(SelectionHandler), "PlaceCurrentBuilding", "BlockPlacementPrefix", true);
                AddPatch(typeof(SelectionHandler), "IsOverUI", "BlockUiPrefix", true);
                AddPatch(typeof(SelectionHandler), "IsPointerOverUIObject", "BlockUiPrefix", true);
                AddPatch(typeof(Model.AnimalGroup), "UpdateNeedsAndHappiness", "MoodUpdatePostfix", false, typeof(AnimalModule));
                AddPatch(typeof(Data.AnimalGroupData), "CalculateNeedsAndHappiness", "MoodCalculationPostfix", false, typeof(AnimalModule));
                if (RoverRange == null || HeightCheck == null || RestrictedCheck == null)
                    throw new MissingMemberException("未找到完整的回收或建造判定接口。");
                Ready = true;
                status = "版本匹配，建造及动物功能就绪；默认全部关闭。";
                Logger.LogInfo(status);
                if (Environment.GetCommandLineArgs().Contains("-trainer-self-test"))
                {
                    AddPatch(AccessTools.TypeByName("SteamManager"), "Awake", "SkipSelfTestStartupPrefix", true);
                    try
                    {
                        RuntimeSelfTest();
                        Logger.LogInfo("运行时自检：全部通过。");
                        File.WriteAllText(Path.Combine(Paths.GameRootPath, "trainer-self-test.txt"), "运行时自检：全部通过。\r\n真实建筑与回收半径、恢复原值、灌溉覆盖区域、气候返回值、人工地图建造判定及界面补丁注册。\r\n未进入玩家关卡。\r\n", new System.Text.UTF8Encoding(true));
                    }
                    catch (Exception testError)
                    {
                        Logger.LogError("运行时自检失败：" + testError);
                        File.WriteAllText(Path.Combine(Paths.GameRootPath, "trainer-self-test.txt"), "运行时自检失败：" + testError, new System.Text.UTF8Encoding(true));
                    }
                    finally { ResetAll(); Application.Quit(); }
                }
            }
            catch (Exception ex)
            {
                Ready = false;
                if (harmony != null) harmony.UnpatchSelf();
                status = ex.Message;
                Logger.LogError(ex);
            }
        }
        private void AddPatch(Type type, string name, string hook, bool prefix, Type hookType = null)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod patch = new HarmonyMethod(hookType ?? typeof(TrainerPlugin), hook);
            if (prefix) harmony.Patch(method, prefix: patch); else harmony.Patch(method, postfix: patch);
            Logger.LogInfo("已挂接：" + type.FullName + "." + name);
        }
        internal static GameState CurrentState()
        {
            if (!GameController.InstanceExists()) return null;
            if (!Global.CampaignStateManager.InstanceExists()) return null;
            return Global.CampaignStateManager.Instance.GameState;
        }
        private static bool SelfTestStatePrefix(ref GameState __result)
        {
            if (selfTestState == null) return true;
            __result = selfTestState;
            return false;
        }
        private static bool SkipSelfTestStartupPrefix() { return false; }
        private sealed class SelfTestBuilding : Building
        {
            public SelfTestBuilding(Data.BuildingData data) : base(40, 40, data) { }
            public override bool PlacementCheckAtRootTile() { return false; }
        }
        private void RuntimeSelfTest()
        {
            // 专用启动参数仅在隔离测试副本使用，不读取或改写玩家关卡。
            Data.BuildingData data = ScriptableObject.CreateInstance<Data.BuildingData>();
            data.affectedTypes = new List<Model.Type>();
            data.allowedTypes = new List<Model.Type>();
            data.range = 7;
            data.footprint = new Vector2Int(3, 3);
            try
            {
                Building building = new SelfTestBuilding(data);
                if (building.Range != 7) throw new Exception("默认范围不正确。");
                BuildingFactor = 3;
                if (building.Range != 21) throw new Exception("建筑范围补丁未生效。");
                RecyclingFactor = 4;
                Building recycler = new Model.Buildings.Tier3.RecyclingSilo(40, 40, data);
                if (recycler.Range != 28) throw new Exception("回收范围补丁未生效或发生叠加。");
                BuildingFactor = RecyclingFactor = 1;
                if (building.Range != 7 || recycler.Range != 7) throw new Exception("恢复原始范围失败。");
                Model.Buildings.Tier1.Irrigator irrigator = new Model.Buildings.Tier1.Irrigator(40, 40, data);
                Vector2Int[] original = (Vector2Int[])AccessTools.Method(irrigator.GetType(), "GetArea").Invoke(irrigator, null);
                HashSet<Cell> expanded = RangePolicy.ExpandArea(original.Select(p => new Cell(p.x, p.y)), 40, 40, 3, 3, 3, 128);
                if (original.Length == 0 || expanded.Count <= original.Distinct().Count()) throw new Exception("灌溉覆盖区域没有扩大。");
                IgnoreConditions = true;
                if (Model.Helpers.ClimateEffectivenessHelper.EvaluateCurrentClimate(data) != ClimateEffectivenessRange.Ideal)
                    throw new Exception("气候条件补丁没有生效。");
                foreach (string method in new string[] { "CanPlaceBuildingAtLocation", "PlaceCurrentBuilding", "IsOverUI", "IsPointerOverUIObject" })
                {
                    Type type = method == "CanPlaceBuildingAtLocation" ? typeof(MapController) : typeof(SelectionHandler);
                    if (!Harmony.GetPatchInfo(AccessTools.DeclaredMethod(type, method)).Owners.Contains(Id))
                        throw new Exception("建造或界面补丁缺失：" + method);
                }
                // 使用人工地图和纯托管控制器引用，检查真实建造判定补丁；不创建关卡或存档。
                MapState map = (MapState)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(MapState));
                map.size = 8;
                map.map = new Tile[64];
                for (int x = 0; x < 8; x++) for (int z = 0; z < 8; z++)
                {
                    Tile tile = (Tile)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Tile));
                    tile.x = x; tile.z = z; tile.height = 2;
                    map.map[x * 8 + z] = tile;
                }
                var buildings = new Dictionary<Vector2Int, Building>();
                AccessTools.Field(typeof(MapState), "_buildings").SetValue(map, buildings);
                GameState state = (GameState)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameState));
                state.mapState = map;
                selfTestState = state;
                AddPatch(typeof(GameController), "get_GameState", "SelfTestStatePrefix", true);
                var controller = (GameController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameController));
                var mapController = (MapController)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(MapController));
                AccessTools.Field(typeof(MapController), "gameController").SetValue(mapController, controller);
                AnyPosition = IgnoreConditions = false;
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("原始限制被意外放宽。");
                AnyPosition = true; IgnoreConditions = false;
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("任意位置开关意外忽略专用条件。");
                AnyPosition = false; IgnoreConditions = true;
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("忽略条件开关意外忽略地形高度。");
                AnyPosition = IgnoreConditions = true;
                if (!mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("放宽建造判定没有生效。");
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(7, 7))) throw new Exception("越界建造没有拦截。");
                buildings.Add(new Vector2Int(2, 2), building);
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("覆盖现有建筑没有拦截。");
                buildings.Clear(); map.GetTile(2, 2).isOnFire = true;
                if (mapController.CanPlaceBuildingAtLocation(building, new Vector2Int(2, 2))) throw new Exception("燃烧地块没有拦截。");
                AnyPosition = IgnoreConditions = false;
                Logger.LogInfo("自检覆盖：真实范围返回值、独立倍率、恢复原值、实际灌溉原区域、气候补丁、建造及界面补丁注册。");
            }
            finally
            {
                selfTestState = null;
                harmony.Unpatch(AccessTools.DeclaredMethod(typeof(GameController), "get_GameState"), AccessTools.Method(typeof(TrainerPlugin), "SelfTestStatePrefix"));
                Destroy(data);
            }
        }
        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame) Visible = !Visible;
            if (!Ready) return;
            if (currencyQueued)
            {
                currencyQueued = false;
                GameState state = CurrentState();
                if (state == null) { status = "请先进入关卡，再设置叶子货币。"; return; }
                state.currency = queuedCurrency;
                status = "已将当前关卡叶子货币设置为 " + queuedCurrency + "。";
                Logger.LogInfo(status);
            }
            UpdateRover();
            animals.Update();
        }
        private void UpdateRover()
        {
            Lib.Recyclers.LandRecycler current = Lib.Recyclers.LandRecycler.InstanceExists() ? Lib.Recyclers.LandRecycler.Instance : null;
            if (current != rover)
            {
                RestoreRover();
                rover = current;
                if (rover != null) originalRoverRange = (int)RoverRange.GetValue(rover);
            }
            if (rover != null)
            {
                GameState state = CurrentState();
                int size = state != null && state.mapState != null ? state.mapState.size : 0;
                RoverRange.SetValue(rover, RangePolicy.ScaleRadius(originalRoverRange, RecyclingFactor, size));
            }
        }
        private void RestoreRover() { if (rover != null && RoverRange != null) RoverRange.SetValue(rover, originalRoverRange); }
        private void ResetAll()
        {
            AnyPosition = IgnoreConditions = false;
            IrrigationFactor = RecyclingFactor = BuildingFactor = 1;
            currencyQueued = false;
            RestoreRover();
            if (animals != null) animals.Reset();
            status = "所有开关已关闭、范围恢复为 1 倍；动物心情恢复按环境计算。已改货币和已完成操作不会回退。";
        }
        private void OnDestroy()
        {
            Ready = false;
            if (animals != null) animals.Reset();
            RestoreRover();
            if (harmony != null) harmony.UnpatchSelf();
            if (skin != null) Destroy(skin);
            if (chineseFont != null) Destroy(chineseFont);
            Instance = null;
        }
        private void OnGUI()
        {
            if (!Visible)
            {
                if (skin != null) GUI.skin = skin;
                if (animals != null) animals.DrawMarker();
                return;
            }
            if (skin == null)
            {
                skin = Instantiate(GUI.skin);
                chineseFont = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "SimHei", "Arial Unicode MS" }, 17);
                skin.font = chineseFont;
                skin.label.fontSize = skin.button.fontSize = skin.toggle.fontSize = skin.textField.fontSize = 17;
                skin.label.wordWrap = true;
                skin.button.padding = new RectOffset(8, 8, 6, 6);
                skin.window.fontSize = 20;
            }
            GUI.skin = skin;
            float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 560f, Screen.height / 660f));
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            panel.x = Mathf.Clamp(panel.x, 0, Screen.width / scale - panel.width);
            panel.y = Mathf.Clamp(panel.y, 0, Screen.height / scale - panel.height);
            panel = GUILayout.Window(1593030, panel, DrawPanel, "伊始之地 · 中文修改器");
            GUI.matrix = old;
        }
        private void DrawPanel(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("F8 显示/隐藏　｜　关闭面板后操作游戏");
            GUILayout.Label(status);
            int nextTab = GUILayout.Toolbar(panelTab, new string[] { "货币与建造", "动物心情与迁移" });
            if (nextTab != panelTab) { panelTab = nextTab; scroll = Vector2.zero; }
            if (panelTab == 1)
            {
                if (animals != null) animals.DrawPanel();
                GUI.enabled = Ready;
                if (GUILayout.Button("关闭全部修改并清除推荐", GUILayout.Height(36))) ResetAll();
                GUI.enabled = true;
                if (GUILayout.Button("隐藏面板（F8 可重新打开）", GUILayout.Height(36))) Visible = false;
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0, 0, panel.width, 28));
                return;
            }
            GameState state = CurrentState();
            GUILayout.Space(6);
            GUILayout.Label("叶子货币　当前：" + (state == null ? "尚未进入关卡" : state.currency.ToString()));
            GUI.enabled = Ready;
            GUILayout.BeginHorizontal();
            currencyInput = GUILayout.TextField(currencyInput, 7, GUILayout.Width(245));
            if (GUILayout.Button("设置数量", GUILayout.Height(34)))
            {
                int amount;
                if (!int.TryParse(currencyInput, out amount) || amount < 0 || amount > 9999999)
                    status = "请输入 0 到 9999999 之间的整数。";
                else { queuedCurrency = amount; currencyQueued = true; }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            IrrigationFactor = FactorRow("灌溉器覆盖范围", IrrigationFactor);
            RecyclingFactor = FactorRow("回收器回收半径", RecyclingFactor);
            BuildingFactor = FactorRow("其他建筑作用半径", BuildingFactor);
            GUILayout.Space(8);
            AnyPosition = GUILayout.Toggle(AnyPosition, " 任意位置建造：放宽地形与高度限制", GUILayout.Height(30));
            IgnoreConditions = GUILayout.Toggle(IgnoreConditions, " 忽略建造条件：供电、资源、气候等", GUILayout.Height(30));
            GUILayout.Label("1 倍为原始范围。三个倍率独立，不相乘。\n特殊建筑专属流程、解锁与游戏阶段仍由游戏控制。\n越界和覆盖现有建筑继续拦截；货币仍按原价扣除。");
            GUILayout.Space(8);
            if (GUILayout.Button("关闭全部修改并恢复原始范围", GUILayout.Height(36))) ResetAll();
            GUI.enabled = true;
            if (GUILayout.Button("隐藏面板（F8 可重新打开）", GUILayout.Height(36))) Visible = false;
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, panel.width, 28));
        }
        private int FactorRow(string label, int factor)
        {
            GUILayout.Label(label + "：" + factor + " 倍");
            return GUILayout.SelectionGrid(factor - 1, new string[] { "1", "2", "3", "4", "5" }, 5, GUILayout.Height(30)) + 1;
        }
        private static void RangePostfix(Building __instance, ref int __result)
        {
            if (!Ready || __result <= 0 || __instance == null) return;
            string name = __instance.GetType().Name;
            bool recycling = name.IndexOf("Recycl", StringComparison.OrdinalIgnoreCase) >= 0 || name == "LoadingPort";
            int factor = recycling ? RecyclingFactor : BuildingFactor;
            GameState state = CurrentState();
            __result = RangePolicy.ScaleRadius(__result, factor, state != null && state.mapState != null ? state.mapState.size : 0);
        }
        private static void IrrigationPostfix(Model.Buildings.Tier1.Irrigator __instance, ref Vector2Int[] __result)
        {
            if (!Ready || IrrigationFactor == 1 || __result == null) return;
            GameState state = CurrentState();
            if (state == null || state.mapState == null) return;
            IEnumerable<Cell> input = __result.Select(p => new Cell(p.x, p.y));
            HashSet<Cell> area = RangePolicy.ExpandArea(input, __instance.x, __instance.z,
                __instance.Footprint.x, __instance.Footprint.y, IrrigationFactor, state.mapState.size);
            __result = area.OrderBy(p => p.X).ThenBy(p => p.Z).Select(p => new Vector2Int(p.X, p.Z)).ToArray();
        }
        private static bool ClimatePrefix(ref ClimateEffectivenessRange __result)
        {
            if (!Ready || !IgnoreConditions) return true;
            __result = ClimateEffectivenessRange.Ideal;
            return false;
        }
        private static void PlacementPostfix(MapController __instance, Building building, Vector2Int location, ref bool __result)
        {
            if (!Ready || (!AnyPosition && !IgnoreConditions) || __result || building == null) return;
            MapState map = __instance.MapState;
            Vector2Int footprint = building.Footprint;
            if (map == null || !RangePolicy.FootprintInside(location.x, location.y, footprint.x, footprint.y, map.size)) return;
            try
            {
                if (!IgnoreConditions && !building.PlacementCheckAtRootTile()) return;
                bool hasResources = false, across = false, restricted = false;
                for (int x = 0; x < footprint.x; x++)
                    for (int z = 0; z < footprint.y; z++)
                    {
                        Tile tile = map.GetTile(location.x + x, location.y + z);
                        if (tile == null || tile.isOnFire || tile.radioactivity > 0 || map.GetBuildingAt(tile) != null) return;
                        if (Lib.Recyclers.LandRecycler.InstanceExists())
                        {
                            var current = Lib.Recyclers.LandRecycler.Instance.CurrentTile;
                            if (current != null && current.X == tile.x && current.Z == tile.z) return;
                        }
                        if (!AnyPosition && (!building.AllowedOnTile(tile) || !(bool)HeightCheck.Invoke(__instance, new object[] { building, tile }))) return;
                        if (IgnoreConditions) { hasResources = across = restricted = true; }
                        else
                        {
                            hasResources |= building.TileHasRequiredResources(tile);
                            across |= building.PlacementCheckAcrossAllTiles(tile);
                            restricted |= (bool)RestrictedCheck.Invoke(__instance, new object[] { tile });
                        }
                    }
                __result = hasResources && across && restricted;
            }
            catch (Exception ex)
            {
                // 专用建筑检查失败时继续拒绝放置，避免缺失关联对象导致关卡损坏。
                if (Instance != null) Instance.status = "该建筑需要专属流程，无法放宽当前放置条件：" + ex.GetBaseException().Message;
            }
        }
        private static bool BlockPlacementPrefix() { return !Visible; }
        private static bool BlockUiPrefix(ref bool __result)
        {
            if (!Visible) return true;
            __result = true;
            return false;
        }
    }
}
