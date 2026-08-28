#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/RelayCommand.cs
// 层级: UI 层 — WPF MVVM 命令实现
// 作用: 轻量 ICommand，供工具栏按钮与卡片操作绑定到 ViewModel 委托。
// -----------------------------------------------------------------------------

using System;
using System.Windows.Input;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// 通用 ICommand 实现，绑定委托到 CanExecute / Execute。
/// RaiseCanExecuteChanged 通知 UI 刷新按钮状态。
/// </summary>
public sealed class RelayCommand : ICommand {

    /// <summary>命令执行体。</summary>
    private readonly Action      _execute;

    /// <summary>启用条件；为 null 表示始终可执行。</summary>
    private readonly Func<bool>  _canExecute;

    /// <param name="execute">命令执行体（支持 async 委托）。</param>
    /// <param name="canExecute">可选启用条件；为 null 时始终启用。</param>
    public RelayCommand(Action execute, Func<bool> canExecute = null) {
        // 执行体必填；CanExecute 可空表示始终可点
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>WPF 查询命令是否可执行。</summary>
    /// <param name="parameter">CommandParameter，本类型忽略它。</param>
    /// <returns>未指定条件时恒为 true，否则询问调用方给的谓词。</returns>
    public bool CanExecute(object parameter) =>
        _canExecute == null || _canExecute();

    /// <summary>执行绑定的无参委托（按钮 Click 入口）。</summary>
    /// <param name="parameter">CommandParameter，本类型忽略它。</param>
    public void Execute(object parameter) => _execute();

    /// <summary>手动通知 WPF 重新查询 CanExecute。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>可执行状态变化通知，由 <see cref="RaiseCanExecuteChanged"/> 手动触发。</summary>
    public event EventHandler CanExecuteChanged;
}

/// <summary>带类型参数的泛型 RelayCommand。</summary>
public sealed class RelayCommand<T> : ICommand {

    /// <summary>命令执行体，参数来自 CommandParameter。</summary>
    private readonly Action<T>    _execute;

    /// <summary>启用条件；为 null 表示始终可执行。</summary>
    private readonly Predicate<T> _canExecute;

    /// <param name="execute">命令执行体。</param>
    /// <param name="canExecute">可选启用条件；为 null 时始终启用。</param>
    public RelayCommand(Action<T> execute, Predicate<T> canExecute = null) {
        // 执行体必填；CanExecute 可空表示始终可点
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>查询命令是否可执行。</summary>
    /// <param name="p">CommandParameter。类型不匹配时一律返回 false。</param>
    /// <returns>未指定条件时恒为 true；否则要求参数类型匹配且谓词为真。</returns>
    public bool CanExecute(object p) =>
        _canExecute == null || (p is T t && _canExecute(t));

    /// <summary>执行绑定的委托。</summary>
    /// <remarks>
    /// 参数类型不匹配时<b>静默跳过</b>：XAML 里 CommandParameter 写错类型是配置问题，
    /// 直接强转会在点击瞬间抛异常崩掉整个界面。
    /// </remarks>
    /// <param name="p">CommandParameter。</param>
    public void Execute(object p) {
        // 仅当参数类型匹配时执行，防止 CommandParameter 类型不对导致崩溃
        if (p is T t)
            _execute(t);
    }

    /// <summary>手动通知 WPF 重新查询 CanExecute。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>可执行状态变化通知，由 <see cref="RaiseCanExecuteChanged"/> 手动触发。</summary>
    public event EventHandler CanExecuteChanged;
}
