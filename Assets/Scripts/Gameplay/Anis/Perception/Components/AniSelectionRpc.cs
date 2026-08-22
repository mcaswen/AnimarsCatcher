using AnimarsCatcher.Gameplay.Contracts;
using Unity.Collections;
using Unity.NetCode;

namespace AnimarsCatcher.Gameplay
{
    /// <summary>
    /// 客户端发送给服务器的选择集分块
    /// </summary>
    public struct AniSelectionChunkRpc : IRpcCommand
    {
        // 客户端为每次选择更新分配的非零递增版本
        public uint Version;

        // 指明当前 payload 对已发布选择集的更新方式
        public AniSelectionUpdateMode Mode;

        // 当前块在本次提交中的零基位置
        public ushort ChunkIndex;

        // 本次提交预期接收的总块数
        public ushort ChunkCount;

        // 所有块合计携带的成员数量
        public int PayloadMemberCount;

        // 应用更新方式后服务器应发布的成员数量
        public int ResultMemberCount;

        // 客户端对最终有序成员计算的完整性 Hash
        public ulong ResultHash;

        // 当前块的元数据和成员共同生成的防冲突 Hash
        public ulong ChunkHash;

        // 当前块携带的 Ani GhostId，协议上限为 120 个
        public FixedList512Bytes<int> GhostIds;
    }

    /// <summary>
    /// 服务器确认选择集已经完整发布后返回给客户端的版本回执
    /// </summary>
    public struct AniSelectionAckRpc : IRpcCommand
    {
        // 服务器已经完整发布的选择集版本
        public uint Version;

        // 服务器确认的最终选择集 Hash
        public ulong SelectionHash;

        // 服务器确认的最终成员数量
        public int MemberCount;
    }
}
