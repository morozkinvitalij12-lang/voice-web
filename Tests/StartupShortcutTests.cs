using System.Runtime.InteropServices;
using System.Security.Principal;
using Xunit;

namespace VoiceSiteLauncher.Tests;

public sealed class StartupShortcutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceSiteLauncher startup tests " + Guid.NewGuid().ToString("N"));
    private string ShortcutPath => Path.Combine(_directory, "VoiceSiteLauncher.lnk");

    public StartupShortcutTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MissingShortcutIsDisabledAndRemovalIsIdempotent()
    {
        Assert.False(StartupShortcut.IsEnabled(ShortcutPath));
        StartupShortcut.Remove(ShortcutPath);
        StartupShortcut.Remove(ShortcutPath);
        Assert.False(StartupShortcut.IsEnabled(ShortcutPath));
    }

    [Theory]
    [InlineData("small", "auto")]
    [InlineData("small", "cpu")]
    [InlineData("base", "cuda")]
    [InlineData("base", "cpu")]
    public void InstallPreservesLaunchOptionsAndCanBeRemoved(string model, string device)
    {
        var options = new LaunchOptions(model, device, false, false);
        StartupShortcut.Install(options, ShortcutPath);
        Assert.True(StartupShortcut.IsEnabled(ShortcutPath));

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(ShortcutPath);
            dynamic link = shortcut;
            Assert.Equal(Environment.ProcessPath!, (string)link.TargetPath, ignoreCase: true);
            Assert.Equal($"--model {model} --device {device}", (string)link.Arguments);
            Assert.Equal(Path.GetDirectoryName(Environment.ProcessPath!)!, (string)link.WorkingDirectory, ignoreCase: true);
        }
        finally
        {
            if (shortcut is not null)
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }

        StartupShortcut.Remove(ShortcutPath);
        Assert.False(StartupShortcut.IsEnabled(ShortcutPath));
    }

    [Fact]
    public void InstallDoesNotOverwriteExistingShortcut()
    {
        File.WriteAllText(ShortcutPath, "existing shortcut");
        Assert.Throws<InvalidOperationException>(() =>
            StartupShortcut.Install(new LaunchOptions("small", "auto", false, false), ShortcutPath));
        Assert.Equal("existing shortcut", File.ReadAllText(ShortcutPath));
    }

    [Fact]
    public void FailedInstallDoesNotReportEnabled()
    {
        var path = Path.Combine(_directory, "missing", "VoiceSiteLauncher.lnk");
        Assert.ThrowsAny<Exception>(() =>
            StartupShortcut.Install(new LaunchOptions("small", "auto", false, false), path));
        Assert.False(StartupShortcut.IsEnabled(path));
    }

    [Fact]
    public void UserTokenResolvesItsStartupFolder()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.Duplicate);
        var path = StartupShortcut.ForUser(identity.AccessToken, path => path);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VoiceSiteLauncher.lnk"), path, ignoreCase: true);
    }

    [Fact]
    public void ShortcutOperationsRunUnderRequestedIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.Duplicate);
        StartupShortcut.ForUser(identity.AccessToken, _ =>
        {
            using var effective = WindowsIdentity.GetCurrent(ifImpersonating: true);
            Assert.NotNull(effective);
            Assert.Equal(identity.User, effective.User);
            StartupShortcut.Install(new LaunchOptions("small", "auto", false, false), ShortcutPath);
            Assert.True(StartupShortcut.IsEnabled(ShortcutPath));
            StartupShortcut.Remove(ShortcutPath);
            Assert.False(StartupShortcut.IsEnabled(ShortcutPath));
            return true;
        });
    }

    [Fact]
    public void UserContextIsRestoredWhenOperationFails()
    {
        using var before = WindowsIdentity.GetCurrent(ifImpersonating: true);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.Duplicate);
        Assert.Throws<InvalidOperationException>(() => StartupShortcut.ForUser<bool>(identity.AccessToken, _ =>
        {
            using var effective = WindowsIdentity.GetCurrent(ifImpersonating: true);
            Assert.NotNull(effective);
            throw new InvalidOperationException("Simulated shortcut failure");
        }));
        using var after = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Assert.Equal(before?.User, after?.User);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
