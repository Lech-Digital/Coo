using Coo.Api.Infrastructure.Ai;
using Coo.Domain.CareLog.Time;

namespace Coo.Api.Infrastructure.Tests;

public sealed class GeminiCareLogParserTests
{
    [Fact]
    public void Read_ModelJson_BecomesEntriesWithTimeExpressions()
    {
        // Arrange
        const string json = """
            {"entries":[
              {"type":"BreastStart","side":"left","time":{"kind":"now"},"sourceText":"Baby A started on the left"},
              {"type":"BottleFeeding","amount":120,"unit":"ml","milk":"formula","time":{"kind":"clock","hh":14,"mm":5},"sourceText":"120 ml at 14:05"},
              {"type":"Sleep","time":{"kind":"clock","hh":13},"endTime":{"kind":"minutesAgo","n":30},"sourceText":"slept 1 till half an hour ago"}
            ],"unparsed":["the weather"]}
            """;

        // Act
        var parsed = GeminiCareLogParser.Read(json);

        // Assert
        parsed.PromptVersion.Should().Be(GeminiCareLogParser.PromptVersion);
        parsed.Entries.Should().HaveCount(3);
        parsed.Entries[0].Time.Should().Be(TimeExpression.Now);
        parsed.Entries[1].Amount.Should().Be(120);
        parsed.Entries[1].Time.Should().Be(TimeExpression.Clock(14, 5));
        parsed.Entries[2].Time.Should().Be(TimeExpression.Clock(13, 0));
        parsed.Entries[2].EndTime.Should().Be(TimeExpression.MinutesAgo(30));
        parsed.Unparsed.Should().Equal("the weather");
    }

    [Fact]
    public void Read_EntriesWithoutTypeOrTime_AreSkippedNotGuessed()
    {
        // Act
        var parsed = GeminiCareLogParser.Read("""{"entries":[{"side":"left","time":{"kind":"now"}},{"type":"Diaper"}],"unparsed":[]}""");

        // Assert
        parsed.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Read_UnknownTimeKind_IsUnspecified()
    {
        // Act
        var parsed = GeminiCareLogParser.Read("""{"entries":[{"type":"Diaper","contents":"wet","time":{"kind":"yesterday-ish"},"sourceText":"x"}],"unparsed":[]}""");

        // Assert
        parsed.Entries.Single().Time.Should().Be(TimeExpression.Unspecified);
    }
}
