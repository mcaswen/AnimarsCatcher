// using UnityEngine;
// using Unity.Entities;

// public class ServerFixedCameraSetup : MonoBehaviour
// {
//     void Awake()
//     {
//         // 仅在 Server 世界保留，Client 世界直接关掉
//         var flags = World.DefaultGameObjectInjectionWorld.Flags;

//         bool isServer = (flags & WorldFlags.GameServer) != 0;
//         if (!isServer) { gameObject.SetActive(false); return; }

//         var camera = GameObject.FindWithTag("MainCamera");

//         transform.SetParent(null); // 先脱离父物体
//         camera.transform.SetParent(transform);
//     }
// }
