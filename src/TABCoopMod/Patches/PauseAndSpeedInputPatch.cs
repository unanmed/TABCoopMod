using System;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;
using TABCoopMod.Sync;
using ZX;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Intercepts pause (configured Pause key + top-right HUD button) and game-speed (+/-) at the input layer
    /// instead of hooking DXGame.set_Paused. Hooking the setter broke level loading (the engine
    /// calls it during load). OnKeyDown/OnKeyUp and DXButton.Activate are only reached from UI/user input.
    /// </summary>
    public static class PauseAndSpeedInputPatch
    {
        private static FieldInfo _pauseButtonField;

        private static bool CanIntercept()
        {
            if (!CommandInterceptor.IsInterceptionEnabled) return false;
            if (LockstepClock.LoadingLevel) return false;
            if (DXGame.Current == null) return false;
            if (DXModalScreen.IsInModal) return false;
            return true;
        }

        private static bool IsPauseButton(DXButton button)
        {
            if (button == null) return false;
            object levelSys = GameBridge.LevelSystemInstance;
            if (levelSys == null) return false;
            if (_pauseButtonField == null && GameBridge.TypeZXSystemGameLevel != null)
            {
                _pauseButtonField = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_PauseButton);
            }
            object pauseBtn = _pauseButtonField?.GetValue(levelSys);
            return pauseBtn != null && object.ReferenceEquals(button, pauseBtn);
        }

        private static bool IsCurrentlyPaused()
        {
            return LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_PauseKey
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, "OnKeyDown", new[] { typeof(DXKeys) });
            }

            [HarmonyPrefix]
            public static bool Prefix(DXKeys __0, ref bool __result)
            {
                if (__0 == DXKeys.F9
                    && CoopSessionManager.IsSessionActive
                    && CoopSessionManager.IsInSyncedLevel
                    && !LockstepClock.LoadingLevel
                    && !DXModalScreen.IsInModal)
                {
                    CoopSessionManager.RequestManualResync();
                    __result = true;
                    return false;
                }

                if (__0 == DXKeys.F7
                    && CoopSessionManager.IsMultiFactionActive
                    && CoopSessionManager.IsInSyncedLevel
                    && !LockstepClock.LoadingLevel
                    && !DXModalScreen.IsInModal)
                {
                    UI.MultiFactionPanel.Toggle();
                    __result = true;
                    return false;
                }

                // Always query the active vanilla layout (Standard/WASD/Custom) instead of
                // hardcoding Space, so custom Pause bindings work and rebound Space is not hijacked.
                if (!GameBridge.IsKeyBoundToCommand(__0, ZXKeyboardCommand.Pause, DXKeys.Space)) return true;
                if (CoopSessionManager.IsGameplayInputLocked)
                {
                    __result = true;
                    return false;
                }
                if (!CanIntercept()) return true;

                PauseSyncController.RequestPause(!IsCurrentlyPaused());
                __result = true;
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_DXButton_PauseActivate
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(DXButton), "Activate", Type.EmptyTypes);
            }

            [HarmonyPrefix]
            public static bool Prefix(DXButton __instance)
            {
                if (CoopSessionManager.IsGameplayInputLocked && IsPauseButton(__instance))
                    return false;
                if (!CanIntercept() || !IsPauseButton(__instance)) return true;

                // Preserve DXButton's native activation debounce while skipping the local Paused toggle.
                var scene = DXScene.Current;
                if (scene != null)
                {
                    if (__instance.LastTimeActivated + __instance.MinTimeBetweenActivations > scene.VirtualTimeInt)
                        return false;
                    __instance.LastTimeActivated = scene.VirtualTimeInt;
                }

                PauseSyncController.RequestPause(!IsCurrentlyPaused());
                return false;
            }
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_SpeedKeys
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, "OnKeyUp", new[] { typeof(DXKeys) });
            }

            [HarmonyPrefix]
            public static bool Prefix(DXKeys __0, ref bool __result)
            {
                // Vanilla OnKeyUp hardcodes +/- (no ZXKeyboardCommand entry exists for game speed).
                double delta;
                switch (__0)
                {
                    case DXKeys.Subtract:
                    case DXKeys.OemMinus: delta = -0.2; break;
                    case DXKeys.Add:
                    case DXKeys.Oemplus: delta = 0.2; break;
                    default: return true;
                }

                if (CoopSessionManager.IsGameplayInputLocked)
                {
                    __result = true;
                    return false;
                }
                if (!CanIntercept()) return true;

                float speed = (float)Math.Max(0.0, Math.Min(20.0, DXGame.Current.GameSpeed + delta));
                CommandInterceptor.Dispatch(new NetCommand
                {
                    Type = NetCommandType.SetGameSpeed,
                    PlayerId = CommandInterceptor.LocalPlayerId,
                    GameSpeed = speed
                });
                __result = true;
                return false;
            }
        }
    }
}
