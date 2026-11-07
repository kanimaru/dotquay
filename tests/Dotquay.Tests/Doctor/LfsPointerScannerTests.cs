using System.Text;
using Dotquay.Core.Doctor;
using Dotquay.Tests.Support;
using Xunit;

namespace Dotquay.Tests.Doctor;

public class LfsPointerScannerTests {
    private const string Oid = "968c222503ef1c9e27e1071361187c210ee72457ee6c6afebfd46aa845adca77";
    private const string Pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:" + Oid
        + "\nsize 2500000\n";

    [Fact]
    public void Scan_FindsPlaceholdersAmongRealFiles_AndReportsThemRelativeToTheAddon() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/GDMP/plugin.cfg", "[plugin]");
        fs.WriteAllText("addons/GDMP/libs/x86_64/GDMP.windows.dll", Pointer);
        fs.WriteAllText("addons/GDMP/libs/arm64/libGDMP.macos.dylib", Pointer);
        fs.WriteAllBytes("addons/GDMP/libs/x86_64/real.so", new byte[5000]);

        var found = LfsPointerScanner.Scan(fs, "addons/GDMP");

        Assert.Equal(["libs/arm64/libGDMP.macos.dylib", "libs/x86_64/GDMP.windows.dll"], found);
    }

    [Fact]
    public void Scan_AnAddonWithNoPlaceholders_FindsNothing() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/vrm/plugin.cfg", "[plugin]");
        fs.WriteAllBytes("addons/vrm/lib.dll", Encoding.UTF8.GetBytes("MZ real binary"));

        Assert.Empty(LfsPointerScanner.Scan(fs, "addons/vrm"));
    }

    [Fact]
    public void Scan_ADocumentThatQuotesThePointerFormat_IsNotAPointer() {
        var fs = new InMemoryFileSystem();
        fs.WriteAllText("addons/GDMP/README.md",
            "A pointer file starts with `version https://git-lfs.github.com/spec/v1`.\nIt also has "
            + "an oid line.\n");

        Assert.Empty(LfsPointerScanner.Scan(fs, "addons/GDMP"));
    }

    [Fact]
    public void Scan_AMissingFolder_FindsNothingRatherThanThrowing()
        => Assert.Empty(LfsPointerScanner.Scan(new InMemoryFileSystem(), "addons/nope"));
}
