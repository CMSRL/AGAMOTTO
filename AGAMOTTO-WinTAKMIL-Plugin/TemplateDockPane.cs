using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using WinTak.Overlays.Services;
using WinTak.Overlays.ViewModels;
using AGAMOTTO.WinTAKMIL;

namespace AGAMOTTO_WinTAK_Plugin
{
    [WinTak.Framework.Docking.Attributes.DockPane(ID, "AGAMOTTO WinTAK Plugin", Content = typeof(TemplateView))]
    internal class TemplateDockPane : WinTak.Framework.Docking.DockPane
    {
        internal const string ID = "AGAMOTTO_DockPane";

        private readonly IMapObjectItemManager _mapObjectItemManager;
        private readonly Orchestrator _orchestrator;

        private bool _isActive;

        private string _ipAddress = "127.0.0.1";
        private string _port = "65432";

        private int _entityCount;
        private int _routeCount;
        private string _connectionStatusText = "not tested";
        private Brush _connectionStatusBrush = Brushes.Gray;

        public ObservableCollection<string> LogLines { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> AssessmentLines { get; } = new ObservableCollection<string>();

        public ICommand SendMapDataCommand { get; private set; }
        public ICommand TestConnectionCommand { get; private set; }

        private const int ResultsPort = 65433;
        private TcpListener _resultsListener;
        private CancellationTokenSource _resultsCts;

        [ImportingConstructor]
        public TemplateDockPane(IMapObjectItemManager mapObjectItemManager)
        {
            _mapObjectItemManager = mapObjectItemManager;
            _orchestrator = new Orchestrator();

            var sendCmd = new ExecutedCommand();
            sendCmd.Executed += OnSendMapDataCommandExecuted;
            SendMapDataCommand = sendCmd;

            var testCmd = new ExecutedCommand();
            testCmd.Executed += async (_, __) => await TestConnectionAsync();
            TestConnectionCommand = testCmd;

            RefreshCounts(); 
            StartResultsListener();
        }

        public new bool IsActive
        {
            get { return _isActive; }
            set
            {
                SetProperty(ref _isActive, value, nameof(IsActive));
                if (value)
                {
                    RefreshCounts();
                }
                else
                {
                    StopResultsListener();
                }
            }
        }

        public string IpAddress
        {
            get { return _ipAddress; }
            set { SetProperty(ref _ipAddress, value); }
        }

        public string Port
        {
            get { return _port; }
            set { SetProperty(ref _port, value); }
        }

        public int EntityCount
        {
            get { return _entityCount; }
            set { SetProperty(ref _entityCount, value); }
        }

        public int RouteCount
        {
            get { return _routeCount; }
            set { SetProperty(ref _routeCount, value); }
        }

        public string ConnectionStatusText
        {
            get { return _connectionStatusText; }
            set { SetProperty(ref _connectionStatusText, value); }
        }

        public Brush ConnectionStatusBrush
        {
            get { return _connectionStatusBrush; }
            set { SetProperty(ref _connectionStatusBrush, value); }
        }

        // Connection Test
        private async Task TestConnectionAsync()
        {
            ConnectionStatusText = "testing...";
            ConnectionStatusBrush = Brushes.Goldenrod;

            int portParsed;
            if (!int.TryParse(Port, out portParsed))
            {
                ConnectionStatusText = "invalid port";
                ConnectionStatusBrush = Brushes.OrangeRed;
                return;
            }

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(IpAddress, portParsed);
                    var done = await Task.WhenAny(connectTask, Task.Delay(1500));

                    if (done == connectTask && client.Connected)
                    {
                        ConnectionStatusText = "connected";
                        ConnectionStatusBrush = Brushes.LimeGreen;
                        LogLines.Add($"[{DateTime.Now:T}] Connected to {IpAddress}:{portParsed}");
                    }
                    else
                    {
                        ConnectionStatusText = "no response";
                        ConnectionStatusBrush = Brushes.OrangeRed;
                        LogLines.Add($"[{DateTime.Now:T}] No response from {IpAddress}:{portParsed}");
                    }
                }
            }
            catch (Exception ex)
            {
                ConnectionStatusText = "failed";
                ConnectionStatusBrush = Brushes.OrangeRed;
                LogLines.Add($"[{DateTime.Now:T}] Connect failed: {ex.Message}");
            }

