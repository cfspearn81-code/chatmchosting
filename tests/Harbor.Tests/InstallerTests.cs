using Harbor.Core;
using Harbor.Installers;
using Xunit;
namespace Harbor.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),"Harbor Java ü " + Guid.NewGuid().ToString("N"));
    public InstallerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root,true);
    private async Task<string> JavaFixture(string major,string architecture)
    {
        var path = Path.Combine(root,"fixture-java");
        await File.WriteAllTextAsync(path,$"#!/bin/sh\necho '    java.version = {major}.0.1' >&2\necho '    os.arch = {architecture}' >&2\n");
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Linux process fixtures require Linux.");
        File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        return path;
    }
    [Fact]
    public async Task ProbesRealExecutableOutputInUnicodePath()
    {
        var java = await new JavaDiscovery().ProbeAsync(await JavaFixture("21","amd64"));
        Assert.Equal(21,java.MajorVersion); Assert.Equal("amd64",java.Architecture);
    }
    [Fact]
    public async Task RejectsUnsupportedRuntimeArchitecture()
    {
        var path = await JavaFixture("21","aarch64");
        await Assert.ThrowsAsync<NotSupportedException>(() => new JavaDiscovery().ProbeAsync(path));
    }
    [Fact]
    public void VanillaLaunchKeepsExecutableArgumentsAndWorkingDirectorySeparate()
    {
        var server = new ServerDefinition { JavaPath="/home/user/Java ü/bin/java", Directory="/home/user/My 世界", MinimumMemoryMb=512, MaximumMemoryMb=2048 };
        var plan = ServerInstaller.CreateLaunchPlan(server);
        Assert.Equal(server.JavaPath,plan.Executable); Assert.Equal(server.Directory,plan.WorkingDirectory);
        Assert.Equal(new[]{"-Xms512M","-Xmx2048M","-jar","server.jar","nogui"},plan.Arguments);
    }
    [Fact]
    public void ForgePlanRequiresSingleUnixArgumentFileAndUsesRelativeArgument()
    {
        var server = new ServerDefinition { Directory=root, Loader=ServerLoader.Forge };
        Assert.Throws<FileNotFoundException>(() => ServerInstaller.CreateLaunchPlan(server));
        var dir = Path.Combine(root,"libraries","net","minecraftforge","forge","1.21.1-52.0.0"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"unix_args.txt"),"upstream args");
        Assert.Contains("@libraries/net/minecraftforge/forge/1.21.1-52.0.0/unix_args.txt",ServerInstaller.CreateLaunchPlan(server).Arguments);
    }
}
