using Unity.Entities;
using Unity.NetCode;

public static class WorldManager
{
    public static bool IsClient(ref SystemState state) => state.WorldUnmanaged.IsClient();
    public static bool IsServer(ref SystemState state) => state.WorldUnmanaged.IsServer();
    public static bool IsThin(ref SystemState state) => state.WorldUnmanaged.IsThinClient();

    public static string Tag(ref SystemState state)
    {
        var c = IsClient(ref state);
        var sv = IsServer(ref state);
        var th = IsThin(ref state);

        if (sv && !c) return "[Server]";

        if (c && !sv) return "[Client]";

        if (th) return "[ThinClient]";

        return "[Client & Server]"; // LocalSimulation
    }
    
    public static World FindClientWorld()
    {
        foreach (var world in World.All)
        {
            if (world.Flags.HasFlag(WorldFlags.GameClient))
            {
                return world;
            }
        }

        return null;
    }

    public static World FindServerWorld()
    {
        foreach (var world in World.All)
        {
            if (world.Flags.HasFlag(WorldFlags.GameServer))
            {
                return world;
            }
        }

        return null;
    }
    
}