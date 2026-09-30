using System.Reflection;
using System.Runtime.CompilerServices;
using NetArchTest.Rules;

namespace Coo.Architecture.Tests;

/// <summary>Type-level rules over the built assemblies (Spec 002 §6).</summary>
public sealed class TypeRuleTests
{
    // Spec 001 §3.5: a deliberate, documented exception to CS-MOD-01. Changing it is a design decision.
    private static readonly string[] ApprovedDomainFriends =
    [
        "Coo.Api.Application",
        "Coo.Api.Application.Tests",
        "Coo.Api.Infrastructure",
        "Coo.Api.Infrastructure.Tests",
        "Coo.Codecs.Tests",
        "Coo.Domain.Tests",
        "Coo.Mobile.Application",
        "Coo.Mobile.Application.Tests",
        "Coo.Mobile.Infrastructure",
        "Coo.Mobile.Infrastructure.Tests",
    ];

    [Fact]
    public void Domain_Types_DoNotDependOnSystemTextJson()
    {
        // Arrange
        var domain = Assembly.Load("Coo.Domain");

        // Act
        var result = Types.InAssembly(domain).ShouldNot().HaveDependencyOn("System.Text.Json").GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty("the Domain never sees JSON (ADR 0010)");
    }

    [Theory]
    [InlineData("Coo.Api.Infrastructure")]
    [InlineData("Coo.Mobile.Infrastructure")]
    public void Infrastructure_Types_AreInternalExceptServiceCollectionExtensions(string assemblyName)
    {
        // Arrange
        var assembly = Assembly.Load(assemblyName);

        // Act
        var result = Types.InAssembly(assembly)
            .That().DoNotHaveName("ServiceCollectionExtensions")
            .Should().NotBePublic()
            .GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty("implementation classes are internal (CS-MOD-01)");
    }

    [Fact]
    public void Domain_InternalsVisibleTo_IsExactlyTheApprovedList()
    {
        // Arrange
        var domain = Assembly.Load("Coo.Domain");

        // Act
        var friends = domain.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .ToList();

        // Assert
        friends.Should().BeEquivalentTo(ApprovedDomainFriends);
    }
}
