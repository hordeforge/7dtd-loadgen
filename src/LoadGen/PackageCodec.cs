using System.IO.Compression;
using System.Text;

namespace SevenDTD.LoadGen;

/// <summary>
/// Wire helpers for 7DTD NetPackage framing (from Assembly-CSharp NetConnectionSimple /
/// NetworkServerLiteNetLib).
///
/// Challenge (pre-auth): raw 17 bytes [0xCA][Guid16].
///
/// Game messages (LiteNetLib, reservedHeaderBytes=1):
///   [channel:u8]
///   [payloadSize:i32][compressed:u8][encrypted:u8][pkgCount:u16]
///   payload of payloadSize bytes (uncompressed path used here):
///     repeated: [contentLen:i32][pkgId:u16][body...]
///     where contentLen = size of (pkgId+body), EXCLUDING the contentLen field itself
///     (see NetConnectionSimple.WriteToStream: length = end - start - 4).
/// </summary>
public static class PackageCodec
{
    public const byte ChallengeChannelMarker = 202;
    public const int ChallengeSize = 17;
    public const byte DeliveryReliableOrdered = 2;
    /// <summary>LiteNetLib reserved header size (channel byte at Items[0]).</summary>
    public const int ReservedHeaderBytes = 1;
    /// <summary>Outer envelope after channel: size(4)+comp(1)+enc(1)+count(2).</summary>
    public const int OuterEnvelopeAfterChannel = 8;

    // NetPackageEntityAliveFlags constants (from live Assembly-CSharp literals)
    public const ushort FlagApproachingEnemy = 1;
    public const ushort FlagApproachingPlayer = 2;
    public const ushort FlagAimingGun = 4;
    public const ushort FlagSpawned = 8;
    public const ushort FlagJumping = 16;
    public const ushort FlagBreakingBlocks = 32;
    public const ushort FlagIsAlert = 64;
    public const ushort FlagFlashlightOn = 128;
    public const ushort FlagGodMode = 256;
    public const ushort FlagCrouching = 512;

    // EnumDamageSource / EnumDamageTypes from Assembly-CSharp
    public const byte DamageSourceExternal = 0;
    public const byte DamageSourceInternal = 1;
    public const byte DamageTypeSuffocation = 16; // drown
    public const byte DamageTypeSuicide = 26;
    public const byte DamageTypeBashing = 3; // "zombie-like" blunt

