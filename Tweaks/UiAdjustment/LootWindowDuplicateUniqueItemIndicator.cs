#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Plugin.Ipc;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.GeneratedSheets;
using SimpleTweaksPlugin.TweakSystem;
using SimpleTweaksPlugin.Utility;

namespace SimpleTweaksPlugin.Tweaks.UiAdjustment;

public unsafe class LootWindowDuplicateUniqueItemIndicator : UiAdjustments.SubTweak
{
    public override string Name => "Enhanced Loot Window";
    protected override string Author => "MidoriKami";
    public override string Description => "Marks unobtainable and already unlocked items in the loot window.";
    public override uint Version => 2;

    private delegate nint OnRequestedUpdateDelegate(nint a1, nint a2, nint a3);
    
    [Signature("40 53 48 83 EC 20 48 8B 42 58", DetourName = nameof(OnNeedGreedRequestedUpdate))]
    private readonly Hook<OnRequestedUpdateDelegate>? needGreedOnRequestedUpdateHook = null!;

    private static AtkUnitBase* AddonNeedGreed => (AtkUnitBase*) Service.GameGui.GetAddonByName("NeedGreed");

    private readonly int[] listItemNodeIdArray = Enumerable.Range(21001, 31).Prepend(2).ToArray();

    private ICallGateSubscriber<bool> allaganIsInitialized = null!;
    private ICallGateSubscriber<uint, bool, uint[], uint> allaganItemCountOwned = null!;

    private const uint CrossBaseId = 1000U;
    private const uint PadlockBaseId = 2000U;
    private const uint InventoryBaseId = 3000U;

    private const int MinionCategory = 81;
    private const int MountCategory = 63;
    private const int MountSubCategory = 175;

    [Flags]
    private enum ItemStatus
    {
        Normal = 0,
        Unobtainable = 1 << 0,
        AlreadyUnlocked = 1 << 1,
        InAnyInventory = 1 << 2
    }

    private static readonly uint[] AllaganToolsInventories =
    {
        0, // Bag0
        1, // Bag1
        2, // Bag2
        3, // Bag3
        1000, // GearSet0
        2000, // Currency
        2500, // Armoire
        2501, // GlamourChest
        3200, // ArmoryOff
        3201, // ArmoryHead
        3202, // ArmoryBody
        3203, // ArmoryHand
        3205, // ArmoryLegs
        3206, // ArmoryFeet
        3207, // ArmoryEar
        3208, // ArmoryNeck
        3209, // ArmoryWrist
        3300, // ArmoryRing
        3500, // ArmoryMain
        4000, // SaddleBag0
        4001, // SaddleBag1
        4100, // PremiumSaddleBag0
        4101, // PremiumSaddleBag1
        10000, // RetainerBag0
        10001, // RetainerBag1
        10002, // RetainerBag2
        10003, // RetainerBag3
        10004, // RetainerBag4
        11000, // RetainerEquippedGear
    };

    public class Config : TweakConfig
    {
        [TweakConfigOption("Mark Un-obtainable Items")]
        public bool MarkUnobtainable = true;

        [TweakConfigOption("Mark Already Unlocked Items")]
        public bool MarkAlreadyObtained = true;

        [TweakConfigOption("Mark Items Owned According to Allagan Tools (requires Allagan Tools)")]
        public bool MarkInAnyInventory = true;
    }

    public Config TweakConfig { get; private set; } = null!;

    public override bool UseAutoConfig => true;
    
    public override void Setup()
    {
        if (Ready) return;
        AddChangelogNewTweak("1.8.2.1");
        AddChangelog("1.8.3.0", "Rebuilt tweak to use images.");
        AddChangelog("1.8.3.0", "Fixed tweak not checking armory and equipped items.");
        AddChangelog("1.8.3.0", "Added 'Lock Loot Window' feature.");
        AddChangelog("1.8.6.0", "Removed Window Lock Feature, 'Lock Window Position' tweak has returned.");

        allaganIsInitialized = Service.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        allaganItemCountOwned = Service.PluginInterface.GetIpcSubscriber<uint, bool, uint[], uint>("AllaganTools.ItemCountOwned");
        
        Ready = true;
    }

