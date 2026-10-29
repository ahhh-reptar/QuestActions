using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using QuestItemAssistant.Windows;

namespace QuestItemAssistant;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/qia";
    private readonly WindowSystem windowSystem = new("QuestItemAssistant");
    private readonly MainWindow hotbar;
    private readonly SettingsWindow settings;
    private readonly Configuration configuration;
    private bool pendingFirstRunSetup;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (configuration.Version < 2)
        {
            configuration.ShowNames = false;
            configuration.Version = 2;
        }
        if (configuration.Version < 4)
        {
            configuration.GrowthDirection = configuration.Horizontal
                ? BarGrowthDirection.Right
                : BarGrowthDirection.Down;
            configuration.Version = 4;
        }
        if (configuration.Version < 7)
            configuration.Version = 7;
        if (configuration.Version < 10)
            configuration.Version = 10;
        configuration.Initialize(PluginInterface);
        configuration.Save();

        hotbar = new MainWindow(configuration, OpenSettings);
        settings = new SettingsWindow(configuration, () => hotbar.Entries, hotbar.RequestResetPosition);
        windowSystem.AddWindow(hotbar);
        windowSystem.AddWindow(settings);
        hotbar.IsOpen = false;
        pendingFirstRunSetup = !configuration.FirstRunSetupCompleted;

        PluginInterface.UiBuilder.Draw += UpdateUiState;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleHotbar;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Quest Actions bar; '/qia config' opens settings."
        });

        Log.Information("Quest Actions loaded. Hotbar visible: {Visible}.", hotbar.IsOpen);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= UpdateUiState;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleHotbar;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        hotbar.Dispose();
        settings.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase) || args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettings();
            return;
        }

        ToggleHotbar();
    }

    private void UpdateUiState()
    {
        hotbar.UpdateVisibility();
        if (pendingFirstRunSetup && ClientState.IsLoggedIn && ObjectTable.LocalPlayer != null)
        {
            settings.IsOpen = true;
            ChatGui.Print("[Quest Actions]: First Time Configuration Opened");
            configuration.FirstRunSetupCompleted = true;
            configuration.Save();
            pendingFirstRunSetup = false;
        }
    }

    private void ToggleHotbar()
    {
        configuration.ShowHotbar = !hotbar.IsOpen;
        configuration.Save();
        hotbar.UpdateVisibility();
    }

    private void OpenSettings() => settings.IsOpen = true;
}
