using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory;
using ExileCore.RenderQ;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.VersionChecker;
using ExileCore.Threads;
using ImGuiNET;
using JM.LinqFaster;
using Serilog;
using SharpDX.Windows;
using Color = SharpDX.Color;

namespace ExileCore
{
    public class Core : IDisposable
    {
        private readonly DebugInformation _coreDebugInformation = new DebugInformation("Core");
        private readonly DebugInformation _menuDebugInformation = new DebugInformation("Menu+Debug");
        private readonly DebugInformation _pluginTickDebugInformation = new DebugInformation("All Plugins Tick");
        private readonly DebugInformation _pluginRenderDebugInformation = new DebugInformation("All Plugins Render");
        private readonly DebugInformation _gameControllerTickDebugInformation = new DebugInformation("GameController Tick");
        private readonly DebugInformation _fpsCounterDebugInformation = new DebugInformation("Fps counter", false);
        private readonly DebugInformation _deltaTimeDebugInformation = new DebugInformation("Delta Time", false);
        private readonly DebugInformation _totalDebugInformation = new DebugInformation("Total Frame Time", "Total waste time");

        private const int JOB_TIMEOUT_MS = 1000 / 5;
        private const int TICKS_BEFORE_SLEEP = 4;
        public static object SyncLocker = new object();
        private readonly CoreSettings _coreSettings;
        private readonly DebugWindow _debugWindow;
        private readonly DX11 _dx11;
        private readonly RenderForm _form;
        private readonly MenuWindow _mainMenu;
        private readonly SettingsContainer _settings;
        private readonly SoundController _soundController;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _elTime = 1000 / 20f;
        private double _endParallelCoroutineTimer;
        private Memory _memory;
        private bool _memoryValid = true;
        private float _minimalFpsTime;
        private double _startParallelCoroutineTimer;
        private double _targetParallelFpsTime;
        private double _tickEnd;
        private double _tickStart;
        private double _timeSec;
        private double ForeGroundTime;
        private int frameCounter;
        private Rectangle lastClientBound;
        private double lastCounterTime;
        private double NextCoroutineTime;
        private double NextRender;
        private int _ticks;
        private double _targetPcFrameTime;
        private double _deltaTargetPcFrameTime;
        public Core(RenderForm form)
        {
            try
            {
                form.Load += (sender, args) =>
                {
                    var f = (RenderForm) sender;
                    WinApi.EnableTransparent(f.Handle);
                    WinApi.SetTransparent(f.Handle);
                };
                _form = form;
                FormHandle = _form.Handle;
                _settings = new SettingsContainer();
                _coreSettings = _settings.CoreSettings;
                _coreSettings.Threads = new RangeNode<int>(_coreSettings.Threads.Value, 0, Environment.ProcessorCount);

                VersionChecker versionChecker;
                using (new PerformanceTimer("Check version"))
                {
                    versionChecker = new VersionChecker();
                    // check every ~100s for an update
                    Task.Run(() =>
                    {
                        while (true)
                        {
                            if (versionChecker.AutoUpdate.IsDownloading)
                            {
                                DebugWindow.LogMsg("Core -> Currently downloading update, dont check again..."); 
                                Thread.Sleep(10 * 1000);
                                continue;
                            }
                            DebugWindow.LogDebug("Core -> Checking for update...");
                            versionChecker.CheckVersionAndPrepareUpdate(_coreSettings.AutoPrepareUpdate);
                            Thread.Sleep(100 * 1000);
                        }
                    });
                }                                    
                DebugWindow.LogMsg($"Core -> Windows 10 is the only supported system!");

                using (new PerformanceTimer("DX11 Load"))
                {
                    _dx11 = new DX11(form, _coreSettings);
                }

                try
                {
                    _soundController = new SoundController("Sounds");
                    _coreSettings.Volume.OnValueChanged += (sender, i) => { _soundController.SetVolume(i / 100f); };
                }
                catch (Exception e)
                {
                    DebugWindow.LogError($"Core -> Loading SoundController failed.");
                    DebugWindow.LogError($"Core -> {e}");
                }

                _coreSettings.VSync.OnValueChanged += (obj, b) => { _dx11.VSync = _coreSettings.VSync.Value; };
                Graphics = new Graphics(_dx11, _coreSettings);

                _mainMenu = new MenuWindow(this, _settings, _dx11.ImGuiRender.fonts, ref versionChecker);
                _debugWindow = new DebugWindow(Graphics, _coreSettings);

                MultiThreadManager = new MultiThreadManager(_coreSettings.Threads);

                TargetPcFrameTime = 1000f / _coreSettings.TargetFps;
                _targetParallelFpsTime = 1000f / _coreSettings.TargetParallelFPS;
                _coreSettings.TargetFps.OnValueChanged += (sender, i) => { TargetPcFrameTime = 1000f / i; };
                _coreSettings.TargetParallelFPS.OnValueChanged += (sender, i) => { _targetParallelFpsTime = 1000f / i; };

                _coreSettings.DynamicFPS.OnValueChanged += (sender, b) =>
                {
                    if (!b) TargetPcFrameTime = 1000f / _coreSettings.TargetFps;
                };

                if (_memory == null) _memory = FindPoe();

                if (GameController == null && _memory != null) Inject();

                NextRender = Time.TotalMilliseconds;
                if (_pluginManager?.Plugins.Count == 0)
                {
                    _coreSettings.Enable.Value = true;
                }

                Graphics.InitImage("missing_texture.png");
            }
            catch (Exception e)
            {
                DebugWindow.LogMsg($"Core constructor -> {e}");
                MessageBox.Show($"Error in Core constructor -> {e}", "Oops... Program fail to launch");
            }
        }

