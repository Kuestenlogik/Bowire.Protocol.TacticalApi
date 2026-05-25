// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi;

/// <summary>
/// Translates the URL shape Bowire passes to the plugin
/// (<c>tacticalapi@host:port</c>, <c>grpc://...</c>, <c>grpcs://...</c>, or a
/// plain <c>host:port</c>) into the absolute <c>http(s)://</c> URI that
/// <see cref="GrpcChannel.ForAddress(string)"/> requires, and builds the
/// <see cref="GrpcChannelOptions"/> for TLS settings declared in the
/// caller's metadata bag.
/// <para>
/// TacticalAPI servers in the wild almost always run behind mTLS — the
/// situational-awareness systems they front are classified or sensitive.
/// The plugin has to honour client certificates and (occasionally) skip
/// server-cert validation for staging without exposing a per-call
/// configuration object the workbench can't see.
/// </para>
/// </summary>
internal static class GrpcTransport
{
    /// <summary>
    /// Legacy metadata keys this plugin first shipped with. The
    /// <c>_bowire:</c> prefix marks them as Bowire-side configuration
    /// instead of gRPC metadata that gets forwarded over the wire. The
    /// shared <c>__bowireMtls__</c> marker introduced for REST / gRPC /
    /// Kafka / AMQP is now the preferred path; these stay supported
    /// because pre-1.0 callers in the field pin against them.
    /// </summary>
    internal const string TlsSkipValidationKey = "_bowire:tls-skip-validation";
    internal const string ClientCertPfxPathKey = "_bowire:client-cert-pfx";
    internal const string ClientCertPasswordKey = "_bowire:client-cert-password";

    /// <summary>
    /// Normalises whatever URL form Bowire hands the plugin into the
    /// absolute <c>http(s)://host:port</c> shape <see cref="GrpcChannel.ForAddress(string)"/>
    /// requires.
    /// </summary>
    /// <remarks>
    /// Accepted inputs:
    /// <list type="bullet">
    ///   <item><c>tacticalapi@host:port</c> — the Bowire protocol-prefix shape; the prefix is stripped, defaults to <c>https://</c>.</item>
    ///   <item><c>grpc://host:port</c> — translated to <c>http://host:port</c> (gRPC's plaintext convention).</item>
    ///   <item><c>grpcs://host:port</c> — translated to <c>https://host:port</c>.</item>
    ///   <item><c>http(s)://host:port</c> — passed through.</item>
    ///   <item><c>host:port</c> — defaulted to <c>https://host:port</c> (TacticalAPI is mTLS-by-default in the field).</item>
    /// </list>
    /// </remarks>
    public static string ResolveGrpcAddress(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);

        var url = serverUrl.Trim();

        // tacticalapi@host:port → strip the Bowire protocol-prefix and
        // default to https. The @-form is how Bowire's URL parser routes
        // to this plugin when several plugins could match a host:port.
        const string Prefix = "tacticalapi@";
        if (url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[Prefix.Length..];

        // gRPC-style scheme shorthands. RFC-flavoured but widely used; we
        // accept them so users can paste a connect string verbatim.
        if (url.StartsWith("grpcs://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[("grpcs://").Length..];
        else if (url.StartsWith("grpc://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[("grpc://").Length..];

        // Plain host:port (no scheme) → assume TLS. TacticalAPI servers in
        // the wild run behind mTLS, so https is the right default; users
        // can downshift via grpc:// when they really do mean plaintext.
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        return url;
    }

    /// <summary>
    /// Builds <see cref="GrpcChannelOptions"/> for the configured transport
    /// behaviour. The metadata bag drives the optional settings; an empty /
    /// null bag produces a no-options default that uses .NET's stock HTTPS
    /// + trust-store + no client cert.
    /// </summary>
    /// <remarks>
    /// Caller is responsible for disposing the returned options' <see cref="HttpClientHandler"/>
    /// when the channel is torn down. <see cref="GrpcChannel.Dispose"/> handles
    /// it for us when <c>DisposeHttpClient = true</c>, which is the default
    /// we set here.
    /// </remarks>
    public static GrpcChannelOptions BuildChannelOptions(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return new GrpcChannelOptions();

        var handler = new HttpClientHandler();
        var changed = false;

        // Preferred path: the shared __bowireMtls__ marker (PEM-based,
        // documented in Kuestenlogik.Bowire.Auth.MtlsConfig). Wins over
        // the legacy _bowire:client-cert-pfx keys when both are present
        // because every other Bowire plugin already speaks it — keeping
        // a single auth vocabulary across the fleet means an mTLS
        // session-profile set up in the workbench works on TacticalAPI
        // the same way it works on REST / gRPC / Kafka / AMQP.
        var sharedMtls = MtlsConfig.TryParseFromMetadata(metadata);
        if (sharedMtls is not null)
        {
            var cert = X509Certificate2.CreateFromPem(
                sharedMtls.CertificatePem, sharedMtls.PrivateKeyPem);
            handler.ClientCertificates.Add(cert);
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            if (sharedMtls.AllowSelfSigned)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            changed = true;
        }

        // Legacy: _bowire:tls-skip-validation. Still honoured so existing
        // recorded sessions / saved auth profiles keep working — the
        // shared marker's AllowSelfSigned property covers the same need.
        if (TryGetBool(metadata, TlsSkipValidationKey, out var skipValidation) && skipValidation)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            changed = true;
        }

        // Legacy: _bowire:client-cert-pfx / _bowire:client-cert-password.
        // Only consulted when the shared marker is absent so a workbench
        // shipping both paths doesn't end up loading two certificates.
        if (sharedMtls is null &&
            metadata.TryGetValue(ClientCertPfxPathKey, out var pfxPath) &&
            !string.IsNullOrWhiteSpace(pfxPath))
        {
            metadata.TryGetValue(ClientCertPasswordKey, out var pfxPassword);
            // X509CertificateLoader is .NET 9+; on earlier targets, fall
            // back to the X509Certificate2(pfxPath, pfxPassword) ctor.
            var cert = X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                pfxPassword,
                X509KeyStorageFlags.DefaultKeySet);
            handler.ClientCertificates.Add(cert);
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            changed = true;
        }

        if (!changed)
        {
            handler.Dispose();
            return new GrpcChannelOptions();
        }

        return new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        };
    }

    /// <summary>
    /// Metadata keys consumed by this transport layer. The caller filters
    /// these out before forwarding the remainder as gRPC request metadata —
    /// shipping <c>_bowire:tls-skip-validation</c> or
    /// <c>__bowireMtls__</c> on the wire would leak configuration intent
    /// (and credentials) to the server.
    /// </summary>
    public static bool IsTransportKey(string key) =>
        key.StartsWith("_bowire:", StringComparison.Ordinal)
        || string.Equals(key, MtlsConfig.MtlsMarkerKey, StringComparison.Ordinal);

    private static bool TryGetBool(IReadOnlyDictionary<string, string> source, string key, out bool value)
    {
        if (source.TryGetValue(key, out var raw) && bool.TryParse(raw, out value))
            return true;
        value = false;
        return false;
    }
}