    protected override void Enable()
    {
        TweakConfig = LoadConfig<Config>() ?? new Config();
        
        needGreedOnRequestedUpdateHook?.Enable();
        Common.AddonSetup += OnAddonSetup;
        Common.AddonFinalize += OnAddonFinalize;
        base.Enable();
    }

    protected override void Disable()
    {
        SaveConfig(TweakConfig);
        
        needGreedOnRequestedUpdateHook?.Disable();
        Common.AddonSetup -= OnAddonSetup;
        Common.AddonFinalize -= OnAddonFinalize;
        base.Disable();
    }

    public override void Dispose()
    {
        needGreedOnRequestedUpdateHook?.Dispose();
        base.Dispose();
    }

    private void OnAddonSetup(SetupAddonArgs obj)
    {
        if (obj.AddonName != "NeedGreed") return;
        
        var listComponentNode = (AtkComponentNode*) obj.Addon->GetNodeById(6);
        if (listComponentNode is null || listComponentNode->Component is null) return;
        
        foreach (uint index in listItemNodeIdArray)
        {
            var componentUldManager = &listComponentNode->Component->UldManager;
                    
            var lootItemNode = Common.GetNodeByID<AtkComponentNode>(componentUldManager, index);
            if (lootItemNode is null) continue;
                
            var crossNode = Common.GetNodeByID(componentUldManager, CrossBaseId + index);
            if (crossNode is null)
            {
                MakeCrossNode(CrossBaseId + index, lootItemNode);
            }
                        
            var padlockNode = Common.GetNodeByID(componentUldManager, PadlockBaseId + index);
            if (padlockNode is null)
            {
                MakePadlockNode(PadlockBaseId + index, lootItemNode);
            }

            var inventoryNode = Common.GetNodeByID(componentUldManager, InventoryBaseId + index);
            if (inventoryNode is null)
            {
                MakeInventoryNode(InventoryBaseId + index, lootItemNode);
            }
        }
    }
    
    private void OnAddonFinalize(SetupAddonArgs obj)
    {
        if (obj.AddonName != "NeedGreed") return;
        
        var listComponentNode = (AtkComponentNode*) obj.Addon->GetNodeById(6);
        if (listComponentNode is null || listComponentNode->Component is null) return;
        
        foreach (uint index in listItemNodeIdArray)
        {
            var componentUldManager = &listComponentNode->Component->UldManager;
                    
            var lootItemNode = Common.GetNodeByID<AtkComponentNode>(componentUldManager, index);
            if (lootItemNode is null) continue;
            
            var crossNode = Common.GetNodeByID<AtkImageNode>(componentUldManager, CrossBaseId + index);
            if (crossNode is not null)
            {
                UiHelper.UnlinkAndFreeImageNode(crossNode, AddonNeedGreed);
            }
                        
            var padlockNode = Common.GetNodeByID<AtkImageNode>(componentUldManager, PadlockBaseId + index);
            if (padlockNode is not null)
            {
                UiHelper.UnlinkAndFreeImageNode(padlockNode, AddonNeedGreed);
            }

            var inventoryNode = Common.GetNodeByID<AtkImageNode>(componentUldManager, InventoryBaseId + index);
            if (inventoryNode is not null)
            {
                UiHelper.UnlinkAndFreeImageNode(inventoryNode, AddonNeedGreed);
            }
        }
    }

