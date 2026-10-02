using Coo.Api.Application.Sync;
using Coo.Api.Infrastructure.Persistence;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>The server's person and source columns are <see cref="SyncText"/>'s forms: one definition (Spec 004 §4.5).</summary>
public sealed class ServerColumnsTests
{
    [Fact]
    public void Person_NullReference_FailsAsSyncTextDoes()
    {
        // Arrange
        var act = () => ServerColumns.Person(null!);

        // Act
        var thrown = act.Should().Throw<ArgumentOutOfRangeException>();

        // Assert
        thrown.Which.ParamName.Should().Be("person");
    }

    [Fact]
    public void Source_NullSource_FailsAsSyncTextDoes()
    {
        // Arrange
        var act = () => ServerColumns.Source(null!);

        // Act
        var thrown = act.Should().Throw<ArgumentNullException>();

        // Assert
        thrown.Which.ParamName.Should().Be("source");
    }

    [Theory]
    [InlineData("user:7a1c0000-0000-7000-8000-0000000000c1", null)]
    [InlineData("this-device", null)]
    [InlineData("user:00000000-0000-0000-0000-000000000000", null)]
    [InlineData("ai-parsed", "p-3")]
    [InlineData("ai-parsed", " ")]
    [InlineData("timer", null)]
    public void ParsePersonAndSource_AnyText_ReadAsSyncTextReadsIt(string text, string? promptVersion)
    {
        // Arrange
        var expectedPerson = SyncText.ParsePerson(text);
        var expectedSource = SyncText.ParseSource(text, promptVersion);

        // Act
        var person = ServerColumns.ParsePerson(text);
        var source = ServerColumns.ParseSource(text, promptVersion);

        // Assert
        person.Should().Be(expectedPerson);
        source.Should().Be(expectedSource);
        ServerColumns.ThisDevice.Should().Be(SyncText.ThisDevice);
        if (person is not null)
            ServerColumns.Person(person).Should().Be(SyncText.Person(person));
        if (source is not null)
            ServerColumns.Source(source).Should().Be(SyncText.Source(source));
    }
}
