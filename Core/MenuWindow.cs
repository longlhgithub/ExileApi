using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using ExileCore.RenderQ;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.PluginAutoUpdate.Settings;
using ExileCore.Shared.VersionChecker;
using ExileCore.Windows;
using ImGuiNET;
using JM.LinqFaster;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileCore
{
    public class MenuWindow : IDisposable
    {
        public DebugInformation AllPlugins { get; set; }
        public List<DebugInformation> MainDebugs { get; set; } = new List<DebugInformation>();
        public List<DebugInformation> NotMainDebugs { get; set; } = new List<DebugInformation>();
        public List<DebugInformation> PluginsDebug { get; set; } = new List<DebugInformation>();
        private static readonly Stopwatch swStartedProgram = Stopwatch.StartNew();
        private readonly SettingsContainer _settingsContainer;
        private readonly Core Core;
        private int _index = -1;
        private readonly Action CoreSettingsAction = () => { };
        private readonly DebugInformation debugInformation;
        private bool firstTime = true;
        private Action MoreInformation;
        private readonly Action OnWindowChange;
        private Windows openWindow;
        private readonly int PluginNameWidth = 200;
        private Action Selected = () => { };
        private string selectedName = "";
        private readonly Stopwatch sw = Stopwatch.StartNew();
        private readonly Array WindowsName;

        public static bool IsOpened;

        public CoreSettings CoreSettings { get; }
        public Dictionary<string, FontContainer> Fonts { get; }
        public List<ISettingsHolder> CoreSettingsDrawers { get; }

        public PluginsUpdateSettings PluginsUpdateSettings { get; }
        public List<ISettingsHolder> PluginsUpdateSettingsDrawers { get; }
        
        public VersionChecker VersionChecker { get; }

        public MenuWindow(Core core, SettingsContainer settingsContainer, Dictionary<string, FontContainer> fonts, ref VersionChecker versionChecker)
        {
            this.Core = core;
            _settingsContainer = settingsContainer;
            CoreSettings = settingsContainer.CoreSettings;
            Fonts = fonts;
            CoreSettingsDrawers = new List<ISettingsHolder>();
            SettingsParser.Parse(CoreSettings, CoreSettingsDrawers);

            PluginsUpdateSettings = settingsContainer.PluginsUpdateSettings;
            PluginsUpdateSettingsDrawers = new List<ISettingsHolder>();
            SettingsParser.Parse(PluginsUpdateSettings, PluginsUpdateSettingsDrawers);

            VersionChecker = versionChecker;

            CoreSettingsAction = () =>
            {
                foreach (var drawer in CoreSettingsDrawers)
                {
                    drawer.Draw();
                }
            };

            _index = -1;
            Selected = CoreSettingsAction;

            debugInformation = new DebugInformation("DebugWindow", false);
            OpenWindow = Windows.MainDebugs;
            WindowsName = Enum.GetValues(typeof(Windows));

            OnWindowChange += () =>
            {
                MoreInformation = null;
                selectedName = "";
            };

            Input.RegisterKey(CoreSettings.MainMenuKeyToggle);
            CoreSettings.MainMenuKeyToggle.OnValueChanged += () => { Input.RegisterKey(CoreSettings.MainMenuKeyToggle); };

            CoreSettings.Enable.OnValueChanged += (sender, b) =>
            {
                if (!CoreSettings.Enable)
                {
                    try
                    {
                        _settingsContainer.SaveCoreSettings();
                        try
                        {
                            _settingsContainer.SavePluginAutoUpdateSettings();
                        }
                        catch (Exception e)
                        {
                            DebugWindow.LogError($"SaveSettings for PluginAutoUpdate error: {e}");
                        }
                        foreach (var plugin in core._pluginManager.Plugins)
                        {
                            try
                            {
                                _settingsContainer.SaveSettings(plugin.Plugin);
                            }
                            catch (Exception e)
                            {
                                DebugWindow.LogError($"SaveSettings for plugin error: {e}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        DebugWindow.LogError($"SaveSettings error: {e}");
                    }

                }
            };
        }

        private Windows OpenWindow
        {
            get => openWindow;
            set
            {
                if (openWindow != value)
                {
                    openWindow = value;
                    OnWindowChange?.Invoke();
                }
            }
        }

        public void Dispose()
        {
            _settingsContainer.SaveCoreSettings();
        }

        private (string text, Color color) VersionStatus()
        {
            switch (VersionChecker.VersionResult)
            {
                case VersionResult.Loading:
                    return ("Loading...", Color.White);
                case VersionResult.UpToDate:
                    return ("Latest Version " + VersionChecker.LocalVersion?.VersionString, Color.Green);
                case VersionResult.MajorUpdate:
                    return ("Major Update Available", Color.Red);
                case VersionResult.MinorUpdate:
                    return ("Update Available", Color.Red);
                case VersionResult.PatchUpdate:
                    return ("Update Available", Color.Red);
                case VersionResult.Error:
                    return ("Version Not Readable", Color.Orange);                    
            }
            return("Version Not Readable", Color.Yellow);
        }

        public unsafe void Render(
            GameController gameController, 
            List<PluginWrapper> plugins, 
            PerformanceWindow performanceWindow
            )
        {
            plugins = plugins?.OrderBy(x => x.Name).ToList();

            if (CoreSettings.ShowDebugWindow)
            {
                debugInformation.TickAction(performanceWindow.Render);
            }

            if (CoreSettings.MainMenuKeyToggle.PressedOnce())
            {
                CoreSettings.Enable.Value = !CoreSettings.Enable;

                if (CoreSettings.Enable)
                {
                    Core.Graphics.LowLevel.ImGuiRender.TransparentState = false;
                }
                else
                {
                    _settingsContainer.SaveCoreSettings();
                    _settingsContainer.SavePluginAutoUpdateSettings();

                    if (gameController != null)
                    {
                        foreach (var plugin in plugins)
                        {
                            _settingsContainer.SaveSettings(plugin.Plugin);
                        }
                    }

                    Core.Graphics.LowLevel.ImGuiRender.TransparentState = true;
                }
            }


            IsOpened = CoreSettings.Enable;
            if (!CoreSettings.Enable) return;

            ImGui.PushFont(Core.Graphics.Font.Atlas);
            ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
            var pOpen = CoreSettings.Enable.Value;
            ImGui.Begin($"HUD S3ttings {VersionChecker.LocalVersion?.VersionString}", ref pOpen);
            CoreSettings.Enable.Value = pOpen;

            ImGui.BeginChild("Left menu window", new Vector2(PluginNameWidth, ImGui.GetContentRegionAvail().Y), true,
                ImGuiWindowFlags.None);

            if (ImGui.Selectable("Core", _index == -1))
            {
                _index = -1;
                Selected = CoreSettingsAction;
            }

            var (versionText, versionColor) = VersionStatus();
            if (versionText != null)
            {
                ImGui.TextColored(versionColor.ToImguiVec4(), versionText);
            }
            if (VersionChecker.VersionResult.IsUpdateAvailable())
            {
                if (VersionChecker.AutoUpdate.IsReadyToUpdate)
                {
                    if (ImGui.Button("Install Updates"))
                    {
                        VersionChecker.AutoUpdate.LaunchUpdater();
                    }
                }
                else
                {
                    if (!VersionChecker.AutoUpdate.IsDownloading)
                    {
                        if (ImGui.Button("Download"))
                        {
                            VersionChecker.CheckVersionAndPrepareUpdate(true);
                        }
                    }
                    else
                    {
                        ImGui.Text("Downloading...");
                    }
                }
            }

            ImGui.Separator();
            var pluginsWithAvailableUpdateCount = PluginsUpdateSettings?.Plugins.Where(p => p.UpdateAvailable).Count();
            var autoUpdateText = "PluginAutoUpdate";
            if (pluginsWithAvailableUpdateCount > 0)
            {
                autoUpdateText += $" ({pluginsWithAvailableUpdateCount})";
            }
            if (ImGui.Selectable(autoUpdateText, _index == -2))
            {
                _index = -2;
                Selected = () => 
                {
                    PluginsUpdateSettings.Draw();
                };
            }

            var textColor = plugins.Count > 0 ? Color.Green : Color.Red;
            ImGui.TextColored(textColor.ToImguiVec4(), $"{plugins.Count} Plugins Loaded");

            ImGui.Separator();

            if (gameController != null && Core._pluginManager != null)
            {
                for (var index = 0; index < plugins.Count; index++)
                {
                    try
                    {
                        var plugin = plugins[index];
                        var temp = plugin.IsEnable;
                        if (ImGui.Checkbox($"##{plugin.Name}{index}", ref temp)) plugin.TurnOnOffPlugin(temp);
                        ImGui.SameLine();

                        if (ImGui.Selectable(plugin.Name, _index == index))
                        {
                            _index = index;
                            Selected = () => plugin.DrawSettings();
                        }
                    }
                    catch (Exception e) 
                    {
                        DebugWindow.LogError($"Listing Plugins failed!");
                        DebugWindow.LogDebug($"{e.Message}");
                    }
                }
            }

            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("Options", ImGui.GetContentRegionAvail(), true);
            Selected?.Invoke();
            ImGui.EndChild();
            ImGui.End();
            ImGui.PopFont();
        }

        private enum Windows
        {
            MainDebugs,
            NotMainDebugs,
            Plugins,
            Caches
        }
    }
}
