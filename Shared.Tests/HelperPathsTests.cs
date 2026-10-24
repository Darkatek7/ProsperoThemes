using System.Text;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The paths ThemeHelper v9 accepts besides the theme roots: the app's install folder and the avatar files. The helper compiles
/// the same <see cref="HelperPaths"/>, so these are its rules.
/// </summary>
public class HelperPathsTests
{
    private static bool Install(string p) => HelperPaths.IsInstallPath(Encoding.ASCII.GetBytes(p));
    private static bool InstallFolder(string p) => HelperPaths.IsInstallFolder(Encoding.ASCII.GetBytes(p));
    private static bool Avatar(string p) => HelperPaths.IsAvatarPath(Encoding.ASCII.GetBytes(p));
    private static bool AvatarFolder(string p) => HelperPaths.IsAvatarFolder(Encoding.ASCII.GetBytes(p));

    [Theory]
    [InlineData("/data/homebrew/PPSA90210/eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/eboot.bin.tmp")]
    [InlineData("/data/homebrew/PPSA90210/sce_sys/prospero-themes/catalog/13.00.json")]
    [InlineData("/data/etaHEN/games/PPSA90210/sce_sys/param.json")]
    [InlineData("/mnt/ext0/PPSA90210/eboot.bin")]
    [InlineData("/mnt/ext1/homebrew/PPSA90210/eboot.bin")]
    [InlineData("/mnt/ext1/etaHEN/games/PPSA90210/eboot.bin")]
    [InlineData("/mnt/usb0/PPSA90210/eboot.bin")]
    [InlineData("/mnt/usb7/homebrew/PPSA90210/a/b/c.d")]
    [InlineData("/mnt/usb4/etaHEN/games/PPSA90210/sce_module/libc.prx")]
    public void A_file_in_an_exact_scan_root_s_app_folder_is_accepted(string path)
    {
        Assert.True(Install(path));
        Assert.True(HelperPaths.IsInstallPath(path));          // the string form the app uses decides the same way
        Assert.False(InstallFolder(path));
    }

    [Theory]
    [InlineData("/data/homebrew/PPSA90210")]
    [InlineData("/data/etaHEN/games/PPSA90210")]
    [InlineData("/mnt/ext0/PPSA90210")]
    [InlineData("/mnt/usb3/homebrew/PPSA90210")]
    public void The_folder_itself_is_a_folder_and_not_a_file_path(string path)
    {
        Assert.True(InstallFolder(path));
        Assert.True(HelperPaths.IsInstallFolder(path));
        Assert.False(Install(path));
        Assert.Equal(path, HelperPaths.InstallFolderOf(path));
        Assert.Equal(path, HelperPaths.InstallFolderOf(path + "/sce_sys/param.json"));
    }

    [Theory]
    [InlineData("/data/PPSA90210/eboot.bin")]                              // not a scan root
    [InlineData("/data/homebrew/PPSA90211/eboot.bin")]                     // another title
    [InlineData("/data/homebrew/PPSA902100/eboot.bin")]
    [InlineData("/data/homebrew/xPPSA90210/eboot.bin")]
    [InlineData("/data/homebrewx/PPSA90210/eboot.bin")]
    [InlineData("/data/homebrew/other/PPSA90210/eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/../PPSA00001/eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/sce_sys/../../PPSA00001/eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/..")]
    [InlineData("/data/homebrew/../etc/PPSA90210/x")]
    [InlineData("/data/homebrew/PPSA90210//eboot.bin")]
    [InlineData("/data//homebrew/PPSA90210/eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/./eboot.bin")]
    [InlineData("/data/homebrew/PPSA90210/a\\b")]
    [InlineData("/data/homebrew/PPSA90210/a b")]
    [InlineData("/data/homebrew/PPSA90210/a\tb")]
    [InlineData("/data/homebrew/PPSA90210/a:b")]
    [InlineData("/data/homebrew/PPSA90210/eboot.bin/")]
    [InlineData("/data/homebrew/PPSA90210/")]
    [InlineData("/mnt/usb8/PPSA90210/eboot.bin")]                          // only usb0..7
    [InlineData("/mnt/usb/PPSA90210/eboot.bin")]
    [InlineData("/mnt/ext2/PPSA90210/eboot.bin")]
    [InlineData("/mnt/ext01/PPSA90210/eboot.bin")]
    [InlineData("/mnt/usb0/games/PPSA90210/eboot.bin")]
    [InlineData("/mnt/usb0/homebrewx/PPSA90210/eboot.bin")]
    [InlineData("/mnt/ext0/etaHEN/PPSA90210/eboot.bin")]
    [InlineData("/user/app/PPSA90210/eboot.bin")]
    [InlineData("/system_ex/PPSA90210/eboot.bin")]
    [InlineData("data/homebrew/PPSA90210/eboot.bin")]
    [InlineData("/")]
    [InlineData("")]
    public void Anything_else_is_refused(string path)
    {
        Assert.False(Install(path), path);
        Assert.False(HelperPaths.IsInstallPath(path), path);
    }

