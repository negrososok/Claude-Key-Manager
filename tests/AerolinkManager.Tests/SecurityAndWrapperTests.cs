using AerolinkManager.Core.Diagnostics;
using AerolinkManager.Core.Security;
using AerolinkManager.Core.Wrapper;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class SecurityAndWrapperTests
{
    [TestMethod]
    public void Sanitizer_RemovesCompleteSecret()
    {
        const string secret = "sk-ant-test-only-AbC9";
        var result = new SecretSanitizer().Sanitize($"failed for {secret}", [secret]);

        Assert.AreEqual("failed for [REDACTED]", result);
    }

    [TestMethod]
    public void LaunchPlan_RejectsManagedWrapperRecursion()
    {
        var path = Path.GetFullPath(@"C:\Aerolink Manager\bin\wrapper.exe");

        Assert.ThrowsException<InvalidOperationException>(() =>
            WrapperLaunchPlan.Create(path, path.ToUpperInvariant(), @"C:\work", []));
    }

    [TestMethod]
    public void LaunchPlan_PreservesArgumentsAndWorkingDirectory()
    {
        string[] arguments = ["-p", "check this project", "--flag=quoted value"];
        var plan = WrapperLaunchPlan.Create(
            @"C:\Program Files\Claude\claude.exe",
            @"C:\Users\test\AppData\Roaming\AerolinkManager\bin\wrapper.exe",
            @"C:\work folder",
            arguments);

        CollectionAssert.AreEqual(arguments, plan.Arguments.ToArray());
        Assert.AreEqual(Path.GetFullPath(@"C:\work folder"), plan.WorkingDirectory);
    }

    [TestMethod]
    public void ProcessRunner_AutoModeOnlyRedirectsWhenParentStreamIsRedirected()
    {
        var codeRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.Core", "Wrapper"));
        var code = File.ReadAllText(Path.Combine(codeRoot, "ClaudeProcessRunner.cs"));

        StringAssert.Contains(code, "_redirectStandardInput ?? Console.IsInputRedirected");
        StringAssert.Contains(code, "_redirectStandardOutput ?? Console.IsOutputRedirected");
        StringAssert.Contains(code, "_redirectStandardError ?? Console.IsErrorRedirected");
        Assert.IsFalse(code.Contains("RedirectStandardInput = true", StringComparison.Ordinal),
            "Always redirecting stdin makes Claude Code treat an interactive terminal like a piped --print run.");
    }

    [TestMethod]
    public void Dpapi_RoundTripsWithoutReturningPlaintext()
    {
        const string plaintext = "sk-ant-test-only-AbC9";
        var protector = new WindowsDpapiSecretProtector();

        var encrypted = protector.Protect(plaintext);

        Assert.AreNotEqual(plaintext, encrypted);
        Assert.AreEqual(plaintext, protector.Unprotect(encrypted));
    }
}
