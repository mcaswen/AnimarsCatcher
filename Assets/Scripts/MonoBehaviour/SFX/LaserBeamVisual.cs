using UnityEngine;

/// <summary>
/// 使用 LineRenderer 显示一次激光射线的起点和终点
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class LaserBeamVisual : MonoBehaviour
{
    private LineRenderer _lineRenderer;

    private void Awake()
    {
        _lineRenderer = GetComponent<LineRenderer>();
    }

    /// <summary>
    /// 设置激光线段的世界空间端点
    /// </summary>
    /// <param name="start">枪口位置</param>
    /// <param name="end">命中位置</param>
    public void Initialize(Vector3 start, Vector3 end)
    {
        if (_lineRenderer == null)
            _lineRenderer = GetComponent<LineRenderer>();

        // LineRenderer 使用两个顶点表达单条激光线段
        _lineRenderer.positionCount = 2;
        _lineRenderer.SetPosition(0, start);
        _lineRenderer.SetPosition(1, end);
    }
}
