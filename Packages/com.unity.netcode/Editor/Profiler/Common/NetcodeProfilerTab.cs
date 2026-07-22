using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// NetCode Profiler 页签的基类
    /// </summary>
    class NetcodeProfilerTab : Tab
    {
        readonly VisualElement m_MainView;

        internal NetcodeProfilerTab(string label, string uniqueId) : base(label)
        {
            var scrollView = new ScrollView();
            m_MainView = new VisualElement();
            m_MainView.AddToClassList("mainview-container");
            var labelTrimmed = label.Trim().Replace(" ", "");
            m_MainView.viewDataKey = $"{uniqueId}-{labelTrimmed}-MainView";
            scrollView.viewDataKey = $"{uniqueId}-{labelTrimmed}-MainViewScrollView";
            scrollView.Add(m_MainView);
            base.Add(scrollView);
            viewDataKey = $"{uniqueId}-{labelTrimmed}-NetcodeProfilerTab";
        }

        internal new void Add(VisualElement element)
        {
            m_MainView.Add(element);
        }
    }
}
