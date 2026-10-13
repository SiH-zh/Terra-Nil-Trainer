using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Controller;
using Model.Commands;
using Model.State;
using UnityEngine;
using View.Handlers;
using Object = System.Object;

namespace TerraNilTrainer
{
    public sealed class UndoModule
    {
        internal static UndoModule Active;
        private static readonly FieldInfo PreviousState = AccessTools.Field(typeof(GameController), "_previousState");
        private static readonly FieldInfo UndoVisible = AccessTools.Field(typeof(UIHandler), "isUndoButtonShowing");
        private readonly TrainerPlugin host;
        private readonly List<GameState> history = new List<GameState>();
        private GameController owner;
        private GameState context;
        private bool enabled, restoring, queued;
        private int buttonDepth;
        private string message = "关闭时使用游戏原有的一步撤销。";
        public bool Enabled { get { return enabled; } }
        public int Count { get { return history.Count; } }
        public UndoModule(TrainerPlugin plugin) { host = plugin; Active = this; }
        public sealed class RestoreFrame
        {
            internal GameState Before, Target;
            internal bool Active;
        }
        public void SetEnabled(bool value, GameController controller = null)
        {
            history.Clear(); queued = false; enabled = value;
            owner = controller ?? (GameController.InstanceExists() ? GameController.Instance : null);
            context = Object.ReferenceEquals(owner, null) ? null : owner.GameState;
            if (enabled && !Object.ReferenceEquals(owner, null) && UIHandler.InstanceExists() && UndoVisible != null && (bool)UndoVisible.GetValue(UIHandler.Instance))
            {
                GameState latest = PreviousState.GetValue(owner) as GameState;
                if (latest != null) history.Add(latest);
            }
            message = enabled ? "已开启：本关卡后续可撤销操作会累积记录，不设步数上限。" : "已关闭并释放额外历史；原游戏当前的一步撤销记录保留。";
        }
        public void Reset() { SetEnabled(false); owner = null; context = null; restoring = false; buttonDepth = 0; }
        private void Observe(GameController controller)
        {
            if (Object.ReferenceEquals(controller, null)) return;
            GameState state = controller.GameState;
            if (!Object.ReferenceEquals(owner, controller) || (!restoring && !Object.ReferenceEquals(context, state)))
            {
                history.Clear(); queued = false; owner = controller; context = state;
                message = "关卡、读档或游戏状态已切换，连续撤销历史已清空。";
            }
        }
        public void Update()
        {
            if (!enabled) return;
            if (GameController.InstanceExists()) Observe(GameController.Instance);
            else if (!Object.ReferenceEquals(owner, null)) { history.Clear(); owner = null; context = null; }
            if (!queued) return;
            queued = false;
            if (history.Count == 0 || Object.ReferenceEquals(owner, null)) { message = "当前没有可连续撤销的操作。"; return; }
            try { owner.UndoPreviousMove(); RefreshButton(); }
            catch (Exception ex) { message = "撤销未完成：" + ex.GetBaseException().Message; if (host != null) host.ReportError(ex); }
        }
        private void RefreshButton()
        { if (enabled && UIHandler.InstanceExists()) UIHandler.Instance.SetUndoButtonTweenVisible(history.Count > 0); }
        public void DrawPanel()
        {
            bool next = GUILayout.Toggle(enabled, " 开启无限撤销上一步（连续多步，不设步数上限）");
            if (next != enabled) SetEnabled(next);
            GUILayout.Label("已记录 " + history.Count + " 步。" + message);
            bool was = GUI.enabled;
            GUI.enabled = was && enabled && history.Count > 0;
            if (GUILayout.Button("撤销上一步 · 剩余 " + history.Count + " 步")) queued = true;
            GUI.enabled = was;
            GUILayout.Label("仅记录开启期间游戏允许撤销的操作；重启、读档、切换关卡或不可撤销流程会清空历史。每步保存完整关卡快照，关闭开关可释放额外历史。修改器按钮本身不作为撤销步骤。");
        }
        public static void CommandPostfix(GameController __instance, Command c)
        {
            var self = Active;
            if (self == null || !self.enabled || !TrainerPlugin.Ready || c == null || !c.isPlayerCommand || self.restoring) return;
            self.Observe(__instance);
            if ((c.building != null && c.building.DontAllowUndo) || (TutorialController.InstanceExists() && TutorialController.Instance.TutorialInProgress))
            { self.history.Clear(); self.message = "此操作按游戏规则不可撤销，连续历史已清空。"; return; }
            GameState snapshot = PreviousState.GetValue(__instance) as GameState;
            if (snapshot == null) return;
            if (self.history.Count == 0 || !Object.ReferenceEquals(self.history[self.history.Count - 1], snapshot)) self.history.Add(snapshot);
            self.message = "可使用本面板或游戏原有撤销按钮，逐步退回之前的操作。";
        }
        public static bool UndoPrefix(GameController __instance, ref RestoreFrame __state)
        {
            __state = null;
            var self = Active;
            if (self == null || !self.enabled || !TrainerPlugin.Ready) return true;
            self.Observe(__instance);
            if (self.history.Count == 0) { self.message = "当前没有可连续撤销的操作。"; return false; }
            var target = self.history[self.history.Count - 1];
            __state = new RestoreFrame { Before = __instance.GameState, Target = target, Active = true };
            PreviousState.SetValue(__instance, target); self.restoring = true;
            return true;
        }
        public static void UndoPostfix(GameController __instance, RestoreFrame __state)
        {
            var self = Active;
            if (self == null || __state == null || !__state.Active) return;
            GameState restored = __instance.GameState;
            if (!Object.ReferenceEquals(restored, __state.Before))
            {
                if (self.history.Count > 0 && Object.ReferenceEquals(self.history[self.history.Count - 1], __state.Target)) self.history.RemoveAt(self.history.Count - 1);
                self.context = restored;
                PreviousState.SetValue(__instance, self.history.Count > 0 ? self.history[self.history.Count - 1] : null);
                self.message = "已撤销一步，剩余 " + self.history.Count + " 步。";
            }
        }
        public static Exception UndoFinalizer(Exception __exception, RestoreFrame __state)
        {
            var self = Active;
            if (self != null && __state != null && __state.Active)
            {
                self.restoring = false;
                if (__exception != null) { self.history.Clear(); self.context = null; self.message = "原游戏撤销失败，连续历史已停止。"; }
            }
            return __exception;
        }
        public static void ButtonPrefix() { if (Active != null) Active.buttonDepth++; }
        public static Exception ButtonFinalizer(Exception __exception)
        {
            if (Active != null) { Active.buttonDepth = Math.Max(0, Active.buttonDepth - 1); if (__exception == null) Active.RefreshButton(); }
            return __exception;
        }
        public static void ButtonVisibilityPostfix(bool show)
        {
            var self = Active;
            if (self == null || !self.enabled || show || self.restoring || self.buttonDepth > 0 || self.history.Count == 0) return;
            self.history.Clear(); self.message = "游戏进入不可撤销或阶段切换流程，连续历史已清空。";
        }
    }
}
