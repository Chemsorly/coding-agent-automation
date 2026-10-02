using System.Collections;
using System.Globalization;
using System.Reflection;
using RangeAttribute = System.ComponentModel.DataAnnotations.RangeAttribute;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Checks settings against the <see cref="RangeAttribute"/>s on <see cref="PipelineConfiguration"/> and on the settings
/// objects it holds (<see cref="CodeReviewConfiguration"/> and its inline comment settings). The API uses it when global
/// settings or a project are saved or imported; the resolver uses it to skip a stored project override that is out of range,
/// so a bad value affects only its own setting.
/// </summary>
public static class PipelineSettingsValidator
{
    /// <summary>The copy method the compiler generates for every record; <c>with</c> expressions call it.</summary>
    private const string RecordCloneMethodName = "<Clone>$";

    /// <summary>One error per setting outside its range, for example "MaxRetries must be between 0 and 10 (was 12).".</summary>
    public static IReadOnlyList<string> Validate(PipelineConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        CollectErrors(config, typeof(PipelineConfiguration), prefix: "", errors);
        return errors;
    }

    /// <summary>One error per project override outside the range of the setting it overrides.</summary>
    public static IReadOnlyList<string> ValidateOverrides(PipelineProject project)
    {
        var errors = new List<string>();
        WithoutInvalidOverrides(project, errors);
        return errors;
    }

    /// <summary>
    /// The project without the overrides that are outside the range of the setting they override; <paramref name="errors"/>
    /// receives one message per removed override. Returns <paramref name="project"/> itself when every override is valid.
    /// </summary>
    public static PipelineProject WithoutInvalidOverrides(PipelineProject project, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(errors);

        object result = project;
        foreach (var setting in typeof(PipelineConfiguration).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (setting.GetCustomAttribute<ProjectOverridableAttribute>() is null)
                continue;
            var overrideProperty = typeof(PipelineProject).GetProperty(setting.Name);
            if (overrideProperty?.GetValue(project) is not { } value)
                continue;

            var valid = ValidPart(setting, value, setting.Name, errors);
            if (!ReferenceEquals(valid, value))
                result = CloneWith(result, overrideProperty, valid);
        }

        return (PipelineProject)result;
    }

    /// <summary>
    /// The range of a setting by its path, for example <c>"MaxRetries"</c> or <c>"CodeReview.InlineComments.MaxRetries"</c>;
    /// null when the setting has none.
    /// </summary>
    public static RangeAttribute? RangeOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var type = typeof(PipelineConfiguration);
        PropertyInfo? property = null;
        foreach (var name in path.Split('.'))
        {
            property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new ArgumentException($"'{path}' is not a setting.", nameof(path));
            type = property.PropertyType;
        }

        return property!.GetCustomAttribute<RangeAttribute>();
    }

    /// <summary>
    /// Every setting path: the properties of <see cref="PipelineConfiguration"/>, with the settings objects it holds
    /// (such as <see cref="PipelineConfiguration.CodeReview"/>) expanded into their own settings.
    /// </summary>
    public static IReadOnlyList<string> SettingPaths()
    {
        var paths = new List<string>();
        CollectPaths(typeof(PipelineConfiguration), prefix: "", paths);
        return paths;
    }

    /// <summary>The setting paths a project can override (see <see cref="ProjectOverridableAttribute"/>).</summary>
    public static IReadOnlyList<string> ProjectOverridablePaths()
    {
        var paths = new List<string>();
        foreach (var setting in typeof(PipelineConfiguration).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (setting.GetCustomAttribute<ProjectOverridableAttribute>() is null)
                continue;
            if (IsSettingsObject(setting.PropertyType))
                CollectPaths(setting.PropertyType, setting.Name + ".", paths);
            else
                paths.Add(setting.Name);
        }

        return paths;
    }

    private static void CollectPaths(Type type, string prefix, List<string> paths)
    {
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (IsSettingsObject(property.PropertyType))
                CollectPaths(property.PropertyType, prefix + property.Name + ".", paths);
            else
                paths.Add(prefix + property.Name);
        }
    }

    private static void CollectErrors(object settings, Type type, string prefix, List<string> errors)
    {
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var value = property.GetValue(settings);
            if (IsSettingsObject(property.PropertyType))
            {
                if (value is not null)
                    CollectErrors(value, property.PropertyType, prefix + property.Name + ".", errors);
                continue;
            }

            IsInRange(property, value, prefix + property.Name, errors);
        }
    }

    /// <summary>
    /// <paramref name="overrides"/> (for example a <see cref="CodeReviewOverrides"/>) without the values outside the range
    /// of the same-named setting on <paramref name="settingsType"/>; the object itself when every value is valid.
    /// </summary>
    private static object WithoutInvalidNested(object overrides, Type settingsType, string prefix, ICollection<string> errors)
    {
        var result = overrides;
        foreach (var overrideProperty in overrides.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var setting = settingsType.GetProperty(overrideProperty.Name, BindingFlags.Instance | BindingFlags.Public);
            if (setting is null || overrideProperty.GetValue(overrides) is not { } value)
                continue;

            var valid = ValidPart(setting, value, prefix + setting.Name, errors);
            if (!ReferenceEquals(valid, value))
                result = CloneWith(result, overrideProperty, valid);
        }

        return result;
    }

    /// <summary>
    /// The part of an override <paramref name="value"/> for <paramref name="setting"/> that is in range: the value itself,
    /// null when it is out of range, or for a settings object the object without its out-of-range values.
    /// </summary>
    private static object? ValidPart(PropertyInfo setting, object value, string path, ICollection<string> errors)
    {
        if (IsSettingsObject(setting.PropertyType))
            return WithoutInvalidNested(value, setting.PropertyType, path + ".", errors);

        return IsInRange(setting, value, path, errors) ? value : null;
    }

    private static bool IsInRange(PropertyInfo setting, object? value, string path, ICollection<string> errors)
    {
        var range = setting.GetCustomAttribute<RangeAttribute>();
        if (range is null || range.IsValid(value))
            return true;

        errors.Add(string.Format(
            CultureInfo.InvariantCulture,
            "{0} must be between {1} and {2} (was {3}).",
            path, range.Minimum, range.Maximum, value));
        return false;
    }

    /// <summary>A settings object holds settings of its own, such as <see cref="CodeReviewConfiguration"/>.</summary>
    private static bool IsSettingsObject(Type type) =>
        type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>A copy of a settings record with one property changed; init setters are ordinary setters to reflection.</summary>
    private static object CloneWith(object target, PropertyInfo property, object? value)
    {
        var clone = target.GetType().GetMethod(RecordCloneMethodName)!.Invoke(target, null)!;
        property.SetValue(clone, value);
        return clone;
    }
}
