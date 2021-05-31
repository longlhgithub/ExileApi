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
using ExileCore.Windows;
using ImGuiNET;
using JM.LinqFaster;
using Serilog;
using SharpDX.Windows;
using Color = SharpDX.Color;

namespace ExileCore
{
    public class Core : IDisposable
    {
        private PerformanceWindow PerformanceWindow { get; }

        private const int TICKS_BEFORE_SLEEP = 4;
        public static object SyncLocker = new object();
        private readonly CoreSettings _coreSettings;
        private readonly DebugWindow _debugWindow;
        private readonly DX11 _dx11;
        private readonly RenderForm _form;
        private readonly MenuWindow _mainMenu;
        private readonly SettingsContainer _settings;
        private readonly SoundController _soundController;
        private Memory _memory;
        private bool _memoryValid = true;
        private double _timeSec;
        private int frameCounter;
        private Rectangle lastClientBound;
        private double lastCounterTime;
        private double NextRender;
        private int _ticks;
        private Stopwatch BackGroundStopwatch { get; } = new Stopwatch();
        private double ForeGroundTime { get; set; }
        public static ILogger Logger { get; set; }
        public static uint FramesCount { get; private set; }
        public ThreadManager ThreadManager { get; } = new ThreadManager();
        public double TargetPcFrameTime { get; private set; }
        public MultiThreadManager MultiThreadManager { get; private set; }
        public PluginManager _pluginManager { get; private set; }
        private IntPtr FormHandle { get; }
        public GameController GameController { get; private set; }
        public bool GameStarted { get; private set; }
        public Graphics Graphics { get; }
        public bool IsForeground => WinApi.IsForegroundWindow(_memory.Process.MainWindowHandle)
                                    || WinApi.IsForegroundWindow(FormHandle)
                                    || _coreSettings.ForceForeground;
        public Core(RenderForm form)
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
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
                PerformanceWindow = new PerformanceWindow(_coreSettings, GameController, ThreadManager);

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
                _coreSettings.TargetFps.OnValueChanged += (sender, i) => { TargetPcFrameTime = 1000f / i; };

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
                }
                else
                {
                    GameController.IsForeGroundCache = IsForeground;
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
                if (_memory == null) return;
                GameController = new GameController(_memory, _soundController, _settings, MultiThreadManager);
                lastClientBound = _form.Bounds;

                using (new PerformanceTimer("Plugin loader"))
                {
                    if (_pluginManager != null) return; 
                    _pluginManager = new PluginManager(
                        GameController, 
                        Graphics, 
                        MultiThreadManager,
                        _settings
                    );
                    Task.Run(() =>
                    {
                        _pluginManager.LoadPlugins();
                        foreach (var plugin in _pluginManager.Plugins)
                        {
                            PerformanceWindow.PluginRenderPerformance.Add(
                                plugin.Name,
                                plugin.RenderDebugInformation
                            );
                        }
                    });

                }
            }
            catch (Exception e)
            {
                DebugWindow.LogError($"Inject -> {e}");
            }
        }

        private static int ChooseSingleProcess(List<(Process, Offsets)> clients)
        {
            var o1 = $"Yes - process #{clients[0].Item1.Id}, started at {clients[0].Item1.StartTime.ToLongTimeString()}";
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

        public void Tick()
        {
            try
            {
                if (!IsForeground)
                {
                    if (!BackGroundStopwatch.IsRunning) BackGroundStopwatch.Restart();
                }
                else
                {
                    BackGroundStopwatch.Reset();
                }
                ForeGroundTime = BackGroundStopwatch.ElapsedMilliseconds;

                if (ForeGroundTime > 100) return;

                Input.Update(FormHandle);
                FramesCount++;

                // Main Control + collect entities
                ThreadManager.AddOrUpdateJob(MainControl());
                var collectEntitiesJob = GameController.EntityListWrapper.CollectEntitiesJob();
                ThreadManager.AddOrUpdateJob(collectEntitiesJob);

                // Start Plugin Tick Jobs
                TickPlugins();

                if (GameController == null || _pluginManager == null || !_pluginManager.AllPluginsLoaded)
                {
                    return;
                }

                AdaptToDynamicFps();

                // GameController Tick
                PerformanceWindow.CorePerformance[PerformanceCoreEnum.GameControllerTick]
                    .TickAction(() => GameController.Tick());

                // Render Main Menu + Debug Window
                PerformanceWindow.CorePerformance[PerformanceCoreEnum.WindowsRender]
                    .TickAction(() =>
                    {
                        _debugWindow.Render();
                        _mainMenu.Render(GameController, _pluginManager?.Plugins, PerformanceWindow);
                    });

                // Render Plugins
                PerformanceWindow.CorePerformance[PerformanceCoreEnum.AllPluginsRender]
                    .TickAction(RenderPlugins);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Core tick -> {ex}");
            }
        }

        private void AdaptToDynamicFps()
        {
            _timeSec += 1000 / (PerformanceWindow.FpsCounter.Tick == 0 ? 60 : PerformanceWindow.FpsCounter.Tick);
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

            //SpinWait.SpinUntil(() => pluginTickJobs.AllF(job => job.IsCompleted), JOB_TIMEOUT_MS);
        }

        private void RenderPlugins()
        {
            if (_pluginManager == null) return;

            foreach (var plugin in _pluginManager?.Plugins)
            {
                if (!plugin.IsEnable) continue;
                //if (!plugin.CanRender) continue;
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

            _dx11.ImGuiRender.InputUpdate(PerformanceWindow.CorePerformance[PerformanceCoreEnum.TotalFrameTime].Tick);
            _dx11.Render(TargetPcFrameTime, this);
            NextRender += TargetPcFrameTime;
            frameCounter++;

            if (Time.TotalMilliseconds - lastCounterTime > 1000)
            {
                PerformanceWindow.FpsCounter.Tick = frameCounter;
                lastCounterTime = Time.TotalMilliseconds;
                frameCounter = 0;
            }

            PerformanceWindow.CorePerformance[PerformanceCoreEnum.TotalFrameTime]
                .Tick = Time.TotalMilliseconds - startTime;
        }
    }
}
