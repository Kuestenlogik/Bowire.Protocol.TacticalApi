// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Tests;

/// <summary>
/// Every <c>```json</c> block in the sample README, sent as it stands. The
/// README promises that copying a body produces a request the server
/// accepts; this is what makes that promise hold after the next edit. The
/// README is linked into the test output by the project file, so the file
/// under test is the one in the repository, not a copy.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class ReadmeBodiesE2ETests : IClassFixture<InProcessSampleServerFixture>
{
    private readonly InProcessSampleServerFixture _server;
    private readonly BowireTacticalApiProtocol _plugin = new();

    public ReadmeBodiesE2ETests(InProcessSampleServerFixture server)
    {
        _server = server;
    }

    public static TheoryData<int> EveryBlock
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < Blocks().Count; i++) data.Add(i);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryBlock))]
    public async Task Every_json_block_in_the_README_is_a_request_the_server_accepts(int index)
    {
        var body = Blocks()[index];
        var (service, method) = Route(body);

        var result = await _plugin.InvokeAsync(
            _server.ServerUrl, service, method,
            jsonMessages: [body], showInternalServices: false, metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Status == "OK",
            $"README block {index} ({service}.{method}) came back {result.Status}: "
            + (result.Metadata.TryGetValue(BowireTacticalApiProtocol.RefusalMessageKey, out var why) ? why : result.Response));
    }

    [Fact]
    public void The_README_has_the_blocks_this_suite_expects()
    {
        // A guard against the regex going quiet: fewer blocks than the README
        // is known to carry means the extraction broke, not the README.
        Assert.True(Blocks().Count >= 6, $"Expected at least 6 json blocks, found {Blocks().Count}.");
    }

    /// <summary>
    /// Which RPC a body is for, by the top-level field the contract gives
    /// each request — the README has no other marker, and needs none.
    /// </summary>
    private static (string Service, string Method) Route(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("situationObjects", out _)) return ("Situation", "AddOrUpdateSituationObjects");
        if (root.TryGetProperty("position", out _)) return ("OwnPose", "UpdatePosition");
        if (root.TryGetProperty("blueForcesToUpdates", out _)) return ("BlueForceTracking", "AddOrUpdateBlueForces");
        throw new InvalidOperationException("A README body this suite does not know how to route: " + body[..Math.Min(80, body.Length)]);
    }

    private static List<string> Blocks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SampleReadme.md");
        var readme = File.ReadAllText(path);
        var blocks = new List<string>();
        foreach (Match match in JsonBlock().Matches(readme))
        {
            // The README indents its blocks by two spaces inside list items;
            // JSON does not care, but strip it so failure output reads clean.
            var lines = match.Groups[1].Value.Split('\n')
                .Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l);
            blocks.Add(string.Join('\n', lines));
        }
        return blocks;
    }

    [GeneratedRegex(@"```json\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
