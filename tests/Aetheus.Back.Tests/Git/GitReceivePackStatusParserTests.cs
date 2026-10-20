// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Tests;

public sealed class GitReceivePackStatusParserTests
{
    private const string MainCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string DevelopCommit = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void GetAcceptedUpdates_ReturnsOnlyRefsConfirmedByReceivePack()
    {
        using var response = BuildResponse("unpack ok\n", "ok refs/heads/main\n", "ng refs/heads/develop rejected\n");
        GitRefUpdate[] requested =
        [
            new("refs/heads/main", MainCommit),
            new("refs/heads/develop", DevelopCommit)
        ];

        var accepted = GitReceivePackStatusParser.GetAcceptedUpdates(response, requested);

        var update = Assert.Single(accepted);
        Assert.Equal("refs/heads/main", update.Reference);
        Assert.Equal(MainCommit, update.NewObjectId);
    }

    [Fact]
    public void GetAcceptedUpdates_MissingReportStatus_FailsClosed()
    {
        using var response = new MemoryStream("0000"u8.ToArray());

        var accepted = GitReceivePackStatusParser.GetAcceptedUpdates(
            response, [new GitRefUpdate("refs/heads/main", MainCommit)]);

        Assert.Empty(accepted);
    }

    [Fact]
    public void GetAcceptedUpdates_UnpackFailure_FailsClosedEvenWithOkRef()
    {
        using var response = BuildResponse("unpack corrupt pack\n", "ok refs/heads/main\n");

        var accepted = GitReceivePackStatusParser.GetAcceptedUpdates(
            response, [new GitRefUpdate("refs/heads/main", MainCommit)]);

        Assert.Empty(accepted);
    }

    [Fact]
    public void GetAcceptedUpdates_StatusCarriedInASidebandPacket_IsReadFromTheNestedPackets()
    {
        // HTTP smart protocol wraps report-status in sideband band 1: the status packets are nested.
        var inner = PacketLine("unpack ok\n").Concat(PacketLine("ok refs/heads/main\n")).Concat("0000"u8.ToArray()).ToArray();
        var outer = PacketLine([1, .. inner]);
        using var response = new MemoryStream([.. outer, .. "0000"u8.ToArray()]);

        var accepted = GitReceivePackStatusParser.GetAcceptedUpdates(
            response, [new GitRefUpdate("refs/heads/main", MainCommit)]);

        var update = Assert.Single(accepted);
        Assert.Equal("refs/heads/main", update.Reference);
    }

    [Fact]
    public void GetAcceptedUpdates_NonHexPacketLength_FailsClosed()
    {
        using var response = new MemoryStream("zzzzunpack ok\n"u8.ToArray());

        var accepted = GitReceivePackStatusParser.GetAcceptedUpdates(
            response, [new GitRefUpdate("refs/heads/main", MainCommit)]);

        Assert.Empty(accepted);
    }

    private static byte[] PacketLine(string text) => PacketLine(Encoding.UTF8.GetBytes(text));

    private static byte[] PacketLine(byte[] payload) =>
        [.. Encoding.ASCII.GetBytes((payload.Length + 4).ToString("x4")), .. payload];

    private static MemoryStream BuildResponse(params string[] packets)
    {
        var bytes = new List<byte>();
        foreach (var packet in packets)
        {
            var payload = Encoding.UTF8.GetBytes(packet);
            bytes.AddRange(Encoding.ASCII.GetBytes((payload.Length + 4).ToString("x4")));
            bytes.AddRange(payload);
        }
        bytes.AddRange("0000"u8.ToArray());
        return new MemoryStream(bytes.ToArray());
    }
}
