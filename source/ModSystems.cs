using Newtonsoft.Json.Linq;
using ProtoBuf;
using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace MapMarkers;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class WaypointProperties
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "0-circle";
    public string Color { get; set; } = "";
    public bool Pinned { get; set; } = false;
    public float Coverage { get; set; } = 0;
}

public class WaypointJson : WaypointProperties
{
    public string[] Blocks { get; set; } = Array.Empty<string>();
    public string[] Entities { get; set; } = Array.Empty<string>();

    public WaypointProperties ToProperties()
    {
        return new()
        {
            Title = Title,
            Icon = Icon,
            Color = Color,
            Pinned = Pinned,
            Coverage = Coverage
        };
    }
}

public class WaypointsConfig
{
    public WaypointJson[] Waypoints { get; set; } = Array.Empty<WaypointJson>();
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public sealed class AddWaypointPacket
{
    public double[] Position { get; set; } = Array.Empty<double>();
    public WaypointProperties Properties { get; set; } = new();
}

public sealed class MapMarkersSystem : ModSystem
{
    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Input.InWorldAction += OnEntityAction;
        _clientApi = api;
        _clientChannel = api.Network.RegisterChannel("mapmarkers-waypointspackets")
            .RegisterMessageType<AddWaypointPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Network.RegisterChannel("mapmarkers-waypointspackets")
            .RegisterMessageType<AddWaypointPacket>()
            .SetMessageHandler<AddWaypointPacket>(OnWaypointPacket);
        _serverApi = api;
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api is ICoreClientAPI clientApi)
        {
            WaypointsConfig? config = null;
            try
            {
                config = clientApi.LoadModConfig<WaypointsConfig>(_configFileName);
                if (config == null)
                {
                    IAsset asset = api.Assets.Get(new("mapmarkers:config/defaultconfig.json"));
                    byte[] data = asset.Data;
                    data = System.Text.Encoding.Convert(System.Text.Encoding.UTF8, System.Text.Encoding.Unicode, data);
                    string json = System.Text.Encoding.Unicode.GetString(data);
                    JObject token = JObject.Parse(json);
                    JsonObject jsonObject = new(token);
                    WaypointsConfig defaultConfig = jsonObject.AsObject<WaypointsConfig>();

                    clientApi.StoreModConfig(defaultConfig, _configFileName);
                    config = defaultConfig;
                }
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
            }

            if (config == null) return;

            CollectBlocksWaypoints(clientApi, config);
            CollectEntitiesWaypoints(clientApi, config);
        }
    }

    private WaypointMapLayer? _waypointMapLayer;
    private static readonly MethodInfo? _resendWaypointsMethod = typeof(WaypointMapLayer).GetMethod("ResendWaypoints", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? _rebuildMapComponentsMethod = typeof(WaypointMapLayer).GetMethod("RebuildMapComponents", BindingFlags.NonPublic | BindingFlags.Instance);
    private const string _configFileName = "waypoints.json";
    private readonly Dictionary<int, WaypointProperties> _blocksWaypoints = new();
    private readonly Dictionary<string, WaypointProperties> _entitiesWaypoints = new();
    private ICoreClientAPI? _clientApi;
    private ICoreServerAPI? _serverApi;
    private IClientNetworkChannel? _clientChannel;

    private void CollectBlocksWaypoints(ICoreClientAPI api, WaypointsConfig config)
    {
        Dictionary<string, WaypointProperties> blocksWaypoints = new();
        foreach (WaypointJson waypoint in config.Waypoints)
        {
            WaypointProperties properties = waypoint.ToProperties();
            foreach (string wildcard in waypoint.Blocks)
            {
                blocksWaypoints.TryAdd(wildcard, properties);
            }
        }

        foreach ((string wildcard, WaypointProperties properties) in blocksWaypoints)
        {
            for (int blockId = 0; blockId < api.World.Blocks.Count; blockId++)
            {
                Block? block = api.World.Blocks[blockId];
                if (block == null) continue;

                if (WildcardUtil.Match(wildcard, block.Code.Path))
                {
                    _blocksWaypoints.TryAdd(blockId, properties);
                }
            }
        }
    }
    private void CollectEntitiesWaypoints(ICoreClientAPI api, WaypointsConfig config)
    {
        Dictionary<string, WaypointProperties> entitiesWaypoints = new();
        foreach (WaypointJson waypoint in config.Waypoints)
        {
            WaypointProperties properties = waypoint.ToProperties();
            foreach (string wildcard in waypoint.Entities)
            {
                entitiesWaypoints.TryAdd(wildcard, properties);
            }
        }

        foreach ((string wildcard, WaypointProperties properties) in entitiesWaypoints)
        {
            foreach (EntityProperties entity in api.World.EntityTypes)
            {
                if (WildcardUtil.Match(wildcard, entity.Code.Path))
                {
                    _entitiesWaypoints.Add(entity.Code.Path, properties);
                }
            }
        }
    }

    private void OnEntityAction(EnumEntityAction action, bool on, ref EnumHandling handled)
    {
        if (action != EnumEntityAction.RightMouseDown || !on) return;

        BlockSelection? blockSelection = _clientApi?.World.Player.CurrentBlockSelection;
        if (blockSelection?.Block != null && _blocksWaypoints.TryGetValue(blockSelection.Block.Id, out WaypointProperties blockWaypoint))
        {
            _clientChannel?.SendPacket(new AddWaypointPacket()
            {
                Position = new double[3] { blockSelection.Position.X, blockSelection.Position.InternalY, blockSelection.Position.Z },
                Properties = blockWaypoint
            });
        }

        EntitySelection? entitySelection = _clientApi?.World.Player.CurrentEntitySelection;
        if (entitySelection?.Entity != null && _entitiesWaypoints.TryGetValue(entitySelection.Entity.Code.Path, out WaypointProperties entityWaypoint))
        {
            _clientChannel?.SendPacket(new AddWaypointPacket()
            {
                Position = new double[3] { entitySelection.Position.X, entitySelection.Position.Y, entitySelection.Position.Z },
                Properties = entityWaypoint
            });
        }
    }

    private void OnWaypointPacket(IServerPlayer player, AddWaypointPacket packet)
    {
        Vec3d position = new(packet.Position[0], packet.Position[1], packet.Position[2]);

        AddWaypoint(player, position, packet.Properties.Coverage, packet.Properties.Title, packet.Properties.Icon, ColorUtil.Hex2Int(packet.Properties.Color), packet.Properties.Pinned);
    }

    private void AddWaypoint(IServerPlayer player, Vec3d position, double markerCoverageRadius, string title, string icon, int markerColorInteger, bool pinned)
    {
        if (_waypointMapLayer == null)
        {
            GetMapLayer();
            if (_waypointMapLayer == null) return;
        }

        foreach (Waypoint waypoint in _waypointMapLayer.Waypoints.Where(w => w.OwningPlayerUid == player.PlayerUID))
        {
            double xDiff = Math.Abs(waypoint.Position.X - position.X);
            double zDiff = Math.Abs(waypoint.Position.Z - position.Z);
            if (
                Math.Max(xDiff, zDiff) < markerCoverageRadius &&
                waypoint.Title == title && waypoint.Icon == icon &&
                waypoint.Color == markerColorInteger
                 )
            {
                return;
            }
        }

        AddWaypointToMap(player, position, title, icon, markerColorInteger, pinned);
    }
    private void AddWaypointToMap(IServerPlayer player, Vec3d pos, string title, string icon, int color, bool pinned = false)
    {
        if (_waypointMapLayer == null) return;

        Waypoint waypoint = new()
        {
            Color = color,
            OwningPlayerUid = player.PlayerUID,
            Position = pos,
            Title = title,
            Icon = icon,
            Pinned = pinned
        };

        _waypointMapLayer.Waypoints.Add(waypoint);
        _resendWaypointsMethod?.Invoke(_waypointMapLayer, new object[] { player });
    }
    private void DeleteClosestWaypoint(IServerPlayer player)
    {
        if (_waypointMapLayer == null) return;

        double closestDistance = double.MaxValue;
        Waypoint? closestWaypoint = null;

        foreach (Waypoint wp in _waypointMapLayer.Waypoints.Where(waypoint => waypoint.OwningPlayerUid == player.PlayerUID))
        {
            double distance = player.Entity.Pos.DistanceTo(wp.Position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestWaypoint = wp;
            }
        }

        if (closestWaypoint != null)
        {
            _waypointMapLayer.Waypoints.Remove(closestWaypoint);
            _rebuildMapComponentsMethod?.Invoke(_waypointMapLayer, null);
            _resendWaypointsMethod?.Invoke(_waypointMapLayer, new object[] { player });
        }
    }

    private void GetMapLayer()
    {
        WorldMapManager? serverWorldMapManager = _serverApi?.ModLoader.GetModSystem<WorldMapManager>();
        _waypointMapLayer = serverWorldMapManager?.MapLayers.Find((MapLayer layer) => layer is WaypointMapLayer) as WaypointMapLayer;
    }
}