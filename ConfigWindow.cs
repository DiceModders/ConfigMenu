using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using DiceModders.UiKit;
using DiceModders.UiKit.Custom;
using TMPro;
using UnityEngine;

namespace ConfigMenu;

/// <summary>
/// The managed ConfigMenu window. The window is created on demand and disposed when the player closes it, so every
/// opening starts from the current values in the BepInEx configuration files.
/// </summary>
internal sealed class ConfigWindow : IDisposable {
    private static readonly Color HeaderColor = new(0.95f, 0.83f, 0.55f, 1f);
    private static readonly Color DimColor = new(0.72f, 0.68f, 0.60f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.42f, 0.38f, 1f);

    private readonly UiScope _scope;
    private readonly ManualLogSource _log;
    private readonly Dictionary<string, UiButton> _pluginButtons = new();
    private readonly Dictionary<ConfigEntryBase, RowBinding> _rowBindings = new();

    private UiWindow? _window;
    private List<PluginConfig> _plugins = new();
    private string? _selectedGuid;
    private ConfigFile? _subscribedFile;
    private EventHandler<SettingChangedEventArgs>? _settingChanged;

    private UiContainer? _pluginList;
    private UiScroll? _pluginScroll;
    private UiScroll? _entryScroll;
    private UiText? _pluginTitle;
    private UiText? _pluginMeta;
    private UiText? _status;
    private bool _disposed;

    public ConfigWindow(UiScope scope, ManualLogSource log) {
        _scope = scope;
        _log = log;
    }

    public void Toggle() {
        if (_disposed) return;

        try {
            if (_window != null && _window.IsAlive && _window.IsVisible) {
                _window.Close();
                return;
            }

            Open();
        }
        catch (Exception e) {
            _log.LogError($"ConfigMenu: could not toggle the window: {e}");
        }
    }

