// -----------------------------------------------------------------------------
// 文件: ViewModels/RelayCommand.cs
// 层级: UI 层 — MVVM 命令实现
// 作用: 提供轻量级 ICommand 实现，供 XAML 按钮绑定使用。
//       RelayCommand: 无参数同步命令。
//       RelayCommand<T>: 带类型参数命令。
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
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object parameter) =>
        _canExecute == null || _canExecute();

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
        _execute    = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object p) =>
        _canExecute == null || (p is T t && _canExecute(t));

    public void Execute(object p) {
        // 仅当参数类型匹配时执行，防止类型不匹配崩溃
        if (p is T t)
            _execute(t);
    }

    /// <summary>手动通知 WPF 重新查询 CanExecute。</summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler CanExecuteChanged;
}
