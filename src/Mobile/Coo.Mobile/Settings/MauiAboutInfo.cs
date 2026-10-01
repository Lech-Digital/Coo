using System.Reflection;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Settings;

/// <summary>The build's informational version (SourceLink adds the commit) and Android's version (Spec 009 §3.6).</summary>
internal sealed class MauiAboutInfo : IAboutInfo
{
    public string Informational { get; } =
        typeof(MauiAboutInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? AppInfo.Current.VersionString;

    public string OsVersion => DeviceInfo.Current.VersionString;
}
