## COMMAND 与 RPC

### 区域

| 区域 | 说明 |
|------|------|
| `COMMAND_USING` | 包含全部 using 语句，必须位于文件开头，也可以包含用户定义的 using 语句 |
| `COMMAND_USING_STATEMENT` | 包含自动生成的 using 语句，必须位于 `COMMAND_USING` 区域内 |
| `COMMAND_READ` | 用于反序列化值的代码片段 |
| `COMMAND_WRITE` | 用于序列化值的代码片段 |
| `COMMAND_READ_PACKED` | 命令进行增量压缩时用于反序列化值的代码片段 |
| `COMMAND_WRITE_PACKED` | 命令进行增量压缩时用于序列化值的代码片段 |

### 变量

| 变量 | 说明 |
|------|------|
| `COMMAND_NAMESPACE` | 生成器命名空间 |
| `COMMAND_FIELD_NAME` | 当前字段名称 |
| `COMMAND_FIELD_TYPE_NAME` | 完整字段类型名称，以点号分隔的命名空间和声明类型作为前缀 |
| `COMMAND_COMPONENT_TYPE` | 组件名称，以点号分隔的命名空间和声明类型作为前缀 |

## Ghost 序列化器

以下区域和变量不面向用户。它们属于用户无法自定义的 `GhostComponentSerializer` 模板

### 区域

| 区域 | 说明 |
|------|------|
| `GHOST_USING_STATEMENT` | 包含全部默认和自定义 using 语句 |
| `GHOST_COMPONENT_IS_BUFFER` | 组件为缓冲区时插入的可选区域，通常用于添加一些 define |
| `GHOST_EMPTY_VARIANT_LIST` | 包含空变体的注册代码，仅存在空变体时才会添加 |
| `GHOST_COPY_FROM_SNAPSHOT_DISABLE_EXTRAPOLATION` | 禁用外推时从快照复制数据的代码 |
| `GHOST_COPY_FROM_SNAPSHOT_ENABLE_EXTRAPOLATION` | 启用外推时从快照复制数据的代码 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_CLAMP_MAX` | 插值时限制最大值的代码 |
| `GHOST_FLUSH_COMPONENT_CHANGE_MASK` | 将变更掩码位写入掩码缓冲区 |
| `GHOST_FLUSH_FINAL_COMPONENT_CHANGE_MASK` | 将剩余变更掩码位写入掩码缓冲区，在组件序列化结束时调用 |
| `GHOST_REFRESH_CHANGE_MASK` | 从掩码缓冲区复制一批 32 个变更掩码位，在反序列化组件数据时使用 |
| `GHOST_PREDICTION_ERROR_HEADER` | 包含计算预测误差前只运行一次的初始化代码 |

### 变量

| 变量 | 说明 |
|------|------|
| `GHOST_NAME` | 组件名称 |
| `GHOST_NAMESPACE` | 组件命名空间 |
| `GHOST_VARIANT_HASH` | 计算得到的变体哈希 |
| `GHOST_VARIANT_TYPE` | 变体类型名称 |
| `GHOST_FIELD_HASH` | 计算得到的字段哈希，运行时随后使用它计算协议版本 |
| `GHOST_PREFAB_TYPE` | `GhostComponent` 的 `PrefabType` 属性 |
| `GHOST_SEND_MASK` | `GhostComponent` 的 `SendMask` 属性 |
| `GHOST_SEND_OWNER` | `GhostComponent` 的 `SendOwner` 属性 |
| `GHOST_MAX_INTERPOLATION_DISTSQ` | 允许的最大距离平方，超过该距离时会将当前字段直接吸附到新值，而不进行插值 |
| `GHOST_CHANGE_MASK_BITS` | 到目前为止计算出的掩码位总数 |

## 类型模板

用户编写自定义模板时可以使用以下变量和区域

### 变量

| 变量 | 说明 |
|------|------|
| `GHOST_FIELD_NAME` | 当前字段在快照数据结构中的路径 |
| `GHOST_QUANTIZE_SCALE` | 将浮点数转换为定点格式时使用的量化因子 |
| `GHOST_DEQUANTIZE_SCALE` | 将定点格式转换为浮点数时使用的反量化因子 |
| `GHOST_COMPONENT_TYPE` | 组件的完整类型名称 |
| `GHOST_FIELD_REFERENCE` | 用于访问字段的字段路径 |
| `GHOST_USING` | using 语句列表 |
| `GHOST_MASK_INDEX` | 变更掩码中的当前位索引，范围为 0 到 31 |

### 区域

| 区域 | 说明 |
|------|------|
| `GHOST_FIELD` | 将为该类型添加到快照中的字段 |
| `GHOST_IMPORTS` | 应包含在序列化器代码中的用户自定义 using 语句 |
| `GHOST_RESTORE_FROM_BACKUP` | 从备份恢复字段值的代码 |
| `GHOST_PREDICT` | 计算当前字段预测值的代码 |
| `GHOST_READ` | 从字节流反序列化字段值的代码 |
| `GHOST_WRITE` | 将字段值序列化到字节流的代码 |
| `GHOST_COPY_TO_SNAPSHOT` | 将组件字段数据复制到快照结构的代码 |
| `COPY_FROM_SNAPSHOT_SETUP` | 根据结构是缓冲区还是组件来设置序列化的占位区域 |
| `GHOST_COPY_FROM_SNAPSHOT` | 复制到 `COPY_FROM_SNAPSHOT_SETUP` 中 |
| `GHOST_COPY_FROM_BUFFER` | 复制到 `COPY_FROM_SNAPSHOT_SETUP` 中 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP` | 可选区域，可以包含 `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE` 区域使用的初始化代码 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ` | 计算当前字段在 Before 和 After 快照之间的距离平方 |
| `GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE` | 在 Before 和 After 快照值之间插值字段值的代码 |
| `GHOST_CALCULATE_CHANGE_MASK` | 检查并更新变更掩码位的代码 |
| `GHOST_CALCULATE_CHANGE_MASK_ZERO` | 检查并设置变更掩码位的代码，在掩码重置时或写入 32 个掩码位后使用 |
| `GHOST_REPORT_PREDICTION_ERROR` | 计算当前字段的当前值与预测值之间误差的代码，通常计算距离 |
| `GHOST_GET_PREDICTION_ERROR_NAME` | 误差名称，用于 Network Debugger 或调试 |
