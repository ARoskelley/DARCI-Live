#nullable enable

using System.Runtime.InteropServices;

namespace DarciControl.Logic.Packaging;

/// <summary>Which OS a distributable is being built FOR — not necessarily the one building it.</summary>
public enum TargetOs
{
    Windows = 0,
    Linux = 1,
}

/// <summary>
/// Everything that differs between a Windows and a Linux distributable, in one place.
///
/// <para>Parameterised now rather than retrofitted later. The differences are small individually — an
/// executable suffix, a launcher extension, a path separator inside a generated script — but they are
/// scattered across publishing, assembly and script generation, and each one hardcoded is a place the
/// Linux path silently produces a broken zip instead of failing.</para>
///
/// <para><b>Verification honesty:</b> the Windows target is built and booted end-to-end here. The Linux
/// target is <i>structurally</i> supported — correct RID, correct executable name, a shell launcher with
/// its executable bit set — but this machine is Windows, so it has never been RUN. That verification
/// belongs to whoever boots Linux.</para>
/// </summary>
public sealed record TargetPlatform
{
    public required TargetOs Os { get; init; }

    /// <summary>The .NET RID handed to <c>dotnet publish</c>.</summary>
    public required string Rid { get; init; }

    /// <summary>What the published core is called on this OS.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>The launcher that ships in the zip.</summary>
    public required string LauncherFileName { get; init; }

    public static TargetPlatform Windows { get; } = new()
    {
        Os = TargetOs.Windows,
        Rid = "win-x64",
        ExecutableName = "Darci.Api.exe",
        LauncherFileName = "Start-DARCI.ps1",
    };

    public static TargetPlatform Linux { get; } = new()
    {
        Os = TargetOs.Linux,
        Rid = "linux-x64",
        ExecutableName = "Darci.Api",
        LauncherFileName = "start-darci.sh",
    };

    public static TargetPlatform For(TargetOs os) => os == TargetOs.Linux ? Linux : Windows;

    /// <summary>
    /// The OS building right now — the sensible default, since the common case is packaging for the
    /// machine you are on. Cross-building to the other OS is a deliberate choice the UI offers.
    /// </summary>
    public static TargetPlatform Host =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? Linux : Windows;

    public bool IsLinux => Os == TargetOs.Linux;
}
