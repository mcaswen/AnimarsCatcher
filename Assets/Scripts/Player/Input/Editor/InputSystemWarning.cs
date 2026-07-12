
using UnityEditor;
using UnityEngine;

namespace Unity.CharacterController.Editor
{
#if !ENABLE_INPUT_SYSTEM
    /// <summary>在编辑器启动时提示项目缺少必需的 Input System</summary>
    [InitializeOnLoad]
    class InputSystemWarning
    {
        static InputSystemWarning()
        {
            Debug.LogWarning("Warning: The Standard Characters use the \"Input System\" package for input handling. Character control input will not work until the \"Input System\" package has been imported.");
        }
    }
#endif
}
