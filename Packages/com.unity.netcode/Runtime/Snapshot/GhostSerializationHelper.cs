using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;

namespace Unity.NetCode
{
    namespace LowLevel.Unsafe
    {
        // 内部 Serializer 辅助器，用于集中保存一组序列化相关数据
        [BurstCompile]
        unsafe struct GhostSerializeHelper
        {
            public byte* snapshotPtr;
            public byte* snapshotDynamicPtr;
            public byte* snapshotDynamicHeaderPtr;
            public int snapshotOffset;
            public int dynamicSnapshotDataOffset;
            public int snapshotSize;
            public int dynamicSnapshotCapacity;
            public int changeMaskUints;
            // 常量数据
            [ReadOnly] public DynamicComponentTypeHandle* ghostChunkComponentTypesPtr;
            [ReadOnly] public DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
            [ReadOnly] public DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            public int ghostChunkComponentTypesPtrLen;
            public GhostSerializerState serializerState;

            public enum ClearOption
            {
                Clear = 0,
                DontClear
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidComponentIndex(int compIdx)
            {
                if (compIdx >= ghostChunkComponentTypesPtrLen)
                    throw new InvalidOperationException($"Component index out of range");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidDynamicSnapshotOffset(in GhostComponentSerializer.State serializer, int maskSize, int bufferLen)
            {
                if ((dynamicSnapshotDataOffset + serializer.SnapshotSize * bufferLen) > dynamicSnapshotCapacity)
                    throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidSnapshotOffset(int compSnapshotSize)
            {
                if ((snapshotOffset + compSnapshotSize) > snapshotSize)
                    throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
            }

            private void CopyComponentToSnapshot(ArchetypeChunk chunk, int ent,
                ref DynamicComponentTypeHandle typeHandle,
                in GhostComponentSerializer.State serializer)
            {
                if(!serializer.HasGhostFields) return;
                var compSize = serializer.ComponentSize;
                var compData = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize).GetUnsafeReadOnlyPtr();
                CheckValidSnapshotOffset(serializer.SnapshotSize);
                serializer.CopyToSnapshot.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState),
                    (IntPtr) snapshotPtr, snapshotOffset, snapshotSize, (IntPtr) (compData + ent * compSize), compSize, 1);
            }

            private void CopyBufferToSnapshot(ArchetypeChunk chunk, int ent,
                ref DynamicComponentTypeHandle typeHandle,
                in GhostComponentSerializer.State serializer)
            {
                if(!serializer.HasGhostFields) return;
                var compSize = serializer.ComponentSize;
                var bufData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                // 通过记录指针、偏移量和长度来收集待序列化 Buffer 数据
                var bufferPointer = (IntPtr) bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var bufferLen);
                var snapshotData = (uint*) (snapshotPtr + snapshotOffset);
                snapshotData[0] = (uint) bufferLen;
                snapshotData[1] = (uint) dynamicSnapshotDataOffset;
                // 序列化 Buffer 内容
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(serializer.ChangeMaskBits, bufferLen);
                CheckValidDynamicSnapshotOffset(serializer, maskSize, bufferLen);
                // 在此准备当前 Tick 的数据
                serializer.CopyToSnapshot.Invoke(
                    (IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                    (IntPtr)(snapshotDynamicPtr + maskSize), dynamicSnapshotDataOffset, serializer.SnapshotSize,
                    bufferPointer, compSize, bufferLen);
                dynamicSnapshotDataOffset += GhostComponentSerializer.SnapshotSizeAligned(maskSize + serializer.SnapshotSize * bufferLen);
            }

