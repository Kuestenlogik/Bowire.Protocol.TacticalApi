// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
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

    /// <summary>
    /// Plugin settings the workbench delivers in the metadata bag. They
    /// configure this client and must not travel to the server as gRPC
    /// headers — the same rule the <c>_bowire:</c> keys already follow, and
    /// these were leaking because they carry no prefix.
    /// </summary>
    internal static readonly string[] SettingKeys =
    [
        InvocationDeadlineSecondsKey,
        StreamIdleSecondsKey,
        AllowSelfSignedCertsKey,
        UseGrpcWebKey,
    ];

    /// <summary>Setting that bounds a unary call; streams are not deadlined (see <see cref="StreamIdleSecondsKey"/>).</summary>
    internal const string InvocationDeadlineSecondsKey = "invocationDeadlineSeconds";

    /// <summary>Setting that ends a subscription after this many seconds without a frame.</summary>
    internal const string StreamIdleSecondsKey = "streamIdleSeconds";

    /// <summary>Setting that switches the wire to gRPC-Web over HTTP/1.1 (#67).</summary>
    internal const string UseGrpcWebKey = "useGrpcWeb";

    /// <summary>Setting that skips server-certificate validation.</summary>
    internal const string AllowSelfSignedCertsKey = "allowSelfSignedCerts";

    /// <summary>
    /// The shared marker the core appends for a <c>grpcweb@</c> hint
    /// (<c>BowireMetadataKeys.GrpcTransport</c>). Honoured here so a caller
    /// that already knows the core's vocabulary does not have to learn a
    /// second one; hard-coded because this plugin takes no dependency on the
    /// core's internal key list.
    /// </summary>
    internal const string GrpcTransportMarkerKey = "__bowireGrpcTransport";
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

        // tacticalapi@host:port → strip the Bowire protocol-prefix. The
        // @-form is how Bowire's URL parser routes to this plugin when
        // several plugins could match a host:port. What is left goes
        // through the same scheme handling as an unprefixed URL: the core
        // strips the prefix itself when the rest carries a scheme, but a
        // caller that hands the plugin `tacticalapi@http://host` directly
        // used to get `https://http://host` back.
        const string Prefix = "tacticalapi@";
        if (url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            url = url[Prefix.Length..];

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
    /// True when the caller asked for gRPC-Web — either through this plugin's
    /// <c>useGrpcWeb</c> setting or through the core's shared
    /// <c>__bowireGrpcTransport=web</c> marker (#67).
    /// </summary>
    internal static bool WantsGrpcWeb(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return false;
        if (TryGetBool(metadata, UseGrpcWebKey, out var fromSetting) && fromSetting) return true;
        return metadata.TryGetValue(GrpcTransportMarkerKey, out var transport)
            && string.Equals(transport, "web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds <see cref="GrpcChannelOptions"/> for the configured transport
    /// behaviour. The metadata bag drives the optional settings; an empty /
    /// null bag produces a no-options default that uses .NET's stock HTTPS
    /// + trust-store + no client cert, over native gRPC on HTTP/2.
    /// </summary>
    /// <remarks>
    /// The returned options own their handler chain (<c>DisposeHttpClient =
    /// true</c>), and the chain owns the certificates it loaded: disposing the
    /// <see cref="GrpcChannel"/> tears all of it down. Throws
    /// <see cref="TransportConfigurationException"/> when the marker's PEM
    /// material does not load — a wrong passphrase, a truncated paste — so
    /// the caller can report that as the configuration error it is rather
    /// than as a TLS failure against the server.
    /// </remarks>
    public static GrpcChannelOptions BuildChannelOptions(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return new GrpcChannelOptions();

        // Nullable + finally, and nulled the moment ownership moves into the
        // options object: that is the shape CA2000 asks for, and the handler
        // really does change owners here — GrpcChannelOptions.DisposeHttpClient
        // makes the channel dispose whichever handler it ends up holding, and
        // each wrapping handler disposes the inner one with itself.
        HttpClientHandler? handler = new();
        HttpMessageHandler? outer = null;
        MtlsCertificatePair? certs = null;
        try
        {
            var changed = false;

            // Accept-anything is the union of every opt-in that means it: the
            // declared setting (a silent no-op until #67), the legacy
            // _bowire: key, and the shared marker's own flag. Any of them set
            // wins over CA pinning below — the operator asked for it.
            var acceptAny =
                (TryGetBool(metadata, AllowSelfSignedCertsKey, out var allowSelfSigned) && allowSelfSigned)
                || (TryGetBool(metadata, TlsSkipValidationKey, out var skipValidation) && skipValidation);
            Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? pinning = null;

            // Preferred path: the shared __bowireMtls__ marker (PEM-based,
            // documented in Kuestenlogik.Bowire.Auth.MtlsConfig). Wins over
            // the legacy _bowire:client-cert-pfx keys when both are present
            // because every other Bowire plugin already speaks it — keeping
            // a single auth vocabulary across the fleet means an mTLS
            // session-profile set up in the workbench works on TacticalAPI
            // the same way it works on REST / gRPC / Kafka / AMQP.
            //
            // The core loads the pair: an encrypted private key needs the
            // marker's passphrase, and the marker's CA certificate is what a
            // pinning validator checks against. Reading only the certificate
            // and key by hand, as this used to, failed on the former and
            // ignored the latter — the marker read in full is what the auth
            // profile's four fields promise.
            var sharedMtls = MtlsConfig.TryParseFromMetadata(metadata);
            if (sharedMtls is not null)
            {
                certs = sharedMtls.TryLoadCertificates(out var error)
                    ?? throw new TransportConfigurationException(
                        $"The {MtlsConfig.MtlsMarkerKey} client certificate could not be loaded: {error}");
                handler.ClientCertificates.Add(certs.ClientCert!);
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                if (sharedMtls.AllowSelfSigned)
                    acceptAny = true;
                else if (sharedMtls.BuildServerValidator(certs.CaCert!) is { } validator)
                    pinning = (sender, cert, chain, errors) => validator(sender, cert!, chain!, errors);
                changed = true;
            }
            // Legacy: _bowire:client-cert-pfx / _bowire:client-cert-password.
            // Only consulted when the shared marker is absent so a workbench
            // shipping both paths doesn't end up loading two certificates.
            else if (metadata.TryGetValue(ClientCertPfxPathKey, out var pfxPath) &&
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

            if (acceptAny)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                changed = true;
            }
            else if (pinning is not null)
            {
                handler.ServerCertificateCustomValidationCallback = pinning;
            }

            if (!changed && !WantsGrpcWeb(metadata))
                return new GrpcChannelOptions();

            // The certificates live exactly as long as the handler that
            // presents them: a wrapper in the chain disposes the pair when
            // the channel disposes the chain. HttpClientHandler does not
            // dispose what is in ClientCertificates, so without this the
            // private key sat in memory until the process ended.
            outer = handler;
            handler = null;
            if (certs is not null)
            {
                outer = new CertificateOwningHandler(outer, certs);
                certs = null;
            }

            // gRPC-Web rides HTTP/1.1, so the version moves with the handler:
            // Grpc.Net.Client defaults to HTTP/2, and a GrpcWebHandler on an
            // HTTP/2 request is a combination no TacNet port answers (#67).
            if (WantsGrpcWeb(metadata))
            {
                var webOptions = new GrpcChannelOptions
                {
                    HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, outer),
                    HttpVersion = HttpVersion.Version11,
                    DisposeHttpClient = true,
                };
                outer = null;
                return webOptions;
            }

            var options = new GrpcChannelOptions
            {
                HttpHandler = outer,
                DisposeHttpClient = true,
            };
            outer = null;
            return options;
        }
        finally
        {
            certs?.Dispose();
            outer?.Dispose();
            handler?.Dispose();
        }
    }

    /// <summary>
    /// Holds the certificate pair a handler chain presents, and disposes it
    /// with the chain. <see cref="DelegatingHandler"/> disposes its inner
    /// handler by default, so this slots in without changing who owns what
    /// below it.
    /// </summary>
    private sealed class CertificateOwningHandler(HttpMessageHandler inner, MtlsCertificatePair certs)
        : DelegatingHandler(inner)
    {
        private MtlsCertificatePair? _certs = certs;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _certs?.Dispose();
                _certs = null;
            }
        }
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
        || key.StartsWith("__bowire", StringComparison.Ordinal)
        || string.Equals(key, MtlsConfig.MtlsMarkerKey, StringComparison.Ordinal)
        || SettingKeys.Contains(key, StringComparer.Ordinal);

    private static bool TryGetBool(IReadOnlyDictionary<string, string> source, string key, out bool value)
    {
        if (source.TryGetValue(key, out var raw) && bool.TryParse(raw, out value))
            return true;
        value = false;
        return false;
    }

    /// <summary>
    /// A positive integer setting, or 0 when the key is absent, unparseable
    /// or not positive — every seconds-valued setting here advertises 0 as
    /// "off", so a bad value degrades to the documented default rather than
    /// to an exception in the middle of a call.
    /// </summary>
    internal static int ReadPositiveSeconds(IReadOnlyDictionary<string, string>? source, string key)
    {
        if (source is null) return 0;
        if (source.TryGetValue(key, out var raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return seconds;
        }
        return 0;
    }
}

/// <summary>
/// The metadata bag asked for a transport this plugin cannot build — today,
/// a <c>__bowireMtls__</c> marker whose PEM material does not load. Reported
/// to the operator as a configuration error, distinct from a server that
/// rejected the handshake.
/// </summary>
public sealed class TransportConfigurationException : Exception
{
    public TransportConfigurationException() { }
    public TransportConfigurationException(string message) : base(message) { }
    public TransportConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
