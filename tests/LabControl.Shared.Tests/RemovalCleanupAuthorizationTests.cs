using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class RemovalCleanupAuthorizationTests
{
    private readonly string _id = Guid.NewGuid().ToString("D");
    private InstallationDocument Ready => new() { InstallationId = _id, CreateStudentAccount = false,
        RemovalReady = true, InstallDirectoryOwned = true };

    [Fact]
    public void Existing_matching_ready_owned_installation_authorizes_cleanup() =>
        RemovalCleanupAuthorization.Require(_id, Ready, false);

    [Fact]
    public void Missing_journal_requires_a_verified_completion_receipt()
    {
        Assert.Throws<IOException>(() => RemovalCleanupAuthorization.Require(_id, null, false));
        RemovalCleanupAuthorization.Require(_id, null, true);
    }

    [Fact]
    public void Receipt_cannot_authorize_removal_of_a_different_installation()
    {
        var newer = Ready;
        newer.InstallationId = Guid.NewGuid().ToString("D");
        Assert.Throws<IOException>(() => RemovalCleanupAuthorization.Require(_id, newer, true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Receipt_cannot_override_unfinished_or_unowned_installation(bool ready, bool owned)
    {
        var document = Ready;
        document.RemovalReady = ready;
        document.InstallDirectoryOwned = owned;
        Assert.Throws<IOException>(() => RemovalCleanupAuthorization.Require(_id, document, true));
    }

    [Fact]
    public void Partial_installation_can_clean_private_data_only_when_install_root_is_absent()
    {
        var partial = Ready;
        partial.InstallDirectoryOwned = false;
        RemovalCleanupAuthorization.Require(_id, partial, false, installDirectoryExists: false);
        Assert.Throws<IOException>(() => RemovalCleanupAuthorization.Require(_id, partial, false, installDirectoryExists: true));
    }

    [Fact]
    public void An_invalid_cleanup_identity_is_never_a_path_authority() =>
        Assert.Throws<IOException>(() => RemovalCleanupAuthorization.Require("../other", null, true));
}
