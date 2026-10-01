using System.Reflection;
using NetArchTest.Rules;

namespace Coo.Architecture.Tests;

/// <summary>SQLite is touched only by the persistence folder of the mobile infrastructure (Spec 004 §5, §10).</summary>
public sealed class PersistenceRuleTests
{
    [Fact]
    public void MobileInfrastructure_OutsidePersistence_DoesNotDependOnSqlite()
    {
        // Arrange
        var assembly = Assembly.Load("Coo.Mobile.Infrastructure");

        // Act
        var result = Types.InAssembly(assembly)
            .That().DoNotResideInNamespace("Coo.Mobile.Infrastructure.Persistence")
            .ShouldNot().HaveDependencyOnAny("Microsoft.Data.Sqlite", "SQLitePCL")
            .GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("Coo.Domain")]
    [InlineData("Coo.Codecs")]
    [InlineData("Coo.Mobile.Application")]
    [InlineData("Coo.Mobile.Presentation")]
    public void OtherProjects_DoNotDependOnSqlite(string assemblyName)
    {
        // Arrange
        var assembly = Assembly.Load(assemblyName);

        // Act
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny("Microsoft.Data.Sqlite", "SQLitePCL").GetResult();

        // Assert
        result.FailingTypeNames.Should().BeNullOrEmpty("the use cases never see SQL (Spec 004 §2)");
    }
}
