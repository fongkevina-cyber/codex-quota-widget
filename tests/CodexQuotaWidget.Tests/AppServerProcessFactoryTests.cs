using System.Text;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class AppServerProcessFactoryTests
{
    [TestMethod]
    public void ProcessConfigurationPinsUtf8AndLocalStdioTransport()
    {
        var startInfo = AppServerProcessFactory.CreateStartInfo(
            new CodexAppServerOptions("codex.exe"));

        CollectionAssert.AreEqual(
            new[] { "app-server", "--stdio" },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual(Encoding.UTF8.CodePage, startInfo.StandardInputEncoding?.CodePage);
        Assert.AreEqual(Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.AreEqual(Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
        Assert.HasCount(0, startInfo.StandardInputEncoding!.GetPreamble());
        Assert.HasCount(0, startInfo.StandardOutputEncoding!.GetPreamble());
        Assert.HasCount(0, startInfo.StandardErrorEncoding!.GetPreamble());
    }
}
