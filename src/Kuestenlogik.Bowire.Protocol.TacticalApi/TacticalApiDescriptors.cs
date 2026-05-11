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
/// The generated <c>SituationServiceReflection.Descriptor</c> static class
/// is emitted by Grpc.Tools from the downloaded <c>situation_service.proto</c>
/// and carries the full transitive descriptor set, which is everything the
/// Bowire sidebar needs to render the service tree.
/// </para>
/// </summary>
internal static class TacticalApiDescriptors
{
    /// <summary>
    /// Build the Bowire service-info list for every service declared by the
    /// bundled TacticalAPI .proto files. Today that's just <c>Situation</c>;
    /// the loop is here so future TacticalAPI services land for free.
    /// </summary>
    public static List<BowireServiceInfo> BuildServiceInfos()
    {
        var fileDescriptor = SituationServiceReflection.Descriptor;
        var result = new List<BowireServiceInfo>(fileDescriptor.Services.Count);

        foreach (var service in fileDescriptor.Services)
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
}
