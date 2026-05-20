namespace AzureTray.Plugin.Contracts;

/// <summary>
/// One generic option exposed to the host's Settings UI. The host renders a
/// control based on <see cref="Kind"/>; values stream back via
/// <see cref="IPluginConfigurable.SetValue"/> and are read via
/// <see cref="IPluginConfigurable.Values"/>.
/// </summary>
/// <remarks>
/// <para>
/// For <see cref="PluginOptionKind.Select"/>, populate
/// <see cref="AllowedValues"/> with the fixed choice list. The host renders a
/// combo-box and validates that the stored value is one of the listed choices.
/// </para>
/// <para>
/// <strong>Security:</strong> use <see cref="PluginOptionKind.Secret"/> for
/// any setting that contains a credential, API key, or other sensitive string.
/// The host stores it encrypted at rest and masks it in the Settings UI;
/// the plugin receives the plaintext value only through
/// <see cref="IPluginConfigurable.Values"/>.
/// Never use <see cref="PluginOptionKind.Text"/> for secrets.
/// </para>
/// </remarks>
/// <param name="Key">Unique key used in <see cref="IPluginConfigurable.Values"/> and <see cref="IPluginConfigurable.SetValue"/>.</param>
/// <param name="Label">User-visible label shown in the Settings UI.</param>
/// <param name="Kind">Determines the editor control the host renders.</param>
/// <param name="Description">Optional description shown as secondary text below the editor.</param>
/// <param name="DefaultValue">Initial value applied before the user changes the setting.</param>
/// <param name="AllowedValues">
/// Fixed choices for <see cref="PluginOptionKind.Select"/> options.
/// Ignored for all other <see cref="PluginOptionKind"/> values.
/// </param>
public sealed record PluginOption(
    string Key,
    string Label,
    PluginOptionKind Kind,
    string? Description = null,
    object? DefaultValue = null,
    string[]? AllowedValues = null);

/// <summary>Determines the editor the host renders for a <see cref="PluginOption"/>.</summary>
public enum PluginOptionKind
{
    /// <summary>Check-box. Value type: <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>Single-line text box. Value type: <see cref="string"/>.</summary>
    Text,

    /// <summary>Numeric stepper. Value type: <see cref="double"/>.</summary>
    Number,

    /// <summary>
    /// Combo-box restricted to <see cref="PluginOption.AllowedValues"/>.
    /// Value type: <see cref="string"/> (one of the listed choices).
    /// Populate <see cref="PluginOption.AllowedValues"/> or the host renders
    /// an empty combo-box.
    /// </summary>
    Select,

    /// <summary>
    /// Masked password box. The host stores the value encrypted at rest and
    /// masks it in the Settings UI. The plugin receives the plaintext value
    /// through <see cref="IPluginConfigurable.Values"/>.
    /// <strong>Use this for any credential, API key, or sensitive string —
    /// never use <see cref="Text"/> for secrets.</strong>
    /// Value type: <see cref="string"/>.
    /// </summary>
    Secret,
}
