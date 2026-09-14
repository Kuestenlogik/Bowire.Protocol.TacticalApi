// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Grpc.Net.Client.Web;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Tests;

/// <summary>
/// The shared <c>__bowireMtls__</c> marker, read in full. The transport used
/// to take the certificate and key from it by hand and ignore the rest: an
/// encrypted private key failed to load, and a CA certificate meant for
/// pinning was never consulted. These pin what the workbench's mTLS auth
/// profile promises through its four fields.
/// </summary>
public sealed class GrpcTransportMtlsTests
{
    [Fact]
    public void SharedMarker_LoadsTheClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");

        var opts = GrpcTransport.BuildChannelOptions(Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
        }));

        var inner = InnerHttpClientHandler(opts.HttpHandler);
        Assert.Single(inner.ClientCertificates);
        Assert.Equal(ClientCertificateOption.Manual, inner.ClientCertificateOptions);
        // No override asked for: the system trust store validates the server.
        Assert.Null(inner.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void SharedMarker_WithPassphrase_LoadsAnEncryptedKey()
    {
        // The profile's passphrase field exists for exactly this key shape.
        // CreateFromPem on an encrypted key throws; the core's loader takes
        // the encrypted path when a passphrase is present.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");
        var encryptedKey = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "korrekt-batterie-pferd",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

        var opts = GrpcTransport.BuildChannelOptions(Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = encryptedKey,
            passphrase = "korrekt-batterie-pferd",
        }));

        var inner = InnerHttpClientHandler(opts.HttpHandler);
        Assert.Single(inner.ClientCertificates);
    }

    [Fact]
    public void SharedMarker_WithCaCertificate_PinsRatherThanAcceptingAnything()
    {
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");
        using var caRsa = RSA.Create(2048);
        using var ca = SelfSigned(caRsa, "CN=tacticalapi-staging-ca");

        var opts = GrpcTransport.BuildChannelOptions(Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
            caCertificate = ca.ExportCertificatePem(),
        }));

        var inner = InnerHttpClientHandler(opts.HttpHandler);
        // A validator is installed — and it is the CA-pinning one, not the
        // accept-anything shortcut a self-signed opt-in installs.
        Assert.NotNull(inner.ServerCertificateCustomValidationCallback);
        Assert.NotSame(
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            inner.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void SharedMarker_AllowSelfSigned_AcceptsAnything_EvenWithACa()
    {
        // The operator's explicit opt-in is the broader permission; a CA in
        // the same profile does not narrow it back down.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");

        var opts = GrpcTransport.BuildChannelOptions(Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
            caCertificate = cert.ExportCertificatePem(),
            allowSelfSigned = true,
        }));

        var inner = InnerHttpClientHandler(opts.HttpHandler);
        Assert.Same(
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            inner.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void SharedMarker_AndGrpcWeb_KeepBothInTheChain()
    {
        // TacNet's :4268 behind mTLS: the web handler wraps the chain that
        // owns the certificate, and the certificate is still presented.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");

        var metadata = Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
        });
        metadata[GrpcTransport.UseGrpcWebKey] = "true";

        var opts = GrpcTransport.BuildChannelOptions(metadata);

        var web = Assert.IsType<GrpcWebHandler>(opts.HttpHandler);
        var inner = InnerHttpClientHandler(web.InnerHandler);
        Assert.Single(inner.ClientCertificates);
        Assert.Equal(new Version(1, 1), opts.HttpVersion);
    }

    [Fact]
    public void SharedMarker_WrongPassphrase_IsAConfigurationError()
    {
        // Not a TLS failure against the server — the operator's own material
        // did not load, and the message has to say so before any dial.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");
        var encryptedKey = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "richtig",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

        var ex = Assert.Throws<TransportConfigurationException>(() =>
            GrpcTransport.BuildChannelOptions(Marker(new
            {
                certificate = cert.ExportCertificatePem(),
                privateKey = encryptedKey,
                passphrase = "falsch",
            })));

        Assert.Contains(MtlsConfig.MtlsMarkerKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMarker_GarbagePem_IsAConfigurationError()
    {
        Assert.Throws<TransportConfigurationException>(() =>
            GrpcTransport.BuildChannelOptions(Marker(new
            {
                certificate = "-----BEGIN CERTIFICATE-----\nnicht\n-----END CERTIFICATE-----",
                privateKey = "-----BEGIN PRIVATE KEY-----\nwirklich\n-----END PRIVATE KEY-----",
            })));
    }

    [Fact]
    public void SharedMarker_WinsOverLegacyPfxKeys()
    {
        // Both paths present must not load two certificates. The legacy path
        // points at a file that does not exist; if it were consulted the
        // loader would throw rather than return.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");

        var metadata = Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
        });
        metadata[GrpcTransport.ClientCertPfxPathKey] = Path.Combine(Path.GetTempPath(), "gibt-es-nicht.pfx");

        var opts = GrpcTransport.BuildChannelOptions(metadata);
        Assert.Single(InnerHttpClientHandler(opts.HttpHandler).ClientCertificates);
    }

    [Fact]
    public void DisposingTheChain_DisposesTheCertificate()
    {
        // HttpClientHandler does not dispose what is in ClientCertificates,
        // so the private key used to stay in memory until process exit. The
        // chain owns the pair now: dispose it and the certificate is gone.
        using var rsa = RSA.Create(2048);
        using var cert = SelfSigned(rsa, "CN=tacticalapi-client");

        var opts = GrpcTransport.BuildChannelOptions(Marker(new
        {
            certificate = cert.ExportCertificatePem(),
            privateKey = rsa.ExportPkcs8PrivateKeyPem(),
        }));
        var loaded = Assert.IsType<X509Certificate2>(InnerHttpClientHandler(opts.HttpHandler).ClientCertificates[0]);
        Assert.NotNull(loaded.GetRSAPrivateKey());

        opts.HttpHandler!.Dispose();

        // A disposed X509Certificate2 has no handle; every accessor throws.
        Assert.Throws<CryptographicException>(() => loaded.GetRSAPrivateKey());
    }

    private static Dictionary<string, string> Marker(object profile) => new(StringComparer.Ordinal)
    {
        [MtlsConfig.MtlsMarkerKey] = JsonSerializer.Serialize(profile),
    };

    private static X509Certificate2 SelfSigned(RSA key, string subject)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>
    /// Walk the chain down to the <see cref="HttpClientHandler"/> that
    /// actually presents the certificate — through whatever owning or
    /// gRPC-Web wrapper sits above it.
    /// </summary>
    private static HttpClientHandler InnerHttpClientHandler(HttpMessageHandler? handler)
    {
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler;
        return Assert.IsType<HttpClientHandler>(handler);
    }
}
