using Artisan.IPC;
using Artisan.RawInformation;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.Events;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using static ECommons.GenericHelpers;
using MemoryHelper = Dalamud.Memory.MemoryHelper;
using ECommons.Automation.UIInput;


namespace Artisan.Tasks;

internal static class TaskSelectRetainer
{
    internal static void EnqueueRetainer(this TaskManager TM, ulong id)
    {
        TM.Enqueue(() => RetainerListHandlers.SelectRetainerByID(id));
        TM.Enqueue(() => RetainerListHandlers.TryGetCurrentRetainer(out _));
    }
}

internal unsafe static class RetainerListHandlers
{
    internal static bool? SelectRetainerByID(ulong id)
    {
        string retainerName = "";
        for (uint i = 0; i < 10; i++)
        {
            var retainer = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance()->GetRetainerBySortedIndex(i);
            if (retainer == null) continue;

            if (retainer->RetainerId == id)
                retainerName = retainer->NameString;

        }

        return SelectRetainerByName(retainerName);
    }


    internal static bool? SelectRetainerByName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new Exception($"Name can not be null or empty");
        }
        if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            var list = new AddonMaster.RetainerList(retainerList);
            foreach (var retainer in list.Retainers)
            {
                if (retainer.Name == name)
                {
                    if (RetainerInfo.GenericThrottle)
                    {
                        Svc.Log.Debug($"Selecting retainer {retainer.Name} with index {retainer.Index}");
                        retainer.Select();
                        return true;
                    }
                }
            }
        }

        return false;
    }


    internal static bool? CloseRetainerList()
    {
        if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            if (RetainerInfo.GenericThrottle)
            {
                var v = stackalloc AtkValue[1]
                {
                    new()
                    {
                        Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                        Int = -1
                    }
                };

                retainerList->FireCallback(1, v);
                return true;
            }
        }
        return false;
    }

    internal static bool TryGetCurrentRetainer(out string name)
    {
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && ProperOnLogin.PlayerPresent && Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Retainer).OrderBy(x => Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position)).TryGetFirst(out var obj))
        {
            name = obj.Name.ToString();
            return true;
        }
        name = "";
        return false;
    }
}

public unsafe class RetainerManager
{
    private static StaticRetainerContainer? _address;
    private static RetainerContainer* _container;

    public RetainerManager(ISigScanner sigScanner)
    {
        if (_address != null)
            return;

        _address ??= new StaticRetainerContainer(sigScanner);
        _container = (RetainerContainer*)_address.Address;
    }

    public bool Ready
        => _container != null && _container->Ready == 1;

    public int Count
        => Ready ? _container->RetainerCount : 0;

    public SeRetainer Retainer(int which)
        => which < Count
            ? ((SeRetainer*)_container->Retainers)[which]
            : throw new ArgumentOutOfRangeException($"Invalid retainer {which} requested, only {Count} available.");
    public void* RetainerAddress(int which)
        => which < Count
            ? &((SeRetainer*)_container->Retainers)[which]
            : throw new ArgumentOutOfRangeException($"Invalid retainer {which} requested, only {Count} available.");
}

public sealed class StaticRetainerContainer : SeAddressBase
{
    public StaticRetainerContainer(ISigScanner sigScanner)
        : base(sigScanner, "48 8B E9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 4E")
    { }
}

public class SeAddressBase
{
    public readonly IntPtr Address;

    public SeAddressBase(ISigScanner sigScanner, string signature, int offset = 0)
    {
        return;
        Address = sigScanner.GetStaticAddressFromSig(signature);
        if (Address != IntPtr.Zero)
            Address += offset;
        var baseOffset = (ulong)Address.ToInt64() - (ulong)sigScanner.Module.BaseAddress.ToInt64();
    }
}

[StructLayout(LayoutKind.Sequential, Size = SeRetainer.Size * 10 + 12)]
public unsafe struct RetainerContainer
{
    public fixed byte Retainers[SeRetainer.Size * 10];
    public fixed byte DisplayOrder[10];
    public byte Ready;
    public byte RetainerCount;
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct SeRetainer
{
    public const int Size = 0x48;

    [FieldOffset(0x00)]
    public ulong RetainerID;

    [FieldOffset(0x08)]
    private fixed byte _name[0x20];

    [FieldOffset(0x29)]
    public byte ClassJob;

    [FieldOffset(0x2A)]
    public byte Level;

    [FieldOffset(0x2C)]
    public uint Gil;

    [FieldOffset(0x38)]
    public uint VentureID;

    [FieldOffset(0x3C)]
    public uint VentureCompleteTimeStamp;

    public bool Available
        => ClassJob != 0;

    public SeString Name
    {
        get
        {
            fixed (byte* name = _name)
            {
                return MemoryHelper.ReadSeStringNullTerminated((IntPtr)name);
            }
        }
    }
}

internal unsafe static class RetainerHandlers
{
    internal static bool? SelectQuit()
    {
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToDalamudString().GetText(true);
        return TrySelectSpecificEntry(text);
    }

