using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;

namespace ConfigMenu;

internal enum ConfigValueKind {
    Bool,
    Choice,
    Range,
    Number,
    ReadOnly
}

/// <summary>
/// Maps a BepInEx <see cref="ConfigEntryBase"/> to the small set of controls ConfigMenu supports. Everything that
/// cannot be edited safely through a toggle, dropdown, slider or stepper is exposed as read-only.
/// </summary>
internal sealed class ConfigEntryAdapter {
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly HashSet<Type> NumericTypes = new() {
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)
    };

    private static readonly HashSet<Type> SliderTypes = new() {
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(float), typeof(double)
    };

    private readonly object[] _choices = Array.Empty<object>();
    private readonly string[] _choiceLabels = Array.Empty<string>();

    public ConfigEntryAdapter(ConfigEntryBase entry) {
        Entry = entry;
        Kind = ConfigValueKind.ReadOnly;

        var type = entry.SettingType;
        var acceptable = entry.Description?.AcceptableValues;

        if (TryGetAcceptableList(acceptable, out var list)) {
            _choices = list;
            _choiceLabels = list.Select(FormatChoice).ToArray();
            Kind = ConfigValueKind.Choice;
        }
        else if (type == typeof(bool)) {
            Kind = ConfigValueKind.Bool;
        }
        else if (type.IsEnum) {
            bool flags = type.GetCustomAttributes(typeof(FlagsAttribute), inherit: false).Length > 0;
            if (!flags) {
                _choices = Enum.GetValues(type).Cast<object>().ToArray();
                _choiceLabels = _choices.Select(FormatChoice).ToArray();
                Kind = ConfigValueKind.Choice;
            }
        }
        else if (NumericTypes.Contains(type) && TryGetRange(acceptable, out double min, out double max)) {
            float rangeMin = (float)min;
            float rangeMax = (float)max;
            if (SliderTypes.Contains(type) && float.IsFinite(rangeMin) && float.IsFinite(rangeMax) &&
                rangeMax > rangeMin && rangeMax - rangeMin <= 1_000_000f) {
                RangeMin = rangeMin;
                RangeMax = rangeMax;
                WholeNumbers = IsIntegral(type);
                NumberFormat = WholeNumbers ? "0" : "0.###";
                Kind = ConfigValueKind.Range;
            }
            else {
                Kind = ConfigValueKind.Number;
            }
        }
        else if (NumericTypes.Contains(type)) {
            Kind = ConfigValueKind.Number;
            NumberFormat = IsIntegral(type) ? "0" : "0.###";
        }

        if (Kind == ConfigValueKind.Number) {
            Step = ComputeStep(type, entry.DefaultValue);
        }
    }

    public ConfigEntryBase Entry { get; }
    public ConfigValueKind Kind { get; }
    public string Label => Entry.Definition.Key;
    public string Section => string.IsNullOrWhiteSpace(Entry.Definition.Section)
        ? "General"
        : Entry.Definition.Section;
    public string Description => Entry.Description?.Description ?? string.Empty;
    public bool WholeNumbers { get; private set; }
    public float RangeMin { get; private set; }
    public float RangeMax { get; private set; }
    public double Step { get; private set; } = 1d;
    public string NumberFormat { get; private set; } = "0.###";

    public object? CurrentValue {
        get {
            try { return Entry.BoxedValue; }
            catch (Exception) { return null; }
        }
    }

    public bool BoolValue => CurrentValue is bool value && value;

    public double NumberValue {
        get {
            double value = ToDouble(CurrentValue, 0d);
            if (Kind == ConfigValueKind.Range) {
                return Math.Clamp(value, RangeMin, RangeMax);
            }
            return value;
        }
    }

    public IReadOnlyList<string> ChoiceLabels => _choiceLabels;
    public IReadOnlyList<object> Choices => _choices;

    public int ChoiceIndex {
        get {
            object? current = CurrentValue;
            for (int i = 0; i < _choices.Length; i++) {
                if (Equals(_choices[i], current)) return i;
            }
            return 0;
        }
    }

    public string DisplayValue {
        get {
            try {
                string serialized = Entry.GetSerializedValue();
                if (!string.IsNullOrEmpty(serialized)) return serialized;
            }
            catch (Exception) {
                // Fall through to a best-effort representation.
            }

            return FormatChoice(CurrentValue);
        }
    }

    public string Hint {
        get {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Description)) parts.Add(Description.Trim());

            if (Kind == ConfigValueKind.Range) {
                parts.Add($"Range: {FormatNumber(RangeMin)}..{FormatNumber(RangeMax)}");
            }
            else if (Kind == ConfigValueKind.Number) {
                parts.Add($"Step: {FormatNumber(Step)}");
            }
            else if (Kind == ConfigValueKind.ReadOnly) {
                parts.Add("Read-only here; edit the .cfg file with a text editor.");
            }

            return string.Join("  ", parts);
        }
    }

    public bool TrySetBool(bool value, out string error) => TrySet(value, out error);

    public bool TrySetChoice(int index, out string error) {
        if (index < 0 || index >= _choices.Length) {
            error = "The selected value is no longer available.";
            return false;
        }

        return TrySet(_choices[index], out error);
    }

    public bool TrySetNumber(double value, out string error) => TrySet(value, out error);

    public bool TryStep(int direction, out string error) {
        double next = NumberValue + Math.Sign(direction) * Step;
        Type type = Entry.SettingType;
        if (IsIntegral(type)) {
            next = Math.Round(next, MidpointRounding.AwayFromZero);
            next = ClampIntegral(type, next);
        }

        return TrySetNumber(next, out error);
    }

    public bool TryReset(out string error) {
        try {
            object? value = Entry.DefaultValue;
            if (value == null) {
                error = "This setting has no default value.";
                return false;
            }

            return TrySet(value, out error);
        }
        catch (Exception e) {
            error = e.Message;
            return false;
        }
    }

    private bool TrySet(object? value, out string error) {
        try {
            if (value == null) {
                error = "The setting value is null.";
                return false;
            }

            object converted = Coerce(value, Entry.SettingType);
            Entry.BoxedValue = converted;

            if (!Entry.ConfigFile.SaveOnConfigSet) {
                Entry.ConfigFile.Save();
            }

            error = string.Empty;
            return true;
        }
        catch (Exception e) {
            error = e.Message;
            return false;
        }
    }

    private static object Coerce(object value, Type targetType) {
        if (targetType.IsInstanceOfType(value)) return value;

        if (targetType.IsEnum) {
            if (value is string text) return Enum.Parse(targetType, text, ignoreCase: true);
            return Enum.ToObject(targetType, value);
        }

        if (targetType == typeof(bool)) {
            return Convert.ToBoolean(value, Invariant);
        }

        return Convert.ChangeType(value, targetType, Invariant) ??
               throw new InvalidCastException($"Cannot convert '{value}' to {targetType.Name}.");
    }

    private static bool TryGetAcceptableList(AcceptableValueBase? acceptable, out object[] values) {
        values = Array.Empty<object>();
        if (acceptable == null) return false;

        Type type = acceptable.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(AcceptableValueList<>)) return false;

        var property = type.GetProperty("AcceptableValues");
        if (property?.GetValue(acceptable) is not Array array || array.Length == 0) return false;

        values = array.Cast<object>().ToArray();
        return true;
    }

    private static bool TryGetRange(AcceptableValueBase? acceptable, out double min, out double max) {
        min = 0d;
        max = 0d;
        if (acceptable == null) return false;

        Type type = acceptable.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(AcceptableValueRange<>)) return false;

        try {
            var minProperty = type.GetProperty("MinValue");
            var maxProperty = type.GetProperty("MaxValue");
            if (minProperty == null || maxProperty == null) return false;

            min = Convert.ToDouble(minProperty.GetValue(acceptable), Invariant);
            max = Convert.ToDouble(maxProperty.GetValue(acceptable), Invariant);
            return double.IsFinite(min) && double.IsFinite(max) && max > min;
        }
        catch (Exception) {
            return false;
        }
    }

    private static double ComputeStep(Type type, object? defaultValue) {
        if (IsIntegral(type)) return 1d;

        double baseValue = Math.Abs(ToDouble(defaultValue, 0d));
        if (!double.IsFinite(baseValue) || baseValue <= 0d) baseValue = 1d;

        double exponent = Math.Floor(Math.Log10(baseValue));
        double step = Math.Pow(10d, exponent - 1d);
        return Math.Clamp(step, 0.0001d, 1_000_000d);
    }

    private static bool IsIntegral(Type type) {
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong);
    }

    private static double ClampIntegral(Type type, double value) {
        if (type == typeof(byte)) return Math.Clamp(value, byte.MinValue, byte.MaxValue);
        if (type == typeof(sbyte)) return Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
        if (type == typeof(short)) return Math.Clamp(value, short.MinValue, short.MaxValue);
        if (type == typeof(ushort)) return Math.Clamp(value, ushort.MinValue, ushort.MaxValue);
        if (type == typeof(int)) return Math.Clamp(value, int.MinValue, int.MaxValue);
        if (type == typeof(uint)) return Math.Clamp(value, uint.MinValue, uint.MaxValue);
        if (type == typeof(long)) return Math.Clamp(value, long.MinValue, long.MaxValue);
        if (type == typeof(ulong)) return Math.Clamp(value, ulong.MinValue, (double)ulong.MaxValue);
        return value;
    }

    private static double ToDouble(object? value, double fallback) {
        if (value == null) return fallback;

        try {
            return value is IConvertible convertible
                ? convertible.ToDouble(Invariant)
                : fallback;
        }
        catch (Exception) {
            return fallback;
        }
    }

    private static string FormatChoice(object? value) {
        return value switch {
            null => string.Empty,
            string text => text,
            bool flag => flag ? "True" : "False",
            Enum enumeration => enumeration.ToString(),
            IFormattable formattable => formattable.ToString(null, Invariant) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatNumber(double value) {
        return value.ToString("0.######", Invariant);
    }
}
