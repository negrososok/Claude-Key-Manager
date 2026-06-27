using AerolinkManager.Core.Configuration;
using AerolinkManager.Core.Diagnostics;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class AppLoggerTests
{
    [TestMethod]
    public void Write_DoesNotThrow_WhenLogsPathIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClaudeManagerLoggerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "logs"), "not a directory");

            new AppLogger(new AppPaths(root)).Write("startup", "must not crash");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
