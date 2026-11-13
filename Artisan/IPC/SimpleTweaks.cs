using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Artisan.IPC
{
    internal static unsafe class SimpleTweaks
    {
        // 反射查一次 SimpleTweaks 要配置 1～2 KB，下面兩個每幀入口因此最多每秒真的查一次。
        // 時間戳各由各自的呼叫執行緒單寫（DisableImprovedLogTweak＝框架更新、IsFocusTweakEnabled＝配方筆記疊加層繪製），
        // 不加鎖 ⇒ 不要從 IPC 端點或背景執行緒呼叫。
        private const long RecheckIntervalMs = 1000;
        private static long nextImprovedLogCheck;
        private static long nextFocusTweakCheck;
        private static bool focusTweakEnabled;

        internal static bool IsEnabled()
        {
            return DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out _, false, true);
        }

        internal static bool IsFocusTweakEnabled()
        {
            var now = Environment.TickCount64;
            if (now < nextFocusTweakCheck)
                return focusTweakEnabled;
            // 先推時間戳再查：查詢擲例外時也不會變成每幀重試。
            nextFocusTweakCheck = now + RecheckIntervalMs;
            focusTweakEnabled = QueryFocusTweakEnabled();
            return focusTweakEnabled;
        }

        private static bool QueryFocusTweakEnabled()
        {
            if (!IsEnabled()) return false;
            if (DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out var tweaks, false, true) && tweaks != null)
            {
                var plugin = tweaks.GetFoP("Plugin");
                if (plugin == null) return false;

                var baseTweak = plugin?.Call("GetTweakById", ["UiAdjustments@AutoFocusRecipeSearch", plugin.GetFoP("Tweaks")]);
                if (baseTweak == null) return false;
                bool enabled = (bool)baseTweak.GetFoP("Enabled");
                return enabled;
            }

            return false;
        }

        internal static bool IsImprovedLogEnabled()
        {
            var baseTweak = FindImprovedLogTweak();
            return baseTweak != null && (bool)baseTweak.GetFoP("Enabled");
        }

        // 使用者在 SimpleTweaks 重新打開這個 tweak 後，最多一秒內會被關掉；
        // 聊天視窗那一句仍然只在真的關掉它的那一次說。
        internal static void DisableImprovedLogTweak()
        {
            var now = Environment.TickCount64;
            if (now < nextImprovedLogCheck)
                return;
            nextImprovedLogCheck = now + RecheckIntervalMs;

            var baseTweak = FindImprovedLogTweak();
            if (baseTweak == null || !(bool)baseTweak.GetFoP("Enabled")) return;
            SetTweakEnabled(baseTweak, false);
            disabledByArtisan = baseTweak;

            DuoLog.Information($"Improved Crafting Log Tweak is disabled whilst using Artisan.");
        }

        // Artisan 自己關掉的那一份；卸載時只還原它。SimpleTweaks 換實例或解析不到時一併放掉，不留住別人的組件。
        private static object? disabledByArtisan;
        private static bool restoreSkipLogged;

        /// <summary>Artisan 卸載時呼叫（框架執行緒）：把 Artisan 關掉的 ImprovedCraftingLog 開回來。</summary>
        /// <remarks>只在「同一份 tweak、現在仍關著、SimpleTweaks 設定仍把它列為開」時還原 ⇒ 使用者原本就關、或之後自己在 SimpleTweaks 關掉的都不動。
        /// 遊戲關閉中不呼叫別的插件；不還原無妨，SimpleTweaks 下次載入照它自己的設定開。</remarks>
        internal static void RestoreImprovedLogTweak()
        {
            if (disabledByArtisan == null)
                return;

            if (Svc.Framework.IsFrameworkUnloading)
            {
                if (!restoreSkipLogged)
                {
                    restoreSkipLogged = true;
                    Svc.Log.Warning("[Artisan] 遊戲關閉中，不還原 SimpleTweaks 的 Improved Crafting Log（SimpleTweaks 下次載入會照它自己的設定開回來）。");
                }
                disabledByArtisan = null;
                return;
            }

            var tweak = FindImprovedLogTweak();
            var ours = disabledByArtisan;
            disabledByArtisan = null;
            if (tweak == null || !ReferenceEquals(tweak, ours) || (bool)tweak.GetFoP("Enabled") || !StillWantedBySimpleTweaks())
                return;

            SetTweakEnabled(tweak, true);
            Svc.Log.Information("[Artisan] 卸載：已把 Artisan 關掉的 SimpleTweaks Improved Crafting Log 開回來。");
        }

        // 走 SimpleTweaks 自己勾選框用的 InternalEnable／InternalDisable（掛／拆 hook、指令與事件）；只翻 Enabled 會留下仍掛著的 hook。
        // 不碰它的 EnabledTweaks 設定 ⇒ 這次關閉只在本次執行有效。找不到方法（別的 SimpleTweaks 版本）才退回只翻旗標。
        private static void SetTweakEnabled(object tweak, bool enabled)
        {
            var method = tweak.GetType().GetMethod(enabled ? "InternalEnable" : "InternalDisable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
            if (method != null)
                method.Invoke(tweak, null);
            else
                tweak.SetFoP("Enabled", enabled);
        }

        // SimpleTweaks 設定的 EnabledTweaks 仍含這個 tweak＝使用者要它開；讀不到設定就當要。
        private static bool StillWantedBySimpleTweaks()
        {
            try
            {
                if (DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out var tweaks, false, true) && tweaks != null
                    && tweaks.GetFoP("Plugin")?.GetFoP("PluginConfig")?.GetFoP("EnabledTweaks") is IEnumerable<string> enabled)
                    return enabled.Contains("ImprovedCraftingLog", StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[Artisan] 讀不到 SimpleTweaks 的已啟用清單（{ex.GetType().Name}），照 Artisan 關掉前的狀態還原。");
            }
            return true;
        }

        // 上一次 GetTweakById 解析到的 tweak 與當時的 SimpleTweaks 實例；只在框架執行緒讀寫。
        // 真的 Tweaks 每次都把全部 tweak 依名稱排序一遍（約 4 KB），所以同一個實例、tweak 沒被處置就沿用；
        // SimpleTweaks 換實例或 tweak 已處置（讀不到 IsDisposed 也算）就照舊重新解析。查不到（null）不沿用。
        private static object? improvedLogOwner;
        private static object? improvedLogTweak;

        private static object? FindImprovedLogTweak()
        {
            // 查不到就放掉快取，不讓已停用或移除的 SimpleTweaks 實例與 tweak 一直被留著。
            if (!DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out var tweaks, false, true) || tweaks == null)
            {
                improvedLogOwner = improvedLogTweak = disabledByArtisan = null;
                return null;
            }
            if (ReferenceEquals(tweaks, improvedLogOwner) && improvedLogTweak != null && improvedLogTweak.GetFoP("IsDisposed") is false)
                return improvedLogTweak;

            var plugin = tweaks.GetFoP("Plugin");
            if (plugin == null)
            {
                improvedLogOwner = improvedLogTweak = disabledByArtisan = null;
                return null;
            }

            var baseTweak = plugin.Call("GetTweakById", ["ImprovedCraftingLog", plugin.GetFoP("Tweaks")]);
            improvedLogOwner = tweaks;
            improvedLogTweak = baseTweak;
            if (!ReferenceEquals(baseTweak, disabledByArtisan))
                disabledByArtisan = null;
            return baseTweak;
        }
    }
}
