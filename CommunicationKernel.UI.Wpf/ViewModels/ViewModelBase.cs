#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/ViewModelBase.cs
// 层级: UI 层 — WPF ViewModel 基类
// 作用: 实现 INotifyPropertyChanged，供所有页面 ViewModel 继承并简化属性通知。
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类。
/// 提供 INotifyPropertyChanged 标准实现和 SetProperty 辅助方法。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged {

    /// <summary>属性变更通知。WPF 数据绑定引擎订阅此事件以侦测变更。</summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 属性 setter 辅助方法：当值确实改变时才触发通知，避免冗余刷新。
    /// </summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">当前后备字段（ref）。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名，由编译器通过 [CallerMemberName] 自动填充。</param>
    /// <returns>true = 值已改变并触发通知；false = 值未改变，无动作。</returns>
    protected bool SetProperty<T>(ref T field, T value,
        [CallerMemberName] string propertyName = null) {
        // 值未变则跳过，避免绑定控件无谓刷新
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        // 更新后备字段
        field = value;

        // 通知 WPF 绑定引擎刷新相关控件
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>
    /// 手动触发指定属性的变更通知（用于计算属性）。
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>SetField 是 SetProperty 的别名，与旧项目命名兼容。</summary>
    protected bool SetField<T>(ref T field, T value,
        [CallerMemberName] string propertyName = null)
        => SetProperty(ref field, value, propertyName);
}
