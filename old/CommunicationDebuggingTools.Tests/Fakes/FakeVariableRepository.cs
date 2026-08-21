using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace CommunicationDebuggingTools.Tests.Fakes {
    /// <summary>内存变量仓储，不落盘。</summary>
    public class FakeVariableRepository : IVariableRepository {
        public List<VariableItem> Items { get; } = new List<VariableItem>();

        public IList<VariableItem> LoadAll () => Items.ToList();

        public void SaveAll (IList<VariableItem> items) {
            Items.Clear();
            if (items != null)
                Items.AddRange(items);
        }
    }
}