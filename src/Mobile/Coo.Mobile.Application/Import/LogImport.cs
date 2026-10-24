using System.Globalization;
using System.Text.RegularExpressions;
using Coo.Contracts.AiParsing;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.Import;

/// <summary>What a file holds, before anything is saved: the entries Coo understood, counted by kind, and what it could not use.</summary>
/// <param name="Entries">Each as a proposal, so they go through the same rules as Tell Coo's (finished feeds, bottles, sleeps).</param>
/// <param name="NoSide">Breast feeds with a length but no side: logged on the left, and said so.</param>
/// <param name="Source">"Huckleberry" when the file is recognised as theirs; otherwise empty.</param>
public sealed record ImportPreview(
    IReadOnlyList<AiProposal> Entries, int Feeds, int Diapers, int Sleeps, int Pumps, int Skipped, int NoSide,
    DateTimeOffset? From, DateTimeOffset? To, string Source)
{
    public static ImportPreview Empty { get; } = new([], 0, 0, 0, 0, 0, 0, null, null, "");
}

/// <param name="Added">New entries written.</param>
/// <param name="AlreadyThere">Entries Coo already had (the same kind starting within two minutes): not written twice.</param>
public sealed record ImportResult(int Added, int AlreadyThere, int Failed);

/// <summary>
/// Reads another baby app's CSV export (Huckleberry's, or any with recognisable columns) into proposals: no format is
/// assumed beyond a header row. Columns are found by their names in Coo's seven languages (type, start, end, duration,
/// amount, side, notes); every other cell is read for words: the side ("10:00 R", "left"), the milk ("formula"), the
/// diaper ("pee", "poo", "both"). Dates in day-first or month-first order are told apart by the file itself. Kinds Coo
/// does not log (solids, baths, tummy time) are counted as skipped, never guessed.
/// </summary>
public static partial class LogFileReader
{
    private static readonly string[] Huckleberry = ["type", "start", "end", "duration", "start condition", "start location", "end condition", "notes"];

    private static readonly Dictionary<string, string[]> Columns = new()
    {
        ["type"] = ["type", "activity", "event", "kind", "category", "typ", "rodzaj", "soort", "tipo", "categorie", "catégorie", "art", "aktivität"],
        ["start"] = ["start", "start time", "started", "begin", "from", "time", "datetime", "date time", "start date", "beginn", "startzeit", "początek", "start czas", "inicio", "début", "debut", "inizio", "starttijd", "begintijd"],
        ["date"] = ["date", "day", "datum", "data", "fecha", "jour"],
        ["end"] = ["end", "end time", "ended", "stop", "finish", "to", "ende", "endzeit", "koniec", "eind", "eindtijd", "fin", "fine"],
        ["duration"] = ["duration", "length", "minutes", "mins", "total duration", "dauer", "czas trwania", "czas", "duur", "durée", "duree", "duración", "duracion", "durata"],
        ["amount"] = ["amount", "quantity", "volume", "ml", "oz", "menge", "ilość", "ilosc", "hoeveelheid", "quantité", "quantite", "cantidad", "quantità", "quantita"],
        ["left"] = ["left minutes", "left min", "left duration"],
        ["right"] = ["right minutes", "right min", "right duration"],
        ["notes"] = ["notes", "note", "comment", "comments", "notiz", "notizen", "notatka", "notatki", "notitie", "notities", "remarque", "nota", "notas"],
    };

