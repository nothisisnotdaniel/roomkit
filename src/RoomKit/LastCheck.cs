using System.Globalization;

namespace RoomKit;

/// <summary>Reads the updater's <c>last-check</c> stamp in the folder next to the token file (the same folder as
/// <c>update-result.json</c>) with the updater's rules: through <see cref="PrivateFolder"/>, so without following a
/// symlink, only as a regular <c>0600</c> file of at most 32 bytes owned by the current user, in a <c>0700</c> folder
/// owned by the user, holding only ASCII digits (epoch seconds) and at most one trailing line break. A refused or
/// malformed stamp, one beyond the <see cref="DateTimeOffset"/> range or one later than now reads as <c>null</c>. The
/// server never writes, deletes or creates anything for it.</summary>
internal sealed class LastCheckReader
{
    public const string FileName = "last-check";

    public const long MaxFileBytes = 32;

    /// <summary>The last second <see cref="DateTimeOffset"/> can hold, as epoch seconds.</summary>
    private static readonly long MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private readonly PrivateFolder folder;

    public LastCheckReader(string folder, uint? owner)
    {
        this.folder = new PrivateFolder(folder, owner);
    }

    /// <summary>The stamp as UTC <c>yyyy-MM-ddTHH:mm:ssZ</c> (invariant culture) when it is valid and not later than
    /// <paramref name="now"/>, else <c>null</c>. Never throws for a bad stamp.</summary>
    public string? Read(DateTimeOffset now)
    {
        byte[]? bytes;
        try
        {
            bytes = folder.Read(FileName, MaxFileBytes, checkFileOwner: true, createFolder: false);
        }
        catch (PrivateFileException)
        {
            return null;
        }

        DateTimeOffset? stamp = bytes is null ? null : Parse(bytes);
        if (stamp is null || stamp.Value > now)
        {
            return null;
        }

        return stamp.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>The time when <paramref name="bytes"/> is <c>^[0-9]+\n?\z</c> and the seconds fit a <c>long</c> and
    /// the <see cref="DateTimeOffset"/> range, else <c>null</c>.</summary>
    public static DateTimeOffset? Parse(byte[] bytes)
    {
        int length = bytes.Length;
        if (length > 0 && bytes[length - 1] == (byte)'\n')
        {
            length--;
        }

        if (length == 0)
        {
            return null;
        }

        long seconds = 0;
        for (int index = 0; index < length; index++)
        {
            byte digit = bytes[index];
            if (digit < (byte)'0' || digit > (byte)'9')
            {
                return null;
            }

            if (seconds > (MaxSeconds - (digit - '0')) / 10)
            {
                return null;
            }

            seconds = (seconds * 10) + (digit - '0');
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
