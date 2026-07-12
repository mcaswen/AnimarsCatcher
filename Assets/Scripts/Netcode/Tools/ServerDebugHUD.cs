using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

/// <summary>在服务器进程中显示连接和角色位置的即时调试信息</summary>
public class ServerDebugHUD : MonoBehaviour
{
    World _serverWorld;
    EntityManager _entityManager;
    EntityQuery _connectionQuery, _inGameQuery, _characterQuery;

    /// <summary>绑定 Server World 并准备调试查询</summary>
    void Start()
    {
        // HUD 必须绑定 Server World，找不到时禁用以避免查询错误世界
        if (!TryBindServerWorld())
        {
            enabled = false;
            return;
        }

        Debug.Log("[ServerDebugHUD] HUD started on Server World: " + _serverWorld.Name);
    }

    /// <summary>绘制服务器连接状态和角色位置</summary>
    void OnGUI()
    {
        if (_serverWorld == null)
        {
            return;
        }

        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        var bigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 32,
            normal = { textColor = Color.white }
        };

        GUI.color = Color.white;

        int connectionCount = _connectionQuery.CalculateEntityCount();
        int inGameConnectionCount = _inGameQuery.CalculateEntityCount();

        GUILayout.BeginArea(new Rect(100, 120, Screen.width - 40, Screen.height - 40));
        GUILayout.Label($"[ServerHUD] World= {_serverWorld.Name} ", bigStyle);
        GUILayout.Label($"Connections = {connectionCount}   InGame = {inGameConnectionCount}", bigStyle);

        // 查询与 Transform 数组来自同一 EntityQuery，索引可一一对应
        using (var characterEntities = _characterQuery.ToEntityArray(Allocator.Temp))
        using (var transforms = _characterQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp))
        {
            for (int i = 0; i < characterEntities.Length; i++)
            {
                var position = transforms[i].Position;
                GUILayout.Label($"Character[{i}] pos = ({position.x:F2}, {position.y:F2}, {position.z:F2})", bigStyle);
            }
        }
        GUILayout.EndArea();
    }

    /// <summary>判断服务器进程是否包含指定启动参数</summary>
    /// <param name="flag">待查询参数</param>
    /// <returns>参数是否存在</returns>
    static bool HasArgument(string flag)
    {
        var arguments = System.Environment.GetCommandLineArgs();

        for (int i = 0; i < arguments.Length; i++)
            if (string.Equals(arguments[i], flag, System.StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>查找 Server World 并创建 HUD 使用的实体查询</summary>
    /// <returns>是否成功绑定服务器世界</returns>
    bool TryBindServerWorld()
    {
        // Host 进程同时存在多个 World，必须按 World 标志查找服务器实例
        foreach (var world in World.All)
        {
            Debug.Log($"World: {world.Name}, Flags={world.Flags}");
            if (IsServerWorld(world))
            {
                _serverWorld = world;
                _entityManager = world.EntityManager;

                _connectionQuery = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                _inGameQuery = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamInGame>());
                _characterQuery = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<CharacterTag>(), ComponentType.ReadOnly<LocalTransform>());

                Debug.LogWarning("[ServerDebugHUD] Bound to Server World: " + _serverWorld.Name);
                
                return true;
            }
        }

        Debug.LogWarning("[ServerDebugHUD] No Server World found.");

        return false;
    }

    /// <summary>根据 NetCode World 标志判断服务器职责</summary>
    /// <param name="world">待判断 World</param>
    /// <returns>是否为服务器世界</returns>
    static bool IsServerWorld(World world)
    {
        if (world.IsServer()) return true;
        if (world.IsClient() || world.IsThinClient()) return false;
        
        return false;
    }
}
