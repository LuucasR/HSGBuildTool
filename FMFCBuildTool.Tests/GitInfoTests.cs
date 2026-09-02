using System;
using System.IO;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The .git directory is read directly rather than by shelling out to git, so these
/// cover the formats that reading entails — and, above all, that every malformed or
/// missing case fails soft. A build must never be blocked by provenance data.
/// </summary>
public class GitInfoTests : IDisposable
{
    private readonly string _root;

    public GitInfoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Reads_branch_and_short_commit_from_a_loose_ref()
    {
        WriteGit("ref: refs/heads/main", ("refs/heads/main", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"));

        var result = GitInfo.Read(ProjectFile());

        Assert.True(result.HasValue);
        Assert.Equal("main", result.Branch);
        Assert.Equal("a1b2c3d", result.Commit);
        Assert.Equal("main@a1b2c3d", result.Label);
    }

    /// <summary>A branch name with slashes must not be truncated at the first one.</summary>
    [Fact]
    public void Keeps_the_whole_branch_name()
    {
        WriteGit("ref: refs/heads/feature/nav-rebuild",
            ("refs/heads/feature/nav-rebuild", "0123456789abcdef0123456789abcdef01234567"));

        Assert.Equal("feature/nav-rebuild", GitInfo.Read(ProjectFile()).Branch);
    }

    /// <summary>git gc moves refs out of the tree and into one packed-refs file.</summary>
    [Fact]
    public void Falls_back_to_packed_refs()
    {
        WriteGit("ref: refs/heads/main");

        File.WriteAllText(
            Path.Combine(_root, ".git", "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted\n" +
            "feedface1234567890abcdef1234567890abcdef refs/heads/main\n" +
            "^0000000000000000000000000000000000000000\n");

        var result = GitInfo.Read(ProjectFile());

        Assert.Equal("main", result.Branch);
        Assert.Equal("feedfac", result.Commit);
    }

    [Fact]
    public void Reports_a_detached_head()
    {
        WriteGit("abcdef01234567890abcdef01234567890abcdef");

        var result = GitInfo.Read(ProjectFile());

        Assert.Equal("(detached)", result.Branch);
        Assert.Equal("abcdef0", result.Commit);
    }

    /// <summary>The .git of a submodule or linked worktree is a file pointing elsewhere.</summary>
    [Fact]
    public void Follows_a_gitdir_pointer_file()
    {
        var real = Path.Combine(_root, "actual-git");

        Directory.CreateDirectory(Path.Combine(real, "refs", "heads"));

        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/work\n");
        File.WriteAllText(Path.Combine(real, "refs", "heads", "work"), "11112222333344445555666677778888aaaabbbb\n");

        File.WriteAllText(Path.Combine(_root, ".git"), $"gitdir: {real}\n");

        var result = GitInfo.Read(ProjectFile());

        Assert.Equal("work", result.Branch);
        Assert.Equal("1111222", result.Commit);
    }

    /// <summary>A project in a subfolder still finds the repository above it.</summary>
    [Fact]
    public void Walks_up_to_find_the_repository()
    {
        WriteGit("ref: refs/heads/main", ("refs/heads/main", "cafebabe1234567890abcdef1234567890abcdef"));

        var nested = Path.Combine(_root, "Game", "Client");

        Directory.CreateDirectory(nested);

        Assert.Equal("cafebab", GitInfo.Read(Path.Combine(nested, "FMFC.uproject")).Commit);
    }

    [Fact]
    public void Returns_nothing_outside_a_repository()
    {
        var result = GitInfo.Read(ProjectFile());

        Assert.False(result.HasValue);
        Assert.Equal("", result.Label);
    }

    /// <summary>A fresh repository with no commits: real, and nothing to report.</summary>
    [Fact]
    public void Returns_nothing_when_the_branch_has_no_commit_yet()
    {
        WriteGit("ref: refs/heads/main");

        Assert.False(GitInfo.Read(ProjectFile()).HasValue);
    }

    [Fact]
    public void Returns_nothing_for_a_garbled_head()
    {
        WriteGit("");

        Assert.False(GitInfo.Read(ProjectFile()).HasValue);
    }

    private string ProjectFile() => Path.Combine(_root, "FMFC.uproject");

    private void WriteGit(string head, params (string Reference, string Commit)[] refs)
    {
        var git = Path.Combine(_root, ".git");

        Directory.CreateDirectory(git);

        File.WriteAllText(Path.Combine(git, "HEAD"), head + "\n");

        foreach (var (reference, commit) in refs)
        {
            var path = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, commit + "\n");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
