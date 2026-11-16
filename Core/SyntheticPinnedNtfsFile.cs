#if CHAPPY_TESTING
namespace ChappyStorageCompressor.Core;

/// <summary>
/// The existing Part33 generated-NTFS-only test guard now exercises the
/// SAME NativeFile.PinnedFile kernel primitive used by real DedupeEngine.
/// It is not a second rename/delete engine or a production permit.
/// </summary>
internal sealed class SyntheticPinnedNtfsFile : IDisposable
{
    private readonly string _root;
    private readonly string _originalPath;
    private readonly FileSnapshot _openingSnapshot;
    private readonly NativeFile.PinnedFile _pin;

    private SyntheticPinnedNtfsFile(string root, string path,
        FileSnapshot original, NativeFile.PinnedFile pin)
    {
        _root = root;
        _originalPath = path;
        _openingSnapshot = original;
        _pin = pin;
    }

    internal FileIdentity OpeningIdentity => _pin.OpeningIdentity;
    internal string OpeningHash => _pin.OpeningHash;
    internal FileIdentity CurrentHandleIdentity => _pin.CurrentHandleIdentity;

    internal static string RequireRoot(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, ".chappy-part23-generated-only")) ||
            File.ReadAllText(Path.Combine(root, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException(
                "Part33 handle operations require generated-only NTFS TEMP root");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Part33 root is a reparse point");
        return root;
    }

    internal static string RequireDirectFile(string root, string path,
        bool mustExist = true)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root,
                StringComparison.OrdinalIgnoreCase) ||
            !full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(full).Equals(".mp4",
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("part33-",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Part33 only permits direct-child generated .mp4 fixtures");
        if (mustExist &&
            (!File.Exists(full) ||
             (File.GetAttributes(full) & (FileAttributes.ReparsePoint |
                FileAttributes.Directory)) != 0 ||
             NativeFile.HasNamedDataStreams(full)))
            throw new InvalidDataException("Part33 requires regular NTFS file without ADS");
        return full;
    }

    internal static SyntheticPinnedNtfsFile Open(string rootInput, string path)
    {
        string root = RequireRoot(rootInput);
        string full = RequireDirectFile(root, path);
        FileSnapshot original = Safety.Capture(full);
        if (original.Identity.LinkCount != 1 || !Safety.SameSnapshot(original))
            throw new InvalidDataException("Part33 may pin only a single-link exact original");
        string hash = Safety.ComputeSha256(full, CancellationToken.None);
        var pin = NativeFile.PinnedFile.Open(full, original.Identity, hash);
        try
        {
            var wrapped = new SyntheticPinnedNtfsFile(root, full, original, pin);
            if (!wrapped.OriginalPathStillMatches())
                throw new InvalidDataException("File changed while opening pinned handle");
            return wrapped;
        }
        catch { pin.Dispose(); throw; }
    }

    internal bool OriginalPathStillMatches()
    {
        try
        {
            string root = RequireRoot(_root);
            RequireDirectFile(root, _originalPath);
            return _pin.PathStillMatches(_originalPath) &&
                Safety.SameSnapshot(_openingSnapshot);
        }
        catch { return false; }
    }

    // Passes the generated child-race callback to the exact Core primitive.
    internal void RenameNoReplace(string destination,
        Action? afterLastPathCheck = null)
    {
        string target = RequireDirectFile(RequireRoot(_root), destination,
            mustExist: false);
        if (!OriginalPathStillMatches())
            throw new InvalidDataException("Part33 original path changed");
        _pin.RenameNoReplace(target, afterLastPathCheck);
    }

    internal void DeleteOnClose()
    {
        if (!OriginalPathStillMatches() || CurrentHandleIdentity.LinkCount != 1)
            throw new InvalidDataException("Part33 refused delete of changed original");
        _pin.DeleteCurrent(requireIndependent: true);
    }

    public void Dispose() => _pin.Dispose();
}
#endif
