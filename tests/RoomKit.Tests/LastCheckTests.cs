using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace RoomKit.Tests;

/// <summary><c>GET /api/update</c> reports the updater's <c>last-check</c> stamp as <c>lastCheck</c> (#480): a valid
/// stamp as UTC <c>yyyy-MM-ddTHH:mm:ssZ</c> (AC-1), <c>null</c> when missing (AC-2), refused or malformed (AC-3) or
/// later than the fake clock (AC-4), never an error (AC-5), and the GET changes nothing (AC-6). The core app sets the
/// process-wide <see cref="RoomContext"/>, so these run in the core room collection.</summary>
[Collection(CoreRoomCollection.Name)]
public sealed class LastCheckTests : IDisposable
{
    /// <summary>2026-03-01T09:30:00Z, <see cref="FakeTime.Start"/>.</summary>
    private const long StampSeconds = 1772357400;

    private const string StampTime = "2026-03-01T09:30:00Z";

    public void Dispose()
    {
        RoomContext.Use(SampleRoom.Options, SampleRoom.Jobs);
    }

    [Theory]
    [InlineData("1772357400\n")]
    [InlineData("1772357400")]
    public async Task ValidStamp_IsReportedInUtc(string content)
    {
        await using CoreServerFactory factory = new();
        factory.Time.Now = FakeTime.Start.AddHours(5);
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), content);

        JsonObject json = await Get(client);

        Assert.Equal(StampTime, json["lastCheck"]!.GetValue<string>());
    }

    [Fact]
    public void ValidStamp_IsFormattedInvariant_UnderAnotherCulture()
    {
        using TempTokenFolder folder = new();
        TempTokenFolder.WritePrivateText(Path.Combine(folder.Path, LastCheckReader.FileName), StampSeconds + "\n");
        LastCheckReader reader = new(folder.Path, UnixOwner.CurrentUser());
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(StampTime, reader.Read(FakeTime.Start));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task MissingStamp_IsNull_AndOtherFieldsStay()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();

        using HttpResponseMessage response = await client.GetAsync(UpdateEndpoint.Route);
        JsonObject json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.ContainsKey("lastCheck"));
        Assert.Null(json["lastCheck"]);
        Assert.Equal(AppVersion.Dev, json["current"]!.GetValue<string>());
        Assert.False(json["available"]!.GetValue<bool>());
        Assert.False(json["autoUpdate"]!.GetValue<bool>());
        Assert.Equal(UpdateChannel.Stable, json["channel"]!.GetValue<string>());
        Assert.Equal(UpdateInterval.Default, json["updateInterval"]!.GetValue<int>());
        Assert.False(File.Exists(StampPath(factory)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("abc\n")]
    [InlineData("17723574a0\n")]
    [InlineData("+1772357400\n")]
    [InlineData("-1772357400\n")]
    [InlineData(" 1772357400\n")]
    [InlineData("1772357400 \n")]
    [InlineData("1772357400\r\n")]
    [InlineData("1772357400\n\n")]
    [InlineData("1772357400\n1")]
    [InlineData("1772357400.0\n")]
    [InlineData("000000000000000000000000000000001\n")]
    [InlineData("100000000000000000000000000000000")]
    [InlineData("99999999999999999999\n")]
    [InlineData("9223372036854775808\n")]
    [InlineData("253402300800\n")]
    public async Task MalformedOrOversizedStamp_IsNull(string content)
    {
        await using CoreServerFactory factory = new();
        factory.Time.Now = DateTimeOffset.MaxValue;
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), content);

        JsonObject json = await Get(client);

        Assert.True(json.ContainsKey("lastCheck"));
        Assert.Null(json["lastCheck"]);
    }

    [Fact]
    public async Task LargestRepresentableStamp_IsReported()
    {
        await using CoreServerFactory factory = new();
        factory.Time.Now = DateTimeOffset.MaxValue;
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), "253402300799\n");

        JsonObject json = await Get(client);

        Assert.Equal("9999-12-31T23:59:59Z", json["lastCheck"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThirtyTwoByteStamp_IsReadButThirtyThreeIsRefused()
    {
        // 31 digits plus the line break are 32 bytes; leading zeros keep the value at the stamp.
        string allowed = new string('0', 31 - StampSeconds.ToString(CultureInfo.InvariantCulture).Length)
            + StampSeconds.ToString(CultureInfo.InvariantCulture) + "\n";
        Assert.Equal(32, allowed.Length);

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), allowed);
        Assert.Equal(StampTime, (await Get(client))["lastCheck"]!.GetValue<string>());

        TempTokenFolder.WritePrivateText(StampPath(factory), "0" + allowed);
        Assert.Null((await Get(client))["lastCheck"]);
    }

    [Fact]
    public async Task FutureStamp_IsNull_EvenByOneSecond()
    {
        await using CoreServerFactory factory = new();
        factory.Time.Now = FakeTime.Start.AddSeconds(-1);
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), StampSeconds + "\n");

        JsonObject json = await Get(client);

        Assert.True(json.ContainsKey("lastCheck"));
        Assert.Null(json["lastCheck"]);
    }

    [Fact]
    public async Task StampEqualToNow_IsReported()
    {
        await using CoreServerFactory factory = new();
        factory.Time.Now = FakeTime.Start;
        HttpClient client = await factory.TokenClient();
        TempTokenFolder.WritePrivateText(StampPath(factory), StampSeconds + "\n");

        JsonObject json = await Get(client);

        Assert.Equal(StampTime, json["lastCheck"]!.GetValue<string>());
    }

    [Fact]
    public async Task SymlinkToValidStamp_IsNull()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        string target = Path.Combine(factory.DataFolder, "real-stamp");
        TempTokenFolder.WritePrivateText(target, StampSeconds + "\n");
        File.CreateSymbolicLink(StampPath(factory), target);

        JsonObject json = await Get(client);

        Assert.Null(json["lastCheck"]);
        Assert.NotNull(new FileInfo(StampPath(factory)).LinkTarget);
    }

    [Fact]
    public async Task DirectoryStamp_IsNull()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        Directory.CreateDirectory(StampPath(factory));

        JsonObject json = await Get(client);

        Assert.Null(json["lastCheck"]);
        Assert.True(Directory.Exists(StampPath(factory)));
    }

    [Fact]
    public async Task LooseModeStamp_IsNull_AndKeepsItsBytesAndMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        string path = StampPath(factory);
        TempTokenFolder.WritePrivateText(path, StampSeconds + "\n");
        const UnixFileMode Loose = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead;
        File.SetUnixFileMode(path, Loose);

        JsonObject json = await Get(client);

        Assert.Null(json["lastCheck"]);
        Assert.Equal(Loose, File.GetUnixFileMode(path));
        Assert.Equal(StampSeconds + "\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task RefusedStampAndRefusedSettings_GiveAllNull_With200()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        WriteRefusedSettings(factory);
        TempTokenFolder.WritePrivateText(StampPath(factory), "not a stamp\n");

        using HttpResponseMessage response = await client.GetAsync(UpdateEndpoint.Route);
        JsonObject json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (string key in new[] { "lastCheck", "autoUpdate", "channel", "updateInterval" })
        {
            Assert.True(json.ContainsKey(key), key);
            Assert.Null(json[key]);
        }
    }

    [Fact]
    public async Task ValidStamp_IsReported_WhenSettingsAreRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        WriteRefusedSettings(factory);
        TempTokenFolder.WritePrivateText(StampPath(factory), StampSeconds + "\n");

        using HttpResponseMessage response = await client.GetAsync(UpdateEndpoint.Route);
        JsonObject json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(StampTime, json["lastCheck"]!.GetValue<string>());
        Assert.Null(json["autoUpdate"]);
        Assert.Null(json["channel"]);
        Assert.Null(json["updateInterval"]);
    }

    [Fact]
    public async Task Get_KeepsTheStampBytesAndMode()
    {
        await using CoreServerFactory factory = new();
        HttpClient client = await factory.TokenClient();
        string path = StampPath(factory);
        TempTokenFolder.WritePrivateText(path, StampSeconds + "\n");
        byte[] before = File.ReadAllBytes(path);
        UnixFileMode? modeBefore = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

        using HttpResponseMessage response = await client.GetAsync(UpdateEndpoint.Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(modeBefore, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Parse_RejectsEveryNonDigit_AndAcceptsOneLineBreak()
    {
        Assert.Equal(FakeTime.Start, LastCheckReader.Parse("1772357400\n"u8.ToArray()));
        Assert.Equal(FakeTime.Start, LastCheckReader.Parse("1772357400"u8.ToArray()));
        Assert.Null(LastCheckReader.Parse([]));
        Assert.Null(LastCheckReader.Parse("\n"u8.ToArray()));
        Assert.Null(LastCheckReader.Parse("1772357400\r\n"u8.ToArray()));
        Assert.Null(LastCheckReader.Parse("9223372036854775807\n"u8.ToArray()));
    }

    private static string StampPath(CoreServerFactory factory)
    {
        return Path.Combine(factory.DataFolder, LastCheckReader.FileName);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void WriteRefusedSettings(CoreServerFactory factory)
    {
        string settings = Path.Combine(factory.DataFolder, UpdateSettingsStore.FileName);
        TempTokenFolder.WritePrivateText(settings, "{\"autoUpdate\":true,\"channel\":\"stable\",\"updateInterval\":60}\n");
        File.SetUnixFileMode(settings, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead);
    }

    private static async Task<JsonObject> Get(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(UpdateEndpoint.Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
}
