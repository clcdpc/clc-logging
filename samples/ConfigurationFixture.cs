using System.Reflection;
using Clc.Configuration;
using Microsoft.Extensions.Hosting;

// Test infrastructure only, linked into tests and package smoke consumers.
// The published package has no public root override. Invoke its existing internal
// path/initialization seams so the real loader records paths without touching ProgramData.
internal static class ConfigurationFixture
{
    internal static ClcApplicationPaths Paths(string applicationDataDirectory) =>
        typeof(ClcApplicationPaths).GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(string), typeof(string)])!
            .CreateDelegate<Func<string, string, ClcApplicationPaths>>()(
                Path.GetFileName(applicationDataDirectory), Path.GetDirectoryName(applicationDataDirectory)!);

    internal static void Initialize(IHostApplicationBuilder builder, ClcApplicationPaths paths, string json = "{}", bool bootstrap = false)
    {
        Directory.CreateDirectory(paths.ConfigurationDirectory);
        File.WriteAllText(paths.ConfigurationFilePath, json);
        var name = Path.GetFileName(paths.ApplicationDataDirectory);
        if (bootstrap)
        {
            typeof(ClcConfigurationBuilderExtensions).GetMethod("AddClcConfigurationWithBootstrap", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IHostApplicationBuilder, string, ClcApplicationPaths, string?, IHostApplicationBuilder>>()(
                    builder, name, paths, null);
        }
        else
        {
            typeof(ClcConfigurationBuilderExtensions).GetMethod("AddClcConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IHostApplicationBuilder, string, ClcApplicationPaths, IHostApplicationBuilder>>()(builder, name, paths);
        }
    }
}
