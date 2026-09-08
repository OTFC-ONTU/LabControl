using System.Diagnostics;
using LabControl.Shared;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// A smoke check of the macOS package (M5 portion 7, D-59 item 3). The script itself is
/// always asserted — every Info.plist key the console depends on, the ad-hoc signature and
/// the DMG step — so a key cannot be dropped by accident on a machine that has not built
/// the bundle. When <c>tools/package-mac.sh</c> has actually been run, the produced
/// <c>LabControl.app</c> is checked too: its plist really carries those keys and its
/// signature really verifies.
/// </summary>
public class MacBundleTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LabControl.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("LabControl.sln was not found above the test binaries.");
    }

    private static string Script() => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "package-mac.sh"));

    private static string? Bundle()
    {
        var app = Path.Combine(RepoRoot(), "artifacts", "package", "mac", "osx-arm64", "LabControl.app");
        return Directory.Exists(app) ? app : null;
    }

    /// <summary>Everything the app bundle must declare, and why the console breaks without it.</summary>
    public static TheoryData<string, string> RequiredKeys() => new()
    {
        { "CFBundleIdentifier", Defaults.ConsoleBundleIdentifier },
        { "CFBundleName", "LabControl" },
        { "CFBundleDisplayName", Defaults.ConsoleProductName },
        { "CFBundleExecutable", Defaults.ConsoleExecutableBaseName },
        { "CFBundleIconFile", "labcontrol" },
        { "CFBundlePackageType", "APPL" },
        { "LSMinimumSystemVersion", "12.0" },
        { "NSHighResolutionCapable", "" },
        { "NSLocalNetworkUsageDescription", "local network" },
        { "CFBundleDocumentTypes", "" },
        { "UTExportedTypeDeclarations", "" },
        { "LSHandlerRank", "Owner" },
    };

    [Theory]
    [MemberData(nameof(RequiredKeys))]
    public void The_packaging_script_writes_every_key_the_console_needs(string key, string value)
    {
        var script = Script();

        Assert.Contains("<key>" + key + "</key>", script, StringComparison.Ordinal);
        if (value.Length > 0)
        {
            Assert.Contains(value, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_packaging_script_declares_both_file_types_and_their_own_UTIs()
    {
        var script = Script();

        foreach (var extension in new[] { Defaults.LabFileExtension, Defaults.BackupFileExtension })
        {
            // The tag specification carries the extension without its dot.
            Assert.Contains("<string>" + extension.TrimStart('.') + "</string>", script, StringComparison.Ordinal);
        }

        Assert.Contains("org.ontfk.labcontrol.lab", script, StringComparison.Ordinal);
        Assert.Contains("org.ontfk.labcontrol.backup", script, StringComparison.Ordinal);
        // Owner, not Alternate: LabControl invented these two extensions.
        Assert.Equal(2, script.Split("<string>Owner</string>").Length - 1);
    }

    [Fact]
    public void The_packaging_script_signs_ad_hoc_verifies_and_does_not_hide_Gatekeeper()
    {
        var script = Script();

        Assert.Contains("codesign --force --deep --sign -", script, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --deep --strict", script, StringComparison.Ordinal);
        Assert.Contains("spctl --assess", script, StringComparison.Ordinal);
        Assert.Contains("hdiutil create", script, StringComparison.Ordinal);
        Assert.Contains("ln -s /Applications", script, StringComparison.Ordinal);
        // D-15: the refusal is reported, never suppressed.
        Assert.Contains("refused, as documented", script, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RequiredKeys))]
    public void The_produced_bundle_really_carries_that_key(string key, string value)
    {
        if (Bundle() is not { } app)
        {
            Assert.Skip("tools/package-mac.sh has not been run; artifacts/package/mac is absent.");
            return;
        }

        var plist = File.ReadAllText(Path.Combine(app, "Contents", "Info.plist"));
        Assert.Contains("<key>" + key + "</key>", plist, StringComparison.Ordinal);
        if (value.Length > 0)
        {
            Assert.Contains(value, plist, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_produced_bundle_is_a_bundle_and_its_signature_verifies()
    {
        if (Bundle() is not { } app)
        {
            Assert.Skip("tools/package-mac.sh has not been run; artifacts/package/mac is absent.");
            return;
        }

        Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", Defaults.ConsoleExecutableBaseName)),
            "the bundle has no executable");
        Assert.True(File.Exists(Path.Combine(app, "Contents", "Resources", "labcontrol.icns")),
            "the bundle has no icon");

        Assert.Equal(0, Run("/usr/bin/plutil", ["-lint", Path.Combine(app, "Contents", "Info.plist")]));
        Assert.Equal(0, Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app]));
    }

    private static int Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
