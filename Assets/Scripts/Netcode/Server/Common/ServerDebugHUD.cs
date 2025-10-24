using System.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

public class ServerDebugHUD : MonoBehaviour
{
    World _serverWorld;
    EntityManager _em;
    EntityQuery _qConn, _qInGame, _qCharacters;

    void Start()
    {
        if (!TryBindServerWorld())
        {
            enabled = false;
            return;
        }

        Debug.Log("[ServerDebugHUD] HUD started on Server World: " + _serverWorld.Name);
    }

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

        int conns = _qConn.CalculateEntityCount();
        int inGame = _qInGame.CalculateEntityCount();

        GUILayout.BeginArea(new Rect(100, 120, Screen.width - 40, Screen.height - 40));
        GUILayout.Label($"[ServerHUD] World= {_serverWorld.Name} ", bigStyle);
        GUILayout.Label($"Connections = {conns}   InGame = {inGame}", bigStyle);

        using (var cubes = _qCharacters.ToEntityArray(Allocator.Temp))
        using (var transforms = _qCharacters.ToComponentDataArray<LocalTransform>(Allocator.Temp))
        {
            for (int i = 0; i < cubes.Length; i++)
            {
                var p = transforms[i].Position;
                GUILayout.Label($"Character[{i}] pos = ({p.x:F2}, {p.y:F2}, {p.z:F2})", bigStyle);
            }
        }
        GUILayout.EndArea();
    }

    static bool HasArg(string flag)
    {
        var args = System.Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], flag, System.StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    bool TryBindServerWorld()
    {
        foreach (var w in World.All)
        {
            Debug.Log($"World: {w.Name}, Flags={w.Flags}");
            if (IsServerWorld(w))
            {
                _serverWorld = w;
                _em = w.EntityManager;

                _qConn = _em.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
                _qInGame = _em.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamInGame>());
                _qCharacters = _em.CreateEntityQuery(ComponentType.ReadOnly<CharacterTag>(), ComponentType.ReadOnly<LocalTransform>());

                Debug.LogWarning("[ServerDebugHUD] Bound to Server World: " + _serverWorld.Name);
                
                return true;
            }
        }

        Debug.LogWarning("[ServerDebugHUD] No Server World found.");

        return false;
    }

    static bool IsServerWorld(World w)
    {
        if (w.IsServer()) return true;
        if (w.IsClient() || w.IsThinClient()) return false;
        
        return false;
    }
}
