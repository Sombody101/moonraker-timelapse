using System.Reflection;

namespace PrintSpectacle.Models;

public class EnvironmentVariableResolver<T>
{
    protected EnvironmentVariableResolver()
    {
        IEnumerable<(PropertyInfo property, EnvVarAttribute attribute)> properties = typeof(T).GetProperties()
            .Select(p => (property: p, attribute: p.GetCustomAttribute<EnvVarAttribute>()))
            .Where(p => p.attribute is not null)!;

        foreach ((PropertyInfo property, EnvVarAttribute? attribute) in properties)
        {
            string variableName = attribute.VariableName;
            if (Environment.GetEnvironmentVariable(variableName) is string environmentValue)
            {
                var propertyValue = Convert.ChangeType(environmentValue, property.PropertyType);
                property.SetValue(this, propertyValue);
                continue;
            }

            if (attribute.DefaultValue is object defaultObject)
            {
                var defaultValue = Convert.ChangeType(defaultObject, property.PropertyType);
                property.SetValue(this, defaultValue);
            }
        }
    }
}
