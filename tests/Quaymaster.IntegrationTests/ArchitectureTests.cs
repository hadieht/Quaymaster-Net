using System.Reflection;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Safety;

namespace Quaymaster.IntegrationTests;

/// <summary>Structural guarantees that cannot be seen from endpoint behaviour.</summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly[] ProductAssemblies =
    [
        typeof(IMutationGateway).Assembly,
        typeof(Quaymaster.Infrastructure.DockerDependencyInjection).Assembly,
        typeof(Program).Assembly,
    ];

    private static readonly HashSet<string> MayUseCommands = new(StringComparer.Ordinal)
    {
        nameof(MutationGateway), // the single gateway
        "DockerEngineAdapter", // implements IDockerCommands
        "ComposeCli", // implements IComposeCli
        "DockerDependencyInjection", // composition root: registers the seams
    };

    private static IEnumerable<Type> Users(Type seam)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in ProductAssemblies.SelectMany(a => a.GetTypes()))
        {
            if (type.IsInterface || seam.IsAssignableFrom(type))
            {
                continue;
            }

            var owner = type.DeclaringType ?? type; // closures of lambdas belong to their declaring class
            var uses =
                type.GetFields(all).Any(f => Mentions(f.FieldType, seam))
                || type.GetProperties(all).Any(p => Mentions(p.PropertyType, seam))
                || type.GetConstructors(all).Any(c => c.GetParameters().Any(p => Mentions(p.ParameterType, seam)))
                || type.GetMethods(all).Any(m => Mentions(m.ReturnType, seam) || m.GetParameters().Any(p => Mentions(p.ParameterType, seam)));
            if (uses)
            {
                yield return owner;
            }
        }
    }

    private static bool Mentions(Type t, Type seam) =>
        t == seam || (t.IsGenericType && t.GetGenericArguments().Any(a => Mentions(a, seam))) || (t.HasElementType && Mentions(t.GetElementType()!, seam));

    [Theory]
    [InlineData(typeof(IDockerCommands))]
    [InlineData(typeof(IComposeCli))]
    public void Only_The_Mutation_Gateway_Depends_On_The_Mutating_Seams(Type seam)
    {
        Users(seam).Select(t => t.Name).Distinct().Should().BeSubsetOf(MayUseCommands);
        Users(seam).Select(t => t.Name).Should().Contain(nameof(MutationGateway), "the gateway really is the consumer");
    }

    [Fact]
    public void The_Read_Interface_Has_No_Mutating_Members()
    {
        var names = typeof(IDockerEngine).GetMethods().Select(m => m.Name).ToList();
        names.Should().NotContain(n => n.Contains("Remove", StringComparison.Ordinal) || n.Contains("Delete", StringComparison.Ordinal)
            || n.Contains("Start", StringComparison.Ordinal) || n.Contains("Stop", StringComparison.Ordinal) || n.Contains("Create", StringComparison.Ordinal)
            || n.Contains("Exec", StringComparison.Ordinal) || n.Contains("Prune", StringComparison.Ordinal));
    }

    [Fact]
    public void Endpoints_Never_Use_The_Engine_Or_The_Seams_Directly()
    {
        var api = typeof(Program).Assembly;
        var offenders = api.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Quaymaster.Api.Endpoints", StringComparison.Ordinal) == true || (t.DeclaringType?.Namespace?.StartsWith("Quaymaster.Api.Endpoints", StringComparison.Ordinal) == true))
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(m => m.GetParameters().Any(p => p.ParameterType == typeof(IDockerEngine) || p.ParameterType == typeof(IDockerCommands) || p.ParameterType == typeof(IComposeCli))))
            .Select(t => t.FullName)
            .ToList();
        offenders.Should().BeEmpty("the Api layer only wires use cases");
    }
}
