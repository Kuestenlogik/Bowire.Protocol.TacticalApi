// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Rheinmetall.TacticalApi.V0;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Flattens the <c>Identity</c> oneof into a dictionary key, for every
/// service that stores objects by identity.
/// </summary>
/// <remarks>
/// Two objects with the same digits in different identity types are
/// different objects, so the case is part of the key. An identity with
/// no case set flattens to the empty string, which no stored object
/// ever has — callers treat it as "no identity given".
/// </remarks>
internal static class IdentityKeys
{
    public static string Of(Identity? identity) => identity?.TypeCase switch
    {
        Identity.TypeOneofCase.UuidIdentity => "uuid:" + identity.UuidIdentity,
        Identity.TypeOneofCase.StringIdentity => "str:" + identity.StringIdentity,
        Identity.TypeOneofCase.Int32Identity =>
            "i32:" + identity.Int32Identity.ToString(CultureInfo.InvariantCulture),
        Identity.TypeOneofCase.Int64Identity =>
            "i64:" + identity.Int64Identity.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    /// <summary>
    /// The int32 / int64 forms are, per <c>types.proto</c>, "not for external
    /// use to create new objects" — they are what a system hands out over a
    /// low-bandwidth link once it has created the object itself.
    /// </summary>
    public static bool IsInternalOnly(Identity? identity) => identity?.TypeCase
        is Identity.TypeOneofCase.Int32Identity or Identity.TypeOneofCase.Int64Identity;
}
