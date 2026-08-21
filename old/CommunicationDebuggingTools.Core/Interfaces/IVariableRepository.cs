using System.Collections.Generic;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Core.Interfaces {

    /// <summary>
    /// 变量加载和保存
    /// </summary>
    public interface IVariableRepository {
        IList<VariableItem> LoadAll ();
        void SaveAll (IList<VariableItem> items);
    }
}