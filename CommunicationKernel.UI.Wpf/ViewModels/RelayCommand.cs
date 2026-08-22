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

    private readonly Action      _execute;
    private readonly Func<bool>  _canExecute;

    /// <param name="execute">命令执行体（支持 async 委托）。</param>
    /// <param name="canExecute">可选启用条件；为 null 时始终启用。</param>
    public RelayCommand(Action execute, Func<bool> canExecute = null) {
        // 执行体必填；CanExecute 可空表示始终可点
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    // 未指定条件时始终可执行，否则询问调用方
    public bool CanExecute(object parameter) =>
        _canExecute == null || _canExecute();

    // 执行绑定的无参委托（按钮 Click 入口）
    public void Execute(object parameter) => _execute();

    /// <summary>手动通知 WPF 重新查询 CanExecute。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler CanExecuteChanged;
}

/// <summary>带类型参数的泛型 RelayCommand。</summary>
public sealed class RelayCommand<T> : ICommand {

    private readonly Action<T>    _execute;
    private readonly Predicate<T> _canExecute;

    /// <param name="execute">命令执行体。</param>
    /// <param name="canExecute">可选启用条件；为 null 时始终启用。</param>
    public RelayCommand(Action<T> execute, Predicate<T> canExecute = null) {
        // 执行体必填；CanExecute 可空表示始终可点
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    // 未指定条件，或参数类型匹配且谓词为真时才可执行
    public bool CanExecute(object p) =>
        _canExecute == null || (p is T t && _canExecute(t));

    public void Execute(object p) {
        // 仅当参数类型匹配时执行，防止 CommandParameter 类型不对导致崩溃
        if (p is T t)
            _execute(t);
    }

    /// <summary>手动通知 WPF 重新查询 CanExecute。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler CanExecuteChanged;
}
