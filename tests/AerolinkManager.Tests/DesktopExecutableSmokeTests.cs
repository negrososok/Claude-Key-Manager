using System.Diagnostics;
using AerolinkManager.Core.Configuration;

namespace AerolinkManager.Tests;

[TestClass]
[TestCategory("UI")]
public sealed class DesktopExecutableSmokeTests
{
    [TestMethod]
    public async Task AppExecutable_StartsAndCreatesMainWindow()
    {
        var testRoot = Path.Combine(Path.GetFullPath("."), "TestResults", "desktop smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        Process? process = null;
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App", "bin", configuration, "net8.0-windows", "ClaudeManager.exe"));
            Assert.IsTrue(File.Exists(executable), $"Desktop executable missing at {executable}");
            var appHome = Path.Combine(testRoot, "appdata");
            new JsonFileStore(new AppPaths(appHome)).SaveConfig(new ManagerConfig { Language = "uk" });
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false
            };
            start.Environment["CLAUDE_MANAGER_HOME"] = appHome;
            start.Environment["CLAUDE_MANAGER_INSTANCE_SUFFIX"] = Guid.NewGuid().ToString("N");
            process = Process.Start(start);
            Assert.IsNotNull(process);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(200);
            }

            var diagnostic = Directory.Exists(Path.Combine(testRoot, "appdata", "logs"))
                ? string.Join(" | ", Directory.GetFiles(Path.Combine(testRoot, "appdata", "logs")).Select(File.ReadAllText))
                : "no startup log";
            Assert.IsFalse(process.HasExited, $"Desktop app exited before creating its window. {diagnostic}");
            Assert.AreNotEqual(IntPtr.Zero, process.MainWindowHandle, $"Desktop app did not create a targetable main window. {diagnostic}");
            StringAssert.Contains(diagnostic, "language\tuk");
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }
}