    private nint OnNeedGreedRequestedUpdate(nint addon, nint a2, nint a3)
    {
        var result = needGreedOnRequestedUpdateHook!.Original(addon, a2, a3);
        var isAllaganToolsAvailable = IsAllaganToolsAvailable();
        PluginLog.Warning($"OnNeedGreedRequestedUpdate (allagan = {isAllaganToolsAvailable})");

        try
        {
            var callingAddon = (AddonNeedGreed*) addon;

            var listComponentNode = (AtkComponentNode*) callingAddon->AtkUnitBase.GetNodeById(6);
            if (listComponentNode is null || listComponentNode->Component is null) return result;
            
            // For each possible item slot, get the item info
            foreach (var index in Enumerable.Range(0, callingAddon->ItemsSpan.Length))
            {
                // If this data slot doesn't have an item id, skip.
                var itemInfo = callingAddon->ItemsSpan[index];
                if (itemInfo.ItemId is 0) continue;

                var adjustedItemId = itemInfo.ItemId > 1_000_000 ? itemInfo.ItemId - 1_000_000 : itemInfo.ItemId;
                
                // If we can't match the item in lumina, skip.
                var itemData = Service.Data.GetExcelSheet<Item>()!.GetRow(adjustedItemId);
                if (itemData is null) continue;

                // If we can't get the ui node, skip
                var listItemNodeId = listItemNodeIdArray[index];
                var listItemNode = Common.GetNodeByID<AtkComponentNode>(&listComponentNode->Component->UldManager, (uint) listItemNodeId);
                if (listItemNode is null || listItemNode->Component is null) continue;

                var state = ItemStatus.Normal;
                switch (itemData)
                {
                    // Item is unique, and has no unlock action, and is unobtainable if we have any in our inventory
                    case { IsUnique: true, ItemAction.Row: 0 } when PlayerHasItem(itemInfo.ItemId):
                        
                    // Item is unobtainable if its a minion/mount and already unlocked
                    case { ItemUICategory.Row: MinionCategory } when IsItemAlreadyUnlocked(itemInfo.ItemId):
                    case { ItemUICategory.Row: MountCategory, ItemSortCategory.Row: MountSubCategory } when IsItemAlreadyUnlocked(itemInfo.ItemId):
                        state = ItemStatus.Unobtainable;
                        break;

                    // Item can be obtained if unlocked
                    case not null when IsItemAlreadyUnlocked(itemInfo.ItemId):
                        state = ItemStatus.AlreadyUnlocked;
                        break;
                }

                if (isAllaganToolsAvailable && IsItemInAnyInventory(itemInfo.ItemId)) {
                    state |= ItemStatus.InAnyInventory;
                }

                UpdateNodeVisibility(listItemNode, listItemNodeId, state);
            }
        }
        catch (Exception e)
        {
            PluginLog.Error(e, "Something went wrong in LootWindowDuplicateUniqueItemIndicator, let MidoriKami know!");
        }

        return result;
    }

    private void UpdateNodeVisibility(AtkComponentNode* listItemNode, int listItemId, ItemStatus status)
    {
        var crossNode = Common.GetNodeByID<AtkImageNode>(&listItemNode->Component->UldManager, CrossBaseId + (uint) listItemId);
        var padlockNode = Common.GetNodeByID<AtkImageNode>(&listItemNode->Component->UldManager, PadlockBaseId + (uint) listItemId);
        var inventoryNode = Common.GetNodeByID<AtkImageNode>(&listItemNode->Component->UldManager, InventoryBaseId + (uint) listItemId);

        if (crossNode is null || padlockNode is null || inventoryNode is null) return;

        crossNode->AtkResNode.ToggleVisibility(TweakConfig.MarkUnobtainable && (status & ItemStatus.Unobtainable) != 0);
        padlockNode->AtkResNode.ToggleVisibility(TweakConfig.MarkAlreadyObtained && (status & ItemStatus.AlreadyUnlocked) != 0);
        inventoryNode->AtkResNode.ToggleVisibility(TweakConfig.MarkInAnyInventory && (status & ItemStatus.InAnyInventory) != 0);
        // inventoryNode->AtkResNode.ToggleVisibility(true);
    }

