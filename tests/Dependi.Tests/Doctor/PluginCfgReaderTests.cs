using Dependi.Core.Doctor;
using Dependi.Tests.Support;
using Xunit;

namespace Dependi.Tests.Doctor;

public class PluginCfgReaderTests
{
    [Fact]
    public void TryReadVersion_WellFormedPluginCfg_ReturnsVersion()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", """
            [plugin]

            name="Foo"
            description="A test addon"
            author="Someone"
            version="2.44.0"
            script="plugin.gd"
            """);

        var version = PluginCfgReader.TryReadVersion(fs, "addons/foo/plugin.cfg");

        Assert.Equal("2.44.0", version);
    }

    [Fact]
    public void TryReadVersion_MissingFile_ReturnsNull()
    {
        var fs = new InMemoryFileSystem();

        var version = PluginCfgReader.TryReadVersion(fs, "addons/foo/plugin.cfg");

        Assert.Null(version);
    }

    [Fact]
    public void TryReadVersion_MissingPluginSection_ReturnsNull()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "not_a_plugin_section = true\n");

        var version = PluginCfgReader.TryReadVersion(fs, "addons/foo/plugin.cfg");

        Assert.Null(version);
    }

    [Fact]
    public void TryReadVersion_MissingVersionKey_ReturnsNull()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "[plugin]\nname=\"Foo\"\n");

        var version = PluginCfgReader.TryReadVersion(fs, "addons/foo/plugin.cfg");

        Assert.Null(version);
    }

    [Fact]
    public void TryReadVersion_NotValidToml_ReturnsNullRatherThanThrowing()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/foo/plugin.cfg", "this is not { valid ]] toml at all");

        var version = PluginCfgReader.TryReadVersion(fs, "addons/foo/plugin.cfg");

        Assert.Null(version);
    }
}
