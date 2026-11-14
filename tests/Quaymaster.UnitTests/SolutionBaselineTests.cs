using FluentAssertions;

namespace Quaymaster.UnitTests;

/// <summary>
/// Solution-level invariants. Feature unit tests live next to the feature in
/// later phases; these guard the shape of the solution itself.
/// </summary>
public class SolutionBaselineTests
{
    [Fact]
    public void Domain_Has_No_Infrastructure_Dependencies()
    {
        // Load explicitly: a ProjectReference alone does not load the assembly
        // until a type from it is touched.
        var domainAssembly = System.Reflection.Assembly.Load("Quaymaster.Domain");
        var domainRefs = domainAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        // The Domain assembly must stay framework-free: no EF Core, no
        // Microsoft.AspNetCore, no Docker SDK.
        var banned = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore.Http", "Docker.DotNet" };
        domainRefs.Should().NotContain(
            n => n == banned[0] || n == banned[1] || n == banned[2],
            "the Domain layer must stay free of infrastructure frameworks");
    }
}
