// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

public sealed class GrpcTransportTests
{
    [Theory]
    // Bowire protocol-prefix form. TacticalAPI servers are mTLS-by-default
    // in the field, so https is the right fallback.
    [InlineData("tacticalapi@situation.example.com:4267", "https://situation.example.com:4267")]
    [InlineData("TACTICALAPI@situation.example.com:4267", "https://situation.example.com:4267")] // case-insensitive prefix
    // gRPC-scheme shorthand → http (plaintext gRPC).
    [InlineData("grpc://localhost:50051", "http://localhost:50051")]
    // gRPC-secure shorthand → https.
    [InlineData("grpcs://localhost:50051", "https://localhost:50051")]
    // Already an http(s) URL — passed through unchanged.
    [InlineData("https://situation.example.com:4267", "https://situation.example.com:4267")]
    [InlineData("http://situation.example.com:4267", "http://situation.example.com:4267")]
    // Bare host:port → assume TLS.
    [InlineData("situation.example.com:4267", "https://situation.example.com:4267")]
    // Trims whitespace.
    [InlineData("  tacticalapi@host:4267  ", "https://host:4267")]
    // The prefix on a URL that already carries a scheme — the shape this
    // repo's own sample README documents — keeps that scheme.
    [InlineData("tacticalapi@http://localhost:5192", "http://localhost:5192")]
    [InlineData("tacticalapi@grpc://localhost:5192", "http://localhost:5192")]
    [InlineData("tacticalapi@https://host:4267", "https://host:4267")]
    public void ResolveGrpcAddress_NormalisesToHttpOrHttps(string input, string expected)
    {
        Assert.Equal(expected, GrpcTransport.ResolveGrpcAddress(input));
    }

    [Fact]
    public void ResolveGrpcAddress_NullOrEmpty_Throws()
    {
        Assert.Throws<ArgumentException>(() => GrpcTransport.ResolveGrpcAddress(""));
        Assert.Throws<ArgumentNullException>(() => GrpcTransport.ResolveGrpcAddress(null!));
    }

    [Fact]
    public void BuildChannelOptions_EmptyMetadata_ReturnsDefault()
    {
        // No transport-level options set ⇒ default GrpcChannelOptions with
        // no custom HttpHandler. Confirms we don't allocate an
        // HttpClientHandler unless something asked us to.
        var opts = GrpcTransport.BuildChannelOptions(null);
        Assert.Null(opts.HttpHandler);

        var opts2 = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>());
        Assert.Null(opts2.HttpHandler);
    }

    [Fact]
    public void BuildChannelOptions_NonTransportKeysOnly_StillReturnsDefault()
    {
        // Keys without the _bowire: transport prefix are wire-level gRPC
        // headers (Authorization etc.) — they don't influence the channel
        // at all, so we shouldn't allocate an HttpHandler for them.
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abc",
            ["x-correlation-id"] = "1234",
        });
        Assert.Null(opts.HttpHandler);
    }

    [Fact]
    public void BuildChannelOptions_TlsSkipValidation_SetsCustomCallback()
    {
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.TlsSkipValidationKey] = "true",
        });

        var handler = Assert.IsType<HttpClientHandler>(opts.HttpHandler);
        // The DangerousAccept* helper is a static delegate the runtime
        // exposes; we identify it by reference equality.
        Assert.Same(
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void BuildChannelOptions_TlsSkipValidationFalse_NoCallback()
    {
        // 'false' is the same as 'unset' — explicit no-op so a stale
        // 'false' in someone's metadata doesn't accidentally still allocate
        // an HttpHandler.
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.TlsSkipValidationKey] = "false",
        });
        Assert.Null(opts.HttpHandler);
    }

    [Fact]
    public void BuildChannelOptions_GrpcWebSetting_SwitchesHandlerAndHttpVersion()
    {
        // Rheinmetall's TacNet exposes gRPC-Web on its own port and their
        // reference client defaults to it; before #67 this plugin could only
        // speak native gRPC over HTTP/2.
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.UseGrpcWebKey] = "true",
        });

        Assert.IsType<Grpc.Net.Client.Web.GrpcWebHandler>(opts.HttpHandler);
        // The version has to move with the handler — gRPC-Web rides HTTP/1.1.
        Assert.Equal(new Version(1, 1), opts.HttpVersion);
    }

    [Fact]
    public void BuildChannelOptions_SharedGrpcWebMarker_SwitchesTheSameWay()
    {
        // The core appends this for a `grpcweb@` hint. Honouring it means a
        // caller that already knows Bowire's vocabulary needs no second one.
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.GrpcTransportMarkerKey] = "web",
        });

        Assert.IsType<Grpc.Net.Client.Web.GrpcWebHandler>(opts.HttpHandler);
    }

    [Fact]
    public void BuildChannelOptions_WithoutGrpcWeb_StaysOnNativeGrpc()
    {
        // The default must not move: :4267 keeps working exactly as before.
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.UseGrpcWebKey] = "false",
        });

        Assert.Null(opts.HttpHandler);
        Assert.Null(opts.HttpVersion);
    }

    [Fact]
    public void BuildChannelOptions_AllowSelfSignedCertsSetting_IsActuallyRead()
    {
        // The setting shipped in the plugin's settings surface and nothing
        // consumed it, so the workbench toggle did nothing at all (#67).
        var opts = GrpcTransport.BuildChannelOptions(new Dictionary<string, string>
        {
            [GrpcTransport.AllowSelfSignedCertsKey] = "true",
        });

        var handler = Assert.IsType<HttpClientHandler>(opts.HttpHandler);
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void IsTransportKey_CoversTheSettingsTheWorkbenchDelivers()
    {
        // Plugin settings configure this client. They carry no `_bowire:`
        // prefix, so they were being forwarded to the server as gRPC headers —
        // the same leak of configuration intent the prefixed keys are filtered
        // for (#67).
        Assert.True(GrpcTransport.IsTransportKey("invocationDeadlineSeconds"));
        Assert.True(GrpcTransport.IsTransportKey("streamIdleSeconds"));
        Assert.True(GrpcTransport.IsTransportKey(GrpcTransport.AllowSelfSignedCertsKey));
        Assert.True(GrpcTransport.IsTransportKey(GrpcTransport.UseGrpcWebKey));
        Assert.True(GrpcTransport.IsTransportKey(GrpcTransport.GrpcTransportMarkerKey));
    }

    [Fact]
    public void IsTransportKey_OnlyBowirePrefixIsTransport()
    {
        Assert.True(GrpcTransport.IsTransportKey(GrpcTransport.TlsSkipValidationKey));
        Assert.True(GrpcTransport.IsTransportKey(GrpcTransport.ClientCertPfxPathKey));
        Assert.True(GrpcTransport.IsTransportKey("_bowire:anything"));

        Assert.False(GrpcTransport.IsTransportKey("Authorization"));
        Assert.False(GrpcTransport.IsTransportKey("x-bowire-anything"));
        // Case-sensitive on the prefix — gRPC metadata is case-insensitive
        // but the _bowire: key is ours alone and case-sensitive lookup
        // catches accidental typos in operator configs.
        Assert.False(GrpcTransport.IsTransportKey("_Bowire:tls-skip-validation"));
    }
}