        public static ILogger Logger { get; set; }
        public static uint FramesCount { get; private set; }

        public double TargetPcFrameTime
        {
            get => _targetPcFrameTime;
            private set
            {
                _targetPcFrameTime = value;
                _deltaTargetPcFrameTime =  value / 1000f;
            }
        }

        public MultiThreadManager MultiThreadManager { get; private set; }
        public static ObservableCollection<DebugInformation> DebugInformations { get; } =
            new ObservableCollection<DebugInformation>();
        public PluginManager _pluginManager { get; private set; }
        private IntPtr FormHandle { get; }
        public GameController GameController { get; private set; }
        public bool GameStarted { get; private set; }
        public Graphics Graphics { get; }
        public bool IsForeground { get; private set; }

        public void Dispose()
        {
            _memory?.Dispose();
            _mainMenu?.Dispose();
            GameController?.Dispose();
            _dx11?.Dispose();
            _pluginManager?.CloseAllPlugins();
        }

        private Job MainControl()
        {
            return new Job("MainControl", () =>
            {
                if (_memory == null)
                {
                    _memory = FindPoe();
                    if (_memory == null) return;
                }

                if (GameController == null && _memory != null)
                {
                    Inject();
                }

                if (GameController == null) return;

                var clientRectangle = WinApi.GetClientRectangle(_memory.Process.MainWindowHandle);

                if (lastClientBound != clientRectangle && _form.Bounds != clientRectangle &&
                    clientRectangle.Width > 2 &&
                    clientRectangle.Height > 2)
                {
                    DebugWindow.LogMsg($"Resize from: {lastClientBound} to {clientRectangle}", 5, Color.Magenta);
                    lastClientBound = clientRectangle;
                    _form.Invoke(new Action(() => { _form.Bounds = clientRectangle; }));
                }

                _memoryValid = !_memory.IsInvalid();

                if (!_memoryValid)
                {
                    GameController.Dispose();
                    GameController = null;
                    _memory = null;
                    _dx11.ImGuiRender.LostFocus -= LostFocus;
                }
                else
                {
                    var isForegroundWindow = WinApi.IsForegroundWindow(_memory.Process.MainWindowHandle) ||
                                             WinApi.IsForegroundWindow(FormHandle) || _coreSettings.ForceForeground;

                    IsForeground = isForegroundWindow;
                    GameController.IsForeGroundCache = isForegroundWindow;
                }
            },
            1000,
            500);
        }

        public static Memory FindPoe()
        {
            var pid = FindPoeProcess();

            if (!pid.HasValue || pid.Value.process.Id == 0)
                DebugWindow.LogMsg("Game not found");
            else
                return new Memory(pid.Value);

            return null;
        }

        private void Inject()
        {
            try
            {
                if (_memory != null)
                {
                    _dx11.ImGuiRender.LostFocus += LostFocus;
                    GameController = new GameController(_memory, _soundController, _settings, MultiThreadManager);
                    lastClientBound = _form.Bounds;

                    using (new PerformanceTimer("Plugin loader"))
                    {
                        _pluginManager = new PluginManager(
                            GameController, 
                            Graphics, 
                            MultiThreadManager,
                            _settings
                        );
                    }
                }
            }
            catch (Exception e)
            {
                DebugWindow.LogError($"Inject -> {e}");
            }
        }

        private static int ChooseSingleProcess(List<(Process, Offsets)> clients)
        {
            var o1 =
                $"Yes - process #{clients[0].Item1.Id}, started at {clients[0].Item1.StartTime.ToLongTimeString()}";

            var o2 = $"No - process #{clients[1].Item1.Id}, started at {clients[1].Item1.StartTime.ToLongTimeString()}";
            const string o3 = "Cancel - quit this application";

            var answer = MessageBox.Show(null, string.Join(Environment.NewLine, o1, o2, o3),
                "Choose a PoE instance to attach to",
                MessageBoxButtons.YesNoCancel);

            return answer == DialogResult.Cancel ? -1 : answer == DialogResult.Yes ? 0 : 1;
        }

        private static (Process process, Offsets offsets)? FindPoeProcess()
        {
            var clients = Process.GetProcessesByName(Offsets.Regular.ExeName).Select(x => (x, Offsets.Regular))
                .ToList();

            clients.AddRange(Process.GetProcessesByName(Offsets.Korean.ExeName).Select(p => (p, Offsets.Korean)));
            var ixChosen = clients.Count > 1 ? ChooseSingleProcess(clients) : 0;

            if (clients.Count > 0)
                return clients[ixChosen];

            return null;
        }