            RefreshCounts();
        }

        // Send Map Data
        private void OnSendMapDataCommandExecuted(object sender, EventArgs e)
        {
            RefreshCounts();
            var success = _orchestrator.SendCurrentSituation(_mapObjectItemManager, IpAddress);
            LogLines.Add($"[{DateTime.Now:T}] Send Map Data -> {(success ? "Sent" : "No items")}");
            RefreshCounts();
        }

        // Counts (entities & routes)
        private void RefreshCounts()
        {
            var entities = new HashSet<string>(StringComparer.Ordinal);
            var routes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in EnumerateAllNodes(_mapObjectItemManager?.RootItems))
            {
                var mi = node?.MapItem;
                if (mi == null) continue;
                var uid = mi.Uid ?? "";
                if (uid.Length == 0) continue;

                var t = mi.GetType();
                bool isRoute =
                    (t.FullName ?? "").IndexOf("RouteMapObject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.GetProperty("Route") != null;

                if (isRoute) routes.Add(uid);
                else entities.Add(uid);
            }

            EntityCount = entities.Count;
            RouteCount = routes.Count;
        }

        private static IEnumerable<MapObjectItem> EnumerateAllNodes(ICollection<MapObjectItem> roots)
        {
            if (roots == null) yield break;
            foreach (var n in roots)
            {
                if (n == null) continue;
                yield return n;
                if (n.Children == null) continue;
                foreach (var c in EnumerateAllNodes(n.Children))
                    yield return c;
            }
        }

        // Results listener (Unity/Python to TAK)

        private void StartResultsListener()
        {
            try
            {
                if (_resultsListener != null) return;
                _resultsCts = new CancellationTokenSource();
                _resultsListener = new TcpListener(IPAddress.Loopback, ResultsPort);
                _resultsListener.Start();

                AppendAssessment($"results listener started on 127.0.0.1:{ResultsPort}");

                Task.Run(async () =>
                {
                    var token = _resultsCts.Token;
                    while (!token.IsCancellationRequested)
                    {
                        TcpClient client = null;
                        try
                        {
                            client = await _resultsListener.AcceptTcpClientAsync().ConfigureAwait(false);
                            _ = Task.Run(() => HandleResultClient(client, token), token);
                        }
                        catch (ObjectDisposedException) { /* stopping */ }
                        catch (Exception ex)
                        {
                            AppendAssessment($"listener error: {ex.Message}");
                            await Task.Delay(250, token).ConfigureAwait(false);
                        }
                    }
                }, _resultsCts.Token);
            }
            catch (Exception ex)
            {
                AppendAssessment($"cannot start results listener: {ex.Message}");
            }
        }

        private async Task HandleResultClient(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var lenBuf = new byte[4];
                while (!token.IsCancellationRequested)
                {
                    int r = await stream.ReadAsync(lenBuf, 0, 4, token).ConfigureAwait(false);
                    if (r == 0) break;
                    if (r < 4) throw new System.IO.IOException("short header");

                    int size = BitConverter.ToInt32(lenBuf, 0);
                    if (size <= 0 || size > 10_000_000) throw new System.IO.IOException("bad size");

                    var buf = new byte[size];
                    int got = 0;
                    while (got < size)
                    {
                        int chunk = await stream.ReadAsync(buf, got, size - got, token).ConfigureAwait(false);
                        if (chunk == 0) throw new System.IO.IOException("closed mid-frame");
                        got += chunk;
                    }

                    var payload = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                    AppendAssessment(payload);
                }
            }
        }

        private void StopResultsListener()
        {
            try
            {
                if (_resultsCts != null) _resultsCts.Cancel();
                if (_resultsListener != null) _resultsListener.Stop();
            }
            catch { /* ignore */ }
            finally
            {
                _resultsListener = null;
                _resultsCts = null;
            }
        }

        private void AppendAssessment(string line)
        {
            var text = $"[{DateTime.Now:T}] {line}";
            System.Windows.Application.Current?.Dispatcher?.Invoke(() => AssessmentLines.Add(text));
        }

        internal class ExecutedCommand : ICommand
        {
            public event EventHandler CanExecuteChanged;
            public event EventHandler Executed;

            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter)
            {
                var handler = Executed;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }
    }
}
