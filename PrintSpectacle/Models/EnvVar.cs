namespace PrintSpectacle.Models;

[AttributeUsage(AttributeTargets.Property)]
public sealed class EnvVarAttribute(string variableName, object? defaultValue = null) : Attribute
{
    public string VariableName { get; set; } = variableName;

    public object? DefaultValue { get; set; } = defaultValue;
}
