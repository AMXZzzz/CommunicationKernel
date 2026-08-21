using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/// 字符编码类型
namespace CommunicationDebuggingTools.Core.Enums {
    public enum StringEncodingKind {
        Utf8 = 0,
        Ascii = 1,
        DefaultAnsi = 2,
        Utf16Le = 3,
        Utf16Be = 4
    }
}