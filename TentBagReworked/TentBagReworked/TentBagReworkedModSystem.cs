using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;
using ProtoBuf;
using System.IO;
using TentBagReworked.Behaviors;
using TentBagReworked.Config;
using TentBagReworked.Items;
using TentBagReworked;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using System.Collections.Generic;
using System;

namespace TentBagReworked
{
    public class TentBagReworkedModSystem : ModSystem
    {
        public static TentBagReworkedModSystem Instance { get; private set; } = null!;

        public ICoreAPI Api { get; private set; } = null!;
        public ICoreClientAPI clientApi { get; private set; } = null!;

        public ILogger Logger => Mod.Logger;
        public string ModId => Mod.Info.ModID;
        public TentBagReworkedConfig Config => _config ?? new TentBagReworkedConfig();

        public static Dictionary<string, List<string>> PendingWaypointNames = new();

        public Dictionary<string, List<Guid>> SchematicHistory = new();

        internal static string ExportFolderPath;
        private TentBagReworkedConfig? _config;
        private FileWatcher? _fileWatcher;
        private IServerNetworkChannel? _channel;

        public TentBagReworkedModSystem()
        {
            Instance = this;
        }

        public override void StartPre(ICoreAPI api)
        {
            Api = api;
        }

        public override void Start(ICoreAPI api)
        {
            api.RegisterCollectibleBehaviorClass("tentbagreworked.packable", typeof(PackableBehavior));
            api.RegisterItemClass("tentbagreworked.itemtentbag", typeof(ItemTentBag));

            ExportFolderPath = api.GetOrCreateDataPath("ModData/TentBagReworked");
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            clientApi = capi;
            clientApi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>()
                .SetMessageHandler<ErrorPacket>(packet => {
                    if (!string.IsNullOrEmpty(packet.Error))
                    {
                        (Api as ICoreClientAPI)?.TriggerIngameError(this, "error", packet.Error);
                    }
                });
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            _channel = sapi.Network.RegisterChannel(Mod.Info.ModID)
                .RegisterMessageType<ErrorPacket>();

            ReloadConfig();
        }

        public void SendClientError(IPlayer? player, string error)
        {
            if (player is IServerPlayer serverPlayer)
            {
                _channel?.SendPacket(new ErrorPacket { Error = error }, serverPlayer);
            }
        }

        public void SendClientChatMessage(IPlayer? player, string message)
        {
            if (player is IServerPlayer serverPlayer)
            {
                serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            }
        }

        public void ReloadConfig()
        {
            _config = Api.LoadModConfig<TentBagReworkedConfig>($"{ModId}.json") ?? new TentBagReworkedConfig();

            (_fileWatcher ??= new FileWatcher(this)).Queued = true;

            string json = JsonConvert.SerializeObject(_config, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            FileInfo fileInfo = new(Path.Combine(GamePaths.ModConfig, $"{ModId}.json"));
            GamePaths.EnsurePathExists(fileInfo.Directory!.FullName);
            File.WriteAllText(fileInfo.FullName, json);

            Api.Event.RegisterCallback(_ => _fileWatcher.Queued = false, 100);
        }

        public override void Dispose()
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;

            _channel = null;
        }

        [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
        private class ErrorPacket
        {
            public string? Error;
        }


    }
}
