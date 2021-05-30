using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using ExileCore.Threads;
using ImGuiNET;
using JM.LinqFaster;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace ExileCore.Windows
{
    public class PerformanceWindow
    {
        public DebugInformation FpsCounter { get; } = new DebugInformation("Fps Counter");
        public Dictionary<PerformanceCoreEnum, DebugInformation> CorePerformance { get; } =
            new Dictionary<PerformanceCoreEnum, DebugInformation>();
        public Dictionary<string, DebugInformation> PluginRenderPerformance { get; } =
            new Dictionary<string, DebugInformation>();
        public ThreadManager ThreadManager { get; }
        private TabEnum CurrentTab { get; set; } = TabEnum.MainDebugs;
        private TabEnum[] TabEnums { get; }
        private CoreSettings CoreSettings { get; }
        private GameController GameController { get; }
        private string SelectedDebugInformationName { get; set; }
        private Action MoreInformation { get; set; }

        public PerformanceWindow(CoreSettings coreSettings, GameController gameController, ThreadManager threadManager)
        {
            InitializeCorePerformance();
            TabEnums = (TabEnum[]) Enum.GetValues(typeof(TabEnum));
            CoreSettings = coreSettings;
            GameController = gameController;
            ThreadManager = threadManager;
        }

        private void InitializeCorePerformance()
        {
            CorePerformance.Clear();
            foreach (var coreEnum in (PerformanceCoreEnum[]) Enum.GetValues(typeof(PerformanceCoreEnum)))
            {
                CorePerformance.Add(coreEnum, new DebugInformation(coreEnum.ToString()));
            }
        }
        
        private enum TabEnum
        {
            MainDebugs,
            PluginsRender,
            Threads,
            Caches
        }

        public void Render()
        {
            MoreInformation?.Invoke();
            var debOpen = CoreSettings.ShowDebugWindow.Value;
            ImGui.Begin("Debug window", ref debOpen);
            CoreSettings.ShowDebugWindow.Value = debOpen;

            ImGui.Text("Program work: ");
            ImGui.SameLine();
            ImGui.TextColored(Color.GreenYellow.ToImguiVec4(), "0");
            ImGui.BeginTabBar("Performance tabs");

            foreach (var tab in TabEnums)
            {
                if (!ImGui.BeginTabItem($"{tab}##WindowName")) continue;
                CurrentTab = tab;
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();

            switch (CurrentTab)
            {
                case TabEnum.MainDebugs:
                {
                    ImGui.Columns(6, "Deb", true);
                    ImGui.SetColumnWidth(0, 200);
                    ImGui.SetColumnWidth(1, 75);
                    ImGui.SetColumnWidth(2, 75);
                    ImGui.SetColumnWidth(3, 100);
                    ImGui.SetColumnWidth(4, 100);
                    ImGui.Text("Name");
                    ImGui.NextColumn();
                    ImGui.TextUnformatted("%");
                    ImGui.NextColumn();
                    ImGui.Text("Tick");
                    ImGui.NextColumn();
                    ImGui.Text("Total");
                    ImGui.SameLine();
                    ImGui.TextDisabled("(?)");

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.None))
                    {
                        ImGui.SetTooltip(
                            $"Update every {DebugInformation.SizeArray / CoreSettings.TargetFps} sec. Time to next update: " +
                            $"{(DebugInformation.SizeArray - CorePerformance[PerformanceCoreEnum.TotalFrameTime].Index) / (float)CoreSettings.TargetFps:0.00} sec.");
                    }

                    ImGui.NextColumn();
                    ImGui.Text("Total %%");
                    ImGui.NextColumn();
                    ImGui.Text($"Data for {DebugInformation.SizeArray / CoreSettings.TargetFps} sec.");
                    ImGui.NextColumn();

                    DrawInfoForFpsCounter(FpsCounter);
                    foreach (var enumDebugInformationTuple in CorePerformance)
                    {
                        DrawInfoForDebugInformation(
                            enumDebugInformationTuple.Value,
                            CorePerformance[PerformanceCoreEnum.TotalFrameTime], 
                            CorePerformance.Count
                        );
                    }

                    ImGui.Columns(1, "", false);
                    break;
                }
                case TabEnum.PluginsRender:
                {
                    ImGui.Columns(6, "Deb", true);
                    ImGui.SetColumnWidth(0, 200);
                    ImGui.SetColumnWidth(1, 75);
                    ImGui.SetColumnWidth(2, 75);
                    ImGui.SetColumnWidth(3, 100);
                    ImGui.SetColumnWidth(4, 100);
                    ImGui.Text("Name");
                    ImGui.NextColumn();
                    ImGui.TextUnformatted("%");
                    ImGui.NextColumn();
                    ImGui.Text("Tick");
                    ImGui.NextColumn();
                    ImGui.Text("Total");
                    ImGui.SameLine();
                    ImGui.TextDisabled("(?)");

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.None))
                    {
                        ImGui.SetTooltip($"Update every {DebugInformation.SizeArray / CoreSettings.TargetFps} sec. Time to next update: " +
                                         $"{(DebugInformation.SizeArray - CorePerformance[PerformanceCoreEnum.TotalFrameTime].Index) / (float)CoreSettings.TargetFps:0.00} sec.");
                    }

                    ImGui.NextColumn();
                    ImGui.Text("Total %%");
                    ImGui.NextColumn();
                    ImGui.Text($"Data for {DebugInformation.SizeArray / CoreSettings.TargetFps} sec.");
                    ImGui.NextColumn();

                    var allPlugins = CorePerformance[PerformanceCoreEnum.AllPluginsRender];
                    DrawInfoForDebugInformation(allPlugins, allPlugins, 1);
                    foreach (var enumDebugInformationTuple in PluginRenderPerformance)
                    {
                        DrawInfoForDebugInformation(
                            enumDebugInformationTuple.Value,
                            allPlugins,
                            PluginRenderPerformance.Count);
                    }

                    ImGui.Columns(1, "", false);
                    break;
                }
                case TabEnum.Threads:
                    ImGui.Columns(4, "Deb", true);
                    ImGui.SetColumnWidth(0, 200);
                    ImGui.SetColumnWidth(1, 100);
                    ImGui.SetColumnWidth(2, 100);
                    ImGui.Text("Name");
                    ImGui.NextColumn();
                    ImGui.Text("Tick");
                    ImGui.NextColumn();
                    ImGui.Text("Total");
                    ImGui.NextColumn();
                    ImGui.Text($"Data for {DebugInformation.SizeArray / CoreSettings.TargetFps} sec.");
                    ImGui.NextColumn();

                    foreach (var threadWithName in ThreadManager.Threads)
                    {
                        DrawInforForThreads(threadWithName.Value.PerformanceTimer);
                    }

                    ImGui.Columns(1, "", false);
                    break;
                case TabEnum.Caches:
                ImGui.Columns(6, "Cache table", true);

                ImGui.Text("Name");
                ImGui.NextColumn();
                ImGui.Text("Count");
                ImGui.NextColumn();
                ImGui.Text("Memory read");
                ImGui.NextColumn();
                ImGui.Text("Cache read");
                ImGui.NextColumn();
                ImGui.Text("Deleted");
                ImGui.NextColumn();
                ImGui.Text("%% Read from memory");
                ImGui.NextColumn();

                var cache = GameController.Cache;

                //Elements
                ImGui.Text("Elements");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheElements.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheElements.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheElements.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheElements.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheElements.Coeff} %%");
                ImGui.NextColumn();

                //Components
                ImGui.Text("Components");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheComponents.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheComponents.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheComponents.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheComponents.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticCacheComponents.Coeff} %%");
                ImGui.NextColumn();

                //Entity
                ImGui.Text("Entity");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityCache.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityCache.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityCache.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityCache.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityCache.Coeff} %%");
                ImGui.NextColumn();

                //Entity list parse
                ImGui.Text("Entity list parse");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityListCache.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityListCache.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityListCache.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityListCache.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticEntityListCache.Coeff} %%");
                ImGui.NextColumn();

                //Server entity
                ImGui.Text("Server Entity list parse");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticServerEntityCache.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticServerEntityCache.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticServerEntityCache.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticServerEntityCache.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StaticServerEntityCache.Coeff}%%");
                ImGui.NextColumn();

                //Read strings
                ImGui.Text("String cache");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StringCache.Count}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StringCache.ReadMemory}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StringCache.ReadCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StringCache.DeletedCache}");
                ImGui.NextColumn();
                ImGui.Text($"{cache.StringCache.Coeff} %%");
                ImGui.NextColumn();
                ImGui.Columns(1, "", false);
                break;
            }

            MoreInformation?.Invoke();
            ImGui.End();
        }

        private void DrawInfoForDebugInformation(DebugInformation deb, DebugInformation total, int groupCount)
        {
            if (SelectedDebugInformationName == deb.Name) ImGui.PushStyleColor(ImGuiCol.Text, Color.OrangeRed.ToImgui());
            ImGui.Text($"{deb.Name}");

            if (ImGui.IsItemClicked()) MoreInformation = () => { AddtionalInfo(deb); };

            if (!string.IsNullOrEmpty(deb.Description))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.None)) ImGui.SetTooltip(deb.Description);
            }

            ImGui.NextColumn();
            var averageF = deb.Ticks.AverageF();
            var avgFromAllPlugins = total.Average / groupCount;
            var byName = Color.Yellow.ToImguiVec4();

            if (averageF <= avgFromAllPlugins * 0.5f)
                byName = Color.Green.ToImguiVec4();
            else if (averageF >= avgFromAllPlugins * 4f)
                byName = Color.Red.ToImguiVec4();
            else if (averageF >= avgFromAllPlugins * 1.5f) byName = Color.Orange.ToImguiVec4();

            ImGui.TextColored(byName, $"{deb.Sum / total.Sum * 100:0.00} %%");
            ImGui.NextColumn();
            ImGui.TextColored(byName, $"{deb.Tick:0.0000}");
            ImGui.NextColumn();
            ImGui.TextColored(byName, $"{deb.Total:0.000}");
            ImGui.NextColumn();
            ImGui.TextColored(byName, $"{deb.Total / total.Total * 100:0.00} %%");
            ImGui.NextColumn();

            ImGui.Text($"Min: {deb.Ticks.Min():0.000} Max: {deb.Ticks.MaxF():00.000} Avg: {averageF:0.000} TAMax: {deb.TotalMaxAverage:00.000}");

            if (averageF >= CoreSettings.LimitDrawPlot)
            {
                ImGui.SameLine();
                ImGui.PlotLines($"##Plot{deb.Name}", ref deb.Ticks[0], DebugInformation.SizeArray);
            }

            ImGui.Separator();
            ImGui.NextColumn();
            if (SelectedDebugInformationName == deb.Name) ImGui.PopStyleColor();
        }

        private void DrawInfoForFpsCounter(DebugInformation fpsCounter)
        {
            var averageF = fpsCounter.Ticks.AverageF();
            ImGui.Text($"{fpsCounter.Name}");
            ImGui.NextColumn();
            ImGui.Text("-");
            ImGui.NextColumn();
            ImGui.Text($"{fpsCounter.Tick:0.0000}");
            ImGui.NextColumn();
            ImGui.Text("-");
            ImGui.NextColumn();
            ImGui.Text("-");
            ImGui.NextColumn();

            ImGui.Text($"Min: {fpsCounter.Ticks.Min():0.000} Max: {fpsCounter.Ticks.MaxF():00.000} Avg: {averageF:0.000} TAMax: {fpsCounter.TotalMaxAverage:00.000}");

            if (averageF >= CoreSettings.LimitDrawPlot)
            {
                ImGui.SameLine();
                ImGui.PlotLines($"##Plot{fpsCounter.Name}", ref fpsCounter.Ticks[0], DebugInformation.SizeArray);
            }

            ImGui.Separator();
            ImGui.NextColumn();
        }

        private void DrawInforForThreads(DebugInformation thread)
        {
            if (SelectedDebugInformationName == thread.Name) ImGui.PushStyleColor(ImGuiCol.Text, Color.OrangeRed.ToImgui());
            ImGui.Text($"{thread.Name}");

            if (ImGui.IsItemClicked()) MoreInformation = () => { AddtionalInfo(thread); };

            ImGui.NextColumn();
            ImGui.Text($"{thread.Tick:0.0000}");
            ImGui.NextColumn();
            ImGui.Text($"{thread.Total:0.000}");
            ImGui.NextColumn();

            var averageF = thread.Ticks.AverageF();
            ImGui.Text($"Min: {thread.Ticks.Min():0.000} Max: {thread.Ticks.MaxF():00.000} Avg: {averageF:0.000} TAMax: {thread.TotalMaxAverage:00.000}");

            if (averageF >= CoreSettings.LimitDrawPlot)
            {
                ImGui.SameLine();
                ImGui.PlotLines($"##Plot{thread.Name}", ref thread.Ticks[0], DebugInformation.SizeArray);
            }

            ImGui.Separator();
            ImGui.NextColumn();
            if (SelectedDebugInformationName == thread.Name) ImGui.PopStyleColor();
        }

        private void AddtionalInfo(DebugInformation deb)
        {
            SelectedDebugInformationName = deb.Name;

            if (!deb.AtLeastOneFullTick)
            {
                ImGui.Text(
                    $"Info {deb.Name} - {DebugInformation.SizeArray / CoreSettings.TargetFps / 60f:0.00} " +
                    $"sec. Index: {deb.Index}/{DebugInformation.SizeArray}");

                var scaleMin = deb.Ticks.Min();
                var scaleMax = deb.Ticks.Max();
                var windowWidth = ImGui.GetWindowWidth();

                ImGui.PlotHistogram($"##Plot{deb.Name}", ref deb.Ticks[0], DebugInformation.SizeArray, 0,
                    $"Avg: {deb.Ticks.Where(x => x > 0).Average():0.0000} Max {scaleMax:0.0000}", scaleMin, scaleMax,
                    new System.Numerics.Vector2(windowWidth - 10, 150));

                if (ImGui.Button($"Close##{deb.Name}")) MoreInformation = null;
            }
            else
            {
                ImGui.Text($"Info {deb.Name} - {DebugInformation.SizeArray * DebugInformation.SizeArray / CoreSettings.TargetFps / 60f:0.00} " +
                           $"sec. Index: {deb.Index}/{DebugInformation.SizeArray}");

                var scaleMin = deb.TicksAverage.MinF();
                var scaleMax = deb.TicksAverage.MaxF();
                var scaleTickMax = deb.Ticks.MaxF();
                var windowWidth = ImGui.GetWindowWidth();

                ImGui.PlotHistogram($"##Plot{deb.Name}", 
                    ref deb.Ticks[0], 
                    DebugInformation.SizeArray, 
                    0, $"{deb.Tick:0.000}", 
                    0,
                    scaleTickMax, 
                    new System.Numerics.Vector2(windowWidth - 50, 150)
                );

                var enumerable = deb.TicksAverage.Where(x => x > 0).ToArray();

                if (enumerable.Length > 0)
                {
                    ImGui.Text($"Index: {deb.IndexTickAverage}/{DebugInformation.SizeArray}");

                    ImGui.PlotHistogram($"##Plot{deb.Name}", 
                        ref deb.TicksAverage[0], 
                        DebugInformation.SizeArray, 
                        0,
                        $"Avg: {enumerable.Average():0.0000} Max {scaleMax:0.0000}", 
                        scaleMin, 
                        scaleMax,
                        new Vector2(windowWidth - 50, 150)
                    );
                }
                else
                    ImGui.Text("Dont have information");

                if (ImGui.Button($"Close##{deb.Name}")) MoreInformation = null;
            }
        }
    }
}
