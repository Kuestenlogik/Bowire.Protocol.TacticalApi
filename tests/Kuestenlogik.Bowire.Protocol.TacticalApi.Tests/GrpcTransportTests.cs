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