    internal static bool? SelectEntrustItems()
    {
        //2378	Entrust or withdraw items.
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2378).Text.ToDalamudString().GetText(true);
        return TrySelectSpecificEntry(text);
    }

    internal static bool? OpenItemContextMenu(uint ItemId, bool lookingForHQ, out int quantity)
    {
        quantity = 0;
        var inventories = new List<InventoryType>
        {
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.RetainerCrystals
        };

        foreach (var inv in inventories)
        {
            //Svc.Log.Debug($"RETAINER PAGE {inv} WITH SIZE {InventoryManager.Instance()->GetInventoryContainer(inv)->Size}");
            for (int i = 0; i < InventoryManager.Instance()->GetInventoryContainer(inv)->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventoryContainer(inv)->GetInventorySlot(i);
                //Svc.Log.Debug($"ITEM {item->ItemId.NameOfItem()} IN {item->Slot}");
                if (item->ItemId == ItemId && ((lookingForHQ && item->Flags == InventoryItem.ItemFlags.HighQuality) || (!lookingForHQ)))
                {
                    quantity = item->Quantity;
                    Svc.Log.Debug($"Found item? {item->Quantity}");
                    var ag = AgentInventoryContext.Instance();
                    ag->OpenForItemSlot(inv, i, 0, AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer)->GetAddonId());
                    var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextMenu", 1).Address;
                    var contextAgent = AgentInventoryContext.Instance();
                    var indexOfRetrieveAll = -1;
                    var indexOfRetrieveQuantity = -1;

                    // Addon#98 "Retrieve from Retainer" is the ONLY retrieve entry the game ever places in
                    // the retainer item context menu. Addon#773 "Retrieve Quantity from Retainer" is not a
                    // menu entry at all - it is the caption of the quantity dialog that the game opens by
                    // itself once the selected stack holds more than one item. Verified against the client
                    // data: Addon rows 88-99 are the inventory context-menu block (91 Discard, 92 Split,
                    // 93 Sell, 96 Equip, 97 Entrust to Retainer, 98 Retrieve from Retainer, 99 Put Up for
                    // Sale), while 772/773 live in the quantity-dialog block right next to 889/914 "Select
                    // the desired quantity."; the client binary emits 97+98 together 24 times over and never
                    // emits 773 anywhere near 98.
                    // The old code asked for #773 whenever Quantity > 1, never found it, and therefore fired
                    // no callback at all - the right-click menu opened and nothing was ever retrieved. The
                    // caller (RetainerInfo.ExtractItem/ExtractSingular) already waits for the quantity dialog
                    // and types the amount via InputNumericValue, so selecting #98 completes the flow.
                    // #773 is still preferred when a client really does expose it, so this cannot regress.
                    var retrieveAllText = LuminaSheets.AddonSheet[98].Text.ExtractText().Trim();
                    var retrieveQuantityText = LuminaSheets.AddonSheet[773].Text.ExtractText().Trim();

                    // Index the entries the way the game (and AutoRetainer, which works on this client) does:
                    // the live menu occupies EventParams[ContexItemStartIndex .. +ContextItemCount]. Scanning
                    // all 98 slots and counting only strings also matched stale leftovers from a previously
                    // opened menu, and the resulting counter was not the menu row index the callback wants.
                    var startIndex = Math.Clamp(contextAgent->ContexItemStartIndex, 0, 98);
                    var itemCount = Math.Clamp(contextAgent->ContextItemCount, 0, 98 - startIndex);
                    var labels = new string[itemCount];

                    for (var entry = 0; entry < itemCount; entry++)
                    {
                        var contextObj = contextAgent->EventParams[startIndex + entry];
                        if (contextObj.Type is not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                            and not FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                            continue;

                        var label = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(contextObj.String)).ExtractText().Trim();
                        labels[entry] = label;

                        if (indexOfRetrieveAll == -1 && retrieveAllText == label) indexOfRetrieveAll = entry;
                        if (indexOfRetrieveQuantity == -1 && retrieveQuantityText == label) indexOfRetrieveQuantity = entry;
                    }

                    Svc.Log.Debug($"Artisan: retainer context menu for item {ItemId} (qty {item->Quantity}) - ContexItemStartIndex={contextAgent->ContexItemStartIndex}, ContextItemCount={contextAgent->ContextItemCount}, disabledMask=0x{contextAgent->ContextItemDisabledMask:X}, ContextMenu addon=0x{(nint)contextMenu:X}");
                    for (var entry = 0; entry < itemCount; entry++)
                    {
                        var contextObj = contextAgent->EventParams[startIndex + entry];
                        Svc.Log.Debug($"Artisan:   entry[{entry}] = EventParams[{startIndex + entry}] type={contextObj.Type} disabled={contextAgent->IsContextItemDisabled(entry)} text=\"{labels[entry] ?? "<not a string>"}\"");
                    }
                    Svc.Log.Debug($"Artisan: Addon#98 \"{retrieveAllText}\" -> index {indexOfRetrieveAll}; Addon#773 \"{retrieveQuantityText}\" -> index {indexOfRetrieveQuantity}");

                    if (contextMenu != null)
                    {
                        // A single item (and crystals/shards, ItemId <= 19) is retrieved outright; a larger
                        // stack makes the game raise the quantity dialog, which the caller then fills in.
                        var index = item->Quantity == 1 || item->ItemId <= 19
                            ? indexOfRetrieveAll
                            : indexOfRetrieveQuantity >= 0 ? indexOfRetrieveQuantity : indexOfRetrieveAll;

                        if (index == -1)
                        {
                            Svc.Log.Warning($"Artisan: couldn't find \"{retrieveAllText}\" in the retainer item context menu, item {ItemId} was not retrieved. " +
                                            $"Menu had {itemCount} entries starting at EventParams[{startIndex}]: {string.Join(" | ", labels.Select(x => x ?? "<not a string>"))}");
                            return true;
                        }

                        if (contextAgent->IsContextItemDisabled(index))
                            Svc.Log.Warning($"Artisan: context menu entry {index} (\"{labels[index]}\") is disabled, retrieving item {ItemId} will probably do nothing.");

                        Svc.Log.Debug($"Artisan: firing retainer context menu entry {index} (\"{labels[index]}\") for item {ItemId}");
                        Callback.Fire(contextMenu, true, 0, index, 0, 0, 0);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    internal static bool InputNumericValue(int value)
    {
        var numeric = (AtkUnitBase*)Svc.GameGui.GetAddonByName("InputNumeric", 1).Address;
        if (numeric != null)
        {
            Svc.Log.Debug($"{value}");
            Callback.Fire(numeric, true, value);
            return true;
        }
        return false;
    }
    internal static bool? ClickCloseEntrustWindow()
    {
        //13530	Close Window
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(13530).Text.ToDalamudString().GetText();
        if (TryGetAddonByName<AtkUnitBase>("RetainerItemTransferProgress", out var addon) && IsAddonReady(addon))
        {
            var button = (AtkComponentButton*)addon->UldManager.NodeList[2]->GetComponent();
            var nodetext = MemoryHelper.ReadSeString(&addon->UldManager.NodeList[2]->GetComponent()->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText).GetText();
            if (nodetext == text && addon->UldManager.NodeList[2]->IsVisible() && button->IsEnabled && RetainerInfo.GenericThrottle)
            {
                button->ClickAddonButton(addon);
                return true;
            }
        }
        else
        {
            RetainerInfo.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? CloseAgentRetainer()
    {
        var a = CSFramework.Instance()->UIModule->GetAgentModule()->GetAgentByInternalId(AgentId.Retainer);
        if (a->IsAgentActive())
        {
            a->Hide();
            return true;
        }
        return false;
    }

    internal static bool TrySelectSpecificEntry(string text)
    {
        return TrySelectSpecificEntry(new string[] { text });
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text)
    {
        if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entry = GetEntries(addon).FirstOrDefault(x => x.StartsWithAny(text));
            if (entry != null)
            {
                var index = GetEntries(addon).IndexOf(entry);
                if (index >= 0 && RetainerInfo.GenericThrottle)
                {
                    new AddonMaster.SelectString((nint)addon).Entries[(ushort)index].Select();
                    return true;
                }
            }
        }
        else
        {
            RetainerInfo.RethrottleGeneric();
        }
        return false;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for (int i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText());
        }
        return list;
    }
}