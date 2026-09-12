// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Protobuf.Reflection;
using Kuestenlogik.Bowire.Models;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi;

/// <summary>
/// Walks the generated protobuf <see cref="ServiceDescriptor"/> graph for
/// TacticalAPI and projects it into Bowire's protocol-neutral
/// <see cref="BowireServiceInfo"/> shape.
/// <para>
/// The generated <c>*Reflection.Descriptor</c> static classes are emitted
/// by Grpc.Tools from the downloaded <c>.proto</c> files and carry the full
/// transitive descriptor set, which is everything the Bowire sidebar needs
/// to render the service tree.
/// </para>
/// </summary>
internal static class TacticalApiDescriptors
{
    /// <summary>
    /// Every upstream <c>.proto</c> file that declares a service. One entry
    /// per service-bearing file, because a <see cref="FileDescriptor"/> only
    /// exposes the services declared in its own file — the transitive
    /// imports it carries are message and enum types, not other files'
    /// services.
    /// <para>
    /// Adding an upstream service is therefore a two-line change: download
    /// the <c>.proto</c> in the csproj fetch target, add its generated
    /// reflection descriptor here. Everything downstream — discovery,
    /// invoke, streaming, the mock emitter — iterates this list rather
    /// than naming a file of its own.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<FileDescriptor> ServiceFiles { get; } =
    [
        SituationServiceReflection.Descriptor,
        OwnPoseServiceReflection.Descriptor,
        BlueForceTrackingServiceReflection.Descriptor,
    ];

    /// <summary>
    /// Build the Bowire service-info list for every service declared by the
    /// bundled TacticalAPI .proto files — <c>Situation</c>,
    /// <c>OwnPose</c> and <c>BlueForceTracking</c> as of the pinned upstream
    /// commit.
    /// </summary>
    public static List<BowireServiceInfo> BuildServiceInfos()
    {
        var result = new List<BowireServiceInfo>();

        foreach (var service in EnumerateServices())
        {
            var methods = new List<BowireMethodInfo>(service.Methods.Count);
            foreach (var m in service.Methods)
            {
                methods.Add(new BowireMethodInfo(
                    Name: m.Name,
                    FullName: $"{service.FullName}/{m.Name}",
                    ClientStreaming: m.IsClientStreaming,
                    ServerStreaming: m.IsServerStreaming,
                    InputType: BuildMessageInfo(m.InputType),
                    OutputType: BuildMessageInfo(m.OutputType),
                    MethodType: ClassifyMethodType(m)));
            }

            result.Add(new BowireServiceInfo(
                Name: service.Name,
                Package: service.File.Package,
                Methods: methods)
            {
                Source = "proto",
            });
        }

        return result;
    }

    /// <summary>Every service across every bundled service-bearing file, in declaration order.</summary>
    internal static IEnumerable<ServiceDescriptor> EnumerateServices()
    {
        foreach (var file in ServiceFiles)
        {
            foreach (var service in file.Services)
            {
                yield return service;
            }
        }
    }

    /// <summary>Shallow message-info projection — fields only, no descent into nested message types.</summary>
    private static BowireMessageInfo BuildMessageInfo(MessageDescriptor msg)
    {
        var fields = new List<BowireFieldInfo>(msg.Fields.InFieldNumberOrder().Count);
        foreach (var f in msg.Fields.InFieldNumberOrder())
        {
            fields.Add(new BowireFieldInfo(
                Name: f.Name,
                Number: f.FieldNumber,
                Type: f.FieldType.ToString(),
                Label: f.IsRepeated ? "repeated" : (f.IsMap ? "map" : "optional"),
                IsMap: f.IsMap,
                IsRepeated: f.IsRepeated && !f.IsMap,
                MessageType: null,
                EnumValues: null));
        }
        return new BowireMessageInfo(msg.Name, msg.FullName, fields);
    }

    private static string ClassifyMethodType(MethodDescriptor m)
    {
        return (m.IsClientStreaming, m.IsServerStreaming) switch
        {
            (false, false) => "Unary",
            (false, true) => "ServerStreaming",
            (true, false) => "ClientStreaming",
            (true, true) => "DuplexStreaming",
        };
    }

    /// <summary>
    /// Resolve a (service, method) tuple against the bundled descriptors.
    /// Returns <c>false</c> when either side doesn't match. Shared
    /// between BowireTacticalApiProtocol's Invoke / InvokeStream paths
    /// and the TacticalApiMockEmitter so all three speak the same
    /// resolution logic.
    /// </summary>
    public static bool TryResolve(
        string service, string method,
        out ServiceDescriptor? serviceDescriptor,
        out MethodDescriptor? methodDescriptor)
        => TryResolve(service, method, out serviceDescriptor, out methodDescriptor, out _);

    /// <summary>
    /// Resolution with a caller-facing explanation of the miss. The live
    /// invoke paths surface <paramref name="error"/> to the operator; the
    /// mock emitter logs its own warning and ignores it.
    /// </summary>
    public static bool TryResolve(
        string service, string method,
        out ServiceDescriptor? serviceDescriptor,
        out MethodDescriptor? methodDescriptor,
        out string? error)
    {
        serviceDescriptor = EnumerateServices().FirstOrDefault(s =>
            string.Equals(s.FullName, service, StringComparison.Ordinal) ||
            string.Equals(s.Name, service, StringComparison.Ordinal));
        if (serviceDescriptor is null)
        {
            methodDescriptor = null;
            error = $"Service '{service}' is not part of the bundled TacticalAPI descriptors. " +
                    $"Known: {string.Join(", ", EnumerateServices().Select(s => s.FullName))}.";
            return false;
        }

        methodDescriptor = serviceDescriptor.Methods.FirstOrDefault(m =>
            string.Equals(m.Name, method, StringComparison.Ordinal));
        if (methodDescriptor is null)
        {
            error = $"Method '{method}' not declared on '{serviceDescriptor.FullName}'. " +
                    $"Known: {string.Join(", ", serviceDescriptor.Methods.Select(m => m.Name))}.";
            return false;
        }

        error = null;
        return true;
    }
}
