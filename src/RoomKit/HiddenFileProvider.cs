using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace RoomKit;

/// <summary>The web root of an app without a module: the module's web files are not found, whatever the case, extra
/// leading slashes or trailing dots and spaces of the requested path; everything else is the inner provider's.</summary>
internal sealed class HiddenFileProvider : IFileProvider
{
    private readonly IFileProvider inner;

    private readonly IReadOnlySet<string> hidden;

    public HiddenFileProvider(IFileProvider inner, IEnumerable<string> hiddenPaths)
    {
        this.inner = inner;
        hidden = new HashSet<string>(hiddenPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        return IsHidden(subpath) ? new NotFoundFileInfo(subpath) : inner.GetFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        IDirectoryContents contents = inner.GetDirectoryContents(subpath);
        if (!contents.Exists || Normalize(subpath).Length > 0)
        {
            return contents;
        }

        return new Filtered([.. contents.Where(file => !IsHidden(file.Name))]);
    }

    public IChangeToken Watch(string filter)
    {
        return inner.Watch(filter);
    }

    private bool IsHidden(string subpath)
    {
        return hidden.Contains(Normalize(subpath));
    }

    /// <summary>Case, leading slashes and trailing dots and spaces are enough: the physical provider refuses <c>:</c>
    /// (so no <c>::$DATA</c> stream alias), every hidden name fits 8.3 (so no short-name alias), and the files are
    /// public shipped scripts, not secrets.</summary>
    private static string Normalize(string path)
    {
        return path.TrimStart('/', '\\').TrimEnd('.', ' ');
    }

    private sealed class Filtered : IDirectoryContents
    {
        private readonly IReadOnlyList<IFileInfo> files;

        public Filtered(IReadOnlyList<IFileInfo> files)
        {
            this.files = files;
        }

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator() => files.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
