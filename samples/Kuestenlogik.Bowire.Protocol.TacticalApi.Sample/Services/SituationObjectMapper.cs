// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Turns an <c>Update…</c> message into the served object it describes,
/// for every one of the eleven types in the <c>UpdateSituationObject</c>
/// oneof — by the contract's own regularity rather than by eleven
/// hand-written copies of the same code.
/// </summary>
/// <remarks>
/// <para>
/// <c>situation_object_updates.proto</c> and <c>situation_object_types.proto</c>
/// are mirror images. Every <c>UpdateX</c> carries the envelope
/// (<c>identity</c>, <c>reporter</c>, <c>reporting_time</c>) and then
/// <c>UpdateProperty…</c> fields; the served <c>X</c> carries
/// <c>identity</c>, <c>creation_meta_data</c> and <c>DataProperty…</c>
/// fields <em>of the same names</em>. Each <c>UpdatePropertyT</c> is the
/// matching <c>DataPropertyT</c> minus its <c>creation_meta_data</c>, field
/// names included (<c>content</c>, <c>contents</c>, <c>source</c>,
/// <c>type</c>, <c>x</c> / <c>y</c> / <c>z</c>). So the mapping is: walk the
/// update's fields, find the served field of the same name, build its
/// property from the update property's fields of the same names, stamp
/// the metadata. Protobuf reflection does that once, for all of them.
/// </para>
/// <para>
/// Three places the mirror is not exact, each handled by name:
/// <c>foreign_key</c> (one identity with a source) lands in the served
/// <c>foreign_keys</c> map under that source; an
/// <c>UpdatePropertySituationObjects</c> (an overlay's contents) holds
/// nested <em>updates</em>, which become nested served objects through
/// this same mapper; and a field with no served counterpart
/// (<c>ActionEvent.additional_location</c> today) is left where the
/// contract left it — nowhere.
/// </para>
/// </remarks>
internal static class SituationObjectMapper
{
    private const string IdentityField = "identity";
    private const string ReporterField = "reporter";
    private const string ReportingTimeField = "reporting_time";
    private const string CreationMetaDataField = "creation_meta_data";
    private const string ForeignKeyField = "foreign_key";
    private const string ForeignKeysField = "foreign_keys";
    private const string DefaultForeignKeySource = "TacticalAPI";

    /// <summary>The envelope every update message carries.</summary>
    internal readonly record struct Envelope(Identity? Identity, Identity? Reporter, Timestamp? ReportingTime);

    /// <summary>
    /// The update inside the oneof with its case name — the name the served
    /// object's oneof uses too (<c>symbol</c>, <c>action_task</c>, …) — or
    /// <c>null</c> when none is set, the case a hand-written request produces.
    /// </summary>
    internal static (string CaseName, IMessage Payload)? Unwrap(UpdateSituationObject update)
    {
        var oneof = UpdateSituationObject.Descriptor.Oneofs.Single(o => o.Name == "type");
        var field = oneof.Accessor.GetCaseFieldDescriptor(update);
        return field?.Accessor.GetValue(update) is IMessage payload ? (field.Name, payload) : null;
    }

    /// <summary>The served object's case name (<c>symbol</c>, <c>action_task</c>, …), or <c>null</c> when none is set.</summary>
    internal static string? CaseName(SituationObject served)
    {
        var oneof = SituationObject.Descriptor.Oneofs.Single(o => o.Name == "type");
        return oneof.Accessor.GetCaseFieldDescriptor(served)?.Name;
    }

    internal static Envelope EnvelopeOf(IMessage payload) => new(
        payload.Descriptor.FindFieldByName(IdentityField)?.Accessor.GetValue(payload) as Identity,
        payload.Descriptor.FindFieldByName(ReporterField)?.Accessor.GetValue(payload) as Identity,
        payload.Descriptor.FindFieldByName(ReportingTimeField)?.Accessor.GetValue(payload) as Timestamp);

    /// <summary>True when the update sets the property of that name — the one the tick would otherwise overwrite.</summary>
    internal static bool Sets(IMessage payload, string fieldName) =>
        payload.Descriptor.FindFieldByName(fieldName) is { } field && field.Accessor.GetValue(payload) is not null;

    /// <summary>
    /// A fresh served object of the update's type, with identity and
    /// creation metadata and nothing else — the shape a create starts from
    /// before <see cref="ApplySparse"/> fills in what the update carries.
    /// </summary>
    internal static SituationObject CreateServed(string caseName, IMessage payload)
    {
        var servedField = SituationObject.Descriptor.FindFieldByName(caseName)
            ?? throw new InvalidOperationException($"No served type for '{caseName}'.");
        var served = NewMessage(servedField.MessageType);
        var envelope = EnvelopeOf(payload);
        served.Descriptor.FindFieldByName(IdentityField)!.Accessor.SetValue(served, envelope.Identity);
        served.Descriptor.FindFieldByName(CreationMetaDataField)!.Accessor.SetValue(served, MetaOf(envelope));

        var wrapper = new SituationObject();
        servedField.Accessor.SetValue(wrapper, served);
        return wrapper;
    }