    private static readonly string[] FeedWords = ["feed", "nurs", "breast", "bottle", "formula", "karmien", "pierś", "piers", "butelk", "voeding", "fles", "borst", "still", "flasche", "fütter", "futter", "tétée", "tetee", "biberon", "allait", "toma", "pecho", "poppata", "allatt", "seno"];
    private static readonly string[] SleepWords = ["sleep", "nap", "sen", "drzemk", "spanie", "slaap", "dutje", "schlaf", "nicker", "sommeil", "sieste", "dodo", "sueño", "sueno", "siesta", "sonno", "pisolino", "nanna"];
    private static readonly string[] DiaperWords = ["diaper", "nappy", "nappies", "pee", "poo", "pieluch", "luier", "windel", "couche", "pañal", "panal", "pannolin"];
    private static readonly string[] BathWords = ["bath", "kąpiel", "kapiel", "bad ", "badje", "baden", "bain", "baño", "bano", "bagnetto", "bagno"];
    private static readonly string[] PumpWords = ["pump", "express", "odciąg", "odciag", "kolf", "abpump", "tire-lait", "tire lait", "extracc", "sacaleche", "tiralatte"];
    private static readonly string[] BottleWords = ["bottle", "formula", "butelk", "mleko modyfik", "fles", "kunstvoeding", "flasche", "pre-milch", "biberon", "lait infantile", "biberón", "fórmula", "formula", "latte artificiale"];
    private static readonly string[] FormulaWords = ["formula", "fórmula", "modyfik", "kunstvoeding", "flesvoeding", "pre-milch", "pre milch", "lait infantile", "artificiale"];
    private static readonly string[] DirtyWords = ["poo", "poop", "dirty", "bm", "stool", "soiled", "kup", "kupk", "poep", "stuhl", "voll", "selle", "caca", "cacca", "sucio"];
    private static readonly string[] WetWords = ["pee", "wet", "urine", "wee", "siku", "mokr", "plas", "nat", "nass", "pipi", "mouill", "mojad", "bagnat"];
    private static readonly string[] MixedWords = ["both", "mixed", "pee and poo", "wet and dirty", "oba", "beide", "les deux", "ambos", "entramb", "misto"];

    public static ImportPreview Read(string text, TimeZoneInfo zone, CultureInfo fallback)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(fallback);
        var rows = CsvText.Read(text ?? "");
        if (rows.Count < 2)
            return ImportPreview.Empty;

        var header = rows[0].Select(Normalise).ToList();
        var columns = FindColumns(header);
        if (!columns.TryGetValue("start", out var startColumn))
            return ImportPreview.Empty with { Skipped = rows.Count - 1 };
        var isHuckleberry = Huckleberry.All(header.Contains);
        var dayFirst = DayFirst(rows.Skip(1).Select(r => Cell(r, startColumn)), fallback);

        var entries = new List<AiProposal>();
        int feeds = 0, diapers = 0, sleeps = 0, pumps = 0, skipped = 0, noSide = 0;
        foreach (var row in rows.Skip(1))
        {
            var proposal = Row(row, columns, zone, dayFirst, out var sideMissing);
            if (proposal is null || QuickLogEntries.NewEntry(proposal) is null)
            {
                skipped++;
                continue;
            }

            entries.Add(proposal);
            switch (proposal.Action)
            {
                case AiActions.BreastLog or AiActions.BottleLog:
                    feeds++;
                    break;
                case AiActions.DiaperLog:
                    diapers++;
                    break;
                case AiActions.SleepLog:
                    sleeps++;
                    break;
                case AiActions.PumpLog:
                    pumps++;
                    break;
            }

            if (sideMissing)
                noSide++;
        }

