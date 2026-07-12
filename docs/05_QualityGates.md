# 测试、质量门禁与专项风险

[返回开发规范总目录](DevelopmentGuidelines.md)

## 1. 测试结构

```text
Assets/Tests/
├── EditMode/
└── PlayMode/
```

1. 纯逻辑、配置校验、FSM 条件和服务器请求校验优先使用 EditMode 测试。
2. 场景加载、UI、Authoring、Baker、World 生命周期和 Hybrid View 使用 PlayMode 测试。
3. NetCode 关键流程至少覆盖 Host + Client；涉及服务器流程时覆盖 Dedicated Server 或 Server World。
4. 修复缺陷时应补充可复现问题的测试，或记录无法自动化的原因和手工验证步骤。
5. 测试程序集与 Runtime、Editor 程序集分离。

## 2. 修改风险等级

### 2.1 低风险

- 文档、注释和不影响行为的局部命名调整。
- 未被引用的新资源。
- 独立 Editor 工具的小范围修复。

最低验证：差异检查、格式检查和相关文件打开验证。

### 2.2 中风险

- 单一 MonoBehaviour、UI 流程、Prefab 或 ScriptableObject 配置修改。
- 单个 ECS System、Authoring 或表现桥接修改。
- 资源导入设置和材质修改。

最低验证：重新编译、相关场景或 Prefab 验证、针对性 EditMode/PlayMode 测试。

### 2.3 高风险

- 网络协议、RPC、Ghost、预测、伤害和资源结算。
- World 创建/销毁、场景加载、SubScene、Build Profile。
- 公共 Prefab、公共配置结构、Packages 和 ProjectSettings。
- 大规模资产迁移、命名空间或 asmdef 调整。

最低验证：Host + Client、异常输入、第二局/重连、相关测试和目标平台 Player Build。

## 3. 合并门禁

每个功能或修复合并前必须完成与风险相称的检查：

1. Unity 脚本重新编译，无新增编译错误。
2. Console 无与修改相关的异常和持续警告。
3. EditMode/PlayMode 相关测试通过。
4. Scene、Prefab 和 ScriptableObject 无 Missing Script、失效引用和意外 Override。
5. Host、Client、返回菜单和第二局按影响范围验证。
6. Invalid RPC、资源不足、断线和重复请求按影响范围验证。
7. Build Profile 场景列表正确。
8. 高风险修改至少执行目标平台开发构建或 CI 构建。
9. Git 中没有遗漏 `.meta`、冲突标记、临时 Dump 和无关改动。
10. 业务源码注释率不低于 15%，推荐约 17%，且没有为达标添加无意义注释。

## 4. 发布前检查

1. 使用干净工作区和锁定的 Packages 状态构建。
2. 验证 Windows 正式 Build Profile 包含正确入口和玩法场景。
3. 验证 AutoLoad SubScene 能在 Player 中加载，且没有重复地图内容。
4. 验证 Host、Client、断线、重连、返回菜单和第二局。
5. 验证服务器拒绝非法场景、非法生成、资源透支和越权攻击请求。
6. 验证胜负结算只触发一次，Singleton 和 Event Hub 唯一。
7. 检查音频、纹理、光照数据和大型资源是否符合构建及内存预算。
8. 检查所有目标质量档位、Shader Variant 和关键材质。
9. 更新版本号、发布说明、许可证和已知问题。

## 5. 当前项目专项禁区

以下规则针对 AnimarsCatcher 当前架构，属于必须检查项：

1. 不在 Runtime 脚本中直接引用 `UnityEditor`。
2. 不使用 `EntityManager == null` 判断有效性。
3. 不在 Baker 和 Runtime System 中重复创建同类型 Singleton。
4. 不创建多个 Resource Event Hub 并向全部 Hub 重复写入奖励。
5. 不允许客户端直接提交任意资源增量、生成数量、场景名或最终伤害。
6. 不在多个 World 之间共享无生命周期保护的静态 NativeContainer。
7. 不在逐帧系统中无条件重算所有单位的 NavMesh 路径。
8. 不在循环处理单个实体时误用 `return` 跳过其余实体。
9. 不在返回主菜单时销毁网络 World 却没有重建方案。
10. 不将同一 Terrain、灯光、碰撞体和地图对象同时放在主场景与 AutoLoad SubScene。
11. 不让 Windows Build Profile 覆盖全局场景列表后保持空列表。
12. 不将 `Obsolete` 当作编译排除机制。
13. 不新增散落的场景字符串、启动参数和端口常量。
14. 不以旧 `.csproj`、旧 `Library/ScriptAssemblies` 或本地缓存证明当前源码可编译。

## 6. 程序 Review 清单

- 职责是否属于当前模块？
- WorldFilter、SystemGroup 和系统顺序是否正确？
- 是否存在跨 World 静态状态？
- NativeContainer、Query 和 ECB 生命周期是否完整？
- RPC 是否验证来源、所有权、范围和状态？
- 客户端是否决定了服务器权威数据？
- 是否存在逐帧分配、查询、日志或路径计算？
- Singleton 是否只有一个创建方？
- 场景卸载、断线和第二局是否安全？
- 是否有相应测试或验证说明？

## 7. 场景与 Prefab Review 清单

- 是否修改了正确的 Scene 或 Prefab？
- 是否存在重复地图、灯光、Collider 或 SubScene 内容？
- 是否出现 Missing Script、失效引用或意外 Override？
- 是否误将开发场景加入 Build Profile？
- Network Prefab 的 GhostOwner、预测模式和 View 是否正确？
- 修改是否需要同步策划、美术或程序负责人？

## 8. Definition of Done

任务完成必须同时满足：

1. 需求行为已实现，并明确影响哪些 Scene、Prefab、配置和 Client/Server World。
2. 代码和资源符合对应专题规范。
3. 已完成与风险等级匹配的测试和构建验证。
4. 已清理临时对象、日志、作弊入口和无关改动。
5. 提交信息清晰，提交粒度单一，相关负责人完成 Review。
6. 已记录必要的迁移步骤、已知限制和回滚方式。

## 9. 规范维护

1. 规范由项目负责人和主程共同维护。
2. 新增规则应对应真实风险、重复问题或明确协作成本。
3. 已失效、无法执行或长期被合理绕过的规则应及时修订。
4. 规范变更使用独立文档提交，并说明影响范围。
5. 存量整改建立任务列表分阶段完成，不压到单次功能开发中。
