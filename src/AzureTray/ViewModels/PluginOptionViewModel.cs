using System;
using CommunityToolkit.Mvvm.ComponentModel;
using AzureTray.Plugin.Contracts;

namespace AzureTray.ViewModels;

// One row in the per-plugin generic-options renderer. The XAML uses Kind to
// pick a checkbox / textbox / numeric stepper; both two-way bind back to the
// same Value property, which fires through to the plugin's SetValue.
public sealed partial class PluginOptionViewModel : ObservableObject
{
    private readonly Action<string, object?> _commit;
    private bool _suspendCommit;

    public PluginOptionViewModel(PluginOption definition, object? initialValue, Action<string, object?> commit)
    {
        Definition = definition;
        _commit = commit;
        _value = initialValue ?? definition.DefaultValue;

        // Surface typed scalars to make bindings cleaner. WPF binds to bool /
        // string / int directly without converters when the source property
        // already has the right type.
        _boolValue = TryAsBool(_value) ?? (TryAsBool(definition.DefaultValue) ?? false);
        _textValue = _value?.ToString() ?? definition.DefaultValue?.ToString() ?? string.Empty;
        _numberValue = TryAsInt(_value) ?? (TryAsInt(definition.DefaultValue) ?? 0);
    }

    public PluginOption Definition { get; }

    public string Label => Definition.Label;
    public string? Description => Definition.Description;
    public bool IsBoolean => Definition.Kind == PluginOptionKind.Boolean;
    public bool IsText => Definition.Kind == PluginOptionKind.Text;
    public bool IsNumber => Definition.Kind == PluginOptionKind.Number;

    [ObservableProperty]
    private object? _value;

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private string _textValue = string.Empty;

    [ObservableProperty]
    private int _numberValue;

    partial void OnBoolValueChanged(bool value)
    {
        if (!IsBoolean || _suspendCommit) return;
        _suspendCommit = true;
        Value = value;
        _commit(Definition.Key, value);
        _suspendCommit = false;
    }

    partial void OnTextValueChanged(string value)
    {
        if (!IsText || _suspendCommit) return;
        _suspendCommit = true;
        Value = value;
        _commit(Definition.Key, value);
        _suspendCommit = false;
    }

    partial void OnNumberValueChanged(int value)
    {
        if (!IsNumber || _suspendCommit) return;
        _suspendCommit = true;
        Value = value;
        _commit(Definition.Key, value);
        _suspendCommit = false;
    }

    private static bool? TryAsBool(object? value) => value switch
    {
        bool b => b,
        _ => null,
    };

    private static int? TryAsInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        _ => null,
    };
}