    private bool IsItemAlreadyUnlocked(uint itemId)
    {
        var exdItem = ExdModule.GetItemRowById(itemId);
        return exdItem is null || UIState.Instance()->IsItemActionUnlocked(exdItem) is 1;
    }
    
    private void MakeCrossNode(uint nodeId, AtkComponentNode* parent)
    {
        var imageNode = UiHelper.MakeImageNode(nodeId, new UiHelper.PartInfo(0, 0, 32, 32));
        imageNode->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents; // 8243;
        imageNode->WrapMode = 1;

        imageNode->LoadIconTexture(61502, 0);

        imageNode->AtkResNode.SetWidth(32);
        imageNode->AtkResNode.SetHeight(32);
        imageNode->AtkResNode.SetScale(1.25f, 1.25f);
        imageNode->AtkResNode.SetPositionShort(14, 14);
        
        imageNode->AtkResNode.ToggleVisibility(true);
        
        var targetTextNode = Common.GetNodeByID<AtkResNode>(&parent->Component->UldManager, 11);
        UiHelper.LinkNodeAfterTargetNode((AtkResNode*) imageNode, parent, targetTextNode);
    }

    private void MakePadlockNode(uint nodeId, AtkComponentNode* parent)
    {
        var imageNode = UiHelper.MakeImageNode(nodeId, new UiHelper.PartInfo(48, 0, 20, 24));
        imageNode->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents; // 8243;
        imageNode->WrapMode = 1;

        imageNode->LoadTexture("ui/uld/ActionBar_hr1.tex");

        imageNode->AtkResNode.Color.A = 0xAA;

        imageNode->AtkResNode.SetWidth(20);
        imageNode->AtkResNode.SetHeight(24);
        imageNode->AtkResNode.SetPositionShort(22, 20); 
        
        imageNode->AtkResNode.ToggleVisibility(true);
        
        var targetTextNode = Common.GetNodeByID<AtkResNode>(&parent->Component->UldManager, 11);
        UiHelper.LinkNodeAfterTargetNode((AtkResNode*) imageNode, parent, targetTextNode);
    }

    private void MakeInventoryNode(uint nodeId, AtkComponentNode* parent)
    {
        var imageNode = UiHelper.MakeImageNode(nodeId, new UiHelper.PartInfo(0, 0, 32, 32));
        imageNode->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Visible | NodeFlags.Enabled | NodeFlags.EmitsEvents; // 8243;
        imageNode->WrapMode = 1;

        imageNode->LoadIconTexture(60512, 0);

        imageNode->AtkResNode.SetWidth(32);
        imageNode->AtkResNode.SetHeight(32);
        imageNode->AtkResNode.SetScale(1f, 1f);
        imageNode->AtkResNode.SetPositionShort(-6, 22);
        // imageNode->AtkResNode.SetPositionShort(7, 30);

        imageNode->AtkResNode.ToggleVisibility(true);

        var targetTextNode = Common.GetNodeByID<AtkResNode>(&parent->Component->UldManager, 5);
        UiHelper.LinkNodeAfterTargetNode((AtkResNode*) imageNode, parent, targetTextNode);
    }

    private bool PlayerHasItem(uint itemId)
    {
        // Only check main inventories, don't include any special inventories
        var inventories = new List<InventoryType>
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            
            InventoryType.EquippedItems,
            
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryWaist,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,

            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
        };

        return inventories.Sum(inventory => InventoryManager.Instance()->GetItemCountInContainer(itemId, inventory)) > 0;
    }

    private bool IsAllaganToolsAvailable()
    {
        try {
            return allaganIsInitialized.InvokeFunc();
        }
        catch {
            return false;
        }
    }

    private bool IsItemInAnyInventory(uint itemId)
    {
        var count = allaganItemCountOwned.InvokeFunc(itemId, true, AllaganToolsInventories);
        PluginLog.Warning($"count {itemId} = {count}");
        return count > 0;
    }
}
