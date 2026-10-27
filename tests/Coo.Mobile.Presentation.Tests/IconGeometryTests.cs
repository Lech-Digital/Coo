using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>
/// Every icon path parses and stays inside its 24 × 24 grid (Spec 005 §7), and is written so the drawing's parser reads
/// it as meant.
/// </summary>
public sealed partial class IconGeometryTests
{
    public static TheoryData<string> Icons => [.. IconGeometry.All.Keys];

    /// <summary>Every icon, by its field name: <see cref="IconGeometry.All"/> lists only some of them.</summary>
    public static TheoryData<string> Fields()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(IconGeometry).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
            data.Add(field.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void Geometry_EveryIcon_WritesEachNumberInFull(string name)
    {
        // Arrange
        var geometry = (string)typeof(IconGeometry).GetField(name)!.GetRawConstantValue()!;

        // Act
        var packed = PackedNumber().Match(geometry);

        // Assert
        packed.Success.Should().BeFalse($"{name} has \"{packed.Value}\"");
    }

    [Theory]
    [InlineData("l6.1.7")]
    [InlineData("l6.1-.7")]
    [InlineData("M18 14l.9 2")]
    public void Geometry_PackedNumber_IsCaught(string geometry)
    {
        // Act
        var caught = PackedNumber().IsMatch(geometry);

        // Assert
        caught.Should().BeTrue();
    }

    [Fact]
    public void Geometry_NumbersInFull_AreNotCaught()
    {
        // Act
        var caught = PackedNumber().IsMatch("M12 3l2.6 5.6 6.1 0.7-4.5 4.2");

        // Assert
        caught.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Icons))]
    public void Path_EveryIcon_ParsesAndFitsTheGrid(string icon)
    {
        // Arrange
        var data = IconGeometry.All[icon];

        // Act
        var points = PathPoints(data);

        // Assert
        points.Should().NotBeEmpty();
        points.Should().OnlyContain(p => p.X >= 0 && p.X <= 24 && p.Y >= 0 && p.Y <= 24, $"{icon} must stay inside 24 × 24");
    }

    [Fact]
    public void Path_SideIcons_AreMirrorImages()
    {
        // Arrange
        var left = PathPoints(IconGeometry.SideLeft);

        // Act
        var right = PathPoints(IconGeometry.SideRight);

        // Assert
        left.Should().Equal(right, "they share their end points; only the arc's sweep differs");
        IconGeometry.SideLeft.Should().NotBe(IconGeometry.SideRight);
    }

    /// <summary>
    /// A small SVG path reader: the commands Coo's icons use (M L H V C S A Z, both cases), returning the absolute end
    /// point of every segment. Unknown commands and wrong argument counts throw.
    /// </summary>
    private static List<(double X, double Y)> PathPoints(string data)
    {
        var tokens = Tokenize(data);
        var points = new List<(double, double)>();
        double x = 0, y = 0, startX = 0, startY = 0;
        var i = 0;
        char command = ' ';
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0]))
                command = tokens[i++][0];

            var relative = char.IsLower(command);
            double Next() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                case 'L':
                    var (mx, my) = (Next(), Next());
                    (x, y) = relative ? (x + mx, y + my) : (mx, my);
                    if (char.ToUpperInvariant(command) == 'M')
                    {
                        (startX, startY) = (x, y);
                        command = relative ? 'l' : 'L';
                    }

                    break;
                case 'H':
                    var hx = Next();
                    x = relative ? x + hx : hx;
                    break;
                case 'V':
                    var vy = Next();
                    y = relative ? y + vy : vy;
                    break;
                case 'C':
                    for (var k = 0; k < 4; k++)
                        Next();
                    var (cx, cy) = (Next(), Next());
                    (x, y) = relative ? (x + cx, y + cy) : (cx, cy);
                    break;
                case 'A':
                    for (var k = 0; k < 5; k++)
                        Next();
                    var (ax, ay) = (Next(), Next());
                    (x, y) = relative ? (x + ax, y + ay) : (ax, ay);
                    break;
                case 'Z':
                    (x, y) = (startX, startY);
                    break;
                default:
                    throw new FormatException($"Unsupported path command '{command}'.");
            }

            points.Add((Math.Round(x, 3), Math.Round(y, 3)));
        }

        return points;
    }

    // "6.1.7" (6.1 then 0.7), "-.7", "l.9": a number without its leading zero right after another number, a sign or a
    // command. The drawing's parser misread them (the milestone star drew as a squiggle).
    [GeneratedRegex(@"\d\.\d+\.\d|[-a-zA-Z\s,]\.\d")]
    private static partial Regex PackedNumber();

    private static List<string> Tokenize(string data)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(current.ToString());
            current.Clear();
        }

        foreach (var c in data)
        {
            if (char.IsLetter(c))
            {
                Flush();
                tokens.Add(c.ToString());
            }
            else if (c is ' ' or ',')
            {
                Flush();
            }
            else if (c == '-' && current.Length > 0)
            {
                Flush();
                current.Append(c);
            }
            else if (c == '.' && current.ToString().Contains('.', StringComparison.Ordinal))
            {
                Flush();
                current.Append(c);
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return tokens;
    }
}
