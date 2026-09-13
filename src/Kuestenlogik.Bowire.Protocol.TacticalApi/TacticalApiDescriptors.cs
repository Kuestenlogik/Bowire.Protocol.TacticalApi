// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
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
                    // Request types are projected with their nested structure, so
                    // the invoke form can render a write and show the contract's
                    // annotations (#68). Response types stay flat: nothing builds a
                    // form from them, the operator reads the JSON, and descending
                    // both sides inflated one discovery response from 4 KB to
                    // 660 KB against this schema.
                    InputType: BuildMessageInfo(m.InputType, descend: true),
                    OutputType: BuildMessageInfo(m.OutputType, descend: false),
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

    /// <summary>
    /// How far the projection descends into nested message types.
    /// </summary>
    /// <remarks>
    /// Four levels, because that is what a write needs:
    /// <c>AddOrUpdateSituationObjectsRequest.situation_objects</c> →
    /// <c>UpdateSituationObject.symbol</c> → <c>UpdateSymbol.name</c> →
    /// <c>UpdatePropertyString.content</c>. The projection used to stop at the
    /// top level, which meant the invoke form showed one repeated field and
    /// nothing inside it — and the contract annotations (#68), which live on
    /// exactly those nested messages, reached nobody. Deeper than this and the
    /// discovery payload grows without helping: the situation-object graph is
    /// wide, and an operator who needs level five is writing the JSON by hand
    /// anyway.
    /// </remarks>
    private const int MaxNestingDepth = 4;

    /// <summary>
    /// Project a message and, up to <see cref="MaxNestingDepth"/>, the messages
    /// its fields carry — with the field annotations the contract states in
    /// prose (#68).
    /// </summary>
    private static BowireMessageInfo BuildMessageInfo(MessageDescriptor msg, bool descend = true) =>
        BuildMessageInfo(msg, depth: descend ? 0 : MaxNestingDepth, ancestry: []);

    private static BowireMessageInfo BuildMessageInfo(
        MessageDescriptor msg, int depth, HashSet<string> ancestry)
    {
        var fields = new List<BowireFieldInfo>(msg.Fields.InFieldNumberOrder().Count);
        foreach (var f in msg.Fields.InFieldNumberOrder())
        {
            // Recursion stops on depth and on the path, not on "seen anywhere":
            // Identity appears under nearly every write message, and a global
            // visited-set would project it once and leave every later field
            // hollow. The schema does contain cycles (a situation object can
            // reference its own kind), which the ancestry check breaks.
            BowireMessageInfo? nested = null;
            if (f.FieldType == FieldType.Message
                && f.MessageType is { } nestedDescriptor
                && depth < MaxNestingDepth
                && !ancestry.Contains(nestedDescriptor.FullName))
            {
                var nextAncestry = new HashSet<string>(ancestry, StringComparer.Ordinal)
                {
                    msg.FullName,
                };
                nested = BuildMessageInfo(nestedDescriptor, depth + 1, nextAncestry);
            }

            fields.Add(new BowireFieldInfo(
                Name: f.Name,
                Number: f.FieldNumber,
                Type: f.FieldType.ToString(),
                Label: f.IsRepeated ? "repeated" : (f.IsMap ? "map" : "optional"),
                IsMap: f.IsMap,
                IsRepeated: f.IsRepeated && !f.IsMap,
                MessageType: nested,
                EnumValues: f.FieldType == FieldType.Enum && f.EnumType is { } enumType
                    ? enumType.Values
                        .Select(v => new BowireEnumValue(v.Name, v.Number))
                        .ToList()
                    : null)
            {
                Required = IsRequiredByContract(f),
                Description = DescribeContract(f),
            });
        }
        return new BowireMessageInfo(msg.Name, msg.FullName, fields);
    }

    /// <summary>
    /// Project one bundled message by its simple name, so the contract
    /// annotations (#68) can be pinned by a test.
    /// </summary>
    /// <remarks>
    /// A seam beside <see cref="BuildServiceInfos"/>, not a replacement for
    /// testing through it: the messages carrying the annotations —
    /// <c>UpdateSymbol</c>, <c>UpdatePropertyString</c> — sit three and four
    /// levels below the request types, and a test that wants to pin one
    /// message's wording should not have to walk there. The route itself is
    /// pinned separately, through discovery. Searches the service files and
    /// everything they import, because the write messages live in
    /// <c>situation_object_updates.proto</c> while the services live next door.
    /// </remarks>
    internal static BowireMessageInfo DescribeMessageForTests(string messageName)
    {
        foreach (var file in ServiceFiles)
        {
            foreach (var candidate in Walk(file))
            {
                var found = candidate.FindTypeByName<MessageDescriptor>(messageName);
                if (found is not null) return BuildMessageInfo(found);
            }
        }

        throw new InvalidOperationException($"No bundled message named '{messageName}'.");

        static IEnumerable<FileDescriptor> Walk(FileDescriptor file)
        {
            yield return file;
            foreach (var dep in file.Dependencies) yield return dep;
        }
    }

    // ---- contract semantics the descriptor cannot carry ---------------------
    //
    // proto3 has no `required`, and Grpc.Tools drops comments unless the
    // generator keeps source info — so the rules the upstream .proto files
    // state in prose reach the workbench from here or not at all. Without them
    // the invoke form shows a flat tree of every property, which invites
    // sending a complete object: a legitimate request that silently overwrites
    // the properties the operator never meant to touch (#68).
    //
    // Provenance: rheinmetall/tactical_api/v0/{situation_object_updates,types}.proto
    // at the pinned commit 58661c9c5de7db1b944a37f6ed05fe16c603cd0e. Re-read
    // them on the next proto bump — the wording below is quoted from there.

    /// <summary>Envelope fields every write and delete message marks "Required:".</summary>
    private static readonly HashSet<string> RequiredEnvelopeFields =
        new(StringComparer.Ordinal) { "identity", "reporter", "reporting_time" };

    /// <summary>
    /// True for the three fields upstream marks <c>Required:</c> on every
    /// write / delete message (<c>UpdateSymbol</c>, <c>UpdateActionTask</c>,
    /// <c>DeleteSituationObject</c>, …). Keyed by field name rather than by a
    /// per-message table because the annotation is uniform across them — a
    /// table would be a second list to keep aligned with the protos.
    /// </summary>
    private static bool IsRequiredByContract(FieldDescriptor f) =>
        RequiredEnvelopeFields.Contains(f.Name)
        && (f.ContainingType.Name.StartsWith("Update", StringComparison.Ordinal)
            || f.ContainingType.Name.StartsWith("Delete", StringComparison.Ordinal));

    /// <summary>
    /// What the operator has to know before filling this field: the
    /// oneof-exclusivity the descriptor knows, plus the rules the upstream
    /// comments state and the descriptor does not carry.
    /// </summary>
    private static string? DescribeContract(FieldDescriptor f)
    {
        var notes = new List<string>(2);

        // Straight from the descriptor, so it cannot drift: "Note: only one of
        // the situation object types is supported at a time!"
        if (f.ContainingOneof is { } oneof)
            notes.Add($"One of '{oneof.Name}' — only one field in this group may be set.");

        switch (f.Name)
        {
            case "identity" when IsRequiredByContract(f):
                notes.Add("Required: the object's unique identity.");
                break;
            case "reporter" when IsRequiredByContract(f):
                notes.Add(
                    "Required: the ID of the reporter that generated the change. "
                    + "Use the value you get from Rheinmetall — \"TacticalAPI\" if in doubt.");
                break;
            case "reporting_time" when IsRequiredByContract(f):
                notes.Add(
                    "Required: time of the change in UTC. Only the most up-to-date information "
                    + "is considered, so use the current time for new changes — but keep the "
                    + "previous timestamp when nothing changed.");
                break;

            // "These data properties specify the fields to be changed. That
            // means the content value can be null. If the value should not be
            // changed, then omit the entire property."
            case "content" when f.ContainingType.Name.StartsWith("UpdateProperty", StringComparison.Ordinal):
                notes.Add(
                    "Sparse update: omit the whole property to leave the current value untouched. "
                    + "Sending the property with no content clears the value.");
                break;

            case "int32_identity":
            case "int64_identity":
                notes.Add("Not for external use — reserved for internal low-bandwidth scenarios.");
                break;

            default:
                break;
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
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