    /// <summary>Build the game's real NetPackageExplosionInitiate payload for thrown dynamite.</summary>
    public static byte[] BuildDynamiteExplosion(
        ushort packageId, int entityId, float x, float y, float z, float delaySeconds = 4f)
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            // worldPos, blockPos, rotation
            w.Write(x); w.Write(y); w.Write(z);
            w.Write((int)MathF.Floor(x)); w.Write((int)MathF.Floor(y)); w.Write((int)MathF.Floor(z));
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);

            using var explosion = new MemoryStream();
            using (var e = new BinaryWriter(explosion, Encoding.UTF8, leaveOpen: true))
            {
                e.Write((short)22);       // dynamite particle index
                e.Write((short)0);        // duration * 10
                e.Write((short)100);      // block radius 5 * 20
                e.Write((short)6);        // entity radius
                e.Write((short)100);      // blast power
                e.Write(3000f);           // block damage
                e.Write(550f);            // entity damage
                e.Write(string.Empty);    // block tags
                e.Write(false);           // affect heat map
                e.Write((short)6);        // explosion damage type
                e.Write((short)0);        // damage multipliers
                e.Write((byte)0);         // buff actions
            }
            byte[] data = explosion.ToArray();
            w.Write((ushort)data.Length);
            w.Write(data);
            w.Write(entityId);
            w.Write(delaySeconds);
            w.Write(true);                // remove/damage blocks
            w.Write(false);               // no serialized ItemValue
        });
    }

    /// <summary>Fallback when PackageIds has not been received yet. Prefer server-advertised version.</summary>
    public static readonly VersionInfo GameVersion = new(1, 3, 1, 4); // EGameReleaseType.V=1

    public readonly record struct VersionInfo(byte ReleaseType, int Major, int Minor, int Build);

    public static byte[] BuildChallengeReply(ReadOnlySpan<byte> challengePacket)
    {
        if (challengePacket.Length != ChallengeSize)
            throw new ArgumentException($"challenge must be {ChallengeSize} bytes, got {challengePacket.Length}");
        if (challengePacket[0] != ChallengeChannelMarker)
            throw new ArgumentException($"challenge marker want {ChallengeChannelMarker}, got {challengePacket[0]}");
        return challengePacket.ToArray();
    }

    public static bool TryParseChallenge(ReadOnlySpan<byte> data, out Guid challenge)
    {
        challenge = default;
        if (data.Length != ChallengeSize || data[0] != ChallengeChannelMarker)
            return false;
        challenge = new Guid(data.Slice(1, 16));
        return true;
    }

    /// <summary>
    /// Build one game package for LiteNetLib.Send matching live NetConnectionSimple + LiteNet.
    /// Always includes channel reserved byte + outer envelope (uncompressed, unencrypted).
    /// </summary>
    public static byte[] FrameChannelPackage(byte channel, ushort packageId, Action<BinaryWriter> writeBody, bool embedChannel = true)
    {
        _ = embedChannel; // always emit full live wire (channel + envelope)
        using var bodyMs = new MemoryStream();
        using (var bodyW = new BinaryWriter(bodyMs, Encoding.UTF8, leaveOpen: true))
            writeBody(bodyW);
        byte[] body = bodyMs.ToArray();

        // Inner: contentLen excludes its own 4 bytes (WriteToStream: end - start - 4)
        int contentLen = 2 + body.Length; // pkgId + body
        int payloadSize = 4 + contentLen; // contentLen field + content

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(channel);
        bw.Write(payloadSize);
        bw.Write((byte)0); // not compressed
        bw.Write((byte)0); // not encrypted
        bw.Write((ushort)1); // package count
        bw.Write(contentLen);
        bw.Write(packageId);
        bw.Write(body);
        return ms.ToArray();
    }

    /// <summary>Inner payload only: [contentLen][pkgId][body] (no outer envelope). Tests/helpers.</summary>
    public static byte[] FramePackageOnly(ushort packageId, Action<BinaryWriter> writeBody)
    {
        using var bodyMs = new MemoryStream();
        using (var bodyW = new BinaryWriter(bodyMs, Encoding.UTF8, leaveOpen: true))
            writeBody(bodyW);
        byte[] body = bodyMs.ToArray();
        int contentLen = 2 + body.Length;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(contentLen);
        bw.Write(packageId);
        bw.Write(body);
        return ms.ToArray();
    }

    public static void WriteVersion(BinaryWriter w, VersionInfo v)
    {
        w.Write(v.ReleaseType);
        w.Write(v.Major);
        w.Write(v.Minor);
        w.Write(v.Build);
    }

    public static VersionInfo ReadVersion(BinaryReader r) =>
        new(r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    /// <summary>PlatformUserIdentifier ToStream (null → 0; Local → 1,1,platform,id).</summary>
    public static void WritePlatformUser(BinaryWriter w, string? platform, string? userId)
    {
        if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(userId))
        {
            w.Write((byte)0);
            return;
        }
        w.Write((byte)1);
        w.Write((byte)1); // include custom? game writes second 1 before strings when non-null
        w.Write(platform);
        w.Write(userId);
    }

    public static void WriteLocalUser(BinaryWriter w, string playerName)
        => WritePlatformUser(w, "Local", playerName);

    public static byte[] BuildPlayerLogin(
        ushort packageId,
        string playerName,
        string versionLong,
        string compVersionLong,
        ulong discordUserId = 0)
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            w.Write(playerName);
            WriteLocalUser(w, playerName); // platformUserAndToken.Item1 + empty token string
            w.Write(""); // platform token
            WriteLocalUser(w, playerName); // crossplatform
            w.Write("");
            w.Write(versionLong);
            w.Write(compVersionLong);
            w.Write(discordUserId);
        });
    }

    public static byte[] BuildEntityPosAndRot(
        ushort packageId,
        int entityId,
        float x, float y, float z,
        float rotX, float rotY, float rotZ,
        bool onGround,
        byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
        {
            w.Write(entityId);
            w.Write(x);
            w.Write(y);
            w.Write(z);
            w.Write(false); // bUseQRotation
            w.Write(rotX);
            w.Write(rotY);
            w.Write(rotZ);
            w.Write(onGround);
        });
    }

    /// <summary>
    /// EntityRelPosAndRot : EntityRotation : EntityTargeted (live Assembly-CSharp write IL).
    /// EntityRotation.write when !bUseQ: entityId + bool + 3×Int16 rot, then RET (no qrot).
    /// RelPos then: dPos 3×Int16, onGround, updateSteps Int16.
    /// Body (!bUseQ) = 4+1+6+6+1+2 = 20. contentLen with pkgId = 22.
    /// Live server GetLength() = 20; mismatch 22-vs-38 was always-writing qrot.
    /// Rot Int16s are packed degrees: deg/360*256 (ProcessPackage multiplies back).
    /// </summary>
    public static byte[] BuildEntityRelPosAndRot(
        ushort packageId,
        int entityId,
        short dx, short dy, short dz,
        short rotX, short rotY, short rotZ,
        bool onGround,
        short updateSteps,
        bool useQRotation = false,
        float qX = 0f, float qY = 0f, float qZ = 0f, float qW = 1f,
        byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
            WriteEntityRelPosAndRotBody(w, entityId, dx, dy, dz, rotX, rotY, rotZ,
                onGround, updateSteps, useQRotation, qX, qY, qZ, qW));
    }

    public static void WriteEntityRelPosAndRotBody(
        BinaryWriter w,
        int entityId,
        short dx, short dy, short dz,
        short rotX, short rotY, short rotZ,
        bool onGround,
        short updateSteps,
        bool useQRotation = false,
        float qX = 0f, float qY = 0f, float qZ = 0f, float qW = 1f)
    {
        // NetPackageEntityTargeted + EntityRotation.write (branch, not both rot and qrot)
        w.Write(entityId);
        w.Write(useQRotation);
        if (!useQRotation)
        {
            w.Write(rotX);
            w.Write(rotY);
            w.Write(rotZ);
        }
        else
        {
            w.Write(qX);
            w.Write(qY);
            w.Write(qZ);
            w.Write(qW);
        }
        // NetPackageEntityRelPosAndRot tail
        w.Write(dx);
        w.Write(dy);
        w.Write(dz);
        w.Write(onGround);
        w.Write(updateSteps);
    }

    /// <summary>Pack degrees into game RelPos rot Int16 (0..256 maps to 0..360°).</summary>
    public static short PackAngle256(float degrees)
    {
        float n = degrees % 360f;
        if (n < 0f) n += 360f;
        return (short)Math.Clamp((int)Math.Round(n / 360f * 256f), 0, 255);
    }

    /// <summary>Body-only sizes from live Assembly-CSharp write IL (excludes frame len/id).</summary>
    public static class GoldenBodySize
    {
        // entityId(4)+pos3f(12)+bUseQ(1)+rot3f(12)+onGround(1) = 30 when !bUseQ
        public const int EntityPosAndRotNoQ = 30;
        // entityId(4)+bUseQ(1)+rot3i16(6)+dPos3i16(6)+onGround(1)+steps i16(2) = 20 when !bUseQ
        // (qrot only when bUseQ=true; never both). contentLen with pkgId = 22.
        public const int EntityRelPosAndRotNoQ = 20;
        // entityId(4)+flags u16(2) = 6
        public const int EntityAliveFlags = 6;
        /// <summary>contentLen on wire = pkgId(2)+body (must match server parse).</summary>
        public const int EntityRelPosAndRotNoQContentLen = 22;
    }

    /// <summary>Extract first package body from a live-framed LiteNet game message.</summary>
    public static byte[] ExtractBody(byte[] framed, bool hasChannelPrefix = true)
    {
        _ = hasChannelPrefix;
        var pkgs = ParseChannelPayload(framed);
        if (pkgs.Count == 0)
            throw new ArgumentException($"no packages in frame len={framed.Length}");
        return pkgs[0].body;
    }

    /// <summary>
    /// Assert package body layouts match game IL. Returns null on success, else error.
    /// Invoked by --golden-wire CLI (not mock-server circular).
    /// </summary>
    public static string? AssertGoldenWireLayouts()
    {
        // PosAndRot
        var posFrame = BuildEntityPosAndRot(99, 42, 1.5f, 2.5f, 3.5f, 10f, 20f, 30f, true);
        var posBody = ExtractBody(posFrame);
        if (posBody.Length != GoldenBodySize.EntityPosAndRotNoQ)
            return $"PosAndRot body len={posBody.Length} want {GoldenBodySize.EntityPosAndRotNoQ}";
        if (BitConverter.ToInt32(posBody, 0) != 42)
            return "PosAndRot entityId mismatch";
        if (Math.Abs(BitConverter.ToSingle(posBody, 4) - 1.5f) > 1e-5)
            return "PosAndRot pos.x mismatch";
        if (posBody[16] != 0) // bUseQRotation false
            return "PosAndRot bUseQRotation want false";
        // rot floats at offset 17
        if (Math.Abs(BitConverter.ToSingle(posBody, 17) - 10f) > 1e-5)
            return "PosAndRot rot.x mismatch (want Single)";

        // RelPosAndRot — !bUseQ: rot Int16 only (NO qrot). Live server contentLen=22.
        var relFrame = BuildEntityRelPosAndRot(
            99, 7, dx: 10, dy: 0, dz: -5,
            rotX: 100, rotY: 200, rotZ: 50,
            onGround: true, updateSteps: 2);
        var relBody = ExtractBody(relFrame);
        if (relBody.Length != GoldenBodySize.EntityRelPosAndRotNoQ)
            return $"RelPos body len={relBody.Length} want {GoldenBodySize.EntityRelPosAndRotNoQ} (no qrot when !bUseQ)";
        // contentLen at offset 9 of full frame = 2+body
        int relContentLen = BitConverter.ToInt32(relFrame, 9);
        if (relContentLen != GoldenBodySize.EntityRelPosAndRotNoQContentLen)
            return $"RelPos contentLen={relContentLen} want {GoldenBodySize.EntityRelPosAndRotNoQContentLen} (live server parse size)";
        if (BitConverter.ToInt32(relBody, 0) != 7)
            return "RelPos entityId mismatch";
        if (relBody[4] != 0)
            return "RelPos bUseQRotation want false";
        // offsets 5,7,9 = Int16 rot (not Single, no qrot after)
        if (BitConverter.ToInt16(relBody, 5) != 100)
            return "RelPos rot.x want Int16 100";
        if (BitConverter.ToInt16(relBody, 7) != 200)
            return "RelPos rot.y want Int16 200";
        if (BitConverter.ToInt16(relBody, 9) != 50)
            return "RelPos rot.z want Int16 50";
        // dPos immediately after rot (offset 11) — NOT after 16-byte qrot
        if (BitConverter.ToInt16(relBody, 11) != 10
            || BitConverter.ToInt16(relBody, 13) != 0
            || BitConverter.ToInt16(relBody, 15) != -5)
            return "RelPos dPos Int16 mismatch (must follow rot, no qrot)";
        if (relBody[17] != 1)
            return "RelPos onGround want true";
        if (BitConverter.ToInt16(relBody, 18) != 2)
            return "RelPos updateSteps want 2";
        // bUseQ path: body is larger (entityId+bool+4f + dPos+onground+steps = 4+1+16+6+1+2 = 30)
        var relQ = BuildEntityRelPosAndRot(
            99, 8, dx: 1, dy: 0, dz: 0, rotX: 0, rotY: 0, rotZ: 0,
            onGround: true, updateSteps: 1, useQRotation: true, qW: 1f);
        var relQBody = ExtractBody(relQ);
        if (relQBody.Length != 30)
            return $"RelPos bUseQ body len={relQBody.Length} want 30";

        // AliveFlags
        var flagsFrame = BuildEntityAliveFlags(99, 9, FlagJumping | FlagSpawned);
        var flagsBody = ExtractBody(flagsFrame);
        if (flagsBody.Length != GoldenBodySize.EntityAliveFlags)
            return $"AliveFlags body len={flagsBody.Length} want {GoldenBodySize.EntityAliveFlags}";
        if (BitConverter.ToInt32(flagsBody, 0) != 9)
            return "AliveFlags entityId mismatch";
        if (BitConverter.ToUInt16(flagsBody, 4) != (FlagJumping | FlagSpawned))
            return "AliveFlags flags mismatch";

        // Outer envelope: channel + size + flags + count + one inner package
        if (posFrame.Length < ReservedHeaderBytes + OuterEnvelopeAfterChannel + 6)
            return "frame missing outer envelope";
        if (posFrame[0] != 0)
            return "channel want 0";
        if (posFrame[5] != 0 || posFrame[6] != 0)
            return "compressed/encrypted want 0";
        if (BitConverter.ToUInt16(posFrame, 7) != 1)
            return "pkgCount want 1";
        int payloadSize = BitConverter.ToInt32(posFrame, 1);
        if (1 + 8 + payloadSize != posFrame.Length)
            return $"payloadSize={payloadSize} + header != framed={posFrame.Length}";
        // contentLen excludes its own 4 bytes
        int contentLen = BitConverter.ToInt32(posFrame, 9);
        if (contentLen != 2 + GoldenBodySize.EntityPosAndRotNoQ)
            return $"contentLen={contentLen} want {2 + GoldenBodySize.EntityPosAndRotNoQ}";

        // Version string for VersionAuthorizer (V 3.x packs Minor as mid*10+patch)
        string ver = VersionLongString(GameVersion);
        if (ver != "V 3.0.1")
            return $"VersionLongString want 'V 3.0.1' got '{ver}' (Minor packed)";

        // Live capture from dedicated (channel0, uncompressed PackageIds, version 1/3/1/4, count 189)
        // First 48 hex bytes of real RECV; full body not needed if envelope+version+count parse.
        byte[] liveHead = Convert.FromHexString(
            "00BC12000000000100B8120000000001030000000100000004000000BD000000144E65745061636B6167655061636B61");
        // Extend with zeros so parser can attempt; incomplete payload should fail map parse but
        // envelope fields must match observed capture.
        if (liveHead[0] != 0) return "live hex channel";
        int livePayload = BitConverter.ToInt32(liveHead, 1);
        if (livePayload != 0x12BC) return $"live payloadSize want 4796 got {livePayload}";
        if (liveHead[5] != 0 || liveHead[6] != 0) return "live comp/enc";
        if (BitConverter.ToUInt16(liveHead, 7) != 1) return "live count";
        int liveContent = BitConverter.ToInt32(liveHead, 9);
        if (liveContent != 0x12B8) return $"live contentLen want 4792 got {liveContent}";
        if (BitConverter.ToUInt16(liveHead, 13) != 0) return "live pkgId want 0";
        // version at offset 15: release=1 major=3 minor=1 build=4
        if (liveHead[15] != 1
            || BitConverter.ToInt32(liveHead, 16) != 3
            || BitConverter.ToInt32(liveHead, 20) != 1
            || BitConverter.ToInt32(liveHead, 24) != 4)
            return "live version fields";
        if (BitConverter.ToInt32(liveHead, 28) != 0xBD)
            return "live map count want 189";

        return null;
    }

    public static byte[] BuildEntityAliveFlags(
        ushort packageId,
        int entityId,
        ushort flags,
        byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
        {
            w.Write(entityId);
            w.Write(flags);
        });
    }

    /// <summary>NetPackageSimpleChat: msg:string + recipientCount:i32 (+ ids). count=0 = broadcast.</summary>
    public static byte[] BuildSimpleChat(ushort packageId, string message, byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
        {
            w.Write(message ?? "");
            w.Write(0); // no recipient filter → all
        });
    }

    /// <summary>
    /// NetPackageEntityLookAt: entityId + lookAt as 3×Int32 (write casts float→int).
    /// </summary>
    public static byte[] BuildEntityLookAt(
        ushort packageId,
        int entityId,
        float lookX, float lookY, float lookZ,
        byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
        {
            w.Write(entityId);
            w.Write((int)lookX);
            w.Write((int)lookY);
            w.Write((int)lookZ);
        });
    }

    /// <summary>
    /// NetPackageDamageEntity write order from Assembly-CSharp IL.
    /// Used for suicide (Internal+Suicide+fatal), drown (Internal+Suffocation),
    /// and external "killed" hits (External+Bashing+fatal).
    /// </summary>
    public static byte[] BuildDamageEntity(
        ushort packageId,
        int entityId,
        byte damageSource,
        byte damageType,
        ushort strength,
        bool fatal,
        int attackerEntityId = -1,
        byte channel = 0)
    {
        return FrameChannelPackage(channel, packageId, w =>
        {
            w.Write(entityId);
            w.Write(damageSource);
            w.Write(damageType);
            w.Write(strength);
            w.Write((byte)0); // hitDirection
            w.Write((short)0); // hitBodyPart
            w.Write((byte)0); // movementState
            w.Write(true); // bPainHit
            w.Write(fatal); // bFatal
            w.Write(false); // bCritical
            w.Write(attackerEntityId);
            w.Write(0f); w.Write(-1f); w.Write(0f); // dirV
            w.Write(0); w.Write(0); w.Write(0); // blockPos Vector3i as 3×Int32
            w.Write(""); // hitTransformName
            w.Write(0f); w.Write(0f); w.Write(0f); // hitTransformPosition
            w.Write(0f); w.Write(0f); // uvHit
            w.Write(1f); // damageMultiplier
            w.Write(0f); // random
            w.Write(true); // bIgnoreConsecutiveDamages
            w.Write(false); // bIsDamageTransfer
            w.Write(false); // bDismember
            w.Write(false); // bCrippleLegs
            w.Write(false); // bTurnIntoCrawler
            w.Write((byte)0); // bonusDamageType
            w.Write((byte)0); // StunType
            w.Write(0f); // StunDuration
            w.Write(false); // bFromBuff
            w.Write((byte)0); // ArmorSlot
            w.Write((byte)0); // ArmorSlotGroup
            w.Write((ushort)0); // ArmorDamage as UInt16
            w.Write(false); // attackingItem null
        });
    }

    public static byte[] BuildSuicide(ushort packageId, int entityId) =>
        BuildDamageEntity(packageId, entityId, DamageSourceInternal, DamageTypeSuicide, strength: 9999, fatal: true, attackerEntityId: entityId);

    public static byte[] BuildDrown(ushort packageId, int entityId) =>
        BuildDamageEntity(packageId, entityId, DamageSourceInternal, DamageTypeSuffocation, strength: 50, fatal: false, attackerEntityId: -1);

    public static byte[] BuildExternalKill(ushort packageId, int entityId, int attackerId = -2) =>
        BuildDamageEntity(packageId, entityId, DamageSourceExternal, DamageTypeBashing, strength: 9999, fatal: true, attackerEntityId: attackerId);

    public static byte[] BuildPackageIdsServer(
        ushort packageId,
        IReadOnlyList<string> typeNames,
        VersionInfo version,
        bool serverUseEac = false)
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            WriteVersion(w, version);
            w.Write(typeNames.Count);
            foreach (var n in typeNames)
                w.Write(n);
            w.Write(serverUseEac);
            w.Write(false); // hasHostUserAndToken
        });
    }

    public static byte[] BuildPlayerLoginAnswer(
        ushort packageId,
        bool allowed,
        string data = "")
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            w.Write(allowed);
            w.Write(data ?? "");
            // PlatformLobbyId.Write — write empty/minimal: typically platform byte + strings
            // Discovery incomplete; write zeros the reader tolerates if denied path not used
            w.Write((byte)0); // minimal lobby id
            WritePlatformUser(w, null, null);
            w.Write("");
            WritePlatformUser(w, null, null);
            w.Write("");
        });
    }

    /// <summary>NetPackageAuthConfirmation has empty body (only package id from NetPackage.write).</summary>
    public static byte[] BuildAuthConfirmation(ushort packageId) =>
        FrameChannelPackage(0, packageId, _ => { });

    /// <summary>NetPackageRequestToEnterGame: empty body.</summary>
    public static byte[] BuildRequestToEnterGame(ushort packageId) =>
        FrameChannelPackage(0, packageId, _ => { });

    /// <summary>
    /// NetPackageRequestToSpawnPlayer: chunkViewDim:i16, PlayerProfile, nearEntityId:i32.
    /// PlayerProfile v5 write: version:i32, archetype, isMale, race, variant:u8, hair, hairColor,
    /// mustache, chops, beard, eyeColor.
    /// </summary>
    public static byte[] BuildRequestToSpawnPlayer(
        ushort packageId,
        int chunkViewDim = 8,
        int nearEntityId = -1,
        bool isMale = true)
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            w.Write((short)chunkViewDim);
            // PlayerProfile.Write v5
            w.Write(5); // version
            w.Write("BaseMale"); // archetype
            w.Write(isMale);
            w.Write("White");
            w.Write((byte)0); // variant
            w.Write(""); // hair
            w.Write(""); // hairColor
            w.Write(""); // mustache
            w.Write(""); // chops
            w.Write(""); // beard
            w.Write("Blue01"); // eyeColor
            w.Write(nearEntityId);
        });
    }

    public static byte[] BuildPlayerSpawnedInWorld(
        ushort packageId,
        int respawnReason,
        int posX, int posY, int posZ,
        int entityId)
    {
        return FrameChannelPackage(0, packageId, w =>
        {
            w.Write(respawnReason);
            w.Write(posX);
            w.Write(posY);
            w.Write(posZ);
            w.Write(entityId);
        });
    }

    /// <summary>
    /// Parse a LiteNet game message: channel + outer envelope + inner packages.
    /// Decompresses Noemax/raw DEFLATE payloads (DeflateStream). Encrypted not supported.
    /// </summary>
    public static List<(ushort id, byte[] body)> ParseChannelPayload(ReadOnlySpan<byte> data, bool hasChannelPrefix = true)
    {
        _ = hasChannelPrefix;
        var list = new List<(ushort, byte[])>();
        // Need: channel(1) + size(4) + comp(1) + enc(1) + count(2) = 9
        if (data.Length < ReservedHeaderBytes + OuterEnvelopeAfterChannel)
            return list;

        int o = 0;
        o += ReservedHeaderBytes; // skip channel
        int payloadSize = BitConverter.ToInt32(data.Slice(o, 4));
        o += 4;
        byte compressed = data[o++];
        byte encrypted = data[o++];
        ushort count = BitConverter.ToUInt16(data.Slice(o, 2));
        o += 2;

        if (payloadSize < 0 || o + payloadSize > data.Length)
            return list;
        if (encrypted != 0)
            return list; // EAC encryption not implemented

        byte[] payloadBytes;
        if (compressed != 0)
        {
            var raw = data.Slice(o, payloadSize).ToArray();
            if (!TryInflate(raw, out payloadBytes!))
                return list;
        }
        else
        {
            payloadBytes = data.Slice(o, payloadSize).ToArray();
        }

        int po = 0;
        for (int i = 0; i < count && po + 6 <= payloadBytes.Length; i++)
        {
            // contentLen = size AFTER the int32 field (pkgId + body). Compare via
            // subtraction (the loop guarantees po+4 <= length) so a crafted huge
            // contentLen cannot overflow po+4+contentLen into a negative that
            // slips past the bound check and drives an out-of-bounds BlockCopy.
            int contentLen = BitConverter.ToInt32(payloadBytes, po);
            if (contentLen < 2 || contentLen > payloadBytes.Length - po - 4)
                break;
            ushort id = BitConverter.ToUInt16(payloadBytes, po + 4);
            int bodyLen = contentLen - 2;
            var body = new byte[bodyLen];
            Buffer.BlockCopy(payloadBytes, po + 6, body, 0, bodyLen);
            list.Add((id, body));
            po += 4 + contentLen;
        }
        return list;
    }

    /// <summary>Try raw DEFLATE then zlib wrapper (Noemax DeflateOutputStream is raw DEFLATE).</summary>
    // Real server packages are well under a megabyte; the cap only exists so a
    // malformed or hostile compressed frame cannot decompression-bomb the host
    // (a few KB expanding to gigabytes would OOM all bots at once). Overshoot =
    // treat as a failed decode.
    private const int MaxInflatedBytes = 64 * 1024 * 1024;

    public static bool TryInflate(byte[] compressed, out byte[]? inflated)
    {
        inflated = null;
        foreach (bool zlib in new[] { false, true })
        {
            try
            {
                using var input = new MemoryStream(compressed);
                Stream dec = zlib
                    ? new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true)
                    : new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
                using (dec)
                using (var output = new MemoryStream())
                {
                    var chunk = new byte[64 * 1024];
                    int read;
                    bool overflow = false;
                    while ((read = dec.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (output.Length + read > MaxInflatedBytes) { overflow = true; break; }
                        output.Write(chunk, 0, read);
                    }
                    if (!overflow && output.Length > 0)
                    {
                        inflated = output.ToArray();
                        return true;
                    }
                }
            }
            catch
            {
                // try next format
            }
        }
        return false;
    }

    /// <summary>
    /// Parse captured live PackageIds hex (optional unit probe). Returns map count or -1 on failure.
    /// </summary>
    public static int TryParseLivePackageIdsMapCount(ReadOnlySpan<byte> data)
    {
        var pkgs = ParseChannelPayload(data);
        if (pkgs.Count == 0) return -1;
        try
        {
            var (_, maps, _) = ParsePackageIdsBody(pkgs[0].body);
            return maps.Length;
        }
        catch
        {
            return -1;
        }
    }

    public static (VersionInfo version, string[] mappings, bool useEac) ParsePackageIdsBody(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var ver = ReadVersion(r);
        int n = r.ReadInt32();
        var maps = new string[n];
        for (int i = 0; i < n; i++)
            maps[i] = r.ReadString();
        bool eac = r.ReadBoolean();
        bool hasHost = r.ReadBoolean();
        if (hasHost)
        {
            // skip platform user + token
            if (r.ReadByte() != 0)
            {
                r.ReadByte();
                _ = r.ReadString();
                _ = r.ReadString();
            }
            _ = r.ReadString();
        }
        return (ver, maps, eac);
    }

    public static (bool allowed, string data) ParseLoginAnswerBody(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        bool allowed = r.ReadBoolean();
        string data = r.ReadString();
        return (allowed, data);
    }

    /// <summary>NetPackagePlayerDenied body after packageId: reason:i32, api:i32, banUntil:i64, custom:string.</summary>
    public static (int reason, string custom) ParsePlayerDeniedBody(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int reason = r.ReadInt32();
        _ = r.ReadInt32();
        _ = r.ReadInt64();
        string custom = r.ReadString();
        return (reason, custom);
    }

    public static (int entityId, int x, int y, int z) ParseSpawnedBody(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int reason = r.ReadInt32();
        int x = r.ReadInt32();
        int y = r.ReadInt32();
        int z = r.ReadInt32();
        int entityId = r.ReadInt32();
        _ = reason;
        return (entityId, x, y, z);
    }

    public static (int entityId, float x, float y, float z, bool onGround) ParsePosAndRotBody(byte[] body)
    {
        // Minimum non-quaternion body = int32 id + 3 floats + useQ + 3 floats +
        // onGround = 30 bytes. Reject short bodies up front so a truncated server
        // packet returns a harmless sentinel (id 0 is never our entity) instead
        // of throwing EndOfStreamException per packet. The rarer useQ=true path
        // (34 bytes) is still guarded by the caller's try/catch.
        if (body == null || body.Length < 30)
            return (0, 0f, 0f, 0f, false);
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        int entityId = r.ReadInt32();
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        float z = r.ReadSingle();
        bool useQ = r.ReadBoolean();
        if (!useQ)
        {
            _ = r.ReadSingle();
            _ = r.ReadSingle();
            _ = r.ReadSingle();
        }
        else
        {
            _ = r.ReadSingle();
            _ = r.ReadSingle();
            _ = r.ReadSingle();
            _ = r.ReadSingle();
        }
        bool onGround = r.ReadBoolean();
        return (entityId, x, y, z, onGround);
    }

    public static (int entityId, ushort flags) ParseAliveFlagsBody(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return (r.ReadInt32(), r.ReadUInt16());
    }

    /// <summary>
    /// Matches VersionInformation.LongStringNoBuild (VersionAuthorizer compares this).
    /// For EGameReleaseType.V with Major &gt;= 3 the stored Minor is packed:
    ///   display = "{V} {Major}.{Minor/10}.{Minor%10}"  e.g. Minor=1 → "V 3.0.1"
    /// Older paths use "{Release} {Major}.{Minor}".
    /// </summary>
    public static string VersionLongString(VersionInfo v)
    {
        string release = v.ReleaseType switch
        {
            0 => "Alpha",
            1 => "V",
            _ => "V",
        };
        if (v.ReleaseType == 1 && v.Major >= 3)
        {
            int mid = v.Minor / 10;
            int patch = v.Minor % 10;
            return $"{release} {v.Major}.{mid}.{patch}";
        }
        return $"{release} {v.Major}.{v.Minor}";
    }
}
