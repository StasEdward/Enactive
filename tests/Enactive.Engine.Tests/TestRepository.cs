namespace Enactive.Engine.Tests;

using System.Reflection;

internal static class TestRepository
{
    public static string Root
    {
        get
        {
            var path = typeof(TestRepository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "TestRepositoryRoot").Value!;
            Assert.True(File.Exists(Path.Combine(path, "Enactive.sln")),
                $"The source checkout used to build these tests is unavailable: {path}");
            return path;
        }
    }
}
