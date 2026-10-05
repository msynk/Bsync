using System.Reflection;
using System.Text;
using Xunit;

namespace Bsync.Tests;

/// <summary>
/// Phase 10: the public API of every package is recorded in <c>Tests/api/{assembly}.txt</c> (ADR-011, ADR-012). A change to
/// the public surface fails this test until the baseline is updated on purpose: run the tests with
/// <c>BSYNC_UPDATE_API=1</c>, review the diff, and add breaking changes to docs/compatibility.md.
/// </summary>
public sealed class PublicApiTests
{
    public static TheoryData<string> Packages() =>
    [
        "Bsync",
        "Bsync.Blazor",
        "Bsync.Server.AspNetCore",
        "Bsync.Server.PostgreSql",
        "Bsync.Server.SqlServer",
        "Bsync.Storage.Sqlite",
        "Bsync.Testing",
    ];

    [Theory(DisplayName = "The public API matches the reviewed baseline")]
    [MemberData(nameof(Packages))]
    public void MatchesBaseline(string assemblyName)
    {
        var actual = Describe(Assembly.Load(assemblyName));
        var path = Path.Combine(RepositoryRoot(), "Tests", "api", $"{assemblyName}.txt");
        if (Environment.GetEnvironmentVariable("BSYNC_UPDATE_API") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"No API baseline at {path}; run the tests with BSYNC_UPDATE_API=1.");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (expected != actual)
        {
            var added = actual.Split('\n').Except(expected.Split('\n')).Take(20);
            var removed = expected.Split('\n').Except(actual.Split('\n')).Take(20);
            Assert.Fail($"The public API of {assemblyName} changed.\nAdded:\n  {string.Join("\n  ", added)}\nRemoved:\n  {string.Join("\n  ", removed)}\nUpdate the baseline with BSYNC_UPDATE_API=1 after review.");
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bsync.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The solution directory was not found.");
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string Describe(Assembly assembly)
    {
        var text = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append(TypeHeader(type)).Append('\n');
            var members = new List<string>();
            if (type.IsEnum)
            {
                members.AddRange(Enum.GetNames(type).Select(name => $"  {name} = {Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}"));
            }
            else
            {
                foreach (var member in type.GetMembers(Declared).Where(IsVisible))
                {
                    if (Member(member) is { } line)
                    {
                        members.Add("  " + line);
                    }
                }
            }

            foreach (var line in members.Order(StringComparer.Ordinal))
            {
                text.Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    private static bool IsVisible(MemberInfo member) => member switch
    {
        MethodBase method => (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) && !method.IsSpecialName || method is ConstructorInfo { IsPublic: true } or ConstructorInfo { IsFamily: true },
        FieldInfo field => (field.IsPublic || field.IsFamily) && !field.IsSpecialName,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily),
        EventInfo @event => @event.AddMethod is { } add && (add.IsPublic || add.IsFamily),
        Type nested => nested.IsNestedPublic || nested.IsNestedFamily,
        _ => false,
    };

    private static string TypeHeader(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
            : type.GetMethod("<Clone>$") is not null ? "record"
            : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";
        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum) && baseType != typeof(MulticastDelegate))
        {
            bases.Add(Name(baseType));
        }

        bases.AddRange(type.GetInterfaces()
            .Where(i => !type.IsEnum && (i.IsPublic || i.IsNestedPublic))
            .Where(i => !i.Name.StartsWith("IEquatable", StringComparison.Ordinal))
            .Select(Name)
            .Order(StringComparer.Ordinal));
        return $"{kind} {Name(type)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty)}{Constraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])}";
    }

    private static string? Member(MemberInfo member) => member switch
    {
        ConstructorInfo ctor => $"{Access(ctor)}ctor({Parameters(ctor)})",
        MethodInfo method when method.Name is "<Clone>$" => null,
        MethodInfo method => $"{Access(method)}{(method.IsStatic ? "static " : method.IsAbstract ? "abstract " : method.IsVirtual && !method.IsFinal ? "virtual " : string.Empty)}{Name(method.ReturnType)} {method.Name}{(method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>" : string.Empty)}({Parameters(method)}){Constraints(method.IsGenericMethodDefinition ? method.GetGenericArguments() : [])}",
        PropertyInfo property => $"{Access(property.GetAccessors(true).First())}{(property.GetAccessors(true).First().IsStatic ? "static " : string.Empty)}{Name(property.PropertyType)} {property.Name} {{ {Accessors(property)}}}",
        FieldInfo field => $"{Access(field)}{(field.IsLiteral ? $"const {Name(field.FieldType)} {field.Name} = {Literal(field.GetRawConstantValue())}" : $"{(field.IsStatic ? "static " : string.Empty)}{(field.IsInitOnly ? "readonly " : string.Empty)}{Name(field.FieldType)} {field.Name}")}",
        EventInfo @event => $"public event {Name(@event.EventHandlerType!)} {@event.Name}",
        Type nested => $"nested {Name(nested)}",
        _ => null,
    };

    private static string Accessors(PropertyInfo property)
    {
        var text = new StringBuilder();
        if (property.GetMethod is { } get && (get.IsPublic || get.IsFamily))
        {
            text.Append("get; ");
        }

        if (property.SetMethod is { } set && (set.IsPublic || set.IsFamily))
        {
            var init = set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            text.Append(init ? "init; " : "set; ");
        }

        return text.ToString();
    }

    private static string Access(MemberInfo member) => member switch
    {
        MethodBase { IsFamily: true } or FieldInfo { IsFamily: true } => "protected ",
        _ => string.Empty,
    };

    private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(p =>
        $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty)}{Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue is null && p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null ? "default" : Literal(p.DefaultValue)) : string.Empty)}"));

    private static string Constraints(Type[] arguments)
    {
        var text = new StringBuilder();
        foreach (var argument in arguments)
        {
            var parts = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add("class");
            }

            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("struct");
            }

            parts.AddRange(argument.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(Name).Order(StringComparer.Ordinal));
            if (parts.Count > 0)
            {
                text.Append($" where {argument.Name} : {string.Join(", ", parts)}");
            }
        }

        return text.ToString();
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying) + "?";
        }

        var name = type.IsNested ? $"{Name(type.DeclaringType!)}.{type.Name}" : type.Namespace is null ? type.Name : $"{type.Namespace}.{type.Name}";
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        if (type.IsNested && type.DeclaringType!.IsGenericType)
        {
            arguments = arguments.Skip(type.DeclaringType.GetGenericArguments().Length).ToArray();
        }

        return arguments.Length == 0 ? name : $"{name}<{string.Join(", ", arguments.Select(Name))}>";
    }
}
