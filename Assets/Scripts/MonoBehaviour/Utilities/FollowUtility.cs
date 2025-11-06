using UnityEngine;

namespace AnimarsCatcher.Mono.Utilities
{
    public static class FollowUtility
    {
        public static Vector3 RectArrange(Transform target, int index, int columns = 5, float spacingX = 2f, float spacingY = 2f)
        {
            int row = index / columns;
            int col = index % columns;
            Vector3 center = target.position - (row + 1) * spacingY * target.forward;
            return center + (col + 1) / 2 * (col % 2 == 1 ? -1 : 1) * spacingX * target.right;
        }
    }
}