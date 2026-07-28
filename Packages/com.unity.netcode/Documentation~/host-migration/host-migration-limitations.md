# 限制与已知问题

了解主机迁移的限制与已知问题，以便在项目中有效实现该功能

<a id="limitations"></a>
## 限制

* 每份主机迁移快照的数据上限为 10 MiB，无法配置
* 主机迁移数据始终上传到美国中部区域，也始终从该区域下载
* 主机选举是随机的，不会对候选玩家进行排序
* 不支持迁移包含子实体的 Ghost
* 目前不支持 WebGL 平台

<a id="known-issues"></a>
## 已知问题

<a id="considerations-for-owned-ghosts"></a>
### 所有权 Ghost 注意事项

新主机上的 Ghost ID 和连接 Network ID 与旧主机不同。新主机重新生成 Ghost 时，会从自身 Ghost ID 池中分配全新 ID。连接上的 Network ID 也是如此：新主机会从 1 开始分配，顺序很可能与旧主机不同

由于 Network ID 不匹配，每当新连接抵达时都需要更新 Ghost 所有者。迁移刚完成时，全部 Ghost 所有者都会设为 -1；当重新加入的客户端连接抵达后，Ghost 所有者值会更新为该客户端当前的 Network ID

<a id="allocation-id-not-found-errors"></a>
### 找不到 Allocation ID 错误

主机迁移期间，新主机有时无法与 Relay 服务器建立连接。这会导致主机迁移失败，而客户端会一直等待新主机将新的 Relay 加入代码报告给 Lobby。如果主机加入代码在 Relay 连接完全建立前就已上报，客户端可能遇到 `allocation ID not found` 错误。如果该问题频繁发生，在连接或开始监听前设置 Relay 数据时，改用 UDP 连接类型可能有所帮助

<a id="entity-scene-loading-behaviour"></a>
### 实体场景加载行为

主机加载的实体场景会被保存并在新主机上重新加载。目前该机制较为简单：如果这些实体场景中的内容被销毁，它们会再次出现在新主机上，因为销毁状态不会被跟踪；场景中的预生成 Ghost 也是如此

<a id="crash-in-serverhostmigrationsystem-after-migration"></a>
### 迁移后 ServerHostMigrationSystem 崩溃

新选出的主机尝试部署主机迁移数据时，该系统可能随机崩溃。系统会选择另一台主机，之后通常可以正常恢复。该问题可能与 Burst 有关

<a id="invalid-ghosts-on-clients-after-migration"></a>
### 迁移后客户端出现无效 Ghost

迁移后有时会出现类似 `Entity Unity.Entities.Entity is not a valid ghost (i.e. it is not a real 'replicated ghost', nor is it a 'predicted spawn' ghost). This can happen if you instantiate a ghost entity on the client manually (without marking it as a predicted spawn).` 的日志，可能与预生成 Ghost 有关。其原因可能是主机迁移后客户端残留的 Ghost 处于无效状态

<a id="prespawned-ghost-instability"></a>
### 预生成 Ghost 不稳定

迁移后有时会发生只影响预生成 Ghost 的同步错误，例如 `Received a ghost (ID -2147483647 Entity(40:25)) with an invalid ghost type 5 (expected 6)`。通常出现该错误后，对应客户端上的预生成 Ghost 会停止同步，但其他 Ghost 不受影响

## 其他资源

* [主机迁移要求](host-migration-requirements.md)
