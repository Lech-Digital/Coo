using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Reports;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Reports;

/// <summary>
/// "Report for the doctor": choose 7, 14 or 30 days and the language — the app's, or the doctor's — and the PDF opens
/// in the share sheet.
/// </summary>
public sealed partial class ReportViewModel(BuildCareReport build, IReportExporter exporter, IClockFormat clock) : ObservableObject
{
    public static IReadOnlyList<int> Periods { get; } = [7, 14, 30];

    [ObservableProperty]
    private int _days = 14;

    /// <summary>The report's language; the app's until the parent picks another.</summary>
    [ObservableProperty]
    private string _language = Strings.Language;

    [ObservableProperty]
    private bool _isMaking;

    public static IReadOnlyList<string> Languages => Strings.Supported;

    [ObservableProperty]
    private string _message = "";

    [RelayCommand]
    private void SetDays(int days) => Days = days;

    [RelayCommand]
    private void SetLanguage(string language) => Language = Strings.Supported.Contains(language) ? language : Strings.Language;

    [RelayCommand]
    private async Task Make()
    {
        if (IsMaking)
            return;
        (IsMaking, Message) = (true, "");
        try
        {
            var report = await build.ExecuteAsync(Days, CancellationToken.None);
            if (report is null)
            {
                Message = Strings.Get("Report.NoBirth");
                return;
            }

            ReportDocument document;
            string fileName;
            using (Strings.InLanguage(Language))
            {
                document = ReportComposer.Compose(report, new Formats(clock.Is24Hour, report.Household.VolumeUnit));
                fileName = ReportComposer.FileName(report);
            }

            if (!await exporter.ShareAsync(document, fileName, CancellationToken.None))
                Message = Strings.Get("Report.Failed");
        }
#pragma warning disable CA1031 // A report that cannot be made says so; it never takes Stats down with it.
        catch (Exception)
#pragma warning restore CA1031
        {
            Message = Strings.Get("Report.Failed");
        }
        finally
        {
            IsMaking = false;
        }
    }
}
