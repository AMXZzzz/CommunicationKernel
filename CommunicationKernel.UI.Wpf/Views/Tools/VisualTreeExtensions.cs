#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Tools/VisualTreeExtensions.cs
// 层级: UI 层 — WPF 视觉树辅助
// 作用: 沿视觉树/逻辑树向上或向下查找指定类型的祖先或后代。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;



//! 视觉树查找
namespace CommunicationKernel.UI.Wpf.Views.Tools {
    /// <summary>视觉树/逻辑树查找扩展。</summary>
    public static class VisualTreeExtensions {

        // ============================================================================
        // 向上查找
        // ============================================================================

        /// <summary>
        /// 向上查找指定类型的祖先（优先视觉树，其次逻辑树）
        /// </summary>
        public static T FindAncestor<T> (this DependencyObject current) where T : DependencyObject {
            while (current != null) {
                if (current is T result)
                    return result;

                // 优先走视觉树
                DependencyObject parent = VisualTreeHelper.GetParent(current);

                // 视觉树断开时回退到逻辑树
                if (parent == null && current is FrameworkElement fe)
                    parent = fe.Parent;

                current = parent;
            }
            return null;
        }

        // ============================================================================
        // 向下查找
        // ============================================================================

        /// <summary>
        /// 向下深度优先查找第一个指定类型的后代
        /// </summary>
        public static T FindDescendant<T> (this DependencyObject root) where T : DependencyObject {
            if (root == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++) {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is T result)
                    return result;

                result = FindDescendant<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// 查找指定类型的所有后代（深度优先）
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="parent"></param>
        /// <returns></returns>
        public static IEnumerable<T> FindVisualChildren<T> (DependencyObject parent) where T : DependencyObject {
            if (parent == null)
                yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++) {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T;
                if (match != null)
                    yield return match;

                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }
    }
}
