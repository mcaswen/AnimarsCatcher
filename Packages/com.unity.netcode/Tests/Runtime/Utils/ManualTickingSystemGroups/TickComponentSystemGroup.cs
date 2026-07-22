using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// 所有手动 Tick 系统的基类，提供统一的更新流程以安全处理运行时系统移除
    /// 尤其用于处理创建这些系统的 World 已被销毁的情况
    /// </summary>
    internal abstract partial class TickComponentSystemGroup : ComponentSystemGroup
    {
        struct UpdateGroup
        {
            public World world;
            public ComponentSystemGroup group;
        }
        private List<UpdateGroup> m_UpdateGroups = new List<UpdateGroup>();
        private List<int> m_InvalidUpdateGroups = new List<int>();

        /// <summary>
        /// 将系统组添加到手动更新列表
        /// </summary>
        /// <param name="grp"></param>
        public void AddSystemGroupToTickList(ComponentSystemGroup grp)
        {
            m_UpdateGroups.Add(new UpdateGroup{world = grp.World, group = grp});
            AddSystemToUpdateList(grp);
        }

        /// <summary>
        /// 更新所有子系统组，并将已失效或已销毁的系统组移出更新列表
        /// </summary>
        protected override void OnUpdate()
        {
            for (int i = 0; i < m_UpdateGroups.Count; ++i)
            {
                if (!m_UpdateGroups[i].world.IsCreated)
                    m_InvalidUpdateGroups.Add(i);
            }
            if (m_InvalidUpdateGroups.Count > 0)
            {
                // 按倒序移除，确保先处理较大的索引，避免后续索引因元素前移而失效
                for (int i = m_InvalidUpdateGroups.Count - 1; i >= 0; --i)
                {
                    var idx = m_InvalidUpdateGroups[i];
                    RemoveSystemFromUpdateList(m_UpdateGroups[idx].group);
                    m_UpdateGroups.RemoveAt(idx);
                }
                m_InvalidUpdateGroups.Clear();
            }
            base.OnUpdate();
        }
    }

}
