using tentbag.configuration;
using tentbag.util;
using Vintagestory.API.Common;
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

        BlockPos start = blockSel.Position.AddCopy(-Config.MaxRadius, 1 - y, -Config.MaxRadius);
        BlockPos end = blockSel.Position.AddCopy(Config.MaxRadius, Math.Max(Config.MaxHeight, 3), Config.MaxRadius);

        if (!CanPack(entity, blockAccessor, start, end, ref solidBlockCount)) {
            return;
        }

        // create schematic of area
        BlockSchematic bs = new();
        bs.AddAreaWithoutEntities(entity.World, blockAccessor, start, end);
        bs.PackIncludingAir(entity.World, start, solidBlockCount);

        // clear area in world
        ClearArea(entity.World, start, end);

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

        // consume player saturation (solid blocks * build effort)
        entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
    }

    private void UnpackContents(EntityPlayer entity, BlockSelection blockSel, ItemSlot slot, string contents, ref int solidBlockCount) {
        IBlockAccessor blockAccessor = entity.World.BlockAccessor;

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
        bs.Place(entity.World.BulkBlockAccessor, entity.World, adjustedStart);
        entity.World.BulkBlockAccessor.Commit();
        bs.PlaceEntitiesAndBlockEntities(blockAccessor, entity.World, adjustedStart, bs.BlockCodes, bs.ItemCodes);

        // manually relight the chunks since the bulk accessor doesn't do it
        (entity.World.Api as ICoreServerAPI)?.WorldManager.FullRelight(start, end);

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

        // consume player saturation
        entity.ReduceOnlySaturation(Config.BuildEffort * solidBlockCount);
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
            else if (IsBannedBlock(block.Code))
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

            // Check if the player has enough satiety to pack the tent
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

        solidBlockCount = localSolidBlockCount;
        return canPack && !ShouldHighlightBlocks(entity, blocks);
    }

    private bool CanUnpack(EntityPlayer entity, IBlockAccessor blockAccessor, BlockPos start, BlockPos end, ref int solidBlockCount) {
        List<BlockPos> blocks = new();
        bool notified = false;
        bool canUnpack = true;

        // Check if the player has enough satiety to pack the tent (do this first to prevent unnecessary block checks)
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
                    if (Config.RequireFloor && !block.SideSolid[BlockFacing.indexUP])
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

    private static bool IsBannedBlock(AssetLocation? block) {
        if (block == null) {
            return false;
        }

        foreach (string banned in Config.BannedBlocks) {
            AssetLocation code = new(banned);
            if (code.Equals(block)) {
                return true;
            }

            if (code.IsWildCard && WildcardUtil.GetWildcardValue(code, block) != null) {
                return true;
            }
        }

        return false;
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