    /// <summary>
    /// Copy the properties the update carries onto the served object, and
    /// only those. <c>situation_object_updates.proto</c>: "If the value
    /// should not be changed, then omit the entire property." A property
    /// sent with no content clears the value — the served property keeps
    /// its creation metadata and loses its content, which is what a null
    /// looks like on the read side.
    /// </summary>
    internal static void ApplySparse(SituationObject target, IMessage payload)
    {
        var served = ServedPayload(target)
            ?? throw new InvalidOperationException("The target has no served object to apply to.");
        var meta = MetaOf(EnvelopeOf(payload));

        foreach (var updateField in payload.Descriptor.Fields.InDeclarationOrder())
        {
            if (updateField.Name is IdentityField or ReporterField or ReportingTimeField) continue;
            if (updateField.Accessor.GetValue(payload) is not IMessage updateProperty) continue;

            if (updateField.Name == ForeignKeyField)
            {
                ApplyForeignKey(served, updateProperty, meta);
                continue;
            }

            var servedField = served.Descriptor.FindFieldByName(updateField.Name);
            if (servedField is null || servedField.FieldType != FieldType.Message) continue;

            var servedProperty = NewMessage(servedField.MessageType);
            CopyPropertyFields(updateProperty, servedProperty);
            servedField.MessageType.FindFieldByName(CreationMetaDataField)?.Accessor.SetValue(servedProperty, meta);
            servedField.Accessor.SetValue(served, servedProperty);
        }
    }

    /// <summary>The served message inside the wrapper's oneof, whatever its type.</summary>
    internal static IMessage? ServedPayload(SituationObject served)
    {
        var oneof = SituationObject.Descriptor.Oneofs.Single(o => o.Name == "type");
        var field = oneof.Accessor.GetCaseFieldDescriptor(served);
        return field is null ? null : field.Accessor.GetValue(served) as IMessage;
    }

    /// <summary>The served object's own identity, whatever its type.</summary>
    internal static Identity? IdentityOf(SituationObject served) =>
        ServedPayload(served)?.Descriptor.FindFieldByName(IdentityField)?.Accessor.GetValue(ServedPayload(served)!) as Identity;

    /// <summary>The served object's <c>expiry_time</c> content, when it has one.</summary>
    internal static Timestamp? ExpiryOf(SituationObject served)
    {
        if (ServedPayload(served) is not { } payload) return null;
        var property = payload.Descriptor.FindFieldByName("expiry_time")?.Accessor.GetValue(payload) as DataPropertyTimestamp;
        return property?.Content;
    }

    // ---- the mirror, field by field -----------------------------------------

    /// <summary>
    /// Every field of the update property lands on the served property
    /// field of the same name: scalars and enums as they are, messages as
    /// they are, repeated fields item by item — with a nested update
    /// (an overlay's contents) mapped to a nested served object on the way.
    /// </summary>
    private static void CopyPropertyFields(IMessage from, IMessage to)
    {
        foreach (var fromField in from.Descriptor.Fields.InDeclarationOrder())
        {
            var toField = to.Descriptor.FindFieldByName(fromField.Name);
            if (toField is null) continue;

            if (fromField.IsRepeated)
            {
                var source = (IList)fromField.Accessor.GetValue(from);
                var sink = (IList)toField.Accessor.GetValue(to);
                foreach (var item in source)
                {
                    var mapped = item is UpdateSituationObject nested ? Nested(nested) : item;
                    if (mapped is not null) sink.Add(mapped);
                }
                continue;
            }

            var value = fromField.Accessor.GetValue(from);
            if (value is null) continue;
            toField.Accessor.SetValue(to, value);
        }
    }

    /// <summary>
    /// An overlay's contents are updates, not objects; each becomes a
    /// served object the way a top-level create does. A nested update
    /// with nothing in its oneof is dropped — there is no header to refuse
    /// it with from inside a property.
    /// </summary>
    private static SituationObject? Nested(UpdateSituationObject nested)
    {
        if (Unwrap(nested) is not var (caseName, payload)) return null;
        var served = CreateServed(caseName, payload);
        ApplySparse(served, payload);
        return served;
    }

    /// <summary>
    /// The update carries one key with its source; the object keeps a
    /// dictionary of them, keyed by that source.
    /// </summary>
    private static void ApplyForeignKey(IMessage served, IMessage updateProperty, CreationMetaData meta)
    {
        if (served.Descriptor.FindFieldByName(ForeignKeysField)?.Accessor.GetValue(served)
            is not IDictionary foreignKeys) return;

        var property = new DataPropertyIdentity { CreationMetaData = meta };
        CopyPropertyFields(updateProperty, property);
        foreignKeys[property.Source ?? DefaultForeignKeySource] = property;
    }

    private static CreationMetaData MetaOf(Envelope envelope) =>
        new() { CreationTime = envelope.ReportingTime, CreatorIdentity = envelope.Reporter };

    private static IMessage NewMessage(MessageDescriptor descriptor) =>
        descriptor.Parser.ParseFrom(ReadOnlySpan<byte>.Empty);
}