        return new ImportPreview(
            [.. entries.OrderBy(e => e.At)], feeds, diapers, sleeps, pumps, skipped, noSide,
            entries.Count == 0 ? null : entries.Min(e => e.At), entries.Count == 0 ? null : entries.Max(e => e.At),
            isHuckleberry ? "Huckleberry" : "");
    }

    private static Dictionary<string, int> FindColumns(List<string> header)
    {
        var found = new Dictionary<string, int>();

        // Exact names first, so "start condition" never takes the place of "start".
        foreach (var exact in new[] { true, false })
        {
            foreach (var (role, names) in Columns)
            {
                if (found.ContainsKey(role))
                    continue;
                var index = header.FindIndex(h => !found.ContainsValue(header.IndexOf(h))
                    && names.Any(n => exact ? h == n : h.StartsWith(n + " ", StringComparison.Ordinal) && !h.Contains("condition", StringComparison.Ordinal) && !h.Contains("location", StringComparison.Ordinal)));
                if (index >= 0)
                    found[role] = index;
            }
        }

        // A date column and a time column: the start is both together.
        if (!found.ContainsKey("start") && found.TryGetValue("date", out var date))
            found["start"] = date;
        else if (found.TryGetValue("date", out var day) && found["start"] != day && header[found["start"]] is "time" or "tijd" or "zeit" or "godzina" or "heure" or "hora" or "ora")
            found["start-time"] = found["start"];
        if (found.TryGetValue("start-time", out _))
            found["start"] = found["date"];
        return found;
    }

    private static AiProposal? Row(IReadOnlyList<string> row, Dictionary<string, int> columns, TimeZoneInfo zone, bool dayFirst, out bool sideMissing)
    {
        sideMissing = false;
        var startText = Cell(row, columns["start"]);
        if (columns.TryGetValue("start-time", out var timeColumn))
            startText = $"{startText} {Cell(row, timeColumn)}";
        if (When(startText, zone, dayFirst) is not { } start)
            return null;

        var used = new HashSet<int>(columns.Values.Where(i => i != columns.GetValueOrDefault("type", -1) && i != columns.GetValueOrDefault("amount", -1)));
        var words = string.Join(" | ", row.Where((_, i) => !used.Contains(i))).ToLowerInvariant();
        var type = (columns.TryGetValue("type", out var t) ? Cell(row, t) : words).ToLowerInvariant();

        DateTimeOffset? end = columns.TryGetValue("end", out var e) ? When(Cell(row, e), zone, dayFirst) : null;
        var duration = columns.TryGetValue("duration", out var d) ? Length(Cell(row, d)) : null;
        if (end is null && duration is { } length)
            end = start + length;
        if (end is { } finish && finish < start)
            end = finish.AddDays(1);

        decimal? Millilitres()
        {
            var amountText = columns.TryGetValue("amount", out var a) ? Cell(row, a) : "";
            // In the amount column any number counts; elsewhere only one with its unit ("120ml"), never a time.
            var match = amountText.Length > 0
                ? AmountPattern().Match(amountText)
                : AmountPattern().Matches(words).FirstOrDefault(m => m.Groups["u"].Success && m.Groups["u"].Value.Length > 0);
            if (match is not { Success: true } || !decimal.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || n <= 0)
                return null;
            var unit = match.Groups["u"].Value.ToLowerInvariant();
            return Math.Round(unit.StartsWith("oz", StringComparison.Ordinal) || unit.StartsWith("fl", StringComparison.Ordinal) ? n * Volume.MillilitresPerUsFluidOunce : n, 0);
        }

        AiProposal Proposal(string action, DateTimeOffset? endAt = null, int? left = null, int? right = null, decimal? ml = null, string? milk = null, string? contents = null) =>
            new(action, start, endAt, null, left, right, ml, milk, contents, startText, []);

        if (Any(type + " ", BathWords))
            return Proposal(AiActions.BathLog);
        if (Any(type, PumpWords))
            return Proposal(AiActions.PumpLog, end, ml: Millilitres());
        if (Any(type, DiaperWords) || (!Any(type, FeedWords) && !Any(type, SleepWords) && (Any(words, DirtyWords) || Any(words, WetWords)) && columns.ContainsKey("type") is false))
        {
            var all = $"{type} | {words}";
            var contents = Any(all, MixedWords) || (Any(all, DirtyWords) && Any(all, WetWords)) ? "mixed" : Any(all, DirtyWords) ? "dirty" : "wet";
            return Proposal(AiActions.DiaperLog, contents: contents);
        }

        if (Any(type, SleepWords))
            return end is { } woke ? Proposal(AiActions.SleepLog, woke) : null;

        if (!Any(type, FeedWords))
            return null;
        var ml = Millilitres();
        if (ml is not null || Any($"{type} | {words}", BottleWords))
        {
            var milk = Any($"{type} | {words}", FormulaWords) ? "formula" : "breast";
            return ml is null ? null : Proposal(AiActions.BottleLog, ml: ml, milk: milk);
        }

        // Coo's own export: a column of minutes per side.
        int ColumnMinutes(string role) => columns.TryGetValue(role, out var c) && int.TryParse(Cell(row, c), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var (leftMinutes, rightMinutes) = (ColumnMinutes("left"), ColumnMinutes("right")) is (0, 0) ? Sides(words) : (ColumnMinutes("left"), ColumnMinutes("right"));
        if (leftMinutes + rightMinutes == 0)
        {
            var total = (int)Math.Round((duration ?? (end - start) ?? TimeSpan.Zero).TotalMinutes);
            if (total <= 0)
                return null;
            var right = Regex.IsMatch(words, @"\b(right|rechts|prawa|droite|derecha|destra)\b");
            sideMissing = !right && !Regex.IsMatch(words, @"\b(left|links|lewa|gauche|izquierda|sinistra)\b");
            (leftMinutes, rightMinutes) = right ? (0, total) : (total, 0);
        }

        return Proposal(AiActions.BreastLog, left: leftMinutes > 0 ? leftMinutes : null, right: rightMinutes > 0 ? rightMinutes : null);
    }

    /// <summary>"10:00R, 05:00L", "L 10m R 5m", "left 10 min": minutes per side.</summary>
    internal static (int Left, int Right) Sides(string words)
    {
        int left = 0, right = 0;
        foreach (Match m in ClockSide().Matches(words))
        {
            var minutes = (int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
            // "0:10R" is minutes:seconds in some exports, hours:minutes in others; a side over three hours is not a side.
            if (minutes > 180)
                minutes = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["s"].Value.StartsWith('l'))
                left += minutes;
            else
                right += minutes;
        }

        foreach (Match m in WordSide().Matches(words))
        {
            var minutes = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["s"].Value.StartsWith('l'))
                left += minutes;
            else
                right += minutes;
        }

        return (left, right);
    }

    internal static TimeSpan? Length(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0)
            return null;
        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return parts.Length == 3 ? new TimeSpan(n[0], n[1], n[2]) : new TimeSpan(n[0], n[1], 0);
        }

        if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var minutes))
            return TimeSpan.FromMinutes((double)minutes);
        var hm = HoursMinutes().Match(text);
        return hm.Success && (hm.Groups["h"].Success || hm.Groups["m"].Success)
            ? TimeSpan.FromHours(hm.Groups["h"].Success ? int.Parse(hm.Groups["h"].Value, CultureInfo.InvariantCulture) : 0)
              + TimeSpan.FromMinutes(hm.Groups["m"].Success ? int.Parse(hm.Groups["m"].Value, CultureInfo.InvariantCulture) : 0)
            : null;
    }

    /// <summary>A local date and time in the household's zone; ISO, day-first or month-first, 12- or 24-hour.</summary>
    internal static DateTimeOffset? When(string text, TimeZoneInfo zone, bool dayFirst)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && HasOffset(text))
            return withOffset;

        DateTime local;
        var iso = Regex.IsMatch(text, @"^\d{4}-\d{1,2}-\d{1,2}");
        var normalised = iso ? text.Replace('T', ' ') : Regex.Replace(text, @"^(\d{1,2})[.\-](\d{1,2})[.\-](\d{2,4})", "$1/$2/$3");
        var culture = CultureInfo.GetCultureInfo(iso ? "" : dayFirst ? "en-GB" : "en-US");
        if (!DateTime.TryParse(normalised.Replace(",", ""), culture, DateTimeStyles.AllowWhiteSpaces, out local))
            return null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static bool HasOffset(string text) => Regex.IsMatch(text, @"(Z|[+\-]\d{2}:?\d{2})$");

    // Any "13/10" says day first, any "10/13" month first; otherwise the phone's own habit decides.
    private static bool DayFirst(IEnumerable<string> starts, CultureInfo fallback)
    {
        foreach (var text in starts)
        {
            var m = Regex.Match(text, @"^(\d{1,2})[./\-](\d{1,2})[./\-]\d{2,4}");
            if (!m.Success)
                continue;
            if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 12)
                return true;
            if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > 12)
                return false;
        }

        return fallback.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith('d');
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index < row.Count ? row[index] : "";

    private static string Normalise(string header) => Regex.Replace(header.Trim().ToLowerInvariant(), @"[_\-\(\)\[\]:]+", " ").Replace("  ", " ").Trim();

    private static bool Any(string text, string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>ml|oz|fl ?oz)?", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"(?<a>\d{1,2}):(?<b>\d{2})\s*(?<s>[lr])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockSide();

    [GeneratedRegex(@"\b(?<s>l|r|left|right)\s*(?<n>\d{1,3})\s*(?:m|min|mins|minutes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordSide();

    [GeneratedRegex(@"(?:(?<h>\d+)\s*(?:h|hr|hrs|hour|hours|godz|uur|std))?\s*(?:(?<m>\d+)\s*(?:m|min|mins|minutes))?")]
    private static partial Regex HoursMinutes();
}

/// <summary>
/// "Import from another app": reads the file into a preview, and on the person's go, writes what Coo does not have yet
/// through the ordinary write path, for the shown family's first baby. Entries are plain manual entries: they sync and
/// can be edited like any other.
/// </summary>
public sealed class ImportLog(IHouseholdStore households, ICareLogStore store, LogEntry log)
{
    /// <summary>The same kind starting this close to an entry already logged is that entry.</summary>
    public static readonly TimeSpan SameEntry = TimeSpan.FromMinutes(2);

    public async Task<ImportPreview?> PreviewAsync(string text, CultureInfo fallback, CancellationToken cancellationToken)
    {
        var (household, _) = await PrepareQuickLog.ShownAsync(households, cancellationToken);
        return household is null ? null : LogFileReader.Read(text, TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId), fallback);
    }

    public async Task<ImportResult> ExecuteAsync(ImportPreview preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var (household, baby) = await PrepareQuickLog.ShownAsync(households, cancellationToken);
        if (household is null || baby is null || preview.From is not { } from || preview.To is not { } to)
            return new ImportResult(0, 0, preview.Entries.Count);

        var existing = (await store.ListAsync(household.Id, from.AddDays(-1), to.AddDays(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry).Where(e => !e.IsDeleted)
            .Select(e => (e.Details.GetType(), e.Timing.OccurredAt)).ToList();
        int added = 0, already = 0, failed = 0;
        foreach (var proposal in preview.Entries)
        {
            if (QuickLogEntries.NewEntry(proposal) is not { } shape)
            {
                failed++;
                continue;
            }

            var startsAt = shape.Timing?.OccurredAt ?? proposal.At;
            if (existing.Any(x => x.Item1 == shape.Details.GetType() && (x.OccurredAt - startsAt).Duration() <= SameEntry))
            {
                already++;
                continue;
            }

            var subject = shape.Details is PumpDetails ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(baby.Id);
            var result = await log.ExecuteAsync(
                new LogEntryCommand(household.Id, subject, shape.Details, shape.Timing, PersonRef.ThisDevicesPerson, EntrySource.Manual), cancellationToken);
            if (result.IsSuccess)
            {
                added++;
                existing.Add((shape.Details.GetType(), startsAt));
            }
            else
            {
                failed++;
            }
        }

        return new ImportResult(added, already, failed);
    }
}
