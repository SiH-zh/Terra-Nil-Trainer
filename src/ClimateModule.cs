using System;
using UnityEngine;
using Model.State;
using Object = System.Object;

namespace TerraNilTrainer
{
    // 只保存本次关卡的手动设定，不向配置文件写入跨关卡目标。
    public sealed class ClimateModule
    {
        internal static ClimateModule Active;
        private readonly TrainerPlugin host;
        private GameState context;
        private readonly int?[] pending = new int?[Names.Length];
        private readonly int?[] targets = new int?[Names.Length];
        private string message = "拖动滑块直接设值；操作过的项目会保持，未操作的项目仍自然变化。";
        public static readonly string[] Names = { "温度", "湿度", "毒性", "辐射", "地质稳定性", "大气对流", "土壤盐度" };

        public ClimateModule(TrainerPlugin plugin) { host = plugin; Active = this; }
        public static ClimateParameter Parameter(int index)
        {
            switch (index)
            {
                case 0: return ClimateParameter.Temperature;
                case 1: return ClimateParameter.Humidity;
                case 2: return ClimateParameter.Toxicity;
                case 3: return ClimateParameter.Radioactivity;
                case 4: return ClimateParameter.GeologicalStability;
                case 5: return ClimateParameter.AirConvection;
                case 6: return ClimateParameter.Salinity;
                default: return null;
            }
        }
        public static bool Available(GameState state, int index)
        {
            if (state == null || state.climateState == null || state.climateState.ClimateParameters == null || index < 0 || index >= Names.Length) return false;
            ClimateParameter parameter = Parameter(index);
            return parameter != null && parameter.maxDisplayValue > parameter.minDisplayValue && state.climateState.HasClimateParameter(parameter);
        }
        public static int ClampDisplay(ClimateParameter parameter, int value)
        { return Mathf.Clamp(value, parameter.minDisplayValue, parameter.maxDisplayValue); }
        public static int ToInternal(ClimateState climate, ClimateParameter parameter, int value)
        { return Mathf.Clamp(climate.ConvertValue1To1000Range(ClampDisplay(parameter, value), parameter), 0, 1000); }
        public static string DisplayUnit(ClimateParameter parameter)
        { return parameter == null ? "" : System.Text.RegularExpressions.Regex.Replace(parameter.unit ?? "", "<[^>]+>", ""); }
        public bool IsHeld(int index) { return index >= 0 && index < Names.Length && targets[index].HasValue; }
        public void Reset()
        {
            context = null;
            for (int i = 0; i < Names.Length; i++) { pending[i] = null; targets[i] = null; }
        }
        public void Observe(GameState state)
        {
            if (Object.ReferenceEquals(state, context)) return;
            Reset(); context = state;
            message = "拖动滑块直接设值；操作过的项目会保持，未操作的项目仍自然变化。";
        }
        public bool QueueValue(GameState state, int index, int value)
        {
            if (!Available(state, index)) return false;
            Observe(state);
            pending[index] = ClampDisplay(Parameter(index), value);
            return true;
        }
        public void Release(int index)
        {
            if (index < 0 || index >= Names.Length) return;
            pending[index] = null; targets[index] = null;
            message = Names[index] + "已恢复自然变化；已发生的生态变化及完成记录不会回退。";
        }
        public void Update() { Process(TrainerPlugin.CurrentState()); }
        public void Process(GameState state)
        {
            Observe(state);
            if (state == null) return;
            for (int i = 0; i < Names.Length; i++)
            {
                if (!pending[i].HasValue) continue;
                int value = pending[i].Value; pending[i] = null;
                if (!Available(state, i)) { targets[i] = null; continue; }
                ClimateParameter parameter = Parameter(i);
                try
                {
                    targets[i] = value;
                    // 使用原生绝对设值及阈值重评估，不能把游戏显示单位直接写入内部0—1000值。
                    state.climateState.SetClimateParameter(parameter, ToInternal(state.climateState, parameter, value), false);
                    message = Names[i] + "已设为 " + state.climateState.GetPlayerFacingValue(parameter) + DisplayUnit(parameter) + " 并保持；可单独恢复自然变化。";
                }
                catch (Exception error)
                {
                    Reset();
                    message = "气候调节已停止：" + error.GetBaseException().Message;
                    if (host != null) host.ReportError(error);
                    break;
                }
            }
        }
        private bool TryTarget(ClimateState climate, ClimateParameter parameter, out float value)
        {
            value = 0f;
            if (!TrainerPlugin.Ready || context == null || !Object.ReferenceEquals(context, TrainerPlugin.CurrentState()) ||
                !Object.ReferenceEquals(climate, context.climateState)) return false;
            for (int i = 0; i < Names.Length; i++)
                if (targets[i].HasValue && Parameter(i) == parameter && Available(context, i))
                { value = ToInternal(climate, parameter, targets[i].Value); return true; }
            return false;
        }
        public static void SetParameterPrefix(ClimateState __instance, ClimateParameter climateParameter, ref float newValue, bool isDeserializing)
        {
            float target;
            // 读档及快照初始化不能被当前关卡的保持状态污染。
            if (!isDeserializing && Active != null && Active.TryTarget(__instance, climateParameter, out target)) newValue = target;
        }
        public static void ChangeParameterPrefix(ClimateState __instance, ClimateParameter climateParameter, ref float delta)
        {
            float target;
            if (Active != null && Active.TryTarget(__instance, climateParameter, out target))
                delta = target - __instance.ClimateParameters[climateParameter];
        }
        public void DrawPanel()
        {
            GameState state = TrainerPlugin.CurrentState();
            Observe(state);
            GUILayout.Label("上帝之手 · 气候调节");
            GUILayout.Label(message);
            bool original = GUI.enabled;
            for (int i = 0; i < Names.Length; i++)
            {
                bool available = Available(state, i);
                ClimateParameter parameter = Parameter(i);
                int value = available ? (pending[i] ?? state.climateState.GetPlayerFacingValue(parameter)) : 0;
                string unit = DisplayUnit(parameter);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Names[i] + "：" + (available ? value + unit + (IsHeld(i) ? " · 保持中" : " · 自然变化") : (state == null ? "请先进入关卡" : "本关未启用")));
                GUI.enabled = original && TrainerPlugin.Ready && (IsHeld(i) || pending[i].HasValue);
                if (GUILayout.Button("恢复自然", GUILayout.Width(100))) Release(i);
                GUILayout.EndHorizontal();
                GUI.enabled = original && TrainerPlugin.Ready && available;
                int minimum = parameter != null ? parameter.minDisplayValue : 0;
                int maximum = parameter != null ? parameter.maxDisplayValue : 100;
                int next = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, minimum, maximum));
                if (available && GUI.enabled && next != value) QueueValue(state, i, next);
                GUI.enabled = original;
                if (available) GUILayout.Label("范围 " + minimum + unit + " ～ " + maximum + unit);
            }
            GUI.enabled = original && TrainerPlugin.Ready;
            if (GUILayout.Button("全部恢复自然变化")) { Reset(); message = "全部恢复自然变化；毒性、辐射和土壤盐度按地图重算，其他参数从当前值继续变化。"; }
            GUI.enabled = original;
            if (Available(state, 4)) GUILayout.Label("地质稳定性关联原游戏地震流程；调节数值不会暂停地震倒计时，也不撤销地震或重置剧情标记。");
            GUILayout.Label("拖到多少就设为多少，范围及单位与游戏一致。不必开启地形涂抹。毒性和辐射调整全局指标，不会清除地图上的污染地块或辐射源。\n保持仅对当前加载的关卡有效；读档、撤销、切换关卡或关闭全部修改会停止保持。数值随游戏正常存档保存，气候变化可能触发生态效果与条件达成。滑块不计入撤销步骤。");
            GUILayout.Space(10);
        }
    }
}
