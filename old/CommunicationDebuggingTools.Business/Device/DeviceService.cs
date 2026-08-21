using System;
using CommunicationDebuggingTools.Core;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Business.Device {
    public partial class DeviceService : IDeviceService {
        private readonly IProtocolResolver _resolver;
        private readonly IDeviceRepository _repository;
        private readonly ITcpProbe _tcpProbe;
        private readonly IAppLogger _log;

        private readonly ConcurrentDictionary<string, IProtocol> _sessions =
            new ConcurrentDictionary<string, IProtocol>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _connectCts =
            new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, int> _commErrors =
            new ConcurrentDictionary<string, int>();

        private const int COMM_ERROR_THRESHOLD = AppConfig.CommErrorThreshold;

        private readonly SynchronizationContext _uiContext;
        private int _pinging;
        private CancellationTokenSource _pingCts;

        public ObservableCollection<DeviceInfo> Devices { get; private set; }

        public DeviceService (
            IProtocolResolver resolver,
            IDeviceRepository repository,
            ITcpProbe tcpProbe = null,
            IAppLogger logger = null) {
            if (resolver == null) throw new ArgumentNullException("resolver");
            if (repository == null) throw new ArgumentNullException("repository");

            _resolver = resolver;
            _repository = repository;
            _tcpProbe = tcpProbe ?? new TcpProbe();
            _log = logger;
            Devices = new ObservableCollection<DeviceInfo>();
            _uiContext = SynchronizationContext.Current;
        }

        private void LogInfo (string msg) {
            if (_log != null) _log.Info("Device", msg);
        }
        private void LogWarn (string msg) {
            if (_log != null) _log.Warn("Device", msg);
        }
        private void LogError (string msg) {
            if (_log != null) _log.Error("Device", msg);
        }
        private void LogError (string msg, Exception ex) {
            if (_log != null) _log.Error("Device", msg, ex);
        }

        public void Load () {
            CancellationTokenSource ping = Interlocked.Exchange(ref _pingCts, null);
            if (ping != null) {
                try { ping.Cancel(); } catch (Exception ex) { LogWarn("取消旧心跳令牌失败: " + ex.Message); }
                try { ping.Dispose(); } catch (Exception ex) { LogWarn("释放旧心跳令牌失败: " + ex.Message); }
            }

            DisconnectAll();
            IList<DeviceInfo> list = _repository.LoadAll();
            RunOnUi(() => {
                Devices.Clear();
                if (list == null) return;
                foreach (DeviceInfo d in list) {
                    ResetRuntimeState(d);
                    Devices.Add(d);
                }
            });
            LogInfo("已加载设备 " + Devices.Count + " 台");
        }

        /// <summary>
        /// 在 UI 线程执行（更新 DeviceInfo / 触发绑定）。
        /// 构造时已捕获 _uiContext；无上下文时直接执行。
        /// </summary>
        private void RunOnUi (Action action) {
            if (action == null) return;
            if (_uiContext == null || ReferenceEquals(SynchronizationContext.Current, _uiContext)) {
                action();
                return;
            }
            _uiContext.Send(_ => action(), null);
        }

        public void Save () {
            List<DeviceInfo> snapshot = null;
            RunOnUi(() => snapshot = Devices.ToList());
            _repository.SaveAll(snapshot ?? new List<DeviceInfo>());
        }

        public void Add (DeviceInfo device) {
            if (device == null) throw new ArgumentNullException("device");
            if (string.IsNullOrEmpty(device.Id))
                device.Id = Guid.NewGuid().ToString("N");

            RunOnUi(() => {
                if (Devices.Any(d => d.Id == device.Id))
                    throw new InvalidOperationException("设备 Id 已存在: " + device.Id);
                Devices.Add(device);
            });

            Save();
            LogInfo("新增设备: " + device.Name);
        }

        public void Update (DeviceInfo device) {
            if (device == null) throw new ArgumentNullException("device");
            if (string.IsNullOrEmpty(device.Id))
                throw new ArgumentException("Id 不能为空");

            DeviceInfo old = null;
            bool needDisconnect = false;

            RunOnUi(() => {
                old = Devices.FirstOrDefault(d => d.Id == device.Id);
                if (old == null)
                    throw new InvalidOperationException("设备不存在: " + device.Id);
                needDisconnect = old.IsConnected && IsConnectionConfigChanged(old, device);
            });

            if (needDisconnect)
                Disconnect(device.Id);

            RunOnUi(() => CopyDeviceFields(device, old));
            Save();
            LogInfo("更新设备: " + old.Name);
        }

        public void Remove (string id) {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Id 不能为空");

            DeviceInfo d = null;
            RunOnUi(() => d = Devices.FirstOrDefault(x => x.Id == id));
            if (d == null) return;

            string name = d.Name;
            Disconnect(id);
            RunOnUi(() => Devices.Remove(d));
            Save();
            LogInfo("删除设备: " + name);
        }
    }
}