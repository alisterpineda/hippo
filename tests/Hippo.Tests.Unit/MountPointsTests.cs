using Hippo.Cache;

namespace Hippo.Tests.Unit;

public class MountPointsTests
{
    private static readonly string Base = Path.GetPathRoot(Path.GetTempPath())!;

    private static string At(params string[] parts) => Path.Combine([Base, .. parts]);

    [Fact]
    public void The_deepest_mount_point_holding_a_path_contains_it()
    {
        string[] mounts = [Base, At("mnt"), At("mnt", "nas"), At("media")];

        Assert.Equal(At("mnt", "nas"), MountPoints.Containing(At("mnt", "nas", "notes"), mounts));
        Assert.Equal(Base, MountPoints.Containing(At("home", "me", "notes"), mounts));
    }

    [Fact]
    public void A_mount_point_contains_itself()
    {
        Assert.Equal(At("mnt", "nas"), MountPoints.Containing(At("mnt", "nas"), [Base, At("mnt", "nas")]));
        Assert.Equal(Base, MountPoints.Containing(Base, [Base]));
    }

    [Fact]
    public void A_mount_point_does_not_contain_a_sibling_that_shares_its_prefix()
    {
        Assert.Equal(Base, MountPoints.Containing(At("mnt", "nas2", "notes"), [Base, At("mnt", "nas")]));
    }

    [Fact]
    public void A_trailing_separator_on_either_side_changes_nothing()
    {
        var nas = At("mnt", "nas");
        var slashed = nas + Path.DirectorySeparatorChar;

        Assert.Equal(slashed, MountPoints.Containing(At("mnt", "nas", "notes"), [Base, slashed]));
        Assert.Equal(nas, MountPoints.Containing(slashed, [Base, nas]));
        Assert.True(MountPoints.IsMounted(slashed, [nas]));
        Assert.True(MountPoints.IsMounted(nas, [slashed]));
    }

    [Fact]
    public void A_path_on_no_mount_point_has_none()
    {
        Assert.Null(MountPoints.Containing(At("notes"), [At("mnt")]));
        Assert.Null(MountPoints.Containing(At("notes"), []));
    }

    [Fact]
    public void A_mount_point_not_listed_is_not_mounted()
    {
        Assert.False(MountPoints.IsMounted(At("mnt", "nas"), [Base, At("mnt")]));
    }

    [Fact]
    public void Letter_case_matters_only_on_windows()
    {
        var upper = At("MNT", "NAS");

        Assert.Equal(OperatingSystem.IsWindows(), MountPoints.IsMounted(upper, [At("mnt", "nas")]));
        Assert.Equal(OperatingSystem.IsWindows() ? At("mnt", "nas") : Base,
            MountPoints.Containing(At("MNT", "NAS", "notes"), [Base, At("mnt", "nas")]));
    }

    [Fact]
    public void The_os_lists_a_mount_point_holding_the_temp_folder()
    {
        Assert.NotNull(MountPoints.Containing(Path.GetTempPath(), MountPoints.Current()));
    }
}
