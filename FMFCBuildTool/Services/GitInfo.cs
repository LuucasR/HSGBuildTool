using System;
using System.IO;
using System.Linq;

namespace FMFCBuildTool.Services;

/// <summary>
/// Branch and short commit of the work tree a project lives in, stamped into every
/// build log and build record.
/// </summary>
/// <remarks>
/// Reads the .git directory directly rather than shelling out to git: the answer is a
/// couple of small file reads, it works on a machine with no git in PATH, and it cannot
/// hang a build behind a process launch.
///
/// Everything here fails soft. A project outside a repository, a half-initialised .git,
/// or a ref format we do not recognise all produce an empty result — a build must never
/// be blocked by provenance data.
/// </remarks>
public static class GitInfo
{
    public readonly record struct Result(string Branch, string Commit)
    {
        public bool HasValue => !string.IsNullOrEmpty(Commit);

        /// <summary>"main@a1b2c3d", or empty when there is nothing to say.</summary>
        public string Label => !HasValue
            ? ""
            : string.IsNullOrEmpty(Branch) ? Commit : $"{Branch}@{Commit}";
    }

    public static Result Read(string projectFileOrDirectory)
    {
        try
        {
            var start = Directory.Exists(projectFileOrDirectory)
                ? projectFileOrDirectory
                : Path.GetDirectoryName(projectFileOrDirectory);

            if (string.IsNullOrEmpty(start))
                return default;

            var gitDir = FindGitDirectory(start);

            if (gitDir is null)
                return default;

            var head = ReadTrimmed(Path.Combine(gitDir, "HEAD"));

            if (string.IsNullOrEmpty(head))
                return default;

            // Detached HEAD: the file is the commit itself.
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
                return new Result("(detached)", Shorten(head));

            var reference = head[4..].Trim();
            var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? reference["refs/heads/".Length..]
                : reference;

            var commit = ReadTrimmed(Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar)))
                         ?? ReadPackedRef(gitDir, reference);

            // A branch with no commits yet: real, and not worth reporting.
            return string.IsNullOrEmpty(commit) ? default : new Result(branch, Shorten(commit));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Walks up looking for .git, which may be a directory or a worktree pointer file.</summary>
    private static string? FindGitDirectory(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".git");

            if (Directory.Exists(candidate))
                return candidate;

            if (File.Exists(candidate))
            {
                // Submodule or linked worktree: "gitdir: ../.git/modules/foo".
                var pointer = ReadTrimmed(candidate);

                if (pointer is not null && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = pointer["gitdir:".Length..].Trim();

                    if (!Path.IsPathRooted(target))
                        target = Path.GetFullPath(Path.Combine(directory.FullName, target));

                    if (Directory.Exists(target))
                        return target;
                }

                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Loose refs win; a ref that has been packed lives in one shared file.</summary>
    private static string? ReadPackedRef(string gitDir, string reference)
    {
        var packed = Path.Combine(gitDir, "packed-refs");

        if (!File.Exists(packed))
            return null;

        foreach (var line in File.ReadLines(packed))
        {
            if (line.Length == 0 || line[0] is '#' or '^')
                continue;

            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);

            if (parts.Length == 2 && parts[1] == reference)
                return parts[0];
        }

        return null;
    }

    private static string? ReadTrimmed(string path)
    {
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path).Trim();

        return text.Length == 0 ? null : text;
    }

    private static string Shorten(string commit)
    {
        var sha = new string(commit.TakeWhile(Uri.IsHexDigit).ToArray());

        return sha.Length >= 7 ? sha[..7] : sha;
    }
}