    public void Open() {
        if (_disposed || _scope.IsDisposed) return;

        try {
            if (_window != null && _window.IsAlive) {
                if (!_window.IsVisible) _window.Show();
                _window.BringToFront();
                return;
            }

            RefreshCatalog();
            BuildWindow();
        }
        catch (Exception e) {
            _log.LogError($"ConfigMenu: could not open the window: {e}");
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        Unsubscribe();

        var window = _window;
        _window = null;
        if (window != null) {
            try { window.Dispose(); }
            catch (Exception e) { _log.LogWarning($"ConfigMenu: window cleanup failed: {e.Message}"); }
        }

        ResetViewFields();
    }

    private void BuildWindow() {
        EnsureSelection();
        _pluginButtons.Clear();
        _rowBindings.Clear();

        var window = _scope.Custom.Window(w => {
            w.Title("Mod Configuration")
             .Size(920f, 620f)
             .Center()
             .Draggable()
             .Closable()
             .Fullscreenable()
             .FullscreenButton()
             .Ornament(OrnamentStyle.BorderWithFourCorners)
        	 .OnClose(HandleClosed);

            var row = w.Body.Row(bodyRow => {
                bodyRow.Column(leftColumn => {
                    leftColumn.Text("Mods")?.Size(18f).Color(HeaderColor);
                    _pluginScroll = leftColumn.Scroll(list => {
                        _pluginList = list;
                        BuildPluginList(list);
                    })?.FillHeight();
                })?.FixedW(232f).FillHeight();

                bodyRow.Column(rightColumn => {
                    _pluginTitle = rightColumn.Text(string.Empty)?.Size(18f).Color(HeaderColor);
                    _pluginMeta = rightColumn.Text(string.Empty)?.Size(12f).Color(DimColor);

                    var toolbar = rightColumn.Row(tools => {
                        tools.Button("Refresh", RefreshClicked)?.FixedW(90f);
                        tools.Button("Reload", ReloadClicked)?.FixedW(90f);
                        tools.Button("Save all", SaveAllClicked)?.FixedW(90f);
                    })?.Height(34f)?.Spacing(8f, 0f);

                    _status = rightColumn.Text("Ready.")?.Size(13f).Color(DimColor);
                    _entryScroll = rightColumn.Scroll(list => BuildEntries(list));
                    _entryScroll?.FillHeight();
                })?.WeightW(1f).FillHeight();
            })?.FillHeight();
        });

        if (window == null) {
            _window = null;
            _log.LogError("ConfigMenu: the window could not be created.");
            return;
        }

        _window = window;
        UpdateHeader();
        Subscribe();
        Status("Ready.");
    }

    private void HandleClosed() {
        Unsubscribe();
        _window = null;
        ResetViewFields();
    }

    private void ResetViewFields() {
        _pluginList = null;
        _pluginScroll = null;
        _entryScroll = null;
        _pluginTitle = null;
        _pluginMeta = null;
        _status = null;
        _pluginButtons.Clear();
        _rowBindings.Clear();
    }

    private PluginConfig? SelectedPlugin {
        get {
            if (string.IsNullOrEmpty(_selectedGuid)) return null;
            return _plugins.FirstOrDefault(plugin => plugin.Guid == _selectedGuid);
        }
    }

    private void EnsureSelection() {
        if (_plugins.Count == 0) {
            _selectedGuid = null;
            return;
        }

        if (string.IsNullOrEmpty(_selectedGuid) ||
            _plugins.All(plugin => plugin.Guid != _selectedGuid)) {
            _selectedGuid = _plugins[0].Guid;
        }
    }

    private void RefreshCatalog() {
        var previous = _selectedGuid;
        _plugins = ConfigCatalog.Scan(_log);

        if (!string.IsNullOrEmpty(previous) && _plugins.Any(plugin => plugin.Guid == previous)) {
            _selectedGuid = previous;
        }
        else {
            _selectedGuid = _plugins.FirstOrDefault()?.Guid;
        }

        if (_window == null || !_window.IsAlive) return;

        if (_pluginList != null) BuildPluginList(_pluginList);
        if (_entryScroll?.Content != null) BuildEntries(_entryScroll.Content);
        UpdateHeader();
        Subscribe();
    }

    private void BuildPluginList(UiContainer list) {
        list.Clear();
        _pluginButtons.Clear();

        if (_plugins.Count == 0) {
            list.Paragraph("No mods with configuration files were found.");
            return;
        }

        foreach (var plugin in _plugins) {
            var model = plugin;
            var button = list.Button($"{model.Name} ({model.EntryCount})", () => SelectPlugin(model.Guid));
            button?.Height(32f);
            button?.Selected(model.Guid == _selectedGuid);

            if (button != null) _pluginButtons[model.Guid] = button;
        }
    }

    private void SelectPlugin(string guid) {
        if (_selectedGuid == guid) return;

        Unsubscribe();
        _selectedGuid = guid;

        foreach (var pair in _pluginButtons) {
            pair.Value.Selected(pair.Key == guid);
        }

        Subscribe();

        if (_entryScroll?.Content != null) BuildEntries(_entryScroll.Content);
        UpdateHeader();
        Status($"Selected {SelectedPlugin?.Name ?? guid}.");
    }

    private void UpdateHeader() {
        var plugin = SelectedPlugin;
        if (_pluginTitle != null) {
            _pluginTitle.Value = plugin == null
                ? "No mods found"
                : $"{plugin.Name} {plugin.Version}".Trim();
        }

        if (_pluginMeta != null) {
            _pluginMeta.Value = plugin == null
                ? string.Empty
                : $"{plugin.Guid}  |  {plugin.ConfigPath}  |  {plugin.EntryCount} settings";
        }
    }

    private void BuildEntries(UiContainer list) {
        list.Clear();
        _rowBindings.Clear();

        var plugin = SelectedPlugin;
        if (plugin == null) {
            list.Paragraph("Select a mod to see its settings.");
            return;
        }

        if (plugin.Sections.Count == 0) {
            list.Paragraph("This mod does not expose any editable settings.");
            return;
        }

        bool showReadOnly = Plugin.ShowReadOnly?.Value ?? true;
        bool anyRows = false;

        foreach (var section in plugin.Sections) {
            var adapters = new List<ConfigEntryAdapter>();
            foreach (var entry in section.Entries) {
                try {
                    var adapter = new ConfigEntryAdapter(entry);
                    if (!showReadOnly && adapter.Kind == ConfigValueKind.ReadOnly) continue;
                    adapters.Add(adapter);
                }
                catch (Exception e) {
                    _log.LogWarning($"ConfigMenu: could not inspect row {entry.Definition}: {e.Message}");
                }
            }

            if (adapters.Count == 0) continue;
            anyRows = true;

            list.Text(section.Name)?.Size(16f).Color(HeaderColor);
            list.Separator(1f);
            list.Spacer(2f);

            foreach (var adapter in adapters) {
                try {
                    BuildEntryRow(list, adapter);
                }
                catch (Exception e) {
                    _log.LogWarning($"ConfigMenu: could not build row {adapter.Entry.Definition}: {e.Message}");
                    list.Paragraph($"{adapter.Label}: could not be displayed ({e.Message})")?.Size(12f).Color(ErrorColor);
                }
            }

            list.Spacer(10f);
        }

        if (!anyRows) {
            list.Paragraph(showReadOnly
                ? "This mod does not expose any settings ConfigMenu can display."
                : "This mod has no editable settings. Enable ShowReadOnly in ConfigMenu's own settings to see them.");
        }
    }

    private void BuildEntryRow(UiContainer list, ConfigEntryAdapter adapter) {
        UiToggle? toggle = null;
        UiDropdown? dropdown = null;
        UiSlider? slider = null;
        UiText? valueText = null;

        var row = list.Row(controlRow => {
            switch (adapter.Kind) {
                case ConfigValueKind.Bool:
                    toggle = controlRow.Toggle(adapter.Label, adapter.BoolValue,
                        value => OnBool(adapter, value));
                    toggle?.WeightW(1f);
                    break;

                case ConfigValueKind.Choice:
                    controlRow.Text(adapter.Label)?.WeightW(1f);
                    dropdown = controlRow.Dropdown(adapter.ChoiceLabels, adapter.ChoiceIndex,
                        index => OnChoice(adapter, index));
                    dropdown?.FixedW(280f);
                    break;

                case ConfigValueKind.Range:
                    controlRow.Text(adapter.Label)?.WeightW(1f);
                    slider = controlRow.Slider(adapter.RangeMin, adapter.RangeMax, (float)adapter.NumberValue,
                        value => OnNumber(adapter, value));
                    slider?.Format(adapter.NumberFormat);
                    slider?.WholeNumbers(adapter.WholeNumbers);
                    slider?.WeightW(1.4f);
                    break;

                case ConfigValueKind.Number:
                    controlRow.Text(adapter.Label)?.WeightW(1f);
                    controlRow.Button("-", () => OnStep(adapter, -1))?.FixedW(30f);
                    valueText = controlRow.Text(adapter.DisplayValue);
                    valueText?.FixedW(120f);
                    valueText?.Align(TextAlignmentOptions.MidlineRight);
                    controlRow.Button("+", () => OnStep(adapter, 1))?.FixedW(30f);
                    break;

                default:
                    controlRow.Text(adapter.Label)?.WeightW(1f);
                    valueText = controlRow.Text(adapter.DisplayValue);
                    valueText?.WeightW(1f);
                    valueText?.Align(TextAlignmentOptions.MidlineRight);
                    valueText?.Color(DimColor);
                    var readOnlyLabel = controlRow.Text("read-only");
                    readOnlyLabel?.FixedW(72f);
                    readOnlyLabel?.Color(DimColor);
                    break;
            }

            var reset = controlRow.Button("Reset", () => OnReset(adapter));
            reset?.FixedW(58f);
        });

        if (row != null) {
            row.Height(34f);
            row.Spacing(8f, 0f);
        }

        _rowBindings[adapter.Entry] = new RowBinding(adapter, toggle, dropdown, slider, valueText);

        string hint = adapter.Hint;
        if (!string.IsNullOrWhiteSpace(hint)) {
            list.Paragraph(hint)?.Size(12f).Color(DimColor);
        }
    }

    private void OnBool(ConfigEntryAdapter adapter, bool value) {
        if (adapter.TrySetBool(value, out string error)) {
            RefreshEntry(adapter.Entry);
            Status($"Saved {adapter.Section}/{adapter.Label}.");
        }
        else {
            StatusError($"Could not save {adapter.Label}: {error}");
        }
    }

    private void OnChoice(ConfigEntryAdapter adapter, int index) {
        if (adapter.TrySetChoice(index, out string error)) {
            RefreshEntry(adapter.Entry);
            Status($"Saved {adapter.Section}/{adapter.Label}.");
        }
        else {
            StatusError($"Could not save {adapter.Label}: {error}");
        }
    }

    private void OnNumber(ConfigEntryAdapter adapter, float value) {
        if (adapter.TrySetNumber(value, out string error)) {
            RefreshEntry(adapter.Entry);
            Status($"Saved {adapter.Section}/{adapter.Label}.");
        }
        else {
            StatusError($"Could not save {adapter.Label}: {error}");
        }
    }

    private void OnStep(ConfigEntryAdapter adapter, int direction) {
        if (adapter.TryStep(direction, out string error)) {
            RefreshEntry(adapter.Entry);
            Status($"Saved {adapter.Section}/{adapter.Label}.");
        }
        else {
            StatusError($"Could not save {adapter.Label}: {error}");
        }
    }

    private void OnReset(ConfigEntryAdapter adapter) {
        if (adapter.TryReset(out string error)) {
            RefreshEntry(adapter.Entry);
            Status($"Reset {adapter.Section}/{adapter.Label} to default.");
        }
        else {
            StatusError($"Could not reset {adapter.Label}: {error}");
        }
    }

    private void RefreshEntry(ConfigEntryBase entry) {
        if (_rowBindings.TryGetValue(entry, out var binding)) binding.Refresh();
    }

    private void Subscribe() {
        Unsubscribe();

        var plugin = SelectedPlugin;
        if (plugin == null) return;

        _settingChanged = (_, args) => {
            try {
                if (args.ChangedSetting.Definition.Section == "UI" &&
                    args.ChangedSetting.Definition.Key == nameof(Plugin.ShowReadOnly)) {
                    if (_entryScroll?.Content != null) BuildEntries(_entryScroll.Content);
                    return;
                }

                RefreshEntry(args.ChangedSetting);
            }
            catch (Exception e) { _log.LogWarning($"ConfigMenu: could not refresh a setting row: {e.Message}"); }
        };

        try {
            plugin.File.SettingChanged += _settingChanged;
            _subscribedFile = plugin.File;
        }
        catch (Exception e) {
            _log.LogWarning($"ConfigMenu: could not subscribe to {plugin.Guid} settings: {e.Message}");
            _settingChanged = null;
        }
    }

    private void Unsubscribe() {
        if (_subscribedFile != null && _settingChanged != null) {
            try { _subscribedFile.SettingChanged -= _settingChanged; }
            catch (Exception e) { _log.LogWarning($"ConfigMenu: could not unsubscribe from settings: {e.Message}"); }
        }

        _subscribedFile = null;
        _settingChanged = null;
    }

    private void RefreshClicked() {
        RefreshCatalog();
        Status($"Refreshed: {_plugins.Count} mod(s) with settings.");
    }

    private void ReloadClicked() {
        var plugin = SelectedPlugin;
        if (plugin == null) return;

        try {
            Unsubscribe();
            plugin.File.Reload();
            plugin.BuildSections();
            Subscribe();
            if (_entryScroll?.Content != null) BuildEntries(_entryScroll.Content);
            UpdateHeader();
            Status($"Reloaded {plugin.ConfigPath} from disk.");
        }
        catch (Exception e) {
            StatusError($"Could not reload {plugin.Name}: {e.Message}");
            _log.LogWarning($"ConfigMenu: reload of {plugin.Guid} failed: {e}");
        }
    }

    private void SaveAllClicked() {
        int saved = 0;
        int failed = 0;

        foreach (var plugin in _plugins) {
            try {
                plugin.File.Save();
                saved++;
            }
            catch (Exception e) {
                failed++;
                _log.LogWarning($"ConfigMenu: could not save {plugin.Guid}: {e.Message}");
            }
        }

        if (failed > 0) StatusError($"Saved {saved} file(s); {failed} failed.");
        else Status($"Saved {saved} config file(s).");
    }

    private void Status(string message) {
        if (_status == null) return;
        _status.Value = message;
        _status.Color(HeaderColor);
    }

    private void StatusError(string message) {
        if (_status != null) {
            _status.Value = message;
            _status.Color(ErrorColor);
        }

        _log.LogWarning($"ConfigMenu: {message}");
    }

    private sealed class RowBinding {
        private readonly ConfigEntryAdapter _adapter;
        private readonly UiToggle? _toggle;
        private readonly UiDropdown? _dropdown;
        private readonly UiSlider? _slider;
        private readonly UiText? _valueText;

        public RowBinding(ConfigEntryAdapter adapter, UiToggle? toggle, UiDropdown? dropdown,
                          UiSlider? slider, UiText? valueText) {
            _adapter = adapter;
            _toggle = toggle;
            _dropdown = dropdown;
            _slider = slider;
            _valueText = valueText;
        }

        public void Refresh() {
            try {
                switch (_adapter.Kind) {
                    case ConfigValueKind.Bool:
                        _toggle?.Set(_adapter.BoolValue);
                        break;
                    case ConfigValueKind.Choice:
                        _dropdown?.Set(_adapter.ChoiceIndex);
                        break;
                    case ConfigValueKind.Range:
                        _slider?.Set((float)_adapter.NumberValue);
                        break;
                    default:
                        if (_valueText != null) _valueText.Value = _adapter.DisplayValue;
                        break;
                }
            }
            catch (Exception) {
                // The window is closed or the widget is gone; the next rebuild will use fresh values.
            }
        }
    }
}
