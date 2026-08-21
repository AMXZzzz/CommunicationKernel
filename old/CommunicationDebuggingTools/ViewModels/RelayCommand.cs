using System;
using System.Windows.Input;

namespace CommunicationDebuggingTools.ViewModels {

    /// <summary>
    /// 通用 ICommand 实现，绑定委托到 CanExecute / Execute。
    /// RaiseCanExecuteChanged 通知 UI 刷新按钮状态。
    /// </summary>
    public sealed class RelayCommand : ICommand {
        private readonly Action          _execute;
        private readonly Func<bool>      _canExecute;

        public RelayCommand (Action execute, Func<bool> canExecute = null) {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute (object parameter) =>
            _canExecute == null || _canExecute();

        public void Execute (object parameter) => _execute();

        public void RaiseCanExecuteChanged () =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public event EventHandler CanExecuteChanged;
    }

    /// <summary>带参数的泛型 RelayCommand。</summary>
    public sealed class RelayCommand<T> : ICommand {
        private readonly Action<T>       _execute;
        private readonly Predicate<T>    _canExecute;

        public RelayCommand (Action<T> execute, Predicate<T> canExecute = null) {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute (object p) =>
            _canExecute == null || (p is T t && _canExecute(t));

        public void Execute (object p) {
            if (p is T t) _execute(t);
        }

        public void RaiseCanExecuteChanged () =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public event EventHandler CanExecuteChanged;
    }
}