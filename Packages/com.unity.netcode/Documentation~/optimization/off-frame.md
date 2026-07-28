# 在间隔帧执行高开销操作

在间隔帧执行高开销操作，分散其影响并提升性能

在客户端托管的服务器上，如果将 [ClientServerTickRate.TargetFrameRateMode](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.FrameRateMode.html) 设为 `BusyWait`，可以将游戏的 Tick 率设为 30Hz、帧率设为 60Hz，使主机每个 Tick 执行两帧。这样每两帧中会有一帧负载较低，可以利用该帧执行额外操作；这种帧称为间隔帧。若要判断当前帧是否会执行 Tick，可以访问服务器 World 的速率管理器

> [!NOTE]
> 服务器 World 在间隔帧并非完全空闲。当连接数和间隔帧足够多时，可以按时间片向多个连接发送数据。例如，如果 Tick 率足够低，具有十个连接的服务器可以在一帧向五个连接发送数据，并在下一帧向另外五个连接发送数据

```cs
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial class DoExtraWorkSystem : SystemBase
{
    protected override void OnUpdate()
    {
        var serverRateManager = ClientServerBootstrap.ServerWorld.GetExistingSystemManaged&lt;SimulationSystemGroup&gt;().RateManager as NetcodeServerRateManager;
        if (!serverRateManager.WillUpdate())
            DoExtraWork(); // 已知当前帧负载较低，可以执行额外工作
    }
}
```
