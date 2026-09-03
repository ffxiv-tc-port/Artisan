using ECommons.DalamudServices;
using ECommons.Reflection;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System.Numerics;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using static ECommons.GenericHelpers;
using Artisan.IPC;
using System.Collections.Generic;
using System.Linq;
using System;
using ECommons.Automation.LegacyTaskManager;

namespace Artisan.Tasks;

internal unsafe static class TaskInteractWithNearestBell
{
    internal static void EnqueueBell(this TaskManager TM)
    {
        TM.Enqueue(YesAlready.Lock);
        TM.Enqueue(PlayerWorldHandlers.SelectNearestBell);
        TM.Enqueue(PlayerWorldHandlers.InteractWithTargetedBell);
    }
}

internal static class YesAlready
{
    // 1.4.0.0 之前的 YesAlready 靠反射寫 Service.Configuration.Enabled 來抑制，
    // 那條路已經兩層過期（內部名實際是 YesAlready 沒有空格，且 Service 上已經
    // 沒有 Configuration 這個成員），只要真的跑到就是 NullReferenceException。
    // 艦隊的 YesAlready 遠高於 1.4.0.0，一律走共享資料 StopRequests。
    internal static bool Reenable = false;
    internal static HashSet<string>? Data = null;

    internal static void GetData()
    {
        if (Data != null) return;
        if (Svc.PluginInterface.TryGetData<HashSet<string>>("YesAlready.StopRequests", out var data))
        {
            Data = data;
        }
    }

    internal static void Lock()
    {
        GetData();
        if (Data != null)
        {
            Svc.Log.Information("Disabling Yes Already");
            Data.Add(Svc.PluginInterface.InternalName);
            Reenable = true;
        }
    }

    internal static void Unlock()
    {
        if (!Reenable) return;

        GetData();
        if (Data != null)
        {
            Svc.Log.Information("Enabling Yes Already");
            Data.Remove(Svc.PluginInterface.InternalName);
            Reenable = false;
        }
    }

    internal static bool IsEnabled()
    {
        GetData();
        if (Data != null)
        {
            return !Data.Contains(Svc.PluginInterface.InternalName);
        }

        return false;
    }

    internal static bool? WaitForYesAlreadyDisabledTask()
    {
        return !IsEnabled();
    }
}

internal unsafe static class PlayerWorldHandlers
{
    internal static bool? SelectNearestBell()
    {
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell]) return true;
        if (!IsOccupied())
        {
            var x = RetainerInfo.GetReachableRetainerBell();
            if (x != null)
            {
                if (RetainerInfo.GenericThrottle)
                {
                    Svc.Targets.Target = x;
                    Svc.Log.Debug($"Set target to {x}");
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool? InteractWithTargetedBell()
    {
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell]) return true;
        var x = Svc.Targets.Target;
        if (x != null && (x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(RetainerInfo.BellName, "リテイナーベル") && !IsOccupied())
        {
            if (Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < RetainerInfo.GetValidInteractionDistance(x) && x.IsTargetable())
            {
                if (RetainerInfo.GenericThrottle && EzThrottler.Throttle("InteractWithBell", 5000))
                {
                    TargetSystem.Instance()->InteractWithObject((GameObject*)x.Address, false);
                    Svc.Log.Debug($"Interacted with {x}");
                    return true;
                }
            }
        }
        return false;
    }
}