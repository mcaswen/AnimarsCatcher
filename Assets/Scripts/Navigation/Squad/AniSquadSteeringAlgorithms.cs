using AnimarsCatcher.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace AnimarsCatcher.Navigation.Grid
{
    /// <summary>
    /// 计算队伍如何沿流向场前进，以及成员如何跟随各自的阵型槽位
    /// </summary>
    public static class AniSquadSteeringAlgorithms
    {
        // 这里使用阵型系统已经算好的槽位，不负责调整列数或重新分配角色
        // 流向场缓冲区只存有用的格子，并由构建器按 CellIndex 排序
        // 队伍锚点和成员都只在 XZ 平面移动
        // 本类只计算速度，实际位置和旋转统一由移动提交系统写入
        public static bool TryGetFlowDirection(
            DynamicBuffer<NavigationFlowFieldCell> field,
            int cellIndex,
            out float3 direction)
        {
            direction = float3.zero;

            int minimum = 0;
            int maximum = field.Length - 1;
            while (minimum <= maximum)
            {
                int index = minimum + ((maximum - minimum) >> 1);
                NavigationFlowFieldCell cell = field[index];
                if (cell.CellIndex == cellIndex)
                {
                    direction = math.normalizesafe(
                        new float3(cell.Direction.x, 0f, cell.Direction.y));

                    // 终点格子的零方向仍是有效结果，只是不再推动锚点
                    return true;
                }

                if (cell.CellIndex < cellIndex)
                {
                    minimum = index + 1;
                }
                else
                {
                    maximum = index - 1;
                }
            }

            return false;
        }

        /// <summary>
        /// 结合队伍整体速度和成员偏离槽位的距离，计算成员下一步想要的速度
        /// </summary>
        /// <param name="currentPosition">成员当前位置</param>
        /// <param name="slotTarget">成员槽位目标</param>
        /// <param name="anchorVelocity">队伍锚点当前速度</param>
        /// <param name="maximumSpeed">成员最大速度</param>
        /// <returns>尚未应用加速度限制的目标速度</returns>
        public static float3 CalculateSlotVelocity(
            float3 currentPosition,
            float3 slotTarget,
            float3 anchorVelocity,
            float maximumSpeed)
        {
            float3 error = slotTarget - currentPosition;
            error = PlanarMath.FlattenY(error);
            float distance = math.length(error);
            float3 desired = anchorVelocity + error * 2.5f;
            float speed = math.length(desired);
            if (speed <= maximumSpeed || speed <= 1e-5f)
            {
                // 合成速度没有超限时原样返回，让成员既跟上队伍又能回到自己的槽位
                return desired;
            }

            return desired * (maximumSpeed / speed);
        }

        /// <summary>
        /// 根据剩余距离和制动距离，计算队伍锚点靠近目标时应采用的速度
        /// </summary>
        /// <param name="currentPosition">队伍锚点当前位置</param>
        /// <param name="targetPosition">解析后的目标位置</param>
        /// <param name="maximumSpeed">全队都能跟上的最大速度</param>
        /// <param name="maximumAcceleration">全队都能跟上的最大加速度</param>
        /// <param name="stoppingDistance">指令到达半径</param>
        /// <returns>尚未应用加速度限制的锚点目标速度</returns>
        public static float3 CalculateAnchorVelocity(
            float3 currentPosition,
            float3 targetPosition,
            float maximumSpeed,
            float maximumAcceleration,
            float stoppingDistance)
        {
            float3 offset = targetPosition - currentPosition;
            offset = PlanarMath.FlattenY(offset);
            float distance = math.length(offset);
            if (distance <= math.max(0f, stoppingDistance))
            {
                // 锚点进入停止范围后先停下，进度系统还会等待所有成员站稳
                return float3.zero;
            }

            // 用 v²/(2a) 估算制动距离，让锚点在接近目标时平滑减速
            float brakingDistance = maximumSpeed * maximumSpeed /
                                     math.max(2f * maximumAcceleration, 1e-3f);
            float speed = distance <= stoppingDistance + brakingDistance
                ? math.sqrt(math.max(0f, 2f * maximumAcceleration *
                                      (distance - stoppingDistance)))
                : maximumSpeed;
            return PlanarMath.NormalizeXZOrDefault(offset, float3.zero) *
                   math.min(maximumSpeed, speed);
        }

        /// <summary>
        /// 根据队伍锚点的位置和朝向，把阵型内的相对坐标换算成世界坐标
        /// </summary>
        /// <param name="anchorPosition">队伍锚点的世界坐标</param>
        /// <param name="anchorRotation">队伍锚点在水平面上的旋转</param>
        /// <param name="localOffset">槽位局部偏移</param>
        /// <returns>槽位世界位置</returns>
        public static float3 CalculateSlotWorldPosition(
            float3 anchorPosition,
            quaternion anchorRotation,
            float3 localOffset)
        {
            // 所有槽位使用同一个锚点旋转，保证整支队伍朝向一致且阵型不会被扭曲
            return anchorPosition + math.mul(anchorRotation, localOffset);
        }

    }
}
