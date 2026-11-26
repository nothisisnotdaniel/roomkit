using System.Net;
using System.Text.RegularExpressions;

namespace RoomKit.Tests;

/// <summary>The guards an app can't get around: <see cref="RoomRoutes.MapPost"/> takes only fixed paths under
/// <c>/api/</c> (so the token gate covers every app POST route), a POST mapped around it stays at 405, and the options
/// and job format checks refuse values RoomKit can't use safely.</summary>
public sealed class RoomKitGuardTests : IDisposable
{
    private readonly TempFactoryRoot root = new();

    public void Dispose()
    {
        root.Dispose();
    }

    [Theory]
    [InlineData("/x")]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/api/{id}")]
    [InlineData("/API/x")]
    [InlineData("/api/x/")]
    [InlineData("/api//x")]
    [InlineData("/api/x?y=1")]
    [InlineData("/apix/y")]
    public async Task MapPost_RefusesARouteThatIsNotAFixedApiPath(string route)
    {
        ArgumentException? refused = null;
        await using SampleServerFactory factory = new(root.Path, map: routes =>
        {
            refused = Assert.Throws<ArgumentException>(() => routes.MapPost(route, () => Results.Ok()));
        });

        Assert.NotNull(refused);
        Assert.Equal("route", refused.ParamName);
    }

    [Fact]
    public async Task MapPost_AddsAGatedRouteAndAPostAroundItStaysRefused()
    {
        await using SampleServerFactory factory = new(root.Path, map: routes =>
        {
            routes.MapPost("/api/sample/act", () => Results.Ok());
            routes.App.MapPost("/api/sample/around", () => Results.Ok());
            routes.App.MapPost("/open", () => Results.Ok());
        });
        HttpClient anonymous = await factory.Client();
        HttpClient token = await factory.TokenClient();

        using HttpResponseMessage gated = await anonymous.PostAsync("/api/sample/act", null);
        using HttpResponseMessage allowed = await token.PostAsync("/api/sample/act", null);
        using HttpResponseMessage around = await token.PostAsync("/api/sample/around", null);
        using HttpResponseMessage open = await anonymous.PostAsync("/open", null);

        Assert.Equal(HttpStatusCode.Unauthorized, gated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, around.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, open.StatusCode);
    }

    public static TheoryData<string> BadOptions() =>
    [
        "AppName", "ShortName", "DataFolderName.dot", "DataFolderName..", "DataFolderName/", "SolutionFileName",
        "Port0", "Port65536", "Repository", "RepositoryPath", "CaptureNote",
    ];

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void Check_RefusesAnUnsafeOption(string bad)
    {
        RoomKitOptions options = bad switch
        {
            "AppName" => SampleRoom.Options with { AppName = " " },
            "ShortName" => SampleRoom.Options with { ShortName = "Sample Room" },
            "DataFolderName.dot" => SampleRoom.Options with { DataFolderName = "." },
            "DataFolderName.." => SampleRoom.Options with { DataFolderName = ".." },
            "DataFolderName/" => SampleRoom.Options with { DataFolderName = "a/b" },
            "SolutionFileName" => SampleRoom.Options with { SolutionFileName = "../x.slnx" },
            "Port0" => SampleRoom.Options with { DefaultPort = 0 },
            "Port65536" => SampleRoom.Options with { DefaultPort = 65536 },
            "Repository" => SampleRoom.Options with { ReleaseRepository = "sampleroom" },
            "RepositoryPath" => SampleRoom.Options with { ReleaseRepository = "example/sampleroom/../x" },
            "CaptureNote" => SampleRoom.Options with { CaptureNote = "Draft\nfrom" },
            _ => throw new ArgumentException(bad),
        };

        RoomContext.Check(SampleRoom.Options);
        Assert.Throws<ArgumentException>(() => RoomContext.Check(options));
    }

    [Theory]
    [InlineData("../x", "job.txt")]
    [InlineData("/abs", "job.txt")]
    [InlineData("work/../jobs", "job.txt")]
    [InlineData("work/./jobs", "job.txt")]
    [InlineData("work/jobs/", "job.txt")]
    [InlineData("", "job.txt")]
    [InlineData("work/jobs", "a/job.txt")]
    [InlineData("work/jobs", "..")]
    [InlineData("work/jobs", ".")]
    [InlineData("work/jobs", "")]
    public void Check_RefusesAnUnsafeJobFormat(string jobsFolder, string recordFile)
    {
        RoomContext.Check(new SampleJobFormatWith("work/jobs", ["job.txt"]));
        Assert.Throws<ArgumentException>(
            () => RoomContext.Check(new SampleJobFormatWith(jobsFolder, [recordFile])));
        Assert.Throws<ArgumentException>(() => RoomContext.Check(new SampleJobFormatWith("work/jobs", [])));
    }

    /// <summary>The sample job format with another jobs folder and record file list.</summary>
    private sealed class SampleJobFormatWith : IJobFormat
    {
        private readonly IJobFormat inner = SampleRoom.Jobs;

        public SampleJobFormatWith(string jobsFolderPath, IReadOnlyList<string> recordFileNames)
        {
            JobsFolderPath = jobsFolderPath;
            RecordFileNames = recordFileNames;
        }

        public string JobsFolderPath { get; }

        public IReadOnlyList<string> RecordFileNames { get; }

        public IReadOnlyList<string> Stages => inner.Stages;

        public IReadOnlyList<string> ActiveStages => inner.ActiveStages;

        public IReadOnlyList<string> BlockedStages => inner.BlockedStages;

        public string DoneStage => inner.DoneStage;

        public IReadOnlyList<string> ReviewKeys => inner.ReviewKeys;

        public Regex IdPattern => inner.IdPattern;

        public IReadOnlyList<RoomJobFolder> JobFolders(string factoryRoot) => inner.JobFolders(factoryRoot);

        public IReadOnlyList<RoomJob> ReadAll(string factoryRoot) => inner.ReadAll(factoryRoot);
    }
}
