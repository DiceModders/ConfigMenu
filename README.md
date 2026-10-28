# ConfigMenu

An in-game editor for BepInEx configuration files in **Dice Kingdoms**. It uses
[DiceModders UiKit](https://github.com/DiceModders/UiKit) for the window and widgets.

Open it with the gear-shaped **settings** button in the game's top-right bar.

## Features

- Lists every loaded BepInEx plugin that has a non-empty configuration file.
- Groups settings by section and shows their descriptions.
- Edits booleans, enums, acceptable-value lists and numbers.
- Saves each change to the plugin's `.cfg` immediately.
- **Reset** restores one setting to its default value.
- **Refresh** rescans loaded plugins and their settings.
- **Reload** re-reads the selected plugin's `.cfg` from disk.
- **Save all** writes every config file explicitly.

## Supported setting types

| Type | Control |
|---|---|
| `bool` | Toggle |
| enum without `[Flags]` | Dropdown |
| `AcceptableValueList<T>` | Dropdown |
| number with `AcceptableValueRange<T>` | Slider |
| number without a range | `-` / value / `+` stepper |
| `string`, `[Flags]` enum and other types | Read-only row in v1 |

Read-only rows can be hidden with the `ShowReadOnly` option in ConfigMenu's own settings.
String and advanced settings still have to be edited in `BepInEx/config/*.cfg` with a text editor.

## Configuration

`BepInEx/config/DiceModders.ConfigMenu.cfg`:

| Section | Key | Default | Meaning |
|---|---|---|---|
| `UI` | `TopBarOrder` | `100` | Position of the top-bar button among mod buttons; takes effect after a restart |
| `UI` | `ShowReadOnly` | `true` | Show read-only settings |
| `UI` | `OpenOnStart` | `false` | Open the window automatically after the game UI is ready |

## Requirements

- Dice Kingdoms with BepInEx 6 for IL2CPP.
- UiKit installed in `BepInEx/plugins`.

## Build

Build UiKit first so the reference exists, then ConfigMenu:

```bash
dotnet build ../UiKit/UiKit.csproj -c Debug --nologo
dotnet build ConfigMenu.csproj -c Debug --nologo
./copy.sh
```

`copy.sh` copies `DiceModders.ConfigMenu.dll` into the game's `BepInEx/plugins` folder.