            [BurstCompile]
            public void CopyEntityToSnapshot(ArchetypeChunk chunk, int ent,
                in GhostCollectionPrefabSerializer typeData, ClearOption option = ClearOption.Clear)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int currentDynamicDataOffset = dynamicSnapshotDataOffset;
                int enableableMaskOffset = 0;
                if (option == ClearOption.Clear)
                {
                    // 清除 ChangeMask 和 EnableMask
                    var bitmaskSize = changeMaskUints + GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);
                    bitmaskSize = GhostComponentSerializer.SnapshotSizeAligned(bitmaskSize * sizeof(uint));
                    UnsafeUtility.MemClear(snapshotPtr+snapshotOffset, bitmaskSize);
                }
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    CheckValidComponentIndex(compIdx);
                    var typeHandle = ghostChunkComponentTypesPtr[compIdx];
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                    if (chunk.Has(ref typeHandle))
                    {
                        if (ghostSerializer.SerializesEnabledBit != 0)
                        {
                            GhostChunkSerializer.UpdateEnableableMasks(chunk, ent, ent + 1, ref typeHandle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                        }
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            CopyBufferToSnapshot(chunk, ent, ref typeHandle, ghostSerializer);
                        }
                        else
                        {
                            CopyComponentToSnapshot(chunk, ent, ref typeHandle, ghostSerializer);
                        }
                    }
                    else if(option == ClearOption.Clear && ghostSerializer.HasGhostFields)
                    {
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            *(uint*)(snapshotPtr + snapshotOffset) = (uint)0;
                            *(uint*)(snapshotPtr + snapshotOffset + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                        }
                        else
                        {
                            for (int i = 0; i < ghostSerializer.SnapshotSize / 4; ++i)
                            {
                                ((uint*) (snapshotPtr + snapshotOffset))[i] = 0;
                            }
                        }
                    }
                    if (ghostSerializer.SerializesEnabledBit != 0)
                    {
                        ++enableableMaskOffset;
                        GhostChunkSerializer.ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                    }
                    snapshotOffset += sizeInSnapshot;
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        var typeHandle = ghostChunkComponentTypesPtr[compIdx];
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                        var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                        if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref typeHandle))
                        {
                            if (ghostSerializer.SerializesEnabledBit != 0)
                            {
                                GhostChunkSerializer.UpdateEnableableMasks(childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk + 1,
                                    ref typeHandle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                            }
                            if (ghostSerializer.ComponentType.IsBuffer)
                            {
                                CopyBufferToSnapshot(childChunk.Chunk, childChunk.IndexInChunk, ref typeHandle, ghostSerializer);
                            }
                            else
                            {
                                CopyComponentToSnapshot(childChunk.Chunk,childChunk.IndexInChunk, ref typeHandle, ghostSerializer);
                            }
                        }
                        else if(option == ClearOption.Clear && ghostSerializer.HasGhostFields)
                        {
                            if (ghostSerializer.ComponentType.IsBuffer)
                            {
                                *(uint*)(snapshotPtr + snapshotOffset) = (uint)0;
                                *(uint*)(snapshotPtr + snapshotOffset + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                            }
                            else
                            {
                                for (int i = 0; i < ghostSerializer.SnapshotSize / 4; ++i)
                                {
                                    ((uint*) (snapshotPtr + snapshotOffset))[i] = 0;
                                }
                            }
                        }
                        if (ghostSerializer.SerializesEnabledBit != 0)
                        {
                            ++enableableMaskOffset;
                            GhostChunkSerializer.ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                        }
                        snapshotOffset += sizeInSnapshot;
                    }
                }
                // 如果已指定要更新的 Header，则更新动态数据总大小
                if(typeData.NumBuffers > 0 && snapshotDynamicHeaderPtr != null)
                    *(uint*)snapshotDynamicHeaderPtr = (uint)(dynamicSnapshotDataOffset - currentDynamicDataOffset);
            }

            [BurstCompile]
            public void CopyChunkToSnapshot(ArchetypeChunk chunk, in GhostCollectionPrefabSerializer typeData)
            {
                // 遍历全部组件并调用序列化方法，将 Snapshot 数据写入并把实体序列化到临时流
                int enableableMaskOffset = 0;
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    CheckValidComponentIndex(compIdx);
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var compSize = ghostSerializer.ComponentSize;
                    // 即使不访问数据，也始终按组件 SnapshotSize 推进偏移量
                    // 否则下一个序列化组件会将数据复制到错误的内存槽位
                    // 部分情况下可能暂时正常，但如果此 Snapshot 后续进入历史并用于插值数据，就可能产生错误结果

                    if (ghostSerializer.SerializesEnabledBit != 0)
                    {
                        var handle = ghostChunkComponentTypesPtr[compIdx];
                        // 无需检查 Chunk 是否具有该组件，因为组件不存在时 chunk.GetEnableableBits 会返回默认值
                        GhostChunkSerializer.UpdateEnableableMasks(chunk, 0, chunk.Count, ref handle, snapshotPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                        ++enableableMaskOffset;
                        GhostChunkSerializer.ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                    }

                    if (!ghostSerializer.HasGhostFields)
                        continue;

                    if (ghostSerializer.ComponentType.IsBuffer)
                    {
                        if (chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var dynamicDataSize = ghostSerializer.SnapshotSize;
                            var bufData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                            {
                                var compData = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var len);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(ghostSerializer.ChangeMaskBits, len);
                                // 设置元素数量及 Buffer 内容在动态数据历史 Buffer 中的偏移量
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize) = (uint)len;
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                ghostSerializer.CopyToSnapshot.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                    (IntPtr)snapshotDynamicPtr, dynamicSnapshotDataOffset + maskSize, dynamicDataSize, (IntPtr)compData, compSize, len);

                                dynamicSnapshotDataOffset += GhostComponentSerializer.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                            }
                        }
                        else
                        {
                            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                            {
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize) = (uint)0;
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                            }
                        }

                        snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);
                    }
                    else
                    {
                        if (chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var compData = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            ghostSerializer.CopyToSnapshot.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState),
                                (IntPtr) snapshotPtr, snapshotOffset, snapshotSize, (IntPtr) compData, compSize, chunk.Count);
                        }
                        else
                        {
                            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                                UnsafeUtility.MemClear(snapshotPtr + snapshotOffset + ent * snapshotSize, ghostSerializer.SnapshotSize);
                        }

                        snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        var compSize = ghostSerializer.ComponentSize;
                        if(ghostSerializer.ComponentType.IsBuffer)
                        {
                            var dynamicDataSize = ghostSerializer.SnapshotSize;
                            var snapshotDataPtr = snapshotPtr;
                            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    if (ghostSerializer.HasGhostFields)
                                    {
                                        var bufData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                        var compData = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.IndexInChunk, out var len);

                                        var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(ghostSerializer.ChangeMaskBits, len);
                                        // 设置元素数量及 Buffer 内容在动态数据历史 Buffer 中的偏移量
                                        *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize) = (uint)len;
                                        *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                        ghostSerializer.CopyToSnapshot.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                            (IntPtr)snapshotDynamicPtr, dynamicSnapshotDataOffset + maskSize, dynamicDataSize, (IntPtr)compData, compSize, len);

                                        dynamicSnapshotDataOffset += GhostComponentSerializer.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                                    }

                                    if (ghostSerializer.SerializesEnabledBit != 0)
                                    {
                                        var handle = ghostChunkComponentTypesPtr[compIdx];
                                        GhostChunkSerializer.UpdateEnableableMasks(childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk+1,
                                            ref handle, snapshotDataPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                    }
                                }
                                else if (ghostSerializer.HasGhostFields)
                                {
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize) = (uint)0;
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotSize + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                }
                                snapshotDataPtr += snapshotSize;
                            }
                            if (ghostSerializer.HasGhostFields)
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(GhostComponentSerializer.DynamicBufferComponentSnapshotSize);

                            if (ghostSerializer.SerializesEnabledBit != 0)
                            {
                                ++enableableMaskOffset;
                                GhostChunkSerializer.ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                            }
                        }
                        else
                        {
                            var snapshotDataPtr = snapshotPtr;
                            for (int ent = 0, chunkEntityCount = chunk.Count; ent < chunkEntityCount; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                // 此处可以跳过，因为内存 Buffer 偏移量根据实体起止索引计算
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    if (ghostSerializer.HasGhostFields)
                                    {
                                        var compData = (byte*) childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                        compData += childChunk.IndexInChunk * compSize;

                                        // TODO：评估批处理是否更快
                                        ghostSerializer.CopyToSnapshot.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState),
                                            (IntPtr) snapshotPtr + ent * snapshotSize, snapshotOffset, snapshotSize, (IntPtr) compData, compSize, 1);
                                    }

                                    if (ghostSerializer.SerializesEnabledBit != 0)
                                    {
                                        var handle = ghostChunkComponentTypesPtr[compIdx];
                                        GhostChunkSerializer.UpdateEnableableMasks(childChunk.Chunk, childChunk.IndexInChunk, childChunk.IndexInChunk+1,
                                            ref handle, snapshotDataPtr, changeMaskUints, enableableMaskOffset, snapshotSize);
                                    }
                                }
                                else if (ghostSerializer.HasGhostFields)
                                {
                                    UnsafeUtility.MemClear(snapshotPtr + snapshotOffset + ent*snapshotSize, ghostSerializer.SnapshotSize);
                                }
                                snapshotDataPtr += snapshotSize;
                            }
                            if (ghostSerializer.HasGhostFields)
                                snapshotOffset += GhostComponentSerializer.SnapshotSizeAligned(ghostSerializer.SnapshotSize);
                            if (ghostSerializer.SerializesEnabledBit != 0)
                            {
                                ++enableableMaskOffset;
                                GhostChunkSerializer.ValidateWrittenEnableBits(enableableMaskOffset, typeData.EnableableBits);
                            }
                        }
                    }
                }
                GhostChunkSerializer.ValidateAllEnableBitsHasBeenWritten(enableableMaskOffset, typeData.EnableableBits);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GatherBufferSize(ArchetypeChunk chunk, int startIndex, int endIndex, GhostCollectionPrefabSerializer typeData)
            {
                var emptyArray = new NativeArray<int>();
                return GatherBufferSize(chunk, startIndex, endIndex, typeData, ref emptyArray);
            }

            [BurstCompile]
            public int GatherBufferSize(ArchetypeChunk chunk, int startIndex, int endIndex, GhostCollectionPrefabSerializer typeData, ref NativeArray<int> buffersSize)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int totalSize = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    if (!ghostSerializer.HasGhostFields || !ghostSerializer.ComponentType.IsBuffer || !chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                        continue;

                    for (int ent = startIndex; ent < endIndex; ++ent)
                    {
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var bufferLen = bufferAccessor.GetBufferLength(ent);
                        var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(ghostSerializer.ChangeMaskBits, bufferLen);
                        var size = GhostComponentSerializer.SnapshotSizeAligned(maskSize + bufferLen * ghostSerializer.SnapshotSize);
                        if(buffersSize.IsCreated)
                            buffersSize[ent] += size;
                        totalSize += size;
                    }
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(ref linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                        if (!ghostSerializer.HasGhostFields || !ghostSerializer.ComponentType.IsBuffer)
                            continue;

                        for (int ent = startIndex; ent < endIndex; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                            if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufferAccessor = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                var bufferLen = bufferAccessor.GetBufferLength(childChunk.IndexInChunk);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(ghostSerializer.ChangeMaskBits, bufferLen);
                                var size = GhostComponentSerializer.SnapshotSizeAligned(maskSize + bufferLen * ghostSerializer.SnapshotSize);
                                if(buffersSize.IsCreated)
                                    buffersSize[ent] += size;
                                totalSize += size;
                            }
                        }
                    }
                }
                return totalSize;
            }
        }
    }
}
