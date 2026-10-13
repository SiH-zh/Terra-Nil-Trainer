using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Controller;
using Controller.Map;
using Model;
using Model.Commands;
using Model.State;
using View.Handlers;
using View.Handlers.Selection;
using View.Tiles.Gameplay;
using TerrainType = Model.Type;
using Object = System.Object;

namespace TerraNilTrainer
{
    public sealed class TerrainChoice
    {
        public readonly TerrainType Type;
        public readonly string Name;
        public TerrainChoice(TerrainType type, string name) { Type = type; Name = name; }
    }
    public sealed class TerrainModule
    {
        internal static TerrainModule Active;
        private readonly TrainerPlugin host;
        private static readonly MethodInfo CursorTile = AccessTools.Method(typeof(SelectionHandler), "GetGameplayTile", new[] { typeof(Vector2) });
        private readonly HashSet<Vector2Int> pending = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, Vector3> previewPositions = new Dictionary<Vector2Int, Vector3>();
        private readonly List<Tile> brushPreview = new List<Tile>();
        private GameState strokeState;
        private Vector2Int? previousCenter;
        private bool enabled, drawing, waitRelease;
        private bool pickerEnabled;
        private GameState pickedState;
        private Tile pickedTile;
        private Vector2 pickerScreen;
        private int selectedChoice = 2, radius = 1;
        private bool square;
        private string filter = "", message = "选择目标地形，开启后在地图上按住左键涂抹；松开提交一笔。";
        private Vector2 choiceScroll;
        internal static readonly Rect StopButton = new Rect(12, 54, 360, 40);
        public bool Enabled { get { return enabled; } }
        public bool Drawing { get { return drawing; } }
        public int PendingCount { get { return pending.Count; } }
        public bool PickerEnabled { get { return pickerEnabled; } }
        public string PickedName { get { return pickedTile == null ? "" : TerrainName(pickedTile.type); } }
        public static readonly TerrainChoice[] Choices = new[] {
            new TerrainChoice(TerrainType.Wasteland,"荒地"), new TerrainChoice(TerrainType.Soil,"净化土壤"),
            new TerrainChoice(TerrainType.Greenery,"草地"), new TerrainChoice(TerrainType.Forest,"森林"),
            new TerrainChoice(TerrainType.ForestGround,"林地地面"), new TerrainChoice(TerrainType.DeadForest,"枯树林"),
            new TerrainChoice(TerrainType.Fynbos,"凡波斯灌丛"), new TerrainChoice(TerrainType.Wetland,"湿地"),
            new TerrainChoice(TerrainType.Rock,"岩石"), new TerrainChoice(TerrainType.Ash,"灰烬"),
            new TerrainChoice(TerrainType.DryRiverbed,"干涸河床"), new TerrainChoice(TerrainType.River,"河流"),
            new TerrainChoice(TerrainType.Ocean,"海洋"), new TerrainChoice(TerrainType.Beach,"沙滩"),
            new TerrainChoice(TerrainType.BeachVegetation,"沙滩植被"), new TerrainChoice(TerrainType.Mangroves,"红树林"),
            new TerrainChoice(TerrainType.TropicalForest,"热带森林"), new TerrainChoice(TerrainType.Coral,"珊瑚"),
            new TerrainChoice(TerrainType.Lagoon,"潟湖"), new TerrainChoice(TerrainType.Snow,"雪地"),
            new TerrainChoice(TerrainType.Tundra,"苔原"), new TerrainChoice(TerrainType.Kelp,"海藻林"),
            new TerrainChoice(TerrainType.Lichen,"地衣"), new TerrainChoice(TerrainType.Ice,"冰面"),
            new TerrainChoice(TerrainType.OceanRock,"海洋岩石"), new TerrainChoice(TerrainType.DryBrush,"干灌丛"),
            new TerrainChoice(TerrainType.Bamboo,"竹林"), new TerrainChoice(TerrainType.Sunflower,"向日葵"),
            new TerrainChoice(TerrainType.DeciduousForest,"落叶林"), new TerrainChoice(TerrainType.BorealForest,"针叶林"),
            new TerrainChoice(TerrainType.RockyScrubland,"岩石灌木地"), new TerrainChoice(TerrainType.LakeFloor,"湖底"),
            new TerrainChoice(TerrainType.LakeFloorVegetation,"湖底植被"), new TerrainChoice(TerrainType.SaltVlei,"盐沼"),
            new TerrainChoice(TerrainType.DesertSand,"沙漠沙地"), new TerrainChoice(TerrainType.DesertDune,"沙丘"),
            new TerrainChoice(TerrainType.ChokedRiverbed,"淤塞河床"), new TerrainChoice(TerrainType.Savannah,"稀树草原"),
            new TerrainChoice(TerrainType.Cacti,"仙人掌"), new TerrainChoice(TerrainType.AlluvialVegetation,"冲积植被"),
            new TerrainChoice(TerrainType.Mudflats,"泥滩"), new TerrainChoice(TerrainType.MudReeds,"芦苇湿地"),
            new TerrainChoice(TerrainType.WaterHyacinth,"水葫芦"), new TerrainChoice(TerrainType.FruitForest,"果树林")
        };
        public TerrainModule(TrainerPlugin plugin) { host = plugin; Active = this; }
        public static bool Supported(TerrainType type) { return Choices.Any(c => c.Type == type); }
        public static bool CanPaint(MapState map, Tile tile)
        {
            if (map == null || tile == null || tile.type == TerrainType.None || tile.type.IsBuilding() ||
                tile.isOnFire || tile.radioactivity > 0 || tile.isPipe > 0 || tile.isAqueduct > 0 ||
                tile.concreteHeight != Tile.EmptyConcreteHeight || (!Supported(tile.type) && tile.type != TerrainType.DirtyOcean && tile.type != TerrainType.DirtySnow)) return false;
            return map.GetBuildingAt(tile) == null;
        }
        public static IEnumerable<Vector2Int> BrushCells(MapState map, Vector2Int center, int size, bool isSquare)
        {
            if (map == null || center.x < 0 || center.y < 0 || center.x >= map.size || center.y >= map.size) yield break;
            size = Math.Max(0, Math.Min(8, size));
            for (int x = Math.Max(0, center.x - size); x <= Math.Min(map.size - 1, center.x + size); x++)
                for (int z = Math.Max(0, center.y - size); z <= Math.Min(map.size - 1, center.y + size); z++)
                {
                    int dx = x - center.x, dz = z - center.y;
                    if (isSquare || dx * dx + dz * dz <= size * size) yield return new Vector2Int(x, z);
                }
        }
        public static IEnumerable<Vector2Int> StrokeLine(Vector2Int from, Vector2Int to)
        {
            int x = from.x, z = from.y, dx = Math.Abs(to.x - x), dz = -Math.Abs(to.y - z);
            int sx = x < to.x ? 1 : -1, sz = z < to.y ? 1 : -1, error = dx + dz;
            while (true)
            {
                yield return new Vector2Int(x, z);
                if (x == to.x && z == to.y) yield break;
                int twice = 2 * error;
                if (twice >= dz) { error += dz; x += sx; }
                if (twice <= dx) { error += dx; z += sz; }
            }
        }
        public static Command BuildCommand(GameState state, IEnumerable<Vector2Int> cells, TerrainType target)
        {
            if (!Supported(target)) throw new ArgumentException("请选择自然地形，不能涂抹生成建筑或危险源。");
            var deltas = new List<Delta>();
            foreach (var coord in cells.Distinct().OrderBy(p => p.x).ThenBy(p => p.y))
            {
                Tile tile = state.mapState.GetTile(coord.x, coord.y);
                if (CanPaint(state.mapState, tile) && tile.type != target)
                    deltas.Add(new Delta(tile, target, null, null, minorEvent: true));
            }
            if (deltas.Count == 0) return null;
            // 原生构造函数计算地形的气候影响；标为玩家命令，使快照、自动存档和撤销继续走游戏流程。
            return new Command(deltas) { isPlayerCommand = true, currencyDelta = 0 };
        }
        public void SetEnabled(bool value)
        {
            if (value && !CanUseScene())
            {
                CancelStroke(); enabled = false;
                message = "请进入已加载的关卡，关闭暂停并结束教程或特殊操作后再开启涂抹。";
                if (host != null) host.ReportStatus(message);
                return;
            }
            CancelStroke(); enabled = value; waitRelease = true;
            // 需求详情的选择和界面要保留；涂抹期间仍由前缀阻断原地图选择及建筑摆放。
            if (value && SelectionHandler.InstanceExists() && !(GameController.Instance.CurrentInteractionMode is Lib.InteractionModes.AnimalNeedsInteraction))
                SelectionHandler.Instance.DeselectAll();
            message = value ? "涂抹已开启：左键拖动，松开提交；右键取消当前笔并退出。" : "涂抹已关闭；未提交的笔画已取消。";
            if (host != null) host.ReportStatus(message);
        }
        public void SetPickerEnabled(bool value) { pickerEnabled = value; ClearPickedTile(); }
        public void Reset() { SetEnabled(false); SetPickerEnabled(false); brushPreview.Clear(); }
        private void ClearPickedTile() { pickedTile = null; pickedState = null; }
        private void CancelStroke()
        { drawing = false; strokeState = null; previousCenter = null; pending.Clear(); previewPositions.Clear(); }
        public static bool SelectionUpdatePrefix()
        { return Active == null || !Active.enabled || !TrainerPlugin.Ready; }
        public static bool PlacementPrefix()
        { return Active == null || !Active.enabled || !TrainerPlugin.Ready; }
        private bool CanUseScene()
        {
            if (!GameController.InstanceExists() || !MapController.InstanceExists() || !MapController.Instance.Initialized ||
                !SelectionHandler.InstanceExists() || !Application.isFocused || Time.timeScale <= 0f) return false;
            if (Menus.PauseMenu.InstanceExists() && Menus.PauseMenu.Instance.IsShown) return false;
            if (TutorialController.InstanceExists() && TutorialController.Instance.TutorialInProgress) return false;
            return IsSupportedInteractionMode(GameController.Instance.CurrentInteractionMode);
        }
        public static bool IsSupportedInteractionMode(Lib.InteractionModes.InteractionMode mode)
        { return mode is Lib.InteractionModes.DefaultInteractionMode || mode is Lib.InteractionModes.AnimalNeedsInteraction; }
        public void Update()
        {
            ClearPickedTile();
            if (!enabled && !pickerEnabled) return;
            var mouse = Mouse.current;
            if (mouse == null) { CancelStroke(); return; }
            if (enabled && mouse.rightButton.wasPressedThisFrame) { SetEnabled(false); return; }
            GameState state = TrainerPlugin.CurrentState();
            if (state == null || state.mapState == null || (drawing && !Object.ReferenceEquals(state, strokeState)))
            {
                if (enabled) { SetEnabled(false); message = "关卡状态已切换，涂抹已退出，未提交的笔画已取消。"; }
                brushPreview.Clear(); return;
            }
            if (!CanUseScene())
            {
                if (enabled) { SetEnabled(false); message = "暂停、离开窗口或进入特殊流程：涂抹已退出，未提交笔画已取消。"; }
                brushPreview.Clear();
                return;
            }
            Vector2 screen = mouse.position.ReadValue();
            bool blocked = TrainerPlugin.OverlayContainsScreenPoint(screen) || SelectionHandler.Instance.IsOverUI(screen);
            Tile hovered = null;
            if (!blocked)
            {
                var view = CursorTile.Invoke(SelectionHandler.Instance, new object[] { screen }) as GameplayTile;
                if (view != null)
                {
                    Tile tile = view.UnderlyingTile;
                    if (tile != null) hovered = state.mapState.GetTile(tile.x, tile.z);
                }
            }
            if (pickerEnabled && hovered != null) { pickedState = state; pickedTile = hovered; pickerScreen = screen; }
            if (!enabled) return;
            if (waitRelease) { if (!mouse.leftButton.isPressed) waitRelease = false; else return; }
            ProcessInput(state, hovered, blocked, mouse.leftButton.wasPressedThisFrame, mouse.leftButton.isPressed, mouse.leftButton.wasReleasedThisFrame);
        }
        public static string TerrainName(TerrainType type)
        {
            TerrainChoice choice = Choices.FirstOrDefault(c => c.Type == type);
            if (choice != null) return choice.Name;
            switch (type)
            {
                case TerrainType.None: return "空地块";
                case TerrainType.DirtyOcean: return "污水海洋";
                case TerrainType.DirtySnow: return "脏雪";
                case TerrainType.Fissure: return "裂隙";
                case TerrainType.Lava: return "熔岩";
                case TerrainType.LavaSource: return "熔岩源";
                case TerrainType.Concrete: return "混凝土地面";
                case TerrainType.ConcreteCleanWater: return "积水混凝土地面";
                case TerrainType.Radiation: return "放射性地块";
                case TerrainType.Chasm: return "裂谷";
                case TerrainType.Road: return "道路";
                case TerrainType.Oil: return "油污地块";
                case TerrainType.WaterSource: return "水源";
                case TerrainType.RockyOutcrop: return "岩石露头";
            }
            if (StringsController.InstanceExists())
            {
                try
                {
                    string key = "Type." + type.GetTypeForString() + ".Name";
                    string text = StringsController.Instance.GetStringOrDefault(key, false);
                    if (!String.IsNullOrWhiteSpace(text) && text != key)
                        return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "").Trim();
                }
                catch { /* 本地化服务尚未初始化时使用下方的可读回退。 */ }
            }
            return (type.IsBuilding() ? "建筑地块" : "特殊地块") + "（类型编号 " + (int)type + "）";
        }
        public static Rect PickerBounds(Vector2 cursor, Vector2 viewport, Vector2 requestedSize)
        {
            if (viewport.x <= 0f || viewport.y <= 0f) return new Rect();
            float mx = Mathf.Min(8f, Mathf.Max(0f, viewport.x * 0.05f)), my = Mathf.Min(8f, Mathf.Max(0f, viewport.y * 0.05f));
            float width = Mathf.Clamp(requestedSize.x, 1f, Mathf.Max(1f, viewport.x - 2f * mx));
            float height = Mathf.Clamp(requestedSize.y, 1f, Mathf.Max(1f, viewport.y - 2f * my));
            float x = cursor.x + 18f, y = cursor.y + 22f;
            if (x + width > viewport.x - mx) x = cursor.x - width - 18f;
            if (y + height > viewport.y - my) y = cursor.y - height - 22f;
            return new Rect(Mathf.Clamp(x, mx, Mathf.Max(mx, viewport.x - mx - width)), Mathf.Clamp(y, my, Mathf.Max(my, viewport.y - my - height)), width, height);
        }
        public void ProcessInput(GameState state, Tile hovered, bool blocked, bool down, bool held, bool released)
        {
            brushPreview.Clear();
            if (!enabled || state == null || state.mapState == null) return;
            if (drawing && !Object.ReferenceEquals(strokeState, state)) { SetEnabled(false); return; }
            if (blocked || hovered == null) previousCenter = null;
            if (!blocked && hovered != null)
                foreach (var point in BrushCells(state.mapState, new Vector2Int(hovered.x, hovered.z), radius, square))
                { Tile tile = state.mapState.GetTile(point.x, point.y); if (tile != null) brushPreview.Add(tile); }
            if (down && !blocked && hovered != null)
            {
                CancelStroke(); drawing = true; strokeState = state;
            }
            if (drawing && held && !blocked && hovered != null)
            {
                Vector2Int center = new Vector2Int(hovered.x, hovered.z);
                foreach (var step in StrokeLine(previousCenter ?? center, center))
                    foreach (var point in BrushCells(state.mapState, step, radius, square))
                    {
                        Tile tile = state.mapState.GetTile(point.x, point.y);
                        if (CanPaint(state.mapState, tile) && tile.type != Choices[selectedChoice].Type && pending.Add(point)) CacheWorld(point, tile);
                    }
                previousCenter = center;
            }
            if (drawing && released)
            {
                if (blocked) { CancelStroke(); message = "鼠标在菜单上松开：本笔已取消。"; }
                else CommitStroke();
            }
        }
        private void CommitStroke()
        {
            GameState state = strokeState;
            try
            {
                if (!Object.ReferenceEquals(state, TrainerPlugin.CurrentState())) throw new InvalidOperationException("关卡状态已改变，本笔取消。");
                Command command = BuildCommand(state, pending, Choices[selectedChoice].Type);
                if (command == null) { message = "本笔没有需要修改的格子；建筑、管线和危险格会跳过。"; return; }
                GameController.Instance.ExecuteCommand(command);
                if (UIHandler.InstanceExists()) UIHandler.Instance.SetUndoButtonTweenVisible(true);
                message = "已将 " + command.deltas.Count + " 格涂抹为“" + Choices[selectedChoice].Name + "”，本笔可撤销。";
                if (host != null) host.ReportStatus(message);
            }
            catch (Exception error)
            {
                enabled = false; message = "涂抹已停止：" + error.GetBaseException().Message;
                if (host != null) host.ReportError(error);
            }
            finally { CancelStroke(); }
        }
        private void CacheWorld(Vector2Int point, Tile tile)
        {
            if (!MapController.InstanceExists()) return;
            var view = MapController.Instance.GetGameplayTile(tile);
            if (view != null) previewPositions[point] = view.GetSurfacePosition() + Vector3.up * 0.12f;
        }
        public void DrawPanel()
        {
            GUILayout.Label("上帝之手 · 地形涂抹");
            bool nextPicker = GUILayout.Toggle(pickerEnabled, " 开启地块拾取器（鼠标悬停显示名称）");
            if (nextPicker != pickerEnabled) SetPickerEnabled(nextPicker);
            GUILayout.Label("拾取器可单独开启，不改变地形；动物需求详情展开时也能查看和涂抹。");
            GUILayout.Label("目标：" + Choices[selectedChoice].Name + "；状态：" + (enabled ? "涂抹中" : "未开启"));
            GUILayout.Label(message);
            filter = GUILayout.TextField(filter);
            choiceScroll = GUILayout.BeginScrollView(choiceScroll, GUILayout.Height(155));
            for (int i = 0; i < Choices.Length; i++)
                if (filter.Length == 0 || Choices[i].Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    if (GUILayout.Button((selectedChoice == i ? "✓ " : "") + Choices[i].Name)) { selectedChoice = i; CancelStroke(); }
            GUILayout.EndScrollView();
            GUILayout.Label("笔刷半径：" + radius + " 格（0为单格）");
            radius = Mathf.RoundToInt(GUILayout.HorizontalSlider(radius, 0, 8));
            square = GUILayout.Toggle(square, " 使用方形笔刷（未勾选为圆形）");
            bool old = GUI.enabled;
            GUI.enabled = old && TrainerPlugin.Ready && TrainerPlugin.CurrentState() != null;
            if (GUILayout.Button(enabled ? "退出涂抹" : "开启地形涂抹", GUILayout.Height(36))) SetEnabled(!enabled);
            GUI.enabled = old;
            GUILayout.Label("左键按住拖动，松开提交整笔；右键取消未提交笔画并退出。菜单和动物需求详情可保持展开，在界面上点击不会涂抹。\n青色为可涂格，红色为跳过格，黄色为待提交格。每笔作为一步原生撤销。保留原高度，水面显示受高度及关卡规则影响。\n涂抹会按原游戏更新气候、动物需求、进度并自动存档，不消耗或增加叶子。暂停、教程、过场及特殊操作期间不可涂抹。");
        }
        public void DrawOverlay()
        {
            DrawPickerOverlay();
            if (!enabled) return;
            if (GUI.Button(StopButton, "涂抹：" + Choices[selectedChoice].Name + " · 右键或点此退出")) SetEnabled(false);
            if (Event.current.type != EventType.Repaint) return;
            Camera camera = Camera.main;
            if (camera == null) return;
            GameState state = TrainerPlugin.CurrentState();
            if (state == null || state.mapState == null) return;
            Color old = GUI.color;
            foreach (var point in previewPositions.Values) DrawPoint(camera, point, new Color(1f, 0.8f, 0.15f, 0.85f), 7f);
            foreach (Tile tile in brushPreview)
            {
                Vector2Int key = new Vector2Int(tile.x, tile.z);
                CacheWorld(key, tile);
                Vector3 position;
                if (previewPositions.TryGetValue(key, out position))
                    DrawPoint(camera, position, CanPaint(state.mapState, tile) ? Color.cyan : Color.red, 11f);
                if (!pending.Contains(key)) previewPositions.Remove(key);
            }
            GUI.color = old;
            GUI.Label(new Rect(12, 98, 450, 28), "本笔待提交 " + pending.Count + " 格 · 松开左键提交");
        }
        private void DrawPickerOverlay()
        {
            if (!pickerEnabled || pickedTile == null || !Object.ReferenceEquals(pickedState, TrainerPlugin.CurrentState()) || Event.current.type != EventType.Repaint) return;
            string text = "地块：" + TerrainName(pickedTile.type) + "\n坐标（" + pickedTile.x + ", " + pickedTile.z + "） · 海拔 " + pickedTile.height;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, wordWrap = true, richText = false, fontSize = 17, padding = new RectOffset(10, 10, 7, 7) };
            float width = Mathf.Min(320f, Mathf.Max(1f, Screen.width - 16f));
            float height = Mathf.Max(56f, style.CalcHeight(new GUIContent(text), width));
            Rect bounds = PickerBounds(new Vector2(pickerScreen.x, Screen.height - pickerScreen.y), new Vector2(Screen.width, Screen.height), new Vector2(width, height));
            // 提示只绘制，不创建可点击控件，不吞掉地图或详情界面的鼠标事件。
            GUI.Box(bounds, text, style);
        }
        private static void DrawPoint(Camera camera, Vector3 world, Color color, float size)
        {
            Vector3 p = camera.WorldToScreenPoint(world);
            if (p.z <= 0 || p.x < 0 || p.x > Screen.width || p.y < 0 || p.y > Screen.height) return;
            GUI.color = color;
            GUI.DrawTexture(new Rect(p.x - size / 2, Screen.height - p.y - size / 2, size, size), Texture2D.whiteTexture);
        }
    }
}
