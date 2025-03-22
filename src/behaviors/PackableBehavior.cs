using tentbag.configuration;
using tentbag.util;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.Server;

namespace tentbag.behaviors;

public class PackableBehavior : CollectibleBehavior {
    private static Config Config => TentBag.Instance.Config;
    private static bool IsAirOrNull(Block? block) => block is not { Replaceable: < 9505 };
    private static bool IsPlantOrRock(Block? block) => Config.ReplacePlantsAndRocks && block?.Replaceable is >= 5500 and <= 6500;
    private static bool IsReplaceable(Block? block) => IsAirOrNull(block) || IsPlantOrRock(block);

    private static void SendClientError(EntityPlayer entity, string error) => TentBag.Instance.SendClientError(entity.Player, error);
    private static void SendClientChatMessage(EntityPlayer entity, string message) => TentBag.Instance.SendClientChatMessage(entity.Player, message);

    private readonly AssetLocation? _emptyBag;
    private readonly AssetLocation? _packedBag;

    private long _highlightId;

    public PackableBehavior(CollectibleObject obj) : base(obj) {
        string domain = obj.Code.Domain;
        string path = obj.CodeWithoutParts(1);
        _emptyBag = new AssetLocation(domain, $"{path}-empty");
        _packedBag = new AssetLocation(domain, $"{path}-packed");
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling) {
        if (blockSel == null || byEntity is not EntityPlayer entity) {
            return;
        }

        handHandling = EnumHandHandling.PreventDefaultAction;

        if (entity.Api.Side != EnumAppSide.Server) {
            return;
        }

        string contents = slot.Itemstack.Attributes.GetString("tent-contents") ?? slot.Itemstack.Attributes.GetString("packed-contents");
        int solidBlockCount = slot.Itemstack.Attributes.GetInt("solid-block-count");
        if (contents == null) {
            PackContents(entity, blockSel, slot, ref solidBlockCount);
        } else {
            UnpackContents(entity, blockSel, slot, contents, ref solidBlockCount);
        } 
    }

    private void PackContents(EntityPlayer entity, BlockSelection blockSel, ItemSlot slot, ref int solidBlockCount) {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

        int y = IsPlantOrRock(blockAccessor.GetBlock(blockSel.Position)) ? 1 : 0;
        int floorShift = Config.GrabFloor ? -1 : 0;

        BlockPos start = blockSel.Position.AddCopy(-Config.MaxRadius, 1 - y + floorShift, -Config.MaxRadius);
        BlockPos end = blockSel.Position.AddCopy(Config.MaxRadius, Math.Max(Config.MaxHeight, 3), Config.MaxRadius);

        if (!CanPack(entity, blockAccessor, start, end, ref solidBlockCount)) {
            return;
        }

        // create schematic of area
        BlockSchematic bs = new();
        bs.AddAreaWithoutEntities(entity.World, blockAccessor, start, end);
        bs.PackIncludingAir(entity.World, start, solidBlockCount);

        
        // clear area in world
        if (!Config.CopyMode) ClearArea(entity.World, start, end);

        // drop packed item on the ground and remove empty from inventory
        ItemStack packed = new(entity.World.GetItem(_packedBag), slot.StackSize);
        packed.Attributes.SetString("packed-contents", bs.ToJson());
        packed.Attributes.SetInt("solid-block-count", solidBlockCount);
        if (Config.PutTentInInventoryOnUse) {
            ItemStack sinkStack = slot.Itemstack.Clone();
            slot.Itemstack.StackSize = 0;
            slot.Itemstack = packed;
            slot.OnItemSlotModified(sinkStack);
        } else {
            entity.World.SpawnItemEntity(packed, blockSel.Position.ToVec3d().Add(0, 1 - y, 0));
            slot.TakeOutWhole();
        }

        // Consume player saturation if in survival mode
        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
        }

