
namespace Unity.NetCode.Generators.Utilities
{
    // 从 com.unity.entities 复制的 TypeHash，用于计算 Serializer 与 Variant Hash
    // 当前无法与 Package DLL 共享程序集或建立相应依赖，因此在此保留副本
    internal static class TypeHash
    {
        // 算法来源：http://www.isthe.com/chongo/src/fnv/hash_64a.c
        // 使用以下偏移基数与质数
        const ulong kFNV1A64OffsetBasis = 14695981039346656037;
        const ulong kFNV1A64Prime = 1099511628211;

        public static ulong FNV1A64(string text)
        {
            ulong result = kFNV1A64OffsetBasis;
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var c in text)
                {
                    result = kFNV1A64Prime * (result ^ (byte)(c & 255));
                    result = kFNV1A64Prime * (result ^ (byte)(c >> 8));
                }
            }
            return result;
        }

        public static ulong FNV1A64(int val)
        {
            ulong result = kFNV1A64OffsetBasis;
            unchecked
            {
                result = (((ulong)(val & 0x000000FF) >>  0) ^ result) * kFNV1A64Prime;
                result = (((ulong)(val & 0x0000FF00) >>  8) ^ result) * kFNV1A64Prime;
                result = (((ulong)(val & 0x00FF0000) >> 16) ^ result) * kFNV1A64Prime;
                result = (((ulong)(val & 0xFF000000) >> 24) ^ result) * kFNV1A64Prime;
            }

            return result;
        }

        public static ulong CombineFNV1A64(ulong hash, params ulong[] values)
        {
            foreach (var value in values)
            {
                hash ^= value;
                hash *= kFNV1A64Prime;
            }

            return hash;
        }
    }

}
