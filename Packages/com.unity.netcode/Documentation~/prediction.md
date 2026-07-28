# 预测

使用预测处理游戏中的延迟

| **主题**                        | **说明**                         |
| :------------------------------ | :------------------------------- |
| **[预测简介](intro-to-prediction.md)** | 客户端预测允许客户端使用自身输入在本地模拟游戏，无需等待服务器的模拟结果 |
| **[Netcode for Entities 中的预测](prediction-n4e.md)** | 在 Netcode for Entities 中实现客户端预测 |
| **[预测平滑](prediction-smoothing.md)** | `GhostPredictionSmoothingSystem` 系统提供了一种随时间校正并减小预测误差的方法，使状态转换更加平滑 |
| **[预测模式切换](prediction-switching.md)** | Netcode 支持按照某些条件，以每个客户端、每个 Ghost 为单位选择启用预测，例如预测客户端角色控制器一定半径内的所有 Ghost。此功能称为预测模式切换 |
| **[预测的边界情况与已知问题](prediction-details.md)** | 使用客户端预测时，需要注意一些已知的边界情况 |
