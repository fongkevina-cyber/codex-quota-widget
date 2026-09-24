using System.Reflection;
using System.Runtime.InteropServices;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class TaskbarNativeMethodsTests
{
    [TestMethod]
    public void EveryDeclaredInteropMethodResolvesToARealSystemExport()
    {
        var methods = typeof(TaskbarNativeMethods)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<DllImportAttribute>(),
            })
            .Where(entry => entry.Attribute is not null)
            .ToArray();

        Assert.IsNotEmpty(methods);

        foreach (var library in methods.Select(entry => entry.Attribute!.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Assert.IsTrue(
                NativeLibrary.TryLoad(library, out var handle),
                $"Unable to load native library '{library}'.");

            try
            {
                foreach (var entry in methods.Where(candidate =>
                             string.Equals(candidate.Attribute!.Value, library, StringComparison.OrdinalIgnoreCase)))
                {
                    var attribute = entry.Attribute!;
                    var exportName = string.IsNullOrEmpty(attribute.EntryPoint)
                        ? entry.Method.Name
                        : attribute.EntryPoint;

                    Assert.IsTrue(
                        NativeLibrary.TryGetExport(handle, exportName, out _),
                        $"{entry.Method.Name} declares no explicit EntryPoint and/or maps to missing export '{exportName}' in {library}.");
                }
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
    }
}
