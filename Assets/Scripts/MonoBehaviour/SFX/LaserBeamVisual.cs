using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class LaserBeamVisual : MonoBehaviour
{
    private LineRenderer _lineRenderer;

    private void Awake()
    {
        _lineRenderer = GetComponent<LineRenderer>();
    }

    public void Initialize(Vector3 start, Vector3 end)
    {
        if (_lineRenderer == null)
            _lineRenderer = GetComponent<LineRenderer>();

        // 2 点线段：起点在枪口，终点在碰撞点
        _lineRenderer.positionCount = 2;
        _lineRenderer.SetPosition(0, start);
        _lineRenderer.SetPosition(1, end);
    }
}
