using AerolinkManager.Core.Wrapper;

namespace AerolinkManager.Tests;

[TestClass]
[TestCategory("Wrapper")]
public sealed class WrapperIntegrationTests
{
    [TestMethod]
    public async Task Runner_PreservesWorkingDirectoryArgumentsEnvironmentAndExitCode()
    {
        var workingDirectory = Path.Combine(Path.GetFullPath("."), "TestResults", "Aerolink Manager Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var script = Path.Combine(workingDirectory, "fake claude.ps1");
            File.WriteAllText(script, "Write-Output ('cwd=' + (Get-Location).Path); Write-Output ('arg=' + $args[0]); Write-Output ('env=' + $env:AEROLINK_TEST); exit 23");
            var plan = WrapperLaunchPlan.Create(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                @"C:\managed\wrapper.exe",
                workingDirectory,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "value with spaces"]);

            var result = await new ClaudeProcessRunner(
                redirectStandardInput: true,
                redirectStandardOutput: true,
                redirectStandardError: true).RunAsync(plan, new Dictionary<string, string> { ["AEROLINK_TEST"] = "present" });

            Assert.AreEqual(23, result.ExitCode);
            StringAssert.Contains(result.CapturedOutput, $"cwd={workingDirectory}");
            StringAssert.Contains(result.CapturedOutput, "arg=value with spaces");
            StringAssert.Contains(result.CapturedOutput, "env=present");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}