    [Fact]
    public void A_path_that_is_not_ascii_or_is_too_long_is_refused()
    {
        Assert.False(HelperPaths.IsInstallPath("/data/homebrew/PPSA90210/café"));
        string longest = "/data/homebrew/PPSA90210/" + new string('a', HelperPaths.MaxPath - 1 - 25);
        Assert.True(HelperPaths.IsInstallPath(longest));
        Assert.False(HelperPaths.IsInstallPath(longest + "a"));
        Assert.False(Install(longest + "a"));
    }

    [Theory]
    [InlineData("eboot.bin", true)]
    [InlineData("sce_sys/prospero-themes/blue-hour.p5theme", true)]
    [InlineData("a_b-c.d", true)]
    [InlineData("", false)]
    [InlineData("/eboot.bin", false)]
    [InlineData("a/", false)]
    [InlineData("a//b", false)]
    [InlineData("../a", false)]
    [InlineData("a/../b", false)]
    [InlineData("./a", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    public void A_relative_name_is_checked_by_the_same_rules(string relative, bool accepted) =>
        Assert.Equal(accepted, HelperPaths.IsInstallRelative(relative));

    // ---- avatars --------------------------------------------------------------------------------

    [Theory]
    [InlineData("avatar64.dds")]
    [InlineData("avatar128.dds")]
    [InlineData("avatar260.dds")]
    [InlineData("avatar440.dds")]
    [InlineData("picture64.dds")]
    [InlineData("picture128.dds")]
    [InlineData("picture260.dds")]
    [InlineData("picture440.dds")]
    [InlineData("avatar.png")]
    [InlineData("picture.png")]
    [InlineData("online.json")]
    public void The_avatar_files_and_their_tmp_are_accepted_in_a_profile_folder_with_either_case_of_hex(string name)
    {
        foreach (string folder in new[] { "/system_data/priv/cache/profile/0x1a2b3c4d", "/system_data/priv/cache/profile/0x1A2B3C4D", "/system_data/priv/cache/profile/0x00000000" })
        {
            Assert.True(Avatar(folder + "/" + name), name);
            Assert.True(Avatar(folder + "/" + name + ".tmp"), name + ".tmp");
            Assert.True(HelperPaths.IsAvatarPath(folder + "/" + name));
            Assert.True(AvatarFolder(folder));
            Assert.False(Avatar(folder));
        }
    }

    [Theory]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/version.json")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/version.json.tmp")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/avatar64.dds.tmp.tmp")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/avatar.dds")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/avatar64.DDS")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/sub/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/../0x00000001/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d//avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4/avatar64.dds")]      // 7 digits
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d5/avatar64.dds")]    // 9 digits
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4g/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/1a2b3c4d/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profiles/0x1a2b3c4d/avatar64.dds")]
    [InlineData("/system_data/priv/cache/0x1a2b3c4d/avatar64.dds")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/avatar64.dds/")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/avatar64.dds\\x")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d/")]
    [InlineData("/system_data/priv/cache/profile/0x1a2b3c4d")]
    public void Every_other_name_in_the_profile_folder_is_refused_and_version_json_is_never_touched(string path)
    {
        Assert.False(Avatar(path), path);
        Assert.False(HelperPaths.IsAvatarPath(path), path);
    }

    [Fact]
    public void The_profile_folder_lists_only_by_itself_and_the_cap_for_a_picture_is_one_mebibyte()
    {
        Assert.True(HelperPaths.IsProfileRoot("/system_data/priv/cache/profile"u8));
        Assert.True(HelperPaths.IsProfileRoot("/system_data/priv/cache/profile/"u8));
        Assert.False(HelperPaths.IsProfileRoot("/system_data/priv/cache"u8));
        Assert.False(HelperPaths.IsProfileRoot("/system_data/priv/cache/profile/0x1a2b3c4d"u8));
        Assert.False(HelperPaths.IsProfileRoot("/system_data/priv/cache/profiles"u8));
        Assert.Equal(1024 * 1024, HelperPaths.AvatarMaxBytes);
        Assert.False(AvatarFolder("/system_data/priv/cache/profile"));
        Assert.False(AvatarFolder("/system_data/priv/cache/profile/0x1a2b3c4d/avatar64.dds"));
    }
}
