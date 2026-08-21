using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;

namespace CommunicationDebuggingTools.Business.Plugins {

    /// <summary>
    /// 协议插件解析器：扫描 Plugin.*.dll，通过反射读取 ProtocolNameAttribute 完成注册。
    /// <para>
    /// 单个插件加载失败只记 Warn，不中断其余插件。
    /// </para>
    /// </summary>
    public class ProtocolResolver : IProtocolResolver {

        private readonly Dictionary<string, Type> _map =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        private readonly IAppLogger _log;

        /// <param name="log">可选；为 null 时失败仍吞掉但不写日志（兼容单测）。</param>
        public ProtocolResolver (IAppLogger log = null) {
            _log = log;
        }

        public void LoadFromFolder (string folder) {
            _map.Clear();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
                if (_log != null)
                    _log.Warn("Protocol", "插件目录不存在: " + (folder ?? ""));
                return;
            }

            string[] files = Directory.GetFiles(folder, "Plugin.*.dll");
            if (files.Length == 0) {
                if (_log != null)
                    _log.Warn("Protocol", "目录下无 Plugin.*.dll: " + folder);
                return;
            }

            foreach (string file in files) {
                try {
                    LoadAssembly(file);
                } catch (Exception ex) {
                    // 业务路径：必须可观测，不能空 catch
                    if (_log != null)
                        _log.Warn("Protocol", "加载插件失败: " + Path.GetFileName(file) + " — " + ex.Message);
                }
            }
        }

        public IProtocol Resolve (string protocolName) {
            if (string.IsNullOrWhiteSpace(protocolName)) return null;
            Type type;
            if (!_map.TryGetValue(protocolName.Trim(), out type)) return null;
            return Activator.CreateInstance(type) as IProtocol;
        }

        public IList<string> GetProtocolNames () =>
            new List<string>(_map.Keys);

        private void LoadAssembly (string dllPath) {
            Assembly asm = Assembly.LoadFrom(dllPath);
            Type[] types;
            try {
                types = asm.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                types = ex.Types;
                if (_log != null && ex.LoaderExceptions != null) {
                    foreach (Exception le in ex.LoaderExceptions) {
                        if (le != null)
                            _log.Warn("Protocol", Path.GetFileName(dllPath) + " 类型加载: " + le.Message);
                    }
                }
            }
            if (types == null) return;

            Type protocolInterface = typeof(IProtocol);
            Type attributeType = typeof(ProtocolNameAttribute);

            int registered = 0;
            foreach (Type t in types) {
                if (t == null || t.IsInterface || t.IsAbstract) continue;
                if (!protocolInterface.IsAssignableFrom(t)) continue;

                ProtocolNameAttribute attr =
                    (ProtocolNameAttribute)Attribute.GetCustomAttribute(t, attributeType);

                if (attr == null || string.IsNullOrWhiteSpace(attr.Name)) continue;

                _map[attr.Name] = t;
                registered++;
            }

            if (_log != null)
                _log.Info("Protocol", "已扫描 " + Path.GetFileName(dllPath) + "，注册 " + registered + " 个协议");
        }
    }
}
