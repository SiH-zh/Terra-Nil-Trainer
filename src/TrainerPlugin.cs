using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
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
    [BepInPlugin(Id, "伊始之地中文修改器", "1.4.1")]
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
        private PanelEdge resizingEdge;
        private Rect resizeStartPanel;
        private Vector2 resizeStartMouse;
        private int resizeControlId;
        private bool resetPanelQueued;
        private Font chineseFont;
        private GUISkin skin;
        private Vector2 scroll;
        private int panelTab;
        private AnimalModule animals;
        private UndoModule undo;
        private TerrainModule terrain;
        private ConfigEntry<Key> panelKey;
        private ConfigEntry<bool> controlKey, shiftKey, altKey;
        private bool recordingKey;
        private int recordStartFrame;
        private bool updateReported, guiReported;
        private bool guiHasCapture;
        private int lastGuiFrame = -100;
        private static readonly Rect ReopenButton = new Rect(12, 12, 132, 36);
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
            undo = new UndoModule(this);
            terrain = new TerrainModule(this);
            panelKey = Config.Bind<Key>(ShortcutSettings.Section, ShortcutSettings.MainEntry, Key.F6, "显示或隐藏修改器面板的主按键。");
            controlKey = Config.Bind<bool>(ShortcutSettings.Section, ShortcutSettings.ControlEntry, false, "快捷键是否需要同时按住控制键。");
            shiftKey = Config.Bind<bool>(ShortcutSettings.Section, ShortcutSettings.ShiftEntry, false, "快捷键是否需要同时按住上档键。");
            altKey = Config.Bind<bool>(ShortcutSettings.Section, ShortcutSettings.AltEntry, false, "快捷键是否需要同时按住交替键。");
            if (!ShortcutSettings.IsSupported(panelKey.Value.ToString())) SetShortcut(new ShortcutChoice());
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
                AddPatch(typeof(SelectionHandler), "IsOverUI", "BlockUiAtPositionPrefix", true);
                AddPatch(typeof(SelectionHandler), "IsPointerOverUIObject", "BlockUiPrefix", true);
                AddPatch(typeof(SelectionHandler), "Update", "SelectionUpdatePrefix", true, typeof(TerrainModule));
                AddPatch(typeof(SelectionHandler), "PlaceCurrentBuilding", "PlacementPrefix", true, typeof(TerrainModule));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(AnimalGroup), "UpdateNeedsAndHappiness"), prefix: new HarmonyMethod(typeof(AnimalModule), "MoodUpdatePrefix"), postfix: new HarmonyMethod(typeof(AnimalModule), "MoodUpdatePostfix"), finalizer: new HarmonyMethod(typeof(AnimalModule), "MoodUpdateFinalizer"));
                AddPatch(typeof(Data.AnimalGroupData), "CalculateNeedsAndHappiness", "MoodCalculationPostfix", false, typeof(AnimalModule));
                AddPatch(typeof(AnimalGroup), "GetTerritoryMaxTilesBase", "TerritoryLimitPostfix", false, typeof(AnimalModule), Type.EmptyTypes);
                AddPatch(typeof(GameController), "ExecuteCommand", "CommandPostfix", false, typeof(UndoModule));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(GameController), "UndoPreviousMove"), prefix: new HarmonyMethod(typeof(UndoModule), "UndoPrefix"), postfix: new HarmonyMethod(typeof(UndoModule), "UndoPostfix"), finalizer: new HarmonyMethod(typeof(UndoModule), "UndoFinalizer"));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(View.Handlers.UIHandler), "PressUndoButton"), prefix: new HarmonyMethod(typeof(UndoModule), "ButtonPrefix"), finalizer: new HarmonyMethod(typeof(UndoModule), "ButtonFinalizer"));
                AddPatch(typeof(View.Handlers.UIHandler), "SetUndoButtonTweenVisible", "ButtonVisibilityPostfix", false, typeof(UndoModule));
                if (RoverRange == null || HeightCheck == null || RestrictedCheck == null)
                    throw new MissingMemberException("未找到完整的回收或建造判定接口。");
                Ready = true;
                status = "版本匹配，功能就绪；无限撤销默认开启，其余修改默认关闭。";
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
        private void AddPatch(Type type, string name, string hook, bool prefix, Type hookType = null, Type[] parameters = null)
        {
            MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters);
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
            if (!updateReported) { updateReported = true; Logger.LogInfo("面板按键更新已运行；当前快捷键：" + ShortcutLabel); }
            UpdateShortcut();
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
            undo.Update();
            terrain.Update();
        }
        internal void ReportError(Exception error) { Logger.LogError(error); }
        internal void ReportStatus(string message) { Logger.LogInfo(message); }
        public static string ShortcutLabel
        {
            get { return Instance != null && Instance.panelKey != null ? Instance.GetShortcut().Describe() : "F6"; }
        }
        private ShortcutChoice GetShortcut()
        {
            return new ShortcutChoice { MainKey = panelKey.Value.ToString(), Control = controlKey.Value, Shift = shiftKey.Value, Alt = altKey.Value };
        }
        private void SetShortcut(ShortcutChoice choice)
        {
            bool save = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            try
            {
                panelKey.Value = (Key)Enum.Parse(typeof(Key), choice.MainKey);
                controlKey.Value = choice.Control; shiftKey.Value = choice.Shift; altKey.Value = choice.Alt;
                Config.Save();
            }
            finally { Config.SaveOnConfigSet = save; }
            status = "快捷键已保存为 " + choice.Describe() + "，本次及下次启动均生效。";
        }
        private void UpdateShortcut()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || panelKey == null) return;
            bool control = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            bool alt = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            if (recordingKey)
            {
                if (Time.frameCount <= recordStartFrame) return;
                if (keyboard.escapeKey.wasPressedThisFrame) { recordingKey = false; status = "快捷键录制已取消。"; return; }
                foreach (var key in keyboard.allKeys)
                    if (key.wasPressedThisFrame && ShortcutSettings.IsSupported(key.keyCode.ToString()))
                    {
                        SetShortcut(new ShortcutChoice { MainKey = key.keyCode.ToString(), Control = control, Shift = shift, Alt = alt });
                        recordingKey = false; Visible = true; return;
                    }
                return;
            }
            if (keyboard[panelKey.Value].wasPressedThisFrame && control == controlKey.Value && shift == shiftKey.Value && alt == altKey.Value)
            {
                Visible = !Visible;
                Logger.LogInfo("快捷键已触发，面板" + (Visible ? "显示。" : "隐藏。"));
            }
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
            recordingKey = false;
            RestoreRover();
            if (animals != null) animals.Reset();
            if (undo != null) undo.Reset();
            if (terrain != null) terrain.Reset();
            status = "所有开关已关闭、范围恢复为 1 倍；动物心情恢复按环境计算。已改货币和已完成操作不会回退。";
        }
        private void OnDestroy()
        {
            Ready = false;
            if (animals != null) animals.Reset();
            if (undo != null) undo.Reset();
            if (terrain != null) terrain.Reset();
            RestoreRover();
            if (harmony != null) harmony.UnpatchSelf();
            if (skin != null) Destroy(skin);
            if (chineseFont != null) Destroy(chineseFont);
            Instance = null;
        }
        private void OnGUI()
        {
            GUISkin oldSkin = GUI.skin;
            Matrix4x4 oldMatrix = GUI.matrix;
            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            bool oldEnabled = GUI.enabled;
            try
            {
                GUI.matrix = Matrix4x4.identity;
                GUI.depth = -1000;
                GUI.color = Color.white;
                GUI.enabled = true;
                RenderGui();
                guiHasCapture = GUIUtility.hotControl != 0;
            }
            finally { GUI.skin = oldSkin; GUI.matrix = oldMatrix; GUI.depth = oldDepth; GUI.color = oldColor; GUI.enabled = oldEnabled; }
        }
        private void RenderGui()
        {
            if (skin != null) GUI.skin = skin;
            if (animals != null) animals.DrawMarker();
            if (animals != null) animals.DrawAllHighlights();
            if (terrain != null) terrain.DrawOverlay();
            if (!Visible)
            {
                CancelPanelResize();
                if (skin != null) GUI.skin = skin;
                if (GUI.Button(ReopenButton, new GUIContent("打开修改器", "当前快捷键：" + ShortcutLabel))) Visible = true;
                if (Event.current.type == EventType.Repaint) lastGuiFrame = Time.frameCount;
                return;
            }
            if (skin == null)
            {
                skin = Instantiate(GUI.skin);
                chineseFont = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "SimHei", "Arial Unicode MS" }, 17);
                skin.font = chineseFont;
                skin.label.fontSize = skin.button.fontSize = skin.toggle.fontSize = skin.textField.fontSize = 17;
                skin.label.wordWrap = true;
                skin.box.wordWrap = skin.button.wordWrap = true;
                skin.button.padding = new RectOffset(8, 8, 6, 6);
                skin.window.fontSize = 20;
                skin.window.padding = new RectOffset(12, 12, 30, 16);
            }
            GUI.skin = skin;
            float scale = GuiScale();
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            Vector2 canvas = new Vector2(Screen.width / scale, Screen.height / scale);
            panel = PanelResize.Clamp(panel, canvas);
            HandlePanelResize(canvas);
            panel = GUILayout.Window(1593030, panel, DrawPanel, "伊始之地 · 中文修改器", GUILayout.Width(panel.width), GUILayout.Height(panel.height));
            if (resetPanelQueued) { resetPanelQueued = false; panel = PanelResize.Clamp(new Rect(30, 60, 510, 565), canvas); }
            DrawResizeBorder();
            GUI.matrix = old;
            if (Event.current.type == EventType.Repaint)
            {
                lastGuiFrame = Time.frameCount;
                if (!guiReported) { guiReported = true; Logger.LogInfo("面板已绘制；分辨率：" + Screen.width + "×" + Screen.height); }
            }
        }
        private void DrawPanel(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label(ShortcutLabel + " 显示/隐藏　｜　" + (terrain != null && terrain.Enabled ? "涂抹模式中，退出涂抹后可建造" : "面板外正常预览和建造"));
            GUILayout.BeginHorizontal();
            GUILayout.Label("拖动边框或四角调整大小，拖动标题移动。");
            if (GUILayout.Button("恢复面板大小", GUILayout.Width(128))) resetPanelQueued = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(recordingKey ? "等待按键…按 Esc（退出键）取消" : "自定义快捷键"))
            {
                recordingKey = true; recordStartFrame = Time.frameCount;
                status = "请按主按键，可同时按住控制键、上档键或交替键；退出键取消。";
            }
            if (GUILayout.Button("恢复默认 F6", GUILayout.Width(150))) { recordingKey = false; SetShortcut(new ShortcutChoice()); }
            GUILayout.EndHorizontal();
            GUILayout.Label(status);
            int nextTab = GUILayout.Toolbar(panelTab, new string[] { "货币与建造", "动物心情与迁移", "上帝之手" });
            if (nextTab != panelTab) { panelTab = nextTab; scroll = Vector2.zero; }
            if (panelTab == 2)
            {
                if (terrain != null) terrain.DrawPanel();
                GUI.enabled = Ready;
                if (GUILayout.Button("关闭全部修改", GUILayout.Height(36))) ResetAll();
                GUI.enabled = true;
                if (GUILayout.Button("隐藏面板（" + ShortcutLabel + " 可重新打开）", GUILayout.Height(36))) Visible = false;
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(18, 8, panel.width - 36, 20));
                return;
            }
            if (panelTab == 1)
            {
                if (animals != null) animals.DrawPanel();
                GUI.enabled = Ready;
                if (GUILayout.Button("关闭全部修改并清除推荐", GUILayout.Height(36))) ResetAll();
                GUI.enabled = true;
                if (GUILayout.Button("隐藏面板（" + ShortcutLabel + " 可重新打开）", GUILayout.Height(36))) Visible = false;
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(18, 8, panel.width - 36, 20));
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
            if (undo != null) undo.DrawPanel();
            GUILayout.Space(8);
            if (GUILayout.Button("关闭全部修改并恢复原始范围", GUILayout.Height(36))) ResetAll();
            GUI.enabled = true;
            if (GUILayout.Button("隐藏面板（" + ShortcutLabel + " 可重新打开）", GUILayout.Height(36))) Visible = false;
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(18, 8, panel.width - 36, 20));
        }
        private void CancelPanelResize()
        {
            if (resizingEdge == PanelEdge.None) return;
            if (GUIUtility.hotControl == resizeControlId) GUIUtility.hotControl = 0;
            resizingEdge = PanelEdge.None;
            guiHasCapture = false;
        }
        private void HandlePanelResize(Vector2 canvas)
        {
            Event input = Event.current;
            int id = GUIUtility.GetControlID(1593032, FocusType.Passive);
            if (resizingEdge != PanelEdge.None)
            {
                if (input.type == EventType.MouseDrag)
                {
                    panel = PanelResize.Drag(resizeStartPanel, input.mousePosition - resizeStartMouse, resizingEdge, canvas);
                    input.Use();
                }
                else if ((input.type == EventType.MouseUp && input.button == 0) || input.type == EventType.Ignore)
                { CancelPanelResize(); if (input.type == EventType.MouseUp) input.Use(); }
                return;
            }
            if (input.type != EventType.MouseDown || input.button != 0 || GUIUtility.hotControl != 0) return;
            PanelEdge edge = PanelResize.Hit(panel, input.mousePosition);
            if (edge == PanelEdge.None) return;
            resizingEdge = edge; resizeStartPanel = panel; resizeStartMouse = input.mousePosition;
            resizeControlId = id; GUIUtility.hotControl = id; input.Use();
        }
        private void DrawResizeBorder()
        {
            if (Event.current.type != EventType.Repaint) return;
            PanelEdge hover = resizingEdge != PanelEdge.None ? resizingEdge : PanelResize.Hit(panel, Event.current.mousePosition);
            Color previous = GUI.color;
            GUI.color = hover == PanelEdge.None ? new Color(0.45f, 0.65f, 0.72f, 0.6f) : new Color(0.3f, 0.9f, 1f, 1f);
            GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(panel.x, panel.yMax - 2, panel.width, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(panel.x, panel.y, 2, panel.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(panel.xMax - 2, panel.y, 2, panel.height), Texture2D.whiteTexture);
            GUI.Label(new Rect(panel.xMax - 18, panel.yMax - 21, 18, 21), "◢");
            GUI.color = previous;
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
        private static bool PanelBlocksGame()
        {
            var mouse = Mouse.current;
            if (mouse == null) return false;
            return OverlayContainsScreenPoint(mouse.position.ReadValue());
        }
        private static float GuiScale()
        {
            return Mathf.Max(0.01f, Mathf.Min(1f, Mathf.Min(Screen.width / 560f, Screen.height / 660f)));
        }
        internal static Rect VisiblePanelBounds
        {
            get
            {
                if (!Visible || Instance == null) return new Rect();
                Rect p = Instance.panel; float scale = GuiScale();
                return new Rect(p.x * scale, p.y * scale, p.width * scale, p.height * scale);
            }
        }
        internal static bool OverlayContainsScreenPoint(Vector2 point)
        {
            if (Instance == null || Screen.width <= 0 || Screen.height <= 0 || Time.frameCount - Instance.lastGuiFrame > 2) return false;
            if (Instance.guiHasCapture || Instance.resizingEdge != PanelEdge.None) return true;
            point.y = Screen.height - point.y;
            if (Instance.terrain != null && Instance.terrain.Enabled && TerrainModule.StopButton.Contains(point)) return true;
            return Visible ? Instance.panel.Contains(point / GuiScale()) : ReopenButton.Contains(point);
        }
        private static bool BlockPlacementPrefix() { return !PanelBlocksGame(); }
        private static bool BlockUiAtPositionPrefix(Vector2 screenPosition, ref bool __result)
        {
            if (!OverlayContainsScreenPoint(screenPosition)) return true;
            __result = true;
            return false;
        }
        private static bool BlockUiPrefix(ref bool __result)
        {
            if (!PanelBlocksGame()) return true;
            __result = true;
            return false;
        }
    }
}