        private void LostFocus(object sender, EventArgs eventArgs)
        {
            if (!WinApi.IsIconic(_memory.Process.MainWindowHandle))
                WinApi.SetForegroundWindow(_memory.Process.MainWindowHandle);
        }

        public ThreadManager ThreadManager { get; } = new ThreadManager();

        public void Tick()
        {
            try
            {
                var tickStartCore = _sw.Elapsed.TotalMilliseconds;
                Input.Update(FormHandle);
                FramesCount++;

                ForeGroundTime = IsForeground ? 0 : ForeGroundTime + _deltaTargetPcFrameTime;
                if (ForeGroundTime > 100) return;

                // Main Control + collect entities
                ThreadManager.AddOrUpdateJob(MainControl());
                var collectEntitiesJob = GameController.EntityListWrapper.CollectEntitiesJob();
                ThreadManager.AddOrUpdateJob(collectEntitiesJob);

                // Start Plugin Tick Jobs
                var tempStartTick = _sw.Elapsed.TotalMilliseconds;
                TickPlugins();
                _pluginTickDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tempStartTick;

                if (GameController == null || _pluginManager == null || !_pluginManager.AllPluginsLoaded)
                {
                    _coreDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tickStartCore;
                    return;
                }

                AdaptToDynamicFps();

                // GameController Tick
                tempStartTick = _sw.Elapsed.TotalMilliseconds;
                GameController.Tick();
                _gameControllerTickDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tempStartTick;

                // Render Main Menu + Debug Window
                tempStartTick = _sw.Elapsed.TotalMilliseconds;
                _debugWindow.Render();
                _mainMenu.Render(GameController, _pluginManager?.Plugins);
                _menuDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tempStartTick;

                // Render Plugins
                tempStartTick = _sw.Elapsed.TotalMilliseconds;
                RenderPlugins();
                _pluginRenderDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tempStartTick;

                _coreDebugInformation.Tick = _sw.Elapsed.TotalMilliseconds - tickStartCore;
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Core tick -> {ex}");
            }
        }

        private void AdaptToDynamicFps()
        {
            _timeSec += GameController.DeltaTime;
            if (!(_timeSec >= 1000)) return;
            _timeSec = 0;
         
            if (!_coreSettings.DynamicFPS) return;

            var fpsArray = GameController.IngameState.FPSRectangle.DiagnosticArrayValues;
            var fps = fpsArray.SkipF((int)(fpsArray.Length * 0.75f)).AverageF() * (_coreSettings.DynamicPercent / 100f);
            var dynamicFps = 1000f / fps;

            TargetPcFrameTime = Math.Min(1000f / _coreSettings.MinimalFpsForDynamic, dynamicFps);
        }

        private void TickPlugins()
        {
            if (_pluginManager == null) return;

            ThreadManager.AbortLongRunningThreads();
            var pluginTickJobs = new List<Job>();

            foreach (var plugin in _pluginManager?.Plugins)
            {
                if (!plugin.IsEnable) continue;
                if (!GameController.InGame && !plugin.Force) continue;
                plugin.CanRender = true;

                var job = (_coreSettings.CollectDebugInformation?.Value == true)
                    ? plugin.PerfomanceTick()
                    : plugin.Tick();

                if (job == null) continue;
                pluginTickJobs.Add(job);
                ThreadManager.AddOrUpdateJob($"Plugin_Tick_{plugin.Name}", job);
            }

            SpinWait.SpinUntil(() => pluginTickJobs.AllF(job => job.IsCompleted), JOB_TIMEOUT_MS);
        }

        private void RenderPlugins()
        {
            if (_pluginManager == null) return;

            foreach (var plugin in _pluginManager?.Plugins)
            {
                if (!plugin.IsEnable) continue;
                if (!plugin.CanRender) continue;
                if (!GameController.InGame && !plugin.Force) continue;

                if (_coreSettings.CollectDebugInformation?.Value == true)
                {
                    plugin.PerfomanceRender();
                }
                else
                {
                    plugin.Render();
                }
            }
        }

        public void Render()
        {
            var startTime = Time.TotalMilliseconds;
            _ticks++;

            if (NextRender > Time.TotalMilliseconds)
            {
                if (_ticks >= TICKS_BEFORE_SLEEP)
                {
                    Thread.Sleep(1);
                    _ticks = 0;
                }
                return;
            }

            _dx11.ImGuiRender.InputUpdate(_totalDebugInformation.Tick*_deltaTargetPcFrameTime);
            _dx11.Render(TargetPcFrameTime, this);
            NextRender += TargetPcFrameTime;
            frameCounter++;

            if (Time.TotalMilliseconds - lastCounterTime > 1000)
            {
                _fpsCounterDebugInformation.Tick = frameCounter;
                _deltaTimeDebugInformation.Tick = 1000f / frameCounter;
                lastCounterTime = Time.TotalMilliseconds;
                frameCounter = 0;
            }

            _totalDebugInformation.Tick = Time.TotalMilliseconds - startTime;
        }
    }
}
