// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Aetheus.Back.Components.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Prevents new HTTP request strings from reaching model binding without an explicit
/// maximum length. Only DTOs reachable from controller body or query parameters are in scope;
/// response-only DTOs are deliberately excluded.</summary>
public class InboundStringBoundsAuditTests
{
    [Fact]
    public void EveryControllerInputDtoString_HasAnExplicitUpperBound()
    {
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        var controllerAssembly = typeof(AuthController).Assembly;

        foreach (var controller in controllerAssembly.GetTypes()
                     .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type)))
        {
            foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in method.GetParameters().Where(IsInputDtoParameter))
                {
                    var path = $"{controller.Name}.{method.Name}({parameter.Name})";
                    if (TryGetDictionary(parameter.ParameterType, out var keyType, out var valueType)
                        && (keyType == typeof(string) || valueType == typeof(string)))
                    {
                        if (parameter.GetCustomAttribute<BoundedDictionaryAttribute>() is null)
                            failures.Add(path + " (direct dictionary body)");
                        continue;
                    }
                    Inspect(parameter.ParameterType, path, failures, []);
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Inbound request strings without an explicit upper bound:\n  - "
            + string.Join("\n  - ", failures));
    }

    private static bool IsInputDtoParameter(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<FromBodyAttribute>() is not null) return true;
        return parameter.GetCustomAttribute<FromQueryAttribute>() is not null
            && IsApplicationDto(Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType);
    }

    private static void Inspect(
        Type type,
        string path,
        ISet<string> failures,
        HashSet<Type> ancestry)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            failures.Add(path + " (direct string body)");
            return;
        }

        if (TryGetDictionary(type, out _, out _)) return;

        if (TryGetEnumerableElement(type, out var elementType))
        {
            Inspect(elementType, path + "[]", failures, ancestry);
            return;
        }

        if (!IsApplicationDto(type) || !ancestry.Add(type)) return;
        try
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                InspectProperty(property, $"{path}.{property.Name}", failures, ancestry);
        }
        finally
        {
            ancestry.Remove(type);
        }
    }

    /// <summary>One property of an inbound DTO; recette R2-107 moved it out of <see cref="Inspect"/>, under the grade-A cyclomatic threshold.</summary>
    private static void InspectProperty(PropertyInfo property, string propertyPath, ISet<string> failures, HashSet<Type> ancestry)
    {
        if (property.PropertyType == typeof(string))
        {
            if (property.GetCustomAttribute<StringLengthAttribute>() is null
                && property.GetCustomAttribute<MaxLengthAttribute>() is null)
            {
                failures.Add(propertyPath);
            }
            return;
        }

        if (TryGetDictionary(property.PropertyType, out var keyType, out var valueType))
        {
            if ((keyType == typeof(string) || valueType == typeof(string))
                && property.GetCustomAttribute<BoundedDictionaryAttribute>() is null)
            {
                failures.Add(propertyPath + " (dictionary)");
            }
            return;
        }

        if (TryGetEnumerableElement(property.PropertyType, out var elementType))
        {
            if (elementType == typeof(string))
            {
                if (property.GetCustomAttribute<MaxItemStringLengthAttribute>() is null)
                    failures.Add(propertyPath + "[]");
            }
            else
            {
                Inspect(elementType, propertyPath + "[]", failures, ancestry);
            }
            return;
        }

        Inspect(property.PropertyType, propertyPath, failures, ancestry);
    }

    private static bool TryGetEnumerableElement(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }
        var enumerable = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementType = enumerable?.GetGenericArguments()[0] ?? typeof(void);
        return enumerable is not null;
    }

    private static bool TryGetDictionary(Type type, out Type keyType, out Type valueType)
    {
        var dictionary = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is null)
        {
            keyType = valueType = typeof(void);
            return false;
        }
        var arguments = dictionary.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool IsApplicationDto(Type type) =>
        type.Namespace?.StartsWith("Aetheus.", StringComparison.Ordinal) == true
        && !type.IsEnum
        && !type.IsPrimitive;
}
