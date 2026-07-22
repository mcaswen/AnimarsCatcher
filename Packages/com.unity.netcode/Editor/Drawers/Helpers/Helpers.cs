using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.NetCode.Samples.Common
{
    [BurstCompile]
    internal static class DrawerHelpers
    {
        static ProfilerMarker s_SetMeshesMarker = new(nameof(s_SetMeshesMarker));

        internal struct Vertex
        {
            public float3 Pos;
            public Color Color;
        }

        static readonly VertexAttributeDescriptor[] k_VertexAttributeDescriptors =
        {
            new(VertexAttribute.Position),
            new(VertexAttribute.Color, VertexAttributeFormat.Float32, 4)
        };

        [BurstCompile]
        public static void DrawLine(in float3 a, in float3 b, ref NativeList<Vertex> verts, ref NativeList<int> indices, in Color color = default)
        {
            var length = verts.Length;
            indices.Add(length);
            indices.Add(length + 1);
            verts.Add(new Vertex { Pos = a, Color = color });
            verts.Add(new Vertex { Pos = b, Color = color });
        }

        [BurstCompile]
        public static unsafe void DrawWireCube(in float3 min, in float3 max, ref NativeList<Vertex> verts, ref NativeList<int> indices, in LocalToWorld l2w, in Color color = default)
        {
            var i = verts.Length;

            var newVertices = stackalloc Vertex[8];
            newVertices[0] = new Vertex { Pos = TransformLocalToWorld(new float3(min.x, min.y, min.z), l2w), Color = color };
            newVertices[1] = new Vertex { Pos = TransformLocalToWorld(new float3(min.x, max.y, min.z), l2w), Color = color };
            newVertices[2] = new Vertex { Pos = TransformLocalToWorld(new float3(min.x, min.y, max.z), l2w), Color = color };
            newVertices[3] = new Vertex { Pos = TransformLocalToWorld(new float3(min.x, max.y, max.z), l2w), Color = color };
            newVertices[4] = new Vertex { Pos = TransformLocalToWorld(new float3(max.x, min.y, min.z), l2w), Color = color };
            newVertices[5] = new Vertex { Pos = TransformLocalToWorld(new float3(max.x, min.y, max.z), l2w), Color = color };
            newVertices[6] = new Vertex { Pos = TransformLocalToWorld(new float3(max.x, max.y, min.z), l2w), Color = color };
            newVertices[7] = new Vertex { Pos = TransformLocalToWorld(new float3(max.x, max.y, max.z), l2w), Color = color };
            verts.AddRange(newVertices, 8);

            var newIndices = stackalloc int[24];
            int* indexPairs = stackalloc int[24]
            {
                0, 4, 1, 6, 2, 5, 3, 7,
                0, 1, 4, 6, 5, 7, 5, 7,
                0, 2, 4, 5, 1, 3, 6, 7
            };
            for (int j = 0; j < 24; ++j)
            {
                newIndices[j] = i + indexPairs[j];
            }

            indices.AddRange(newIndices, 24);
        }

        public static void UpdateMesh(ref Mesh mesh, ref NativeList<Vertex> verts, ref NativeList<int> indices)
        {
            const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds |
                                          MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
            using (s_SetMeshesMarker.Auto())
            {
                if (mesh.vertexCount < verts.Length)
                    mesh.SetVertexBufferParams(RoundTo(verts.Length, 2048), k_VertexAttributeDescriptors);
                mesh.SetVertexBufferData<Vertex>(verts.AsArray(), 0, 0, verts.Length, 0, flags);

                if (mesh.GetIndexCount(0) < indices.Length)
                    mesh.SetIndexBufferParams(RoundTo(indices.Length, 8192), IndexFormat.UInt32);
                mesh.SetIndexBufferData<int>(indices.AsArray(), 0, 0, indices.Length, flags);

                var smd = new SubMeshDescriptor
                {
                    topology = MeshTopology.Lines,
                    vertexCount = verts.Length,
                    indexCount = indices.Length,
                };
                mesh.SetSubMesh(0, smd, flags);

                mesh.UploadMeshData(false);
            }
        }

        /// <summary>
        /// 以指定的 2 次幂倍数为步长，向上取整到下一个倍数值
        /// </summary>
        /// <remarks>这种线性增长方式优于 math.ceilpow2 等指数增长方式，后者会过量分配并拖慢 Mesh.SetVertexBufferData</remarks>
        public static int RoundTo(int value, int roundToWithPow2) => (value + roundToWithPow2 - 1) & ~(roundToWithPow2 - 1);

        [BurstCompile]
        public static unsafe void DrawWireSquare(in float3 center, float size, ref NativeList<int> indices, ref NativeList<Vertex> verts, bool xyPlane, in Color color)
        {
            var i = verts.Length;
            float half = size * 0.5f;

            var corners = stackalloc float3[4];
            if (xyPlane)
            {
                corners[0] = center + new float3(-half, -half, 0);
                corners[1] = center + new float3(-half, half, 0);
                corners[2] = center + new float3(half, half, 0);
                corners[3] = center + new float3(half, -half, 0);
            }
            else
            {
                corners[0] = center + new float3(-half, 0, -half);
                corners[1] = center + new float3(-half, 0, half);
                corners[2] = center + new float3(half, 0, half);
                corners[3] = center + new float3(half, 0, -half);
            }

            for (int j = 0; j < 4; ++j)
            {
                float3 p0 = corners[j];
                float3 p1 = corners[(j + 1) % 4];
                verts.Add(new Vertex { Pos = p0, Color = color });
                verts.Add(new Vertex { Pos = p1, Color = color });
                indices.Add(i + j * 2);
                indices.Add(i + j * 2 + 1);
            }
        }

        [BurstCompile]
        public static void DrawWireCross(in float3 min, in float3 max, ref NativeList<Vertex> verts,
            ref NativeList<int> indices, in LocalToWorld localToWorld, in Color color = default)
        {
            float3 center = (min + max) * 0.5f;
            // X 轴
            DrawLine(
                TransformLocalToWorld(new float3(min.x, center.y, center.z), localToWorld),
                TransformLocalToWorld(new float3(max.x, center.y, center.z), localToWorld),
                ref verts, ref indices, color);
            // Y 轴
            DrawLine(
                TransformLocalToWorld(new float3(center.x, min.y, center.z), localToWorld),
                TransformLocalToWorld(new float3(center.x, max.y, center.z), localToWorld),
                ref verts, ref indices, color);
            // Z 轴
            DrawLine(
                TransformLocalToWorld(new float3(center.x, center.y, min.z), localToWorld),
                TransformLocalToWorld(new float3(center.x, center.y, max.z), localToWorld),
                ref verts, ref indices, color);
        }

        private static float3 TransformLocalToWorld(in float3 p, in LocalToWorld localTolWorld)
        {
            return math.mul(localTolWorld.Value, new float4(p, 1)).xyz;
        }

        public static Mesh CreateMesh(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.HideAndDontSave,

                // 避免持续重算调试 Drawer 的 Bounds，因此设置为足够大的固定值
                bounds = new Bounds(new float3(0), new float3(100_000_000)),
            };
            mesh.MarkDynamic();
            return mesh;
        }

        public static void ConfigureMeshRenderer(ref MeshRenderer meshRenderer)
        {
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }
    }
}
