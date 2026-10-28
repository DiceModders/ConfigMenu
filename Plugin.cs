using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DiceModders.UiKit;

namespace ConfigMenu;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("DiceModders.UiKit")]
public sealed class Plugin : BasePlugin {
    internal static new ManualLogSource Log = null!;
    internal static ConfigEntry<int>? TopBarOrder;
    internal static ConfigEntry<bool>? ShowReadOnly;
    internal static ConfigEntry<bool>? OpenOnStart;

    private UiScope? _ui;
    private ConfigWindow? _window;

    public override void Load() {
        Log = base.Log;

        TopBarOrder = Config.Bind("UI", "TopBarOrder", 100,
            "Order of the ConfigMenu button among mod buttons in the game's top-right bar. " +
            "Lower values come first. Takes effect after a restart.");
        ShowReadOnly = Config.Bind("UI", "ShowReadOnly", true,
            "Show settings that ConfigMenu cannot edit in this version (strings and advanced types) " +
            "as read-only rows. Takes effect immediately.");
        OpenOnStart = Config.Bind("UI", "OpenOnStart", false,
            "Open the configuration window automatically after the game's UI is ready. Intended for testing and screenshots.");

        _ui = UiKit.CreateScope(MyPluginInfo.PLUGIN_GUID);
        if (_ui == null) {
            Log.LogError("ConfigMenu: UiKit is not available; the configuration window will not open.");
            return;
        }

        _window = new ConfigWindow(_ui, Log);

        _ui.WhenReady(ui => {
            try {
                string iconName = ui.Custom.Theme.Icon("settings") != null ? "settings" : "hammer";
                ui.Native.TopBar.AddIconButton(iconName, "Mod configuration", () => _window?.Toggle(),
                    TopBarOrder?.Value ?? 100);

                if (OpenOnStart?.Value ?? false) _window?.Open();
            }
            catch (Exception e) {
                Log.LogError($"ConfigMenu: could not register the top-bar button: {e}");
            }
        });

        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

    public override bool Unload() {
        _window?.Dispose();
        _window = null;

        _ui?.Dispose();
        _ui = null;

        return true;
    }
}
