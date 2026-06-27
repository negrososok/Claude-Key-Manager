using System.Buffers.Binary;
using System.Drawing;

namespace AerolinkManager.Tests;

[TestClass]
public sealed class BrandingResourceTests
{
    [TestMethod]
    public void SharedIcon_ContainsRequiredWindowsSizesIncluding256()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "ClaudeManager.ico"));
        var bytes = File.ReadAllBytes(path);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var sizes = Enumerable.Range(0, count).Select(index =>
        {
            var width = bytes[6 + index * 16];
            return width == 0 ? 256 : width;
        }).ToArray();

        CollectionAssert.IsSubsetOf(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, sizes);
    }

    [TestMethod]
    public void BuiltDesktopAndWrapperExecutablesExposeAssociatedIcon()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var desktop = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.App", "bin", configuration, "net8.0-windows", "ClaudeManager.exe"));
        var wrapper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AerolinkManager.Wrapper", "bin", configuration, "net8.0-windows", "ClaudeManager.Wrapper.exe"));

        using var desktopIcon = Icon.ExtractAssociatedIcon(desktop);
        using var wrapperIcon = Icon.ExtractAssociatedIcon(wrapper);

        Assert.IsNotNull(desktopIcon);
        Assert.IsNotNull(wrapperIcon);
    }

    [TestMethod]
    public void BuiltGatewayExecutableExposesAssociatedIcon()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var gateway = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ClaudeManager.Gateway", "bin", configuration, "net8.0-windows", "ClaudeManager.Gateway.exe"));

        Assert.IsTrue(File.Exists(gateway), $"Gateway executable not found: {gateway}");

        using var gatewayIcon = Icon.ExtractAssociatedIcon(gateway);

        Assert.IsNotNull(gatewayIcon);
    }
}