        ChatNotification(entity, $"Packed {solidBlockCount} blocks, consumed {solidBlockCount * Config.BuildEffort} saturation.");
    }

    private void UnpackContents(EntityPlayer entity, BlockSelection blockSel, ItemSlot slot, string contents, ref int solidBlockCount) {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

        //Customized BulkBlockAccessor with relight on commit
        IBulkBlockAccessor bulkBlockAccessor = entity.World.GetBlockAccessorBulkUpdate(true, true, false);

        int y = IsPlantOrRock(blockAccessor.GetBlock(blockSel.Position)) ? 1 : 0;

        BlockPos start = blockSel.Position.AddCopy(-Config.MaxRadius, 0 - y, -Config.MaxRadius);
        BlockPos end = blockSel.Position.AddCopy(Config.MaxRadius, Math.Max(Config.MaxHeight, 3), Config.MaxRadius);

        if (!CanUnpack(entity, blockAccessor, start, end, ref solidBlockCount)) {
            return;
        }

        // try load schematic data from json contents
        string? error = null;
        BlockSchematic bs = BlockSchematic.LoadFromString(contents, ref error);
        if (!string.IsNullOrEmpty(error)) {
            SendClientError(entity, Lang.UnpackError());
            return;
        }

        // paste the schematic into the world (requires bulk block accessor to prevent door/room issues)
        BlockPos adjustedStart = bs.AdjustStartPos(start.Add(Config.MaxRadius, 1, Config.MaxRadius), EnumOrigin.BottomCenter);
        bs.ReplaceMode = EnumReplaceMode.ReplaceAll;
                
        bs.Place(bulkBlockAccessor, entity.World, adjustedStart);
        bulkBlockAccessor.Commit();

        // Do this after bulk accessor commit
        bs.PlaceEntitiesAndBlockEntities(blockAccessor, entity.World, adjustedStart, bs.BlockCodes, bs.ItemCodes);
                
        // drop empty item on the ground and remove empty from inventory
        ItemStack empty = new(entity.World.GetItem(_emptyBag), slot.StackSize);
        if (Config.PutTentInInventoryOnUse) {
            ItemStack sinkStack = slot.Itemstack.Clone();
            slot.Itemstack.StackSize = 0;
            slot.Itemstack = empty;
            slot.OnItemSlotModified(sinkStack);
        } else {
            entity.World.SpawnItemEntity(empty, blockSel.Position.ToVec3d().Add(0, 1 - y, 0));
            slot.TakeOutWhole();
        }

        if (Config.DropWaypoint) CreateWaypoint(entity);
                
        // Consume player saturation if in survival mode
        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
        }

        ChatNotification(entity, $"Unpacked {solidBlockCount} blocks, consumed {solidBlockCount * Config.BuildEffort} saturation.");
    }

    private static void ChatNotification(EntityPlayer entity, string message)
    {
        if (Config.ShowChatNotification)
        {
            SendClientChatMessage(entity, message);
        }
    }

    private static void CreateWaypoint(EntityPlayer player)
    {
        string format = "/waypoint addati {0} ={1} ={2} ={3} {4} {5} Death: {6}";
        string icon = Config.WaypointIcon;
        BlockPos pos = player.ServerPos.AsBlockPos;
        bool isPinned = Config.PinWaypoint;
        string color = Config.WaypointColor;
        string buildTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        string message = string.Format(format, icon, pos.X, pos.Y, pos.Z, isPinned, color, buildTime);

        player.Api.ChatCommands.ExecuteUnparsed(message, new TextCommandCallingArgs
        {
            Caller = new Caller
            {
                Player = player.Player,
                Pos = player.Pos.XYZ,
                FromChatGroupId = Vintagestory.API.Config.GlobalConstants.CurrentChatGroup
            }
        });
    }

    private static void ClearArea(IWorldAccessor world, BlockPos start, BlockPos end) {
        // first we bulk clear the area to prevent decor duplication issues
        ClearArea(world.BulkBlockAccessor, start, end);
        // second we individually clear each block to prevent lighting/room/minimap issues
        ClearArea(world.BlockAccessor, start, end);
    }

    private static void ClearArea(IBlockAccessor blockAccessor, BlockPos start, BlockPos end) {
        blockAccessor.WalkBlocks(start, end, (block, x, y, z) => {
            if (block.BlockId == 0) {
                // ignore air blocks, nothing to clear
                return;
            }

            // set block to air
            blockAccessor.SetBlock(0, new BlockPos(x, y, z, 0));
        });
        // commit bulk job, if any
        blockAccessor.Commit();
    }

    private bool CanPack(EntityPlayer entity, IBlockAccessor blockAccessor, BlockPos start, BlockPos end, ref int solidBlockCount)
    {
        List<BlockPos> blocks = new();
        bool notified = false;
        bool canPack = true;
        int localSolidBlockCount = 0;        

        blockAccessor.WalkBlocks(start, end, (block, posX, posY, posZ) =>
        {
            BlockPos pos = new(posX, posY, posZ, 0);
            if (!entity.World.Claims.TryAccess(entity.Player, pos, EnumBlockAccessFlags.BuildOrBreak))
            {
                notified = true;
                blocks.Add(pos);
            }
            else if (IsBannedBlock(block))
            {
                if (!notified)
                {
                    SendClientError(entity, Lang.IllegalItemError(block.GetPlacedBlockName(entity.World, pos)));
                    notified = true;
                }

                blocks.Add(pos);
            }

            if (block.Id != 0)
            {
                localSolidBlockCount++;
            }

            // Check if the player has enough saturation to pack the tent
            EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
            EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
            if (hunger != null && playerGameMode == EnumGameMode.Survival)
            {
                float currentHunger = hunger.Saturation;

                if (localSolidBlockCount * Config.BuildEffort > currentHunger)
                {
                    if (!notified)
                    {
                        SendClientError(entity, Lang.PackHungerError());
                        notified = true;
                    }
                    canPack = false;
                }
            }
        });

        // If build area is only air blocks, don't pack it
        if (localSolidBlockCount == 0)
        {
            if (!notified)
            {
                SendClientError(entity, Lang.EmptyBuildError());
                notified = true;
            }
            canPack = false;
        }

        solidBlockCount = localSolidBlockCount;
        return canPack && !ShouldHighlightBlocks(entity, blocks);
    }

    private bool CanUnpack(EntityPlayer entity, IBlockAccessor blockAccessor, BlockPos start, BlockPos end, ref int solidBlockCount) {
        List<BlockPos> blocks = new();
        bool notified = false;
        bool canUnpack = true;

        // Check if the player has enough saturation to unpack the tent (do this first to prevent unnecessary block checks)
        EntityBehaviorHunger? hunger = entity.GetBehavior<EntityBehaviorHunger>();
        EnumGameMode playerGameMode = entity.Player.WorldData.CurrentGameMode;
        if (hunger != null && playerGameMode == EnumGameMode.Survival)
        {
            float currentHunger = hunger.Saturation;

            if (solidBlockCount * Config.BuildEffort > currentHunger)
            {
                if (!notified)
                {
                    SendClientError(entity, Lang.UnpackHungerError());
                    notified = true;
                }
                canUnpack = false;
            }
        }

        if (canUnpack)
        {
            blockAccessor.WalkBlocks(start, end, (block, posX, posY, posZ) => {
                BlockPos pos = new(posX, posY, posZ, 0);
                if (!entity.World.Claims.TryAccess(entity.Player, pos, EnumBlockAccessFlags.BuildOrBreak))
                {
                    notified = true;
                    blocks.Add(pos);
                } else if (pos.Y == start.Y)
                {
                    // ReSharper disable once InvertIf
                    if (Config.RequireSolidGround && !block.SideSolid[BlockFacing.indexUP])
                    {
                        if (!notified)
                        {
                            SendClientError(entity, Lang.SolidGroundError());
                            notified = true;
                        }

                        blocks.Add(pos);
                    }
                } else if (!IsReplaceable(block))
                {
                    if (!notified)
                    {
                        SendClientError(entity, Lang.ClearAreaError());
                        notified = true;
                    }

                    blocks.Add(pos);
                }
            });
        }

        return canUnpack && !ShouldHighlightBlocks(entity, blocks);
    }

    private static bool IsBannedBlock(Block block) {
        AssetLocation? blockCode = block.Code;

        if (blockCode == null) {
            return false;
        }

        if (Config.AllowListMode)
        {
            // Always allow air blocks
            if (block.BlockId == 0) return false;

            // Grass and Rocks check
            if (IsPlantOrRock(block)) return false;

            foreach (string allowed in Config.AllowedBlocks)
            {
                AssetLocation code = new(allowed);
                if (code.Equals(blockCode))
                {
                    return false;
                }

                if (code.IsWildCard && WildcardUtil.GetWildcardValue(code, blockCode) != null)
                {
                    return false;
                }
            }

            return true;
        }
        else
        {
            foreach (string banned in Config.BannedBlocks)
            {
                AssetLocation code = new(banned);
                if (code.Equals(blockCode))
                {
                    return true;
                }

                if (code.IsWildCard && WildcardUtil.GetWildcardValue(code, blockCode) != null)
                {
                    return true;
                }
            }

            return false;
        }

    }

    private bool ShouldHighlightBlocks(EntityPlayer entity, List<BlockPos> blocks) {
        if (blocks.Count <= 0) {
            return false;
        }

        if (_highlightId > 0) {
            entity.Api.Event.UnregisterCallback(_highlightId);
        }

        int color = Config.HighlightErrorColor.ToColor().Reverse();
        List<int> colors = Enumerable.Repeat(color, blocks.Count).ToList();
        entity.World.HighlightBlocks(entity.Player, 1337, blocks, colors);

        _highlightId = entity.Api.Event.RegisterCallback(_ => {
            List<BlockPos> empty = Array.Empty<BlockPos>().ToList();
            entity.World.HighlightBlocks(entity.Player, 1337, empty);
        }, 2500);

        return true;
    }
}
