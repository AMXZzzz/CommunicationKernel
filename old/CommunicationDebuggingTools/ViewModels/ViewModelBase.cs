using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CommunicationDebuggingTools.ViewModels {

    /// <summary>
    /// ViewModel 基类：实现 INotifyPropertyChanged，提供 SetField 辅助。
    /// 所有 ViewModel 继承此类；UI 线程上调用（WPF binding 已处理跨线程属性通知）。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 赋值并触发通知。字段值未变时静默返回 false。
        /// </summary>
        protected bool SetField<T> (ref T field, T value, [CallerMemberName] string name = null) {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged ([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}